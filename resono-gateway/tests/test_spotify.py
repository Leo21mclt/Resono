import pytest
from app.catalog.spotify import SpotifyProvider
from app.catalog.manager import CatalogManager
from app.catalog.models import CatalogTrack
from app.core.models import deterministic_guid

def test_spotify_id_extraction():
    provider = SpotifyProvider()
    assert provider._extract_id("spotify:track:4cOdK2wGLETKBW3PvgPWqT") == "4cOdK2wGLETKBW3PvgPWqT"
    assert provider._extract_id("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=abc") == "4cOdK2wGLETKBW3PvgPWqT"
    assert provider._extract_id("4cOdK2wGLETKBW3PvgPWqT") == "4cOdK2wGLETKBW3PvgPWqT"

def test_deterministic_guid_stability():
    uri1 = "spotify:track:4cOdK2wGLETKBW3PvgPWqT"
    uri2 = "spotify:track:4cOdK2wGLETKBW3PvgPWqT"
    guid1 = deterministic_guid(uri1)
    guid2 = deterministic_guid(uri2)
    assert guid1 == guid2
    # Verify it matches UUID format
    assert len(guid1) == 36
    assert guid1.count("-") == 4

@pytest.mark.asyncio
async def test_catalog_manager_fallback():
    # An unconfigured Spotify provider falls back seamlessly to iTunes
    mgr = CatalogManager()
    res = await mgr.search("Rick Astley Never Gonna Give You Up", limit=5)
    assert len(res.tracks) > 0
    t = res.tracks[0]
    canonical = mgr.to_canonical_track(t)
    assert canonical.canonical_id == deterministic_guid(t.id)
    assert canonical.title != ""
