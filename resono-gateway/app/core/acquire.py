import asyncio
import os
import re
import shutil
import time
import logging
from pathlib import Path
from typing import Any
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import settings
from app.core.models import CanonicalTrack, AudioCandidate
from app.core.backend import soulseek_backend
from app.core.cache import cache_manager
from app.core.jobs import job_manager
from app.core.validate import validate_audio_file
from app.matcher.scoring import matcher
from app.db.models import SourceMapping, DownloadJob

logger = logging.getLogger("resono.acquire")

# In-memory transient blacklist for unreachable/firewalled/stalled Soulseek peers
_FAILED_PEERS: dict[str, float] = {}

def blacklist_peer(username: str, duration_sec: float = 900.0):
    if username:
        clean = username.lower().strip()
        _FAILED_PEERS[clean] = time.time() + duration_sec
        logger.warning(f"[BLACKLIST] Peer '{username}' blacklisted for {int(duration_sec)}s")

def is_peer_blacklisted(username: str) -> bool:
    if not username:
        return False
    clean = username.lower().strip()
    exp = _FAILED_PEERS.get(clean)
    if exp:
        if time.time() < exp:
            return True
        else:
            _FAILED_PEERS.pop(clean, None)
    return False

def clean_for_soulseek(text: str) -> str:
    """Normalize quotes, remove parentheticals, and strip troublesome punctuation for Soulseek."""
    text = text.replace("’", "'").replace("‘", "'").replace("`", "'").replace("´", "'")
    text = text.replace("“", '"').replace("”", '"')
    text = re.sub(r"\s*[\(\[\{].*?[\)\]\}]", "", text)
    text = re.sub(r"[#\$%\*:\?\/\\\|_\-\+]", " ", text)
    return " ".join(text.split()).strip()

def get_primary_artist(artist: str) -> str:
    """Extract primary artist before collaborations or featured guests."""
    parts = re.split(r",|\s+(?:feat\.?|ft\.?|&|x|\+|with)\s+", artist, flags=re.IGNORECASE)
    return parts[0].strip() if parts else artist

class AcquisitionManager:
    """
    Orchestrates the complete audio acquisition lifecycle:
    QUEUED -> SEARCHING -> MATCHED -> DOWNLOADING -> VALIDATING -> COMPLETED
    Uses atomic cache writes (.part -> validate -> rename) and persists source mappings.
    """
    def __init__(self):
        self.downloads_path = settings.SLSKD_DOWNLOADS_PATH

    async def acquire_track_audio(self, track: CanonicalTrack, db: AsyncSession) -> Path:
        """
        Idempotently acquire and cache audio for a canonical track.
        Coalesces concurrent requests so only ONE download job is executed.
        """
        # 1. Immediate cache check
        cached = cache_manager.find_cached_file(track.canonical_id)
        if cached:
            logger.info(f"[PLAYBACK] Cache hit for '{track.title}' (canonical_id: {track.canonical_id})")
            return cached

        # 2. Coalesce uncached acquisition job
        async def _do_acquire() -> Path:
            return await self._run_acquisition_pipeline(track, db)

        return await job_manager.get_or_create(track.canonical_id, _do_acquire)

    async def _run_acquisition_pipeline(self, track: CanonicalTrack, db: AsyncSession) -> Path:
        logger.info(f"[ACQUIRE] Starting acquisition for '{track.title}' by '{track.artist_name}' ({track.canonical_id})")

        # -------------------------------------------------------------
        # STEP 1: Fast-Path Check for Known-Good Source Mapping
        # -------------------------------------------------------------
        res = await db.execute(
            select(SourceMapping)
            .where(
                SourceMapping.track_id == track.canonical_id,
                SourceMapping.failure_count == 0,
                SourceMapping.confidence_score >= settings.MATCHER_CONFIDENCE_THRESHOLD
            )
            .order_by(SourceMapping.confidence_score.desc())
        )
        known_sources = list(res.scalars().all())

        for known_source in known_sources:
            logger.info(f"[SOURCE] Testing known-good source mapping: peer={known_source.peer} score={known_source.confidence_score}")
            candidate = AudioCandidate(
                backend=soulseek_backend.name,
                peer_id=known_source.peer,
                remote_path=known_source.remote_path,
                filename=known_source.remote_path.replace("\\", "/").split("/")[-1],
                size_bytes=known_source.file_size,
                duration_ms=known_source.duration_ms,
                bitrate=known_source.bitrate,
                codec=known_source.codec or "mp3"
            )
            final_path = await self._attempt_download_and_cache(candidate, track, db)
            if final_path:
                known_source.success_count += 1
                await db.commit()
                return final_path
            else:
                logger.warning(f"[SOURCE] Known-good source {known_source.peer} failed or stalled; incrementing failure count.")
                known_source.failure_count += 1
                await db.commit()


        # -------------------------------------------------------------
        # STEP 2: Distributed Search & Exact Matching
        # -------------------------------------------------------------
        clean_title = clean_for_soulseek(track.title) or track.title
        primary_artist = clean_for_soulseek(get_primary_artist(track.artist_name)) or track.artist_name
        search_query = f"{primary_artist} {clean_title}".strip()
        logger.info(f"[SEARCH] Querying Soulseek network for '{search_query}'...")
        candidates = await soulseek_backend.search(search_query)
        logger.info(f"[SEARCH] Found {len(candidates)} candidate files across network")

        # Fallback search if primary artist query returned few candidates
        if len(candidates) < 2:
            alt_artist = clean_for_soulseek(track.artist_name)
            alt_query = f"{alt_artist} {clean_title}".strip()
            if alt_query != search_query:
                logger.info(f"[SEARCH] Low candidate count ({len(candidates)}), trying alternate query: '{alt_query}'")
                alt_candidates = await soulseek_backend.search(alt_query)
                candidates.extend(alt_candidates)

        # Filter out blacklisted / unreachable peers
        active_candidates = [c for c in candidates if not is_peer_blacklisted(c.peer_id)]
        if not active_candidates and candidates:
            logger.warning("[SEARCH] All candidates were from blacklisted peers; resetting blacklist to retry.")
            _FAILED_PEERS.clear()
            active_candidates = candidates

        ranked = matcher.find_ranked_matches(active_candidates, track)
        if not ranked:
            logger.warning(f"[MATCH] No candidate met confidence threshold >= {settings.MATCHER_CONFIDENCE_THRESHOLD}")
            raise FileNotFoundError(f"No confident recording match found on Soulseek for '{track.title}'")

        # -------------------------------------------------------------
        # STEP 3: Multi-Candidate Fallback Loop (try top 3 candidates)
        # -------------------------------------------------------------
        for match_idx, matched in enumerate(ranked[:3], 1):
            candidate = matched.candidate
            logger.info(f"[ACQUIRE] Attempting candidate #{match_idx}: peer={candidate.peer_id} file='{candidate.filename}' score={matched.score.total_score} (free_slot={candidate.slots_free}, queue={candidate.queue_length}, speed={candidate.upload_speed})")
            final_path = await self._attempt_download_and_cache(candidate, track, db)
            if final_path:
                # Record initial source mapping
                await cache_manager.record_source_mapping(
                    canonical_id=track.canonical_id,
                    peer=candidate.peer_id,
                    remote_path=candidate.remote_path,
                    file_size=candidate.size_bytes,
                    confidence_score=matched.score.total_score,
                    codec=candidate.codec,
                    bitrate=candidate.bitrate,
                    duration_ms=candidate.duration_ms,
                    db=db
                )
                return final_path
            else:
                blacklist_peer(candidate.peer_id, duration_sec=600.0)
                logger.warning(f"[ACQUIRE] Candidate #{match_idx} from peer {candidate.peer_id} failed or stalled. Trying next candidate...")

        raise FileNotFoundError(f"Failed to acquire recording for '{track.title}' after trying available candidates.")

    async def _attempt_download_and_cache(self, candidate: AudioCandidate, track: CanonicalTrack, db: AsyncSession) -> Path | None:
        """Attempt download and validation for a single candidate. Returns Path on success, None on failure."""
        logger.info(f"[DOWNLOAD] Enqueueing transfer on {soulseek_backend.name} -> peer={candidate.peer_id}")
        try:
            enqueued = await soulseek_backend.enqueue_download(candidate)
            if not enqueued:
                logger.warning(f"[DOWNLOAD] Daemon rejected transfer for peer {candidate.peer_id}")
                blacklist_peer(candidate.peer_id, duration_sec=600.0)
                return None
        except Exception as e:
            logger.warning(f"[DOWNLOAD] Exception enqueuing download for {candidate.peer_id}: {e}")
            blacklist_peer(candidate.peer_id, duration_sec=600.0)
            return None

        # Poll transfer status with fast 15s max timeout (fails fast in 3s if stalled)
        downloaded_file = await self._await_download_completion(candidate, timeout_seconds=15)
        if not downloaded_file:
            blacklist_peer(candidate.peer_id, duration_sec=600.0)
            return None

        # Atomic Cache Write & Mutagen Validation
        codec = candidate.codec or downloaded_file.suffix.lstrip(".").lower()
        part_path = cache_manager.cache_dir / f"{track.canonical_id}.{codec}.part"
        final_path = cache_manager.cache_dir / f"{track.canonical_id}.{codec}"

        try:
            shutil.copy2(downloaded_file, part_path)
            validation = validate_audio_file(part_path, expected_duration_ms=track.duration_ms)
            if not validation.is_valid:
                part_path.unlink(missing_ok=True)
                logger.warning(f"[ACQUIRE] Downloaded audio failed validation: {validation.error_message}")
                return None

            part_path.replace(final_path)
            logger.info(f"[CACHE] Stored verified audio: {final_path.name} ({validation.file_size} bytes, {validation.duration_seconds:.1f}s)")
            await cache_manager.register_playback(track.canonical_id, final_path, codec, db)
            return final_path
        except Exception as e:
            part_path.unlink(missing_ok=True)
            logger.error(f"[ACQUIRE] Cache write error for peer {candidate.peer_id}: {e}")
            return None

    async def _await_download_completion(self, candidate: AudioCandidate, timeout_seconds: int = 15) -> Path | None:
        """Poll the daemon until download completes, aborting early if queued or stalled with no progress."""
        elapsed = 0.0
        poll_interval = 0.2
        consecutive_zero_bytes_seconds = 0.0

        while elapsed < timeout_seconds:
            await asyncio.sleep(poll_interval)
            elapsed += poll_interval

            if hasattr(soulseek_backend, "check_download_progress"):
                progress_info = await soulseek_backend.check_download_progress(candidate.peer_id, candidate.remote_path)
                if progress_info:
                    download_id = progress_info.get("id")
                    if progress_info.get("is_failed"):
                        logger.error(f"[DOWNLOAD] Transfer failed on daemon: {progress_info.get('state')}")
                        blacklist_peer(candidate.peer_id, duration_sec=900.0)
                        if download_id:
                            await soulseek_backend.cancel_download(download_id, username=candidate.peer_id)
                        return None

                    if progress_info.get("is_completed"):
                        logger.info(f"[DOWNLOAD] Transfer marked completed by daemon for {candidate.peer_id}")
                        break

                    bytes_transferred = progress_info.get("bytes_transferred", 0)
                    if bytes_transferred == 0:
                        consecutive_zero_bytes_seconds += poll_interval
                        # Fast abort if peer is stuck in queue (> 2.5s) or has sent 0 bytes (> 3.2s)
                        is_queued = progress_info.get("is_queued", False)
                        threshold = 2.5 if is_queued else 3.2
                        if consecutive_zero_bytes_seconds >= threshold:
                            logger.warning(f"[DOWNLOAD] Peer {candidate.peer_id} stalled ({consecutive_zero_bytes_seconds:.1f}s with 0 bytes, queued={is_queued}). Aborting.")
                            blacklist_peer(candidate.peer_id, duration_sec=600.0)
                            if download_id:
                                await soulseek_backend.cancel_download(download_id, username=candidate.peer_id)
                            return None
                    else:
                        consecutive_zero_bytes_seconds = 0.0

            local_found = self._find_downloaded_file(candidate)
            if local_found and local_found.stat().st_size > 0:
                if not candidate.size_bytes or local_found.stat().st_size >= candidate.size_bytes * 0.95:
                    return local_found

        return self._find_downloaded_file(candidate)

    def _find_downloaded_file(self, candidate: AudioCandidate) -> Path | None:
        """Locate the downloaded file on disk in settings.SLSKD_DOWNLOADS_PATH."""
        if not self.downloads_path.exists():
            return None

        # 1. Exact path search
        expected = self.downloads_path / candidate.peer_id / candidate.filename
        if expected.exists() and expected.stat().st_size > 0:
            return expected

        # 2. Recursive search by filename (safe against special characters and brackets like [FLAC])
        target_name = candidate.filename.lower()
        for root, _, files in os.walk(self.downloads_path):
            for f in files:
                if target_name in f.lower() or f.lower() in target_name:
                    full_p = Path(root) / f
                    if full_p.is_file() and full_p.stat().st_size > 0:
                        return full_p

        return None

acquisition_manager = AcquisitionManager()
