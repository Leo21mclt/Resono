import asyncio
import shutil
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
        best_candidate: AudioCandidate | None = None
        res = await db.execute(
            select(SourceMapping)
            .where(
                SourceMapping.track_id == track.canonical_id,
                SourceMapping.failure_count == 0,
                SourceMapping.confidence_score >= settings.MATCHER_CONFIDENCE_THRESHOLD
            )
            .order_by(SourceMapping.confidence_score.desc())
        )
        known_source = res.scalar_one_or_none()
        if known_source:
            logger.info(f"[SOURCE] Found known-good source mapping: peer={known_source.peer} score={known_source.confidence_score}")
            best_candidate = AudioCandidate(
                backend=soulseek_backend.name,
                peer_id=known_source.peer,
                remote_path=known_source.remote_path,
                filename=known_source.remote_path.replace("\\", "/").split("/")[-1],
                size_bytes=known_source.file_size,
                duration_ms=known_source.duration_ms,
                bitrate=known_source.bitrate,
                codec=known_source.codec or "mp3"
            )

        # -------------------------------------------------------------
        # STEP 2: Distributed Search & Exact Matching
        # -------------------------------------------------------------
        if not best_candidate:
            search_query = f"{track.artist_name} {track.title}"
            logger.info(f"[SEARCH] Querying Soulseek network for '{search_query}'...")
            candidates = await soulseek_backend.search(search_query, timeout_seconds=6)
            logger.info(f"[SEARCH] Found {len(candidates)} candidate files across network")

            matched = matcher.find_best_match(candidates, track)
            if not matched:
                logger.warning(f"[MATCH] No candidate met confidence threshold >= {settings.MATCHER_CONFIDENCE_THRESHOLD}")
                raise FileNotFoundError(f"No confident recording match found on Soulseek for '{track.title}'")

            best_candidate = matched.candidate
            logger.info(f"[MATCH] Selected peer={best_candidate.peer_id} file='{best_candidate.filename}' score={matched.score.total_score}")

            # Record initial source mapping
            await cache_manager.record_source_mapping(
                canonical_id=track.canonical_id,
                peer=best_candidate.peer_id,
                remote_path=best_candidate.remote_path,
                file_size=best_candidate.size_bytes,
                confidence_score=matched.score.total_score,
                codec=best_candidate.codec,
                bitrate=best_candidate.bitrate,
                duration_ms=best_candidate.duration_ms,
                db=db
            )

        # -------------------------------------------------------------
        # STEP 3: Enqueue Download & Poll Completion
        # -------------------------------------------------------------
        logger.info(f"[DOWNLOAD] Enqueueing transfer on {soulseek_backend.name} -> peer={best_candidate.peer_id}")
        enqueued = await soulseek_backend.enqueue_download(best_candidate)
        if not enqueued:
            raise RuntimeError(f"Soulseek daemon rejected download for peer {best_candidate.peer_id}")

        # Poll transfer status
        downloaded_file = await self._await_download_completion(best_candidate, timeout_seconds=60)
        if not downloaded_file:
            raise TimeoutError(f"Download timed out or failed on peer {best_candidate.peer_id}")

        # -------------------------------------------------------------
        # STEP 4: Atomic Cache Write & Mutagen Validation
        # -------------------------------------------------------------
        codec = best_candidate.codec or downloaded_file.suffix.lstrip(".").lower()
        part_path = cache_manager.cache_dir / f"{track.canonical_id}.{codec}.part"
        final_path = cache_manager.cache_dir / f"{track.canonical_id}.{codec}"

        try:
            # Copy to .part path
            shutil.copy2(downloaded_file, part_path)

            # Validate audio integrity
            validation = validate_audio_file(part_path, expected_duration_ms=track.duration_ms)
            if not validation.is_valid:
                part_path.unlink(missing_ok=True)
                raise ValueError(f"Downloaded audio failed validation: {validation.error_message}")

            # Atomic rename: .part -> final
            part_path.replace(final_path)
            logger.info(f"[CACHE] Stored verified audio: {final_path.name} ({validation.file_size} bytes, {validation.duration_seconds:.1f}s)")

            # Register in cache manager DB
            await cache_manager.register_playback(track.canonical_id, final_path, codec, db)
            return final_path

        except Exception as e:
            part_path.unlink(missing_ok=True)
            logger.error(f"[ACQUIRE] Validation or cache write failed: {e}")
            raise

    async def _await_download_completion(self, candidate: AudioCandidate, timeout_seconds: int = 60) -> Path | None:
        """Poll the daemon until the download completes, then locate the file on disk."""
        elapsed = 0
        poll_interval = 2

        while elapsed < timeout_seconds:
            # Check progress on daemon
            if hasattr(soulseek_backend, "check_download_progress"):
                progress_info = await soulseek_backend.check_download_progress(candidate.peer_id, candidate.remote_path)
                if progress_info:
                    if progress_info.get("is_failed"):
                        logger.error(f"[DOWNLOAD] Transfer failed on daemon: {progress_info.get('state')}")
                        return None
                    if progress_info.get("is_completed"):
                        logger.info(f"[DOWNLOAD] Transfer marked completed by daemon")
                        break

            # Also check if file exists directly on disk in downloads folder
            local_found = self._find_downloaded_file(candidate)
            if local_found and local_found.stat().st_size >= candidate.size_bytes * 0.95:
                return local_found

            await asyncio.sleep(poll_interval)
            elapsed += poll_interval

        # Final check for file on disk
        return self._find_downloaded_file(candidate)

    def _find_downloaded_file(self, candidate: AudioCandidate) -> Path | None:
        """Locate the downloaded file on disk in settings.SLSKD_DOWNLOADS_PATH."""
        if not self.downloads_path.exists():
            return None

        # 1. Exact path search
        expected = self.downloads_path / candidate.peer_id / candidate.filename
        if expected.exists() and expected.stat().st_size > 0:
            return expected

        # 2. Recursive search by filename
        for p in self.downloads_path.rglob(f"*{candidate.filename}*"):
            if p.is_file() and p.stat().st_size > 0:
                return p

        return None

acquisition_manager = AcquisitionManager()
