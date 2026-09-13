import pytest
from fastapi.testclient import TestClient
from app.main import app

client = TestClient(app)

def test_health_endpoint():
    response = client.get("/health")
    assert response.status_code == 200
    data = response.json()
    assert "status" in data
    assert data["service"] == "Resono Gateway"

def test_catalog_search_endpoint():
    response = client.get("/catalog/search?q=Drake")
    assert response.status_code == 200
    data = response.json()
    assert "tracks" in data
    assert "artists" in data
    assert "albums" in data
    assert len(data["tracks"]) > 0

def test_jellyfin_search_endpoint():
    response = client.get("/jellyfin/search?q=Drake")
    assert response.status_code == 200
    data = response.json()
    assert "artists" in data
    assert "albums" in data
    assert "tracks" in data
    if len(data["tracks"]) > 0:
        t = data["tracks"][0]
        assert "canonicalId" in t
        assert "streamUrl" in t
        assert len(t["providerIds"]) > 0
        assert any(k in t["providerIds"] for k in ("Spotify", "Itunes", "Deezer", "Mb", "Resono"))

def test_jellyfin_telemetry():
    response = client.post("/jellyfin/telemetry/playback", json={
        "canonicalId": "00000000-0000-0000-0000-000000000001",
        "isFavorite": True,
        "playCount": 1
    })
    assert response.status_code == 200
    assert response.json()["status"] in ("updated", "acknowledged")

def test_jellyfin_charts():
    response = client.get("/jellyfin/charts?country=PE")
    assert response.status_code == 200
    data = response.json()
    assert "charts" in data
    assert len(data["charts"]) >= 2
    assert any("PE" in c["name"] for c in data["charts"])

