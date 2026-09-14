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

    async def search(
        self,
        query: str,
        timeout_seconds: int | float | None = None,
        target_track: Any | None = None
    ) -> list[AudioCandidate]:
        timeout = float(timeout_seconds or settings.SLSKD_SEARCH_TIMEOUT_SECONDS)
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

            logger.info(f"Initiated real-time Soulseek search '{query}' (id={search_id}), collecting results (up to {timeout}s)...")

            elapsed = 0.0
            poll_interval = 0.3
            candidates: list[AudioCandidate] = []
            found_early_match = False

            # Helper for background stop
            base_url = self.base_url
            def _fire_background_stop(sid: str):
                async def _stop():
                    try:
                        async with httpx.AsyncClient(base_url=base_url, timeout=3.0) as cl:
                            await cl.put(f"/api/v0/searches/{sid}")
                    except Exception:
                        pass
                asyncio.create_task(_stop())

            while elapsed < timeout:
                await asyncio.sleep(poll_interval)
                elapsed += poll_interval

                try:
                    # Poll real-time responses directly while search is running
                    resp_res = await client.get(
                        f"/api/v0/searches/{search_id}/responses",
                        params={"offset": 0, "limit": 200},
                    )
                    if resp_res.status_code == 200:
                        responses = resp_res.json()
                        if isinstance(responses, list) and len(responses) > 0:
                            candidates = self._parse_responses(responses)

                            # If target_track is provided, score incoming candidates in real-time
                            if target_track and candidates:
                                from app.matcher.scoring import matcher
                                ranked = matcher.find_ranked_matches(candidates, target_track)
                                if ranked:
                                    top = ranked[0]
                                    # Early-exit criteria:
                                    # High confidence (>= 0.85) with free slots and zero queue,
                                    # OR solid confidence (>= 0.80) after at least 1.2s
                                    is_stellar = (
                                        top.score.total_score >= 0.85
                                        and top.candidate.slots_free
                                        and top.candidate.queue_length == 0
                                    )
                                    is_solid = (
                                        elapsed >= 1.2
                                        and top.score.total_score >= 0.80
                                        and top.candidate.slots_free
                                        and top.candidate.queue_length == 0
                                    )
                                    if is_stellar or is_solid:
                                        logger.info(
                                            f"[SEARCH-REALTIME] Early-exit match at {elapsed:.1f}s! "
                                            f"Peer: '{top.candidate.peer_id}', file: '{top.candidate.filename}', "
                                            f"score: {top.score.total_score:.2f} ({top.candidate.codec} {top.candidate.bitrate}k)"
                                        )
                                        found_early_match = True
                                        _fire_background_stop(search_id)
                                        return [r.candidate for r in ranked]
                except Exception as e:
                    logger.debug(f"Polling responses for search {search_id}: {e}")

                # Check if search completed naturally
                try:
                    status_res = await client.get(f"/api/v0/searches/{search_id}")
                    if status_res.status_code == 200:
                        data = status_res.json()
                        if isinstance(data, dict) and data.get("isComplete", False):
                            logger.debug(f"Search {search_id} marked complete by daemon at {elapsed:.1f}s.")
                            break
                except Exception:
                    pass

            if not found_early_match:
                _fire_background_stop(search_id)

            # Final check for responses if we haven't collected any yet
            if not candidates:
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
                    logger.error(f"Failed to fetch final responses for search {search_id}: {e}")

            logger.info(f"Completed search '{query}' at {elapsed:.1f}s with {len(candidates)} total candidates.")
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
                        state = str(f.get("state", "Unknown"))
                        size = f.get("size", 0)
                        bytes_transferred = f.get("bytesTransferred", 0)
                        state_lower = state.lower()
                        is_completed = "succeeded" in state_lower
                        is_failed = any(w in state_lower for w in ("failed", "aborted", "cancelled", "errored", "rejected", "timedout"))
                        is_queued = "queued" in state_lower
                        return {
                            "id": f.get("id"),
                            "filename": f_name,
                            "state": state,
                            "size": size,
                            "bytes_transferred": bytes_transferred,
                            "is_completed": is_completed,
                            "is_failed": is_failed,
                            "is_queued": is_queued,
                            "progress": round(bytes_transferred / size, 3) if size > 0 else 0.0
                        }
        return None

    async def cancel_download(self, download_id: str, **kwargs) -> bool:
        username = kwargs.get("username", "")
        async with self._get_client() as client:
            res = await client.delete(f"/api/v0/transfers/downloads/{username}/{download_id}")
            return res.status_code in (200, 204)

slskd_client = SlskdClient()

