from abc import ABC, abstractmethod
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack

class CatalogProvider(ABC):
    @property
    @abstractmethod
    def name(self) -> str:
        """Provider name"""
        pass

    @abstractmethod
    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        """Search for artists, albums, and tracks"""
        pass

    @abstractmethod
    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        """Get artist details and albums"""
        pass

    @abstractmethod
    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        """Get album details and tracklist"""
        pass

    @abstractmethod
    async def get_track(self, track_id: str) -> CatalogTrack | None:
        """Get track metadata"""
        pass

    async def get_lyrics(self, track_id: str) -> dict | None:
        """Get track lyrics (synced and/or plain)"""
        return None
