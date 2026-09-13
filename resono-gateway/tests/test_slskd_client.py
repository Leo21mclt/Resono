import pytest
from unittest.mock import AsyncMock, patch, MagicMock
from app.slskd.client import SlskdClient

@pytest.mark.asyncio
async def test_search_uses_pagination_params():
    slskd = SlskdClient(base_url="http://mock-slskd:5030")

    mock_http = AsyncMock()

    # 1. Mock POST /api/v0/searches
    mock_post_resp = MagicMock()
    mock_post_resp.status_code = 200
    mock_post_resp.json.return_value = {"id": "search-uuid-1234"}
    mock_post_resp.raise_for_status = MagicMock()
    mock_http.post.return_value = mock_post_resp

    # 2. Mock GET responses based on route
    async def mock_get(url, **kwargs):
        resp = MagicMock()
        resp.status_code = 200
        if "/responses" in url:
            resp.json.return_value = [
                {
                    "username": "timoz",
                    "uploadSpeed": 2379998,
                    "queueLength": 0,
                    "hasFreeUploadSlot": True,
                    "files": [
                        {
                            "filename": "Music\\Daft Punk\\Random Access Memories\\08 - Get Lucky.flac",
                            "size": 43627996,
                            "length": 369,
                            "bitRate": 1000,
                            "isLocked": False,
                        }
                    ],
                }
            ]
        else:
            resp.json.return_value = {
                "id": "search-uuid-1234",
                "searchText": "Daft Punk Get Lucky",
                "fileCount": 50,
                "responseCount": 20,
                "isComplete": True,
                "state": "Completed, Cancelled",
            }
        return resp

    mock_http.get.side_effect = mock_get

    mock_cm = AsyncMock()
    mock_cm.__aenter__.return_value = mock_http
    mock_cm.__aexit__.return_value = None

    with patch.object(slskd, "_get_client", return_value=mock_cm):
        with patch("asyncio.sleep", return_value=None):
            candidates = await slskd.search("Daft Punk Get Lucky", timeout_seconds=1)

    assert len(candidates) == 1
    assert candidates[0].filename == "08 - Get Lucky.flac"
    assert candidates[0].peer_id == "timoz"
    assert candidates[0].codec == "flac"
    assert candidates[0].duration_seconds == 369

    # Verify pagination params were passed to GET /responses
    mock_http.get.assert_called_with(
        "/api/v0/searches/search-uuid-1234/responses",
        params={"offset": 0, "limit": 250},
    )
