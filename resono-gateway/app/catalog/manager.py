import logging
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider
from app.catalog.spotify import SpotifyProvider
from app.catalog.itunes import ITunesProvider
from app.core.models import CanonicalTrack, deterministic_guid
from app.db.models import Track, Album, Artist
from app.db.database import AsyncSessionLocal

logger = logging.getLogger("resono.catalog.manager")

class CatalogManager:
    """
    Multi-provider catalog orchestrator.
    Prioritizes SpotifyProvider as primary canonical catalog.
    Gracefully falls back to ITunesProvider if Spotify is unconfigured or unavailable.
    """
    def __init__(
        self,
        primary_provider: CatalogProvider | None = None,
        fallback_provider: CatalogProvider | None = None
    ):
        self.primary: CatalogProvider = primary_provider or SpotifyProvider()
        self.fallback: CatalogProvider = fallback_provider or ITunesProvider()

    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        # 1. Try Spotify (primary) if configured
        if isinstance(self.primary, SpotifyProvider) and self.primary.is_configured():
            try:
                res = await self.primary.search(query, limit=limit)
                if res.tracks or res.albums or res.artists:
                    return res
                logger.info(f"Primary provider returned 0 results for '{query}', trying fallback...")
            except Exception as e:
                logger.warning(f"Primary catalog provider error: {e}")

        # 2. Fall back to iTunes
        logger.info(f"Searching via fallback catalog provider ({self.fallback.name}) for '{query}'")
        return await self.fallback.search(query, limit=limit)

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        if artist_id.startswith("spotify:"):
            if isinstance(self.primary, SpotifyProvider) and self.primary.is_configured():
                return await self.primary.get_artist(artist_id)
        elif artist_id.startswith("itunes:"):
            return await self.fallback.get_artist(artist_id)

        # Generic lookup: try primary then fallback
        res = await self.primary.get_artist(artist_id)
        if not res and self.fallback:
            res = await self.fallback.get_artist(artist_id)
        return res

    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        if album_id.startswith("spotify:"):
            if isinstance(self.primary, SpotifyProvider) and self.primary.is_configured():
                return await self.primary.get_album(album_id)
        elif album_id.startswith("itunes:"):
            return await self.fallback.get_album(album_id)

        res = await self.primary.get_album(album_id)
        if not res and self.fallback:
            res = await self.fallback.get_album(album_id)
        return res

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        if track_id.startswith("spotify:"):
            if isinstance(self.primary, SpotifyProvider) and self.primary.is_configured():
                return await self.primary.get_track(track_id)
        elif track_id.startswith("itunes:"):
            return await self.fallback.get_track(track_id)

        res = await self.primary.get_track(track_id)
        if not res and self.fallback:
            res = await self.fallback.get_track(track_id)
        return res

    def to_canonical_track(self, track: CatalogTrack) -> CanonicalTrack:
        """Convert a CatalogTrack into the backend-neutral CanonicalTrack with deterministic GUID."""
        guid = deterministic_guid(track.id)
        return CanonicalTrack(
            canonical_id=guid,
            spotify_id=track.id,
            artist_id=track.artist_id,
            album_id=track.album_id,
            title=track.title,
            artist_name=track.artist_name,
            album_title=track.album_title,
            duration_ms=track.duration_ms,
            disc_number=track.disc_number,
            track_number=track.track_number,
            isrc=track.isrc,
            artwork_url=track.artwork_url,
            explicit=track.explicit
        )

    async def sync_track_to_db(self, track: CatalogTrack, session: AsyncSession) -> Track:
        """
        Persist canonical Track, Album, and Artist records into SQLite.
        Uses deterministic GUIDs derived from Spotify IDs for stable primary keys.
        """
        # 1. Check or insert Artist
        res_artist = await session.execute(select(Artist).where(Artist.spotify_id == track.artist_id))
        db_artist = res_artist.scalar_one_or_none()
        if not db_artist:
            db_artist = Artist(
                id=deterministic_guid(track.artist_id),
                spotify_id=track.artist_id,
                name=track.artist_name,
                artwork_url=track.artwork_url
            )
            session.add(db_artist)
            await session.flush()

        # 2. Check or insert Album
        res_album = await session.execute(select(Album).where(Album.spotify_id == track.album_id))
        db_album = res_album.scalar_one_or_none()
        if not db_album:
            db_album = Album(
                id=deterministic_guid(track.album_id),
                spotify_id=track.album_id,
                artist_id=db_artist.id,
                title=track.album_title,
                artwork_url=track.artwork_url
            )
            session.add(db_album)
            await session.flush()

        # 3. Check or insert Track
        res_track = await session.execute(select(Track).where(Track.spotify_id == track.id))
        db_track = res_track.scalar_one_or_none()
        if not db_track:
            db_track = Track(
                id=deterministic_guid(track.id),
                spotify_id=track.id,
                artist_id=db_artist.id,
                album_id=db_album.id,
                title=track.title,
                duration_ms=track.duration_ms,
                disc_number=track.disc_number,
                track_number=track.track_number,
                isrc=track.isrc,
                explicit=track.explicit
            )
            session.add(db_track)
            await session.flush()

        await session.commit()
        return db_track

catalog_manager = CatalogManager()

