from __future__ import annotations
import logging
import re
import time
from typing import Any
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
        default_name = getattr(settings, "CATALOG_PROVIDER", "deezer").lower().strip()
        return self.providers.get(default_name, self.providers["deezer"])

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

        prim_name = provider or getattr(settings, "CATALOG_PROVIDER", "deezer")
        fall_name = fallback or getattr(settings, "FALLBACK_CATALOG_PROVIDER", "apple")
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

        # 4. If still 0 results and query contains quotes or " by ", sanitize and try again
        sanitized = re.sub(r'["\']', '', clean_query)
        if " by " in sanitized.lower():
            sanitized = re.sub(r'\s+by\s+', ' ', sanitized, flags=re.IGNORECASE).strip()
        if sanitized != clean_query:
            try:
                res_clean = await primary.search(sanitized, limit=limit)
                if res_clean.tracks or res_clean.albums or res_clean.artists:
                    self._cache[cache_key] = (now + self._cache_ttl_sec, res_clean)
                    return res_clean
                if fallback_prov and fallback_prov != primary:
                    res_fall2 = await fallback_prov.search(sanitized, limit=limit)
                    if res_fall2.tracks or res_fall2.albums or res_fall2.artists:
                        self._cache[cache_key] = (now + self._cache_ttl_sec, res_fall2)
                        return res_fall2
            except Exception:
                pass

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

        # Fallback search across providers (Deezer first for real HD portraits)
        for prov in (self.providers["deezer"], self.providers["spotify"], self.providers["apple"]):
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

        # Try SQLite database lookup for canonical UUID or ID
        try:
            from app.db.database import AsyncSessionLocal
            from app.db.models import Album, Track
            from sqlalchemy import select
            from sqlalchemy.orm import selectinload
            async with AsyncSessionLocal() as session:
                stmt = select(Album).options(selectinload(Album.artist), selectinload(Album.tracks)).where((Album.id == album_id) | (Album.spotify_id == album_id))
                db_res = await session.execute(stmt)
                al = db_res.scalar_one_or_none()
                if al:
                    artist_name = al.artist.name if al.artist else ""
                    cat_album = CatalogAlbum(
                        id=al.id,
                        title=al.title,
                        artist_name=artist_name,
                        artist_id=al.artist_id,
                        release_date=al.release_date or "",
                        artwork_url=al.artwork_url or ""
                    )
                    cat_tracks = [
                        CatalogTrack(
                            id=t.id,
                            title=t.title,
                            artist_name=artist_name,
                            album_title=al.title,
                            album_id=al.id,
                            duration_ms=t.duration_ms,
                            track_number=t.track_number,
                            disc_number=t.disc_number,
                            artwork_url=al.artwork_url or ""
                        )
                        for t in (al.tracks or [])
                    ]
                    return cat_album, cat_tracks
        except Exception:
            pass

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

        # Try SQLite database lookup for canonical UUID or ID
        try:
            from app.db.database import AsyncSessionLocal
            from app.db.models import Track
            from sqlalchemy import select
            from sqlalchemy.orm import selectinload
            async with AsyncSessionLocal() as session:
                stmt = select(Track).options(selectinload(Track.artist), selectinload(Track.album)).where((Track.id == track_id) | (Track.spotify_id == track_id))
                db_res = await session.execute(stmt)
                t = db_res.scalar_one_or_none()
                if t:
                    artist_name = t.artist.name if t.artist else ""
                    album_title = t.album.title if t.album else ""
                    artwork_url = t.album.artwork_url if t.album else ""
                    return CatalogTrack(
                        id=t.id,
                        title=t.title,
                        artist_name=artist_name,
                        album_title=album_title,
                        duration_ms=t.duration_ms,
                        artwork_url=artwork_url
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

    async def get_chart_albums(self, country: str = "0", limit: int = 30) -> list[CatalogAlbum]:
        deezer = self.providers["deezer"]
        if hasattr(deezer, "get_chart_albums"):
            return await deezer.get_chart_albums(country=country, limit=limit)
        return []

    async def get_chart_playlists(self, country: str = "0", limit: int = 30) -> list[dict[str, Any]]:
        deezer = self.providers["deezer"]
        if hasattr(deezer, "get_chart_playlists"):
            return await deezer.get_chart_playlists(country=country, limit=limit)
        return []

    async def get_recommendations(self, seed_artists: list[str], limit: int = 30) -> list[CatalogAlbum]:
        """
        Generate unauthenticated, zero-contamination album recommendations based on seed artist names/IDs.
        If no seed artists or if fetching yields empty, falls back to live chart albums.
        """
        deezer = self.providers["deezer"]
        recommended_albums: list[CatalogAlbum] = []
        seen_album_titles: set[str] = set()

        if seed_artists:
            for artist_str in seed_artists[:4]:
                try:
                    if hasattr(deezer, "get_artist_related"):
                        related = await deezer.get_artist_related(artist_str, limit=5)
                        for rel in related[:3]:
                            if hasattr(deezer, "get_artist_albums"):
                                albs = await deezer.get_artist_albums(rel.id, limit=3)
                                for a in albs:
                                    norm = a.title.lower().strip()
                                    if norm not in seen_album_titles:
                                        seen_album_titles.add(norm)
                                        recommended_albums.append(a)
                                        if len(recommended_albums) >= limit:
                                            return recommended_albums
                except Exception as e:
                    logger.debug(f"Recommendation fetch for {artist_str} error: {e}")

        # If we need more albums to reach limit, fill with live chart albums
        if len(recommended_albums) < limit:
            chart_albs = await self.get_chart_albums(limit=limit)
            for ca in chart_albs:
                norm = ca.title.lower().strip()
                if norm not in seen_album_titles:
                    seen_album_titles.add(norm)
                    recommended_albums.append(ca)
                    if len(recommended_albums) >= limit:
                        break

        return recommended_albums


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
