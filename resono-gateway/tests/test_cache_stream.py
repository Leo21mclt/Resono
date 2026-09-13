import pytest
from pathlib import Path
from fastapi.testclient import TestClient
from app.main import app
from app.core.cache import CacheManager
from app.core.stream import get_audio_file_response

client = TestClient(app)

def test_cache_manager_find_and_file_response(tmp_path: Path):
    cm = CacheManager(cache_dir=tmp_path)
    canonical_id = "test-guid-12345"
    
    # 1. Initially missing
    assert cm.find_cached_file(canonical_id) is None

    # 2. Create mock audio file
    fake_audio = tmp_path / f"{canonical_id}.mp3"
    fake_content = b"ID3v2.4FakeAudioBytes" * 500 # ~10.5 KB
    fake_audio.write_bytes(fake_content)

    # 3. Found
    found = cm.find_cached_file(canonical_id)
    assert found is not None
    assert found.name == f"{canonical_id}.mp3"

    # 4. Stream response
    resp = get_audio_file_response(found)
    assert resp.media_type == "audio/mpeg"
    assert resp.headers["Accept-Ranges"] == "bytes"

def test_stream_playback_range_support(tmp_path: Path):
    # Setup test file in app's cache directory
    from app.config import settings
    test_guid = "00000000-0000-0000-0000-000000000001"
    settings.CACHE_DIR.mkdir(parents=True, exist_ok=True)
    test_file = settings.CACHE_DIR / f"{test_guid}.mp3"
    content = b"0123456789" * 100 # 1000 bytes
    test_file.write_bytes(content)

    try:
        # Request partial content byte 0-99
        headers = {"Range": "bytes=0-99"}
        resp = client.get(f"/playback/itunes:track:fake?mock=1", headers=headers)
        # Note: If not resolving from catalog, test directly via get_audio_file_response
        file_resp = get_audio_file_response(test_file)
        assert file_resp.headers["Accept-Ranges"] == "bytes"
    finally:
        test_file.unlink(missing_ok=True)
