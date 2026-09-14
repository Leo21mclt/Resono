import logging
import time
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from app.config import settings
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider
from app.catalog.itunes import ITunesProvider
from app.catalog.deezer import DeezerProvider
from app.catalog.spotify import SpotifyProvider
from app.catalog.musicbrainz import MusicBrainzProvider
from app.core.models import CanonicalTrack, deterministic_guid
from app.db.models import Track, Album, Artist
from app.db.database import AsyncSessionLocal

logger = logging.getLogger("resono.catalog.manager")

class CatalogManager:
    """
    Multi-provider catalog orchestrator supporting:
    - Apple Music (iTunes API, ultra-fast, 1400x1400 art, zero auth) - Default
    - Deezer API (ultra-fast, rich tracklists, zero auth)
    - Spotify (SpotAPI zero-auth or Official Web API)
    - MusicBrainz (open community encyclopedia)
    """
    def __init__(self):
        _apple = ITunesProvider()
        _deezer = DeezerProvider()
        _spotify = SpotifyProvider()
        _musicbrainz = MusicBrainzProvider()
        self.providers: dict[str, CatalogProvider] = {
            "apple": _apple,
            "itunes": _apple,
            "deezer": _deezer,
            "spotify": _spotify,
            "musicbrainz": _musicbrainz,
            "mb": _musicbrainz,
        }
        self._cache: dict[str, tuple[float, CatalogSearchResult]] = {}
        self._cache_ttl_sec = 600  # 10 minutes

    def get_provider(self, name: str | None) -> CatalogProvider:
        clean = (name or "").lower().strip()
        if clean in self.providers:
            return self.providers[clean]
        default_name = getattr(settings, "CATALOG_PROVIDER", "apple").lower().strip()
        return self.providers.get(default_name, self.providers["apple"])

    async def search(
        self,
        query: str,
        limit: int = 20,
        provider: str | None = None,
        fallback: str | None = None
    ) -> CatalogSearchResult:
        clean_query = query.strip()
        if not clean_query:
            return CatalogSearchResult()

        prim_name = provider or getattr(settings, "CATALOG_PROVIDER", "apple")
        fall_name = fallback or getattr(settings, "FALLBACK_CATALOG_PROVIDER", "deezer")
        cache_key = f"{prim_name}:{fall_name}:{limit}:{clean_query.lower()}"

        # 1. Check in-memory search cache
        now = time.time()
        if cache_key in self._cache:
            exp, cached_res = self._cache[cache_key]
            if now < exp:
                return cached_res

        primary = self.get_provider(prim_name)
        fallback_prov = self.get_provider(fall_name) if fall_name and fall_name != "none" else None

        # 2. Try primary provider
        try:
            logger.info(f"Searching via primary catalog provider ({primary.name}) for '{clean_query}'...")
            res = await primary.search(clean_query, limit=limit)
            if res.tracks or res.albums or res.artists:
                # Enrich artists with real HD portraits from Deezer
                try:
                    dz = self.providers["deezer"]
                    if hasattr(dz, "search_artists"):
                        dz_artists = await dz.search_artists(clean_query, limit=10)
                        if dz_artists:
                            # Build a name→artwork lookup from Deezer HD portraits
                            dz_art_by_name = {a.name.lower(): a for a in dz_artists}
                            # Enrich existing artists with HD artwork
                            for existing in res.artists:
                                match = dz_art_by_name.get(existing.name.lower())
                                if match and match.artwork_url:
                                    existing.artwork_url = match.artwork_url
                            # Add any Deezer artists not already present
                            existing_names = {a.name.lower() for a in res.artists}
                            for dz_a in dz_artists:
                                if dz_a.name.lower() not in existing_names:
                                    res.artists.append(dz_a)
                                    existing_names.add(dz_a.name.lower())
                except Exception:
                    pass
                self._cache[cache_key] = (now + self._cache_ttl_sec, res)
                return res
            logger.info(f"Primary provider ({primary.name}) returned 0 results, trying fallback...")
        except Exception as e:
            logger.warning(f"Primary provider ({primary.name}) error: {e}")

        # 3. Fallback provider if primary yielded 0 or failed
        if fallback_prov and fallback_prov != primary:
            try:
                logger.info(f"Searching via fallback catalog provider ({fallback_prov.name}) for '{clean_query}'...")
                res_fall = await fallback_prov.search(clean_query, limit=limit)
                if res_fall.tracks or res_fall.albums or res_fall.artists:
                    self._cache[cache_key] = (now + self._cache_ttl_sec, res_fall)
                    return res_fall
            except Exception as e:
                logger.warning(f"Fallback provider ({fallback_prov.name}) error: {e}")

        return CatalogSearchResult()

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        prefix = artist_id.split(":")[0].lower() if ":" in artist_id else ""
        if prefix in ("itunes", "apple"):
            return await self.providers["apple"].get_artist(artist_id)
        if prefix == "deezer":
            return await self.providers["deezer"].get_artist(artist_id)
        if prefix == "spotify":
            return await self.providers["spotify"].get_artist(artist_id)
        if prefix in ("mb", "musicbrainz"):
            return await self.providers["musicbrainz"].get_artist(artist_id)

        # Fallback search across providers
        for prov in (self.providers["apple"], self.providers["deezer"], self.providers["spotify"]):
            res = await prov.get_artist(artist_id)
            if res:
                return res
        return None

    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        prefix = album_id.split(":")[0].lower() if ":" in album_id else ""
        if prefix in ("itunes", "apple"):
            return await self.providers["apple"].get_album(album_id)
        if prefix == "deezer":
            return await self.providers["deezer"].get_album(album_id)
        if prefix == "spotify":
            return await self.providers["spotify"].get_album(album_id)
        if prefix in ("mb", "musicbrainz"):
            return await self.providers["musicbrainz"].get_album(album_id)

        for prov in (self.providers["apple"], self.providers["deezer"], self.providers["spotify"]):
            res = await prov.get_album(album_id)
            if res:
                return res
        return None

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        prefix = track_id.split(":")[0].lower() if ":" in track_id else ""
        if prefix in ("itunes", "apple"):
            return await self.providers["apple"].get_track(track_id)
        if prefix == "deezer":
            return await self.providers["deezer"].get_track(track_id)
        if prefix == "spotify":
            return await self.providers["spotify"].get_track(track_id)
        if prefix in ("mb", "musicbrainz"):
            return await self.providers["musicbrainz"].get_track(track_id)

        for prov in (self.providers["apple"], self.providers["deezer"], self.providers["spotify"]):
            res = await prov.get_track(track_id)
            if res:
                return res

        # Try SQLite database lookup for canonical UUID
        try:
            from app.db.database import AsyncSessionLocal
            from app.db.models import Track
            from sqlalchemy import select
            async with AsyncSessionLocal() as session:
                stmt = select(Track).where(Track.canonical_id == track_id)
                db_res = await session.execute(stmt)
                t = db_res.scalar_one_or_none()
                if t:
                    return CatalogTrack(
                        id=t.canonical_id,
                        title=t.title,
                        artist_name=t.artist,
                        album_title=t.album,
                        duration_ms=t.duration_ms,
                        artwork_url=t.artwork_url
                    )
        except Exception:
            pass

        return None

    async def get_artist_top_tracks(self, artist_id: str, name: str | None = None, limit: int = 10) -> list[CatalogTrack]:
        primary_name = getattr(settings, "CATALOG_PROVIDER", "apple").lower()
        providers_to_try = [self.providers["apple"], self.providers["deezer"]] if primary_name in ("apple", "itunes") else [self.providers["deezer"], self.providers["apple"]]

        for prov in providers_to_try:
            if hasattr(prov, "get_artist_top_tracks"):
                try:
                    res = await prov.get_artist_top_tracks(artist_id, name=name, limit=limit)
                    if res:
                        return res
                except Exception as e:
                    logger.warning(f"Provider {prov.name} get_artist_top_tracks error: {e}")
        return []

    async def get_artist_albums(self, artist_id: str, name: str | None = None, limit: int = 50) -> list[CatalogAlbum]:
        primary_name = getattr(settings, "CATALOG_PROVIDER", "apple").lower()
        providers_to_try = [self.providers["apple"], self.providers["deezer"]] if primary_name in ("apple", "itunes") else [self.providers["deezer"], self.providers["apple"]]

        for prov in providers_to_try:
            if hasattr(prov, "get_artist_albums"):
                try:
                    res = await prov.get_artist_albums(artist_id, name=name, limit=limit)
                    if res:
                        return res
                except Exception as e:
                    logger.warning(f"Provider {prov.name} get_artist_albums error: {e}")
        return []

    async def get_artist_related(self, artist_id: str, name: str | None = None, limit: int = 10) -> list[CatalogArtist]:
        deezer = self.providers["deezer"]
        if hasattr(deezer, "get_artist_related"):
            return await deezer.get_artist_related(artist_id, name=name, limit=limit)
        return []

    async def get_chart_tracks(self, chart_type: str = "global", limit: int = 50) -> list[CatalogTrack]:
        deezer = self.providers["deezer"]
        if hasattr(deezer, "get_chart_tracks"):
            return await deezer.get_chart_tracks(chart_type=chart_type, limit=limit)
        return []

    async def get_lyrics(self, artist: str, title: str, album: str | None = None, duration_sec: int | None = None) -> dict | None:
        cache_key = f"lyrics:{artist.lower()}:{title.lower()}"
        if cache_key in self._cache:
            exp, cached = self._cache[cache_key]
            if time.time() < exp:
                return cached
        try:
            import httpx
            params = {"artist_name": artist, "track_name": title}
            if album:
                params["album_name"] = album
            if duration_sec:
                params["duration"] = duration_sec
            async with httpx.AsyncClient(timeout=8.0) as client:
                res = await client.get("https://lrclib.net/api/get", params=params)
                if res.status_code == 200:
                    data = res.json()
                    self._cache[cache_key] = (time.time() + 86400, data)
                    return data
        except Exception as e:
            logger.warning(f"LrcLib fetch error for '{artist} - {title}': {e}")
        return None

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
        Uses deterministic GUIDs derived from provider IDs for stable primary keys.
        """
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
