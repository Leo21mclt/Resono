import pytest
import shutil
from pathlib import Path
from unittest.mock import AsyncMock, patch
from sqlalchemy import select

from app.core.models import CanonicalTrack, AudioCandidate, deterministic_guid
from app.core.acquire import AcquisitionManager
from app.core.cache import CacheManager
from app.core.validate import validate_audio_file
from app.db.models import Track, Album, Artist, SourceMapping, CacheEntry
from app.db.database import AsyncSessionLocal

@pytest.fixture
def sample_canonical_track():
    track_id = "spotify:track:test12345"
    return CanonicalTrack(
        canonical_id=deterministic_guid(track_id),
        spotify_id=track_id,
        artist_id="spotify:artist:artist123",
        album_id="spotify:album:album123",
        title="Get Lucky",
        artist_name="Daft Punk",
        album_title="Random Access Memories",
        duration_ms=248000,
        track_number=8
    )

def test_audio_validation_nonexistent(tmp_path: Path):
    res = validate_audio_file(tmp_path / "nonexistent.mp3")
    assert not res.is_valid
    assert "not found" in res.error_message.lower()

def test_audio_validation_truncated_or_corrupt(tmp_path: Path):
    corrupt_file = tmp_path / "corrupt.mp3"
    corrupt_file.write_bytes(b"NOT_A_REAL_MP3_STREAM_HEADER")
    res = validate_audio_file(corrupt_file)
    assert not res.is_valid

@pytest.mark.asyncio
async def test_virtual_track_survives_cache_eviction(tmp_path: Path, sample_canonical_track):
    """Test that deleting the audio file on disk leaves the database Track completely intact."""
    cm = CacheManager(cache_dir=tmp_path)
    
    # 1. Create a dummy audio file in cache
    audio_path = tmp_path / f"{sample_canonical_track.canonical_id}.mp3"
    audio_path.write_bytes(b"dummy_audio" * 1000)
    assert audio_path.exists()

    async with AsyncSessionLocal() as session:
        # Check or insert parent artist and album first to satisfy foreign keys
        res_a = await session.execute(select(Artist).where(Artist.id == "dummy_artist"))
        if not res_a.scalar_one_or_none():
            session.add(Artist(id="dummy_artist", spotify_id="spotify:artist:dummy", name="Daft Punk"))
        
        res_al = await session.execute(select(Album).where(Album.id == "dummy_album"))
        if not res_al.scalar_one_or_none():
            session.add(Album(id="dummy_album", spotify_id="spotify:album:dummy", artist_id="dummy_artist", title="Random Access Memories"))
        await session.flush()

        # Check or insert track in DB
        res_tr = await session.execute(select(Track).where(Track.id == sample_canonical_track.canonical_id))
        if not res_tr.scalar_one_or_none():
            db_track = Track(
                id=sample_canonical_track.canonical_id,
                spotify_id=sample_canonical_track.spotify_id,
                artist_id="dummy_artist",
                album_id="dummy_album",
                title=sample_canonical_track.title,
                duration_ms=sample_canonical_track.duration_ms
            )
            session.add(db_track)
        await session.commit()

        # Register cache entry
        await cm.register_playback(sample_canonical_track.canonical_id, audio_path, "mp3", session)

        # 2. Simulate Eviction (unlink file)
        audio_path.unlink()
        assert not audio_path.exists()

        # 3. Verify Track still exists in database
        res = await session.execute(select(Track).where(Track.id == sample_canonical_track.canonical_id))
        persisted = res.scalar_one_or_none()
        assert persisted is not None
        assert persisted.title == "Get Lucky"
        assert persisted.spotify_id == sample_canonical_track.spotify_id

@pytest.mark.asyncio
async def test_source_mapping_persistence(sample_canonical_track):
    cm = CacheManager()
    async with AsyncSessionLocal() as session:
        # Ensure parent track exists
        res_t = await session.execute(select(Track).where(Track.id == sample_canonical_track.canonical_id))
        if not res_t.scalar_one_or_none():
            db_artist = Artist(id="artist123", spotify_id="spotify:artist:artist123", name="Daft Punk")
            db_album = Album(id="album123", spotify_id="spotify:album:album123", artist_id="artist123", title="Random Access Memories")
            db_track = Track(
                id=sample_canonical_track.canonical_id,
                spotify_id=sample_canonical_track.spotify_id,
                artist_id="artist123",
                album_id="album123",
                title=sample_canonical_track.title,
                duration_ms=sample_canonical_track.duration_ms
            )
            session.add_all([db_artist, db_album, db_track])
            await session.commit()

        await cm.record_source_mapping(
            canonical_id=sample_canonical_track.canonical_id,
            peer="DaftPeer42",
            remote_path="Shared/Music/Daft Punk/Get Lucky.flac",
            file_size=35000000,
            confidence_score=0.98,
            codec="flac",
            bitrate=1000,
            duration_ms=248000,
            db=session
        )

        res = await session.execute(
            select(SourceMapping).where(SourceMapping.track_id == sample_canonical_track.canonical_id)
        )
        mapping = res.scalar_one_or_none()
        assert mapping is not None
        assert mapping.peer == "DaftPeer42"
        assert mapping.confidence_score == 0.98
        assert mapping.success_count >= 1
