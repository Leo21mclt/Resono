import os
import shutil
import logging
from pathlib import Path
from datetime import datetime, timezone
from sqlalchemy import select, delete
from sqlalchemy.ext.asyncio import AsyncSession
from app.config import settings
from app.db.models import CacheEntry, Track, SourceMapping
from app.db.database import AsyncSessionLocal

logger = logging.getLogger("resono.cache")

class CacheManager:
    """
    Adaptive cache manager.
    Tracks audio files on disk, records play metrics, and performs eviction
    based on the retention score formula while keeping metadata intact.
    """
    def __init__(self, cache_dir: Path | None = None):
        self.cache_dir = cache_dir or settings.CACHE_DIR
        self.cache_dir.mkdir(parents=True, exist_ok=True)

    def get_track_file_path(self, canonical_id: str, codec: str = "mp3") -> Path:
        return self.cache_dir / f"{canonical_id}.{codec}"

    def find_cached_file(self, canonical_id: str) -> Path | None:
        """Check if any audio file for this canonical ID exists in cache."""
        import uuid
        clean_id = canonical_id.replace("-", "").strip()
        candidates = [canonical_id]
        if "-" in canonical_id:
            candidates.append(clean_id)
        elif len(clean_id) == 32:
            try:
                candidates.append(str(uuid.UUID(hex=clean_id)))
            except Exception:
                pass

        for cid in candidates:
            for ext in ("mp3", "flac", "m4a", "ogg", "opus", "wav"):
                p = self.cache_dir / f"{cid}.{ext}"
                if p.exists() and p.stat().st_size > 0:
                    return p
        return None

    def get_total_cache_size_bytes(self) -> int:
        total = 0
        for entry in self.cache_dir.glob("*.*"):
            if entry.is_file():
                total += entry.stat().st_size
        return total

    async def register_playback(self, canonical_id: str, file_path: Path, codec: str, db: AsyncSession) -> CacheEntry:
        """Register or update a track playback event in the cache database."""
        file_size = file_path.stat().st_size if file_path.exists() else 0
        res = await db.execute(select(CacheEntry).where(CacheEntry.track_id == canonical_id))
        entry = res.scalar_one_or_none()

        now = datetime.now(timezone.utc)
        if entry:
            entry.play_count += 1
            entry.last_played_at = now
            entry.file_path = str(file_path)
            entry.file_size = file_size
            entry.codec = codec
            entry.tier = "HOT"
        else:
            entry = CacheEntry(
                track_id=canonical_id,
                file_path=str(file_path),
                file_size=file_size,
                codec=codec,
                tier="HOT",
                play_count=1,
                last_played_at=now,
                retention_score=100.0
            )
            db.add(entry)

        await db.commit()
        await self.enforce_retention_policy(db)
        return entry

    async def enforce_retention_policy(self, db: AsyncSession):
        """Evict lowest retention score tracks if cache size exceeds limit."""
        max_bytes = int(settings.MAX_CACHE_SIZE_GB * 1024 * 1024 * 1024)
        current_size = self.get_total_cache_size_bytes()

        if current_size <= max_bytes:
            return

        logger.info(f"Cache size ({current_size / (1024*1024):.1f} MB) exceeds limit ({max_bytes / (1024*1024):.1f} MB). Running eviction...")

        res = await db.execute(select(CacheEntry).where(CacheEntry.tier != "PROTECTED"))
        entries = list(res.scalars().all())

        # Calculate retention scores
        scored_entries = []
        for e in entries:
            score = (e.play_count * 10.0) - (e.file_size / (10.0 * 1024 * 1024))
            scored_entries.append((score, e))

        scored_entries.sort(key=lambda x: x[0]) # Lowest retention score first

        for score, entry in scored_entries:
            if current_size <= max_bytes:
                break
            p = Path(entry.file_path)
            if p.exists():
                size = p.stat().st_size
                p.unlink(missing_ok=True)
                current_size -= size
                logger.info(f"Evicted audio for track {entry.track_id} (freed {size} bytes, score {score:.1f})")
            entry.tier = "VIRTUAL"

        await db.commit()

    async def record_source_mapping(
        self,
        canonical_id: str,
        peer: str,
        remote_path: str,
        file_size: int,
        confidence_score: float,
        codec: str | None,
        bitrate: int | None,
        duration_ms: int | None,
        db: AsyncSession
    ) -> None:
        """Persist or update peer source reliability metrics in SQLite."""
        res = await db.execute(
            select(SourceMapping).where(
                SourceMapping.track_id == canonical_id,
                SourceMapping.peer == peer,
                SourceMapping.remote_path == remote_path
            )
        )
        mapping = res.scalar_one_or_none()
        now = datetime.now(timezone.utc)

        if mapping:
            mapping.confidence_score = confidence_score
            mapping.success_count += 1
            mapping.last_used_at = now
        else:
            mapping = SourceMapping(
                track_id=canonical_id,
                peer=peer,
                remote_path=remote_path,
                file_size=file_size,
                duration_ms=duration_ms,
                bitrate=bitrate,
                codec=codec,
                confidence_score=confidence_score,
                success_count=1,
                last_used_at=now
            )
            db.add(mapping)

        await db.commit()

cache_manager = CacheManager()

