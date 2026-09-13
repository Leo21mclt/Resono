import asyncio
import httpx
import logging
from typing import Any
from app.config import settings
from app.core.backend import SoulseekBackend
from app.core.models import AudioCandidate
from app.slskd.models import SlskdTransferStatus

logger = logging.getLogger("resono.slskd")

class SlskdClient(SoulseekBackend):
    """
    Adapter for slskd Soulseek daemon implementing SoulseekBackend.
    Normalizes slskd responses into canonical AudioCandidate models.
    """
    def __init__(self, base_url: str | None = None, api_key: str | None = None):
        self.base_url = (base_url or settings.SLSKD_URL).rstrip("/")
        self.api_key = api_key or settings.SLSKD_API_KEY
        self.headers = {
            "Content-Type": "application/json",
            "Accept": "application/json",
        }
        if self.api_key:
            self.headers["X-API-Key"] = self.api_key

    @property
    def name(self) -> str:
        return "slskd"

    def _get_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            headers=self.headers,
            timeout=httpx.Timeout(settings.SLSKD_TIMEOUT_SECONDS)
        )

    async def check_health(self) -> bool:
        try:
            async with self._get_client() as client:
                res = await client.get("/api/v0/server")
                if res.status_code == 200:
                    data = res.json()
                    return bool(data.get("isConnected") and data.get("isLoggedIn"))
                return False
        except Exception as e:
            logger.warning(f"slskd health check failed: {e}")
            return False

    async def get_session(self) -> dict[str, Any]:
        async with self._get_client() as client:
            res = await client.get("/api/v0/server")
            res.raise_for_status()
            return res.json()

    async def search(self, query: str, timeout_seconds: int | None = None) -> list[AudioCandidate]:
        timeout = timeout_seconds or settings.SLSKD_SEARCH_TIMEOUT_SECONDS
        async with self._get_client() as client:
            # 1. Initiate search
            payload = {"searchText": query}
            init_res = await client.post("/api/v0/searches", json=payload)
            init_res.raise_for_status()
            search_info = init_res.json()
            search_id = search_info.get("id")

            if not search_id:
                logger.error(f"Search failed to return searchId for query '{query}'")
                return []

            logger.info(f"Initiated Soulseek search '{query}' (id={search_id}), collecting results for up to {timeout}s...")

            # 2. Wait and poll search status
            # NOTE: slskd accumulates results in memory while state is InProgress;
            # /responses is only populated once the search is completed or stopped.
            elapsed = 0.0
            poll_interval = 0.5
            is_complete = False

            while elapsed < timeout:
                await asyncio.sleep(poll_interval)
                elapsed += poll_interval

                try:
                    status_res = await client.get(f"/api/v0/searches/{search_id}")
                    if status_res.status_code == 200:
                        data = status_res.json()
                        if isinstance(data, dict):
                            is_complete = data.get("isComplete", False)
                            file_count = data.get("fileCount", 0)
                            response_count = data.get("responseCount", 0)

                            if is_complete:
                                break
                            # If we have collected a solid pool of files after at least 3 seconds, stop early
                            if elapsed >= 3.0 and file_count >= 30:
                                logger.debug(f"Search {search_id} reached {file_count} files ({response_count} peers) at {elapsed:.1f}s, stopping early.")
                                break
                except Exception as e:
                    logger.debug(f"Polling status for search {search_id}: {e}")

            # 3. Stop search if not already completed so slskd transitions and materializes responses
            if not is_complete:
                try:
                    await client.put(f"/api/v0/searches/{search_id}")
                    # Wait briefly for slskd to transition to isComplete: true
                    for _ in range(6):  # up to 3s max (typically takes ~0.2-0.5s)
                        await asyncio.sleep(0.5)
                        st = await client.get(f"/api/v0/searches/{search_id}")
                        if st.status_code == 200:
                            st_data = st.json()
                            if isinstance(st_data, dict) and st_data.get("isComplete", False):
                                break
                except Exception as e:
                    logger.warning(f"Error stopping search {search_id}: {e}")

            # 4. Retrieve materialized responses from slskd
            candidates: list[AudioCandidate] = []
            try:
                resp_res = await client.get(
                    f"/api/v0/searches/{search_id}/responses",
                    params={"offset": 0, "limit": 250},
                )
                if resp_res.status_code == 200:
                    responses = resp_res.json()
                    if isinstance(responses, list):
                        candidates = self._parse_responses(responses)
            except Exception as e:
                logger.error(f"Failed to fetch responses for search {search_id}: {e}")

            logger.info(f"Completed search '{query}' with {len(candidates)} total candidates.")
            return candidates

    def _parse_responses(self, responses: list[dict[str, Any]]) -> list[AudioCandidate]:
        candidates: list[AudioCandidate] = []
        for r in responses:
            username = r.get("username", "")
            upload_speed = r.get("uploadSpeed", 0)
            queue_len = r.get("queueLength", 0)
            has_free_slot = r.get("hasFreeUploadSlot", True)
            files = r.get("files", [])

            for f in files:
                remote_path = f.get("filename", "")
                size = f.get("size", 0)
                is_locked = f.get("isLocked", False)
                bitrate = f.get("bitRate")
                duration = f.get("length")

                norm_path = remote_path.replace("\\", "/")
                parts = norm_path.split("/")
                filename = parts[-1] if parts else remote_path
                folder = parts[-2] if len(parts) >= 2 else ""

                ext = ""
                if "." in filename:
                    ext = filename.rsplit(".", 1)[-1].lower()

                # Filter only audio files
                if ext not in ("mp3", "flac", "m4a", "aac", "ogg", "wav", "alac"):
                    continue

                candidates.append(AudioCandidate(
                    backend="slskd",
                    peer_id=username,
                    remote_path=remote_path,
                    filename=filename,
                    folder=folder,
                    size_bytes=size,
                    duration_seconds=duration,
                    bitrate=bitrate,
                    codec=ext,
                    slots_free=has_free_slot,
                    queue_length=queue_len,
                    upload_speed=upload_speed,
                    is_locked=is_locked,
                    source_metadata=f
                ))
        return candidates

    async def enqueue_download(self, candidate: AudioCandidate) -> bool:
        async with self._get_client() as client:
            payload = [{"filename": candidate.remote_path, "size": candidate.size_bytes}]
            res = await client.post(f"/api/v0/transfers/downloads/{candidate.peer_id}", json=payload)
            return res.status_code in (200, 201, 202)

    async def get_downloads(self) -> list[dict[str, Any]]:
        async with self._get_client() as client:
            res = await client.get("/api/v0/transfers/downloads")
            return res.json() if res.status_code == 200 else []

    async def check_download_progress(self, peer_id: str, remote_path: str) -> dict[str, Any] | None:
        """Check download state and byte progress for a specific transfer."""
        downloads = await self.get_downloads()
        for user_entry in downloads:
            if user_entry.get("username", "").lower() == peer_id.lower():
                # Some slskd versions have "files" at root of user entry or under directories
                all_files = list(user_entry.get("files", []))
                for d in user_entry.get("directories", []):
                    all_files.extend(d.get("files", []))

                for f in all_files:
                    f_name = f.get("filename", "")
                    if f_name == remote_path or f_name.endswith(remote_path.replace("\\", "/").split("/")[-1]):
                        state = f.get("state", "Unknown")
                        size = f.get("size", 0)
                        bytes_transferred = f.get("bytesTransferred", 0)
                        is_completed = "succeeded" in state.lower() or "completed" in state.lower()
                        is_failed = "failed" in state.lower() or "aborted" in state.lower() or "cancelled" in state.lower()
                        return {
                            "id": f.get("id"),
                            "filename": f_name,
                            "state": state,
                            "size": size,
                            "bytes_transferred": bytes_transferred,
                            "is_completed": is_completed,
                            "is_failed": is_failed,
                            "progress": round(bytes_transferred / size, 3) if size > 0 else 0.0
                        }
        return None

    async def cancel_download(self, download_id: str, **kwargs) -> bool:
        username = kwargs.get("username", "")
        async with self._get_client() as client:
            res = await client.delete(f"/api/v0/transfers/downloads/{username}/{download_id}")
            return res.status_code in (200, 204)

slskd_client = SlskdClient()

