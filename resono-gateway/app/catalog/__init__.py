from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider
from app.catalog.spotify import SpotifyProvider
from app.catalog.itunes import ITunesProvider
from app.catalog.manager import CatalogManager, catalog_manager

__all__ = [
    "CatalogSearchResult",
    "CatalogArtist",
    "CatalogAlbum",
    "CatalogTrack",
    "CatalogProvider",
    "SpotifyProvider",
    "ITunesProvider",
    "CatalogManager",
    "catalog_manager",
]

