import httpx
import logging
from typing import Any
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider

logger = logging.getLogger("resono.catalog.deezer")

class DeezerProvider(CatalogProvider):
    """
    Fast public Deezer catalog provider.
    100% unauthenticated, ultra-fast (~80ms), with HD 1000x1000 artwork.
    """
    def __init__(self):
        self.base_url = "https://api.deezer.com"

    @property
    def name(self) -> str:
        return "deezer"

    def _get_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=10.0,
            headers={"User-Agent": "Resono/0.1.0 (Deezer Public Catalog)"}
        )

    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        try:
            async with self._get_client() as client:
                res = await client.get("/search", params={"q": query, "limit": limit})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])

            tracks: list[CatalogTrack] = []
            albums_map: dict[str, CatalogAlbum] = {}
            artists_map: dict[str, CatalogArtist] = {}

            for it in items:
                track_id = str(it.get("id", ""))
                track_title = it.get("title", "")
                duration_sec = it.get("duration", 0)

                artist_data = it.get("artist", {})
                artist_id = str(artist_data.get("id", ""))
                artist_name = artist_data.get("name", "Unknown Artist")
                artist_pic = artist_data.get("picture_xl") or artist_data.get("picture_big") or artist_data.get("picture_medium")

                album_data = it.get("album", {})
                album_id = str(album_data.get("id", ""))
                album_title = album_data.get("title", "Unknown Album")
                cover_art = album_data.get("cover_xl") or album_data.get("cover_big") or album_data.get("cover_medium")

                if artist_id and artist_name and artist_id not in artists_map:
                    artists_map[artist_id] = CatalogArtist(
                        id=f"deezer:artist:{artist_id}",
                        name=artist_name,
                        artwork_url=artist_pic
                    )

                if album_id and album_title and album_id not in albums_map:
                    albums_map[album_id] = CatalogAlbum(
                        id=f"deezer:album:{album_id}",
                        title=album_title,
                        artist_name=artist_name,
                        artist_id=f"deezer:artist:{artist_id}",
                        artwork_url=cover_art
                    )

                if track_id and track_title:
                    tracks.append(CatalogTrack(
                        id=f"deezer:track:{track_id}",
                        title=track_title,
                        artist_name=artist_name,
                        artist_id=f"deezer:artist:{artist_id}",
                        album_title=album_title,
                        album_id=f"deezer:album:{album_id}",
                        duration_ms=duration_sec * 1000,
                        disc_number=1,
                        track_number=1,
                        isrc=it.get("isrc"),
                        artwork_url=cover_art,
                        explicit=bool(it.get("explicit_lyrics", False))
                    ))

            return CatalogSearchResult(
                artists=list(artists_map.values()),
                albums=list(albums_map.values()),
                tracks=tracks
            )
        except Exception as e:
            logger.error(f"Deezer search failed for '{query}': {e}")
            return CatalogSearchResult()

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        raw_id = artist_id.replace("deezer:artist:", "")
        try:
            async with self._get_client() as client:
                res = await client.get(f"/artist/{raw_id}")
                res.raise_for_status()
                data = res.json()
            if not data or "name" not in data:
                return None
            return CatalogArtist(
                id=f"deezer:artist:{raw_id}",
                name=data.get("name", "Unknown Artist"),
                artwork_url=data.get("picture_xl") or data.get("picture_big")
            )
        except Exception as e:
            logger.error(f"Deezer get_artist failed for '{artist_id}': {e}")
            return None

    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        raw_id = album_id.replace("deezer:album:", "")
        try:
            async with self._get_client() as client:
                res = await client.get(f"/album/{raw_id}")
                res.raise_for_status()
                data = res.json()
            if not data or "title" not in data:
                return None

            cover = data.get("cover_xl") or data.get("cover_big")
            artist_data = data.get("artist", {})
            artist_name = artist_data.get("name", "Unknown Artist")
            artist_id = str(artist_data.get("id", ""))

            album = CatalogAlbum(
                id=f"deezer:album:{raw_id}",
                title=data.get("title", "Unknown Album"),
                artist_name=artist_name,
                artist_id=f"deezer:artist:{artist_id}",
                release_date=data.get("release_date"),
                total_tracks=data.get("nb_tracks", 1),
                artwork_url=cover
            )

            tracks_data = data.get("tracks", {}).get("data", [])
            tracks: list[CatalogTrack] = []
            for t in tracks_data:
                tracks.append(CatalogTrack(
                    id=f"deezer:track:{t.get('id')}",
                    title=t.get("title", ""),
                    artist_name=artist_name,
                    artist_id=f"deezer:artist:{artist_id}",
                    album_title=album.title,
                    album_id=album.id,
                    duration_ms=t.get("duration", 0) * 1000,
                    disc_number=t.get("disk_number", 1),
                    track_number=t.get("track_position", 1),
                    isrc=t.get("isrc"),
                    artwork_url=cover,
                    explicit=bool(t.get("explicit_lyrics", False))
                ))

            return album, tracks
        except Exception as e:
            logger.error(f"Deezer get_album failed for '{album_id}': {e}")
            return None

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        raw_id = track_id.replace("deezer:track:", "")
        try:
            async with self._get_client() as client:
                res = await client.get(f"/track/{raw_id}")
                res.raise_for_status()
                data = res.json()
            if not data or "title" not in data:
                return None

            artist_data = data.get("artist", {})
            album_data = data.get("album", {})
            cover = album_data.get("cover_xl") or album_data.get("cover_big")

            return CatalogTrack(
                id=f"deezer:track:{raw_id}",
                title=data.get("title", ""),
                artist_name=artist_data.get("name", "Unknown Artist"),
                artist_id=f"deezer:artist:{artist_data.get('id', '')}",
                album_title=album_data.get("title", "Unknown Album"),
                album_id=f"deezer:album:{album_data.get('id', '')}",
                duration_ms=data.get("duration", 0) * 1000,
                disc_number=data.get("disk_number", 1),
                track_number=data.get("track_position", 1),
                isrc=data.get("isrc"),
                artwork_url=cover,
                explicit=bool(data.get("explicit_lyrics", False))
            )
        except Exception as e:
            logger.error(f"Deezer get_track failed for '{track_id}': {e}")
            return None
