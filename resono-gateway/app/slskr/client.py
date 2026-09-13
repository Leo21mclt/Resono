import asyncio
import httpx
import logging
from typing import Any
from app.config import settings
from app.core.backend import SoulseekBackend
from app.core.models import AudioCandidate

logger = logging.getLogger("resono.slskr")

class SlskrClient(SoulseekBackend):
    """
    Adapter for snapetech/slskr Soulseek daemon.
    Normalizes slskr response structures into canonical AudioCandidate models.
    """
    def __init__(self, base_url: str | None = None, api_key: str | None = None):
        self.base_url = (base_url or getattr(settings, "SLSKR_URL", settings.SLSKD_URL)).rstrip("/")
        self.api_key = api_key or getattr(settings, "SLSKR_API_KEY", settings.SLSKD_API_KEY)
        self.headers = {
            "Content-Type": "application/json",
            "Accept": "application/json",
        }
        if self.api_key:
            self.headers["X-API-Key"] = self.api_key
            self.headers["Authorization"] = f"Bearer {self.api_key}"

    @property
    def name(self) -> str:
        return "slskr"

    def _get_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            headers=self.headers,
            timeout=httpx.Timeout(settings.SLSKD_TIMEOUT_SECONDS)
        )

    async def check_health(self) -> bool:
        try:
            async with self._get_client() as client:
                res = await client.get("/health")
                if res.status_code == 200:
                    data = res.json()
                    return data.get("status") == "ok"
                # Fallback to /api/v0/session
                s_res = await client.get("/api/v0/session")
                if s_res.status_code == 200:
                    return s_res.json().get("state") == "connected"
                return False
        except Exception as e:
            logger.warning(f"slskr health check failed: {e}")
            return False

    async def search(self, query: str, timeout_seconds: int = 6) -> list[AudioCandidate]:
        candidates: list[AudioCandidate] = []
        try:
            async with self._get_client() as client:
                # 1. Initiate search
                payload = {"searchText": query}
                init_res = await client.post("/api/v0/searches", json=payload)
                if init_res.status_code not in (200, 201):
                    logger.error(f"slskr search initiation returned status {init_res.status_code}")
                    return []
                
                search_data = init_res.json()
                search_id = search_data.get("id") or search_data.get("searchId")
                if not search_id:
                    logger.error(f"slskr search failed to return ID for query '{query}'")
                    return []

                logger.info(f"slskr search '{query}' initiated (id={search_id}), collecting for {timeout_seconds}s...")

                # 2. Poll responses
                elapsed = 0.0
                poll_interval = 1.0

                while elapsed < timeout_seconds:
                    await asyncio.sleep(poll_interval)
                    elapsed += poll_interval

                    try:
                        resp_res = await client.get(f"/api/v0/searches/{search_id}/responses")
                        if resp_res.status_code == 200:
                            current = self._parse_candidates(resp_res.json())
                            if len(current) > len(candidates):
                                candidates = current
                                logger.debug(f"slskr collected {len(candidates)} audio candidates...")
                            if len(candidates) >= 50 and elapsed >= 3.0:
                                break
                    except Exception as e:
                        logger.debug(f"slskr polling responses: {e}")

        except Exception as e:
            logger.error(f"slskr search failed for '{query}': {e}")

        logger.info(f"slskr search '{query}' completed with {len(candidates)} normalized candidates.")
        return candidates

    def _parse_candidates(self, responses: list[dict[str, Any]]) -> list[AudioCandidate]:
        candidates: list[AudioCandidate] = []
        for r in responses:
            peer = r.get("username", "")
            upload_speed = r.get("uploadSpeed", 0)
            queue_len = r.get("queueLength", 0)
            has_free_slot = r.get("hasFreeUploadSlot", True)
            files = r.get("files", [])

            for f in files:
                remote_path = f.get("filename", "")
                size = f.get("size", 0)
                is_locked = f.get("isLocked", False)
                bitrate = f.get("bitRate")
                duration_sec = f.get("length")

                # Parse filename and folder
                norm_path = remote_path.replace("\\", "/")
                parts = norm_path.split("/")
                filename = parts[-1] if parts else remote_path
                folder = parts[-2] if len(parts) >= 2 else ""

                ext = ""
                if "." in filename:
                    ext = filename.rsplit(".", 1)[-1].lower()

                # Audio file filter
                if ext not in ("mp3", "flac", "m4a", "aac", "ogg", "wav", "alac"):
                    continue

                candidates.append(AudioCandidate(
                    backend="slskr",
                    peer_id=peer,
                    remote_path=remote_path,
                    filename=filename,
                    folder=folder,
                    size_bytes=size,
                    duration_seconds=duration_sec,
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
        try:
            async with self._get_client() as client:
                payload = [{"filename": candidate.remote_path, "size": candidate.size_bytes}]
                res = await client.post(f"/api/v0/transfers/downloads/{candidate.peer_id}", json=payload)
                return res.status_code in (200, 201, 202)
        except Exception as e:
            logger.error(f"slskr download enqueue failed for peer {candidate.peer_id}: {e}")
            return False

    async def get_downloads(self) -> list[dict[str, Any]]:
        try:
            async with self._get_client() as client:
                res = await client.get("/api/v0/transfers/downloads")
                return res.json() if res.status_code == 200 else []
        except Exception:
            return []

    async def cancel_download(self, download_id: str, **kwargs) -> bool:
        username = kwargs.get("username", "")
        try:
            async with self._get_client() as client:
                res = await client.delete(f"/api/v0/transfers/downloads/{username}/{download_id}")
                return res.status_code in (200, 204)
        except Exception:
            return False

slskr_client = SlskrClient()
