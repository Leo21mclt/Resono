import httpx
import logging
from typing import Any
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider

logger = logging.getLogger("resono.catalog.musicbrainz")

class MusicBrainzProvider(CatalogProvider):
    """
    Community-maintained open MusicBrainz catalog.
    100% free and open, with Cover Art Archive integration.
    """
    def __init__(self):
        self.base_url = "https://musicbrainz.org/ws/2"

    @property
    def name(self) -> str:
        return "musicbrainz"

    def _get_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=10.0,
            headers={"User-Agent": "Resono/0.1.0 (contact@resono.local)"}
        )

    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        try:
            async with self._get_client() as client:
                res = await client.get("/recording", params={"query": query, "limit": limit, "fmt": "json"})
                res.raise_for_status()
                data = res.json()
            recordings = data.get("recordings", [])

            tracks: list[CatalogTrack] = []
            albums_map: dict[str, CatalogAlbum] = {}
            artists_map: dict[str, CatalogArtist] = {}

            for rec in recordings:
                track_id = rec.get("id", "")
                track_title = rec.get("title", "")
                duration_ms = rec.get("length") or 0

                artist_credit = rec.get("artist-credit", [])
                artist_name = artist_credit[0].get("name", "Unknown Artist") if artist_credit else "Unknown Artist"
                artist_id = artist_credit[0].get("artist", {}).get("id", "") if artist_credit else ""

                releases = rec.get("releases", [])
                album_id = releases[0].get("id", "") if releases else ""
                album_title = releases[0].get("title", "Unknown Album") if releases else "Unknown Album"
                release_date = releases[0].get("date") if releases else None

                art_url = f"https://coverartarchive.org/release/{album_id}/front-500" if album_id else None

                if artist_id and artist_name and artist_id not in artists_map:
                    artists_map[artist_id] = CatalogArtist(
                        id=f"mb:artist:{artist_id}",
                        name=artist_name,
                        artwork_url=art_url
                    )

                if album_id and album_title and album_id not in albums_map:
                    albums_map[album_id] = CatalogAlbum(
                        id=f"mb:album:{album_id}",
                        title=album_title,
                        artist_name=artist_name,
                        artist_id=f"mb:artist:{artist_id}",
                        release_date=release_date[:10] if release_date else None,
                        artwork_url=art_url
                    )

                isrcs = rec.get("isrcs", [])
                isrc = isrcs[0] if isrcs else None

                if track_id and track_title:
                    tracks.append(CatalogTrack(
                        id=f"mb:track:{track_id}",
                        title=track_title,
                        artist_name=artist_name,
                        artist_id=f"mb:artist:{artist_id}",
                        album_title=album_title,
                        album_id=f"mb:album:{album_id}",
                        duration_ms=duration_ms,
                        disc_number=1,
                        track_number=1,
                        isrc=isrc,
                        artwork_url=art_url
                    ))

            return CatalogSearchResult(
                artists=list(artists_map.values()),
                albums=list(albums_map.values()),
                tracks=tracks
            )
        except Exception as e:
            logger.error(f"MusicBrainz search failed for '{query}': {e}")
            return CatalogSearchResult()

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        raw_id = artist_id.replace("mb:artist:", "")
        try:
            async with self._get_client() as client:
                res = await client.get(f"/artist/{raw_id}", params={"fmt": "json"})
                res.raise_for_status()
                data = res.json()
            if not data or "name" not in data:
                return None
            return CatalogArtist(
                id=f"mb:artist:{raw_id}",
                name=data.get("name", "Unknown Artist")
            )
        except Exception as e:
            logger.error(f"MusicBrainz get_artist failed for '{artist_id}': {e}")
            return None

    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        raw_id = album_id.replace("mb:album:", "")
        try:
            async with self._get_client() as client:
                res = await client.get(f"/release/{raw_id}", params={"inc": "recordings+artists", "fmt": "json"})
                res.raise_for_status()
                data = res.json()
            if not data or "title" not in data:
                return None

            artist_credit = data.get("artist-credit", [])
            artist_name = artist_credit[0].get("name", "Unknown Artist") if artist_credit else "Unknown Artist"
            artist_id = artist_credit[0].get("artist", {}).get("id", "") if artist_credit else ""
            art_url = f"https://coverartarchive.org/release/{raw_id}/front-500"

            album = CatalogAlbum(
                id=f"mb:album:{raw_id}",
                title=data.get("title", "Unknown Album"),
                artist_name=artist_name,
                artist_id=f"mb:artist:{artist_id}",
                release_date=data.get("date"),
                artwork_url=art_url
            )

            tracks: list[CatalogTrack] = []
            for media in data.get("media", []):
                disc_num = media.get("position", 1)
                for tr in media.get("tracks", []):
                    rec = tr.get("recording", {})
                    tracks.append(CatalogTrack(
                        id=f"mb:track:{rec.get('id', tr.get('id'))}",
                        title=tr.get("title", ""),
                        artist_name=artist_name,
                        artist_id=f"mb:artist:{artist_id}",
                        album_title=album.title,
                        album_id=album.id,
                        duration_ms=tr.get("length") or 0,
                        disc_number=disc_num,
                        track_number=tr.get("position", 1),
                        artwork_url=art_url
                    ))

            return album, tracks
        except Exception as e:
            logger.error(f"MusicBrainz get_album failed for '{album_id}': {e}")
            return None

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        raw_id = track_id.replace("mb:track:", "")
        try:
            async with self._get_client() as client:
                res = await client.get(f"/recording/{raw_id}", params={"inc": "releases+artists", "fmt": "json"})
                res.raise_for_status()
                data = res.json()
            if not data or "title" not in data:
                return None

            artist_credit = data.get("artist-credit", [])
            artist_name = artist_credit[0].get("name", "Unknown Artist") if artist_credit else "Unknown Artist"
            artist_id = artist_credit[0].get("artist", {}).get("id", "") if artist_credit else ""

            releases = data.get("releases", [])
            album_id = releases[0].get("id", "") if releases else ""
            album_title = releases[0].get("title", "Unknown Album") if releases else "Unknown Album"
            art_url = f"https://coverartarchive.org/release/{album_id}/front-500" if album_id else None

            return CatalogTrack(
                id=f"mb:track:{raw_id}",
                title=data.get("title", ""),
                artist_name=artist_name,
                artist_id=f"mb:artist:{artist_id}",
                album_title=album_title,
                album_id=f"mb:album:{album_id}",
                duration_ms=data.get("length") or 0,
                disc_number=1,
                track_number=1,
                artwork_url=art_url
            )
        except Exception as e:
            logger.error(f"MusicBrainz get_track failed for '{track_id}': {e}")
            return None
