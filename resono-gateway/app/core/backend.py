from abc import ABC, abstractmethod
from typing import Any
from app.core.models import AudioCandidate

class SoulseekBackend(ABC):
    """
    Abstract interface for Soulseek daemon backends (slskr, slskd, etc.).
    Shields the rest of Resono from daemon-specific quirks and schemas.
    """
    @property
    @abstractmethod
    def name(self) -> str:
        """Backend identifier name"""
        pass

    @abstractmethod
    async def check_health(self) -> bool:
        """Check if daemon is reachable and connected to Soulseek server"""
        pass

    @abstractmethod
    async def search(self, query: str, timeout_seconds: int = 6) -> list[AudioCandidate]:
        """Execute search on Soulseek network and return normalized AudioCandidates"""
        pass

    @abstractmethod
    async def enqueue_download(self, candidate: AudioCandidate) -> bool:
        """Enqueue download of the selected AudioCandidate"""
        pass

    @abstractmethod
    async def get_downloads(self) -> list[dict[str, Any]]:
        """List active and completed transfers"""
        pass

    @abstractmethod
    async def cancel_download(self, download_id: str, **kwargs) -> bool:
        """Cancel an in-flight transfer"""
        pass

def get_soulseek_backend(backend_type: str | None = None) -> SoulseekBackend:
    from app.config import settings
    selected = (backend_type or settings.SOULSEEK_BACKEND).lower().strip()
    if selected == "slskr":
        from app.slskr.client import SlskrClient
        return SlskrClient()
    else:
        from app.slskd.client import SlskdClient
        return SlskdClient()

soulseek_backend = get_soulseek_backend()

