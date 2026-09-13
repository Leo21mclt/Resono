import httpx
import logging
from typing import Any
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider

logger = logging.getLogger("resono.catalog.itunes")

class ITunesProvider(CatalogProvider):
    def __init__(self):
        self.base_url = "https://itunes.apple.com"

    def _get_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=10.0,
            headers={"User-Agent": "Resono/0.1.0 (Jellyfin Virtual Music)"}
        )

    @property
    def name(self) -> str:
        return "itunes"

    def _upgrade_art(self, url: str | None) -> str | None:
        if not url:
            return None
        # iTunes artwork URLs have dimensions like 100x100bb.jpg; upgrade to 1000x1000
        return url.replace("100x100bb", "1000x1000bb").replace("60x60bb", "1000x1000bb")

    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        try:
            async with self._get_client() as client:
                res = await client.get("/search", params={
                    "term": query,
                    "media": "music",
                    "entity": "song",
                    "limit": limit
                })
                res.raise_for_status()
                data = res.json()
            results = data.get("results", [])

            tracks: list[CatalogTrack] = []
            albums_map: dict[str, CatalogAlbum] = {}
            artists_map: dict[str, CatalogArtist] = {}

            for item in results:
                artist_id = str(item.get("artistId", ""))
                artist_name = item.get("artistName", "")
                album_id = str(item.get("collectionId", ""))
                album_name = item.get("collectionName", "")
                track_id = str(item.get("trackId", ""))
                track_name = item.get("trackName", "")
                art = self._upgrade_art(item.get("artworkUrl100"))

                if artist_id and artist_name and artist_id not in artists_map:
                    artists_map[artist_id] = CatalogArtist(
                        id=f"itunes:artist:{artist_id}",
                        name=artist_name,
                        artwork_url=art,
                        genres=[item.get("primaryGenreName")] if item.get("primaryGenreName") else []
                    )

                if album_id and album_name and album_id not in albums_map:
                    albums_map[album_id] = CatalogAlbum(
                        id=f"itunes:album:{album_id}",
                        title=album_name,
                        artist_name=artist_name,
                        artist_id=f"itunes:artist:{artist_id}",
                        release_date=item.get("releaseDate", "")[:10] if item.get("releaseDate") else None,
                        total_tracks=item.get("trackCount", 1),
                        artwork_url=art
                    )

                if track_id and track_name:
                    tracks.append(CatalogTrack(
                        id=f"itunes:track:{track_id}",
                        title=track_name,
                        artist_name=artist_name,
                        artist_id=f"itunes:artist:{artist_id}",
                        album_title=album_name,
                        album_id=f"itunes:album:{album_id}",
                        duration_ms=item.get("trackTimeMillis", 0),
                        disc_number=item.get("discNumber", 1),
                        track_number=item.get("trackNumber", 1),
                        artwork_url=art,
                        explicit=(item.get("trackExplicitness") == "explicit")
                    ))

            return CatalogSearchResult(
                artists=list(artists_map.values()),
                albums=list(albums_map.values()),
                tracks=tracks
            )
        except Exception as e:
            logger.error(f"iTunes search failed for '{query}': {e}")
            return CatalogSearchResult()

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        raw_id = artist_id.replace("itunes:artist:", "")
        try:
            async with self._get_client() as client:
                res = await client.get("/lookup", params={"id": raw_id, "entity": "album"})
                res.raise_for_status()
                data = res.json()
            results = data.get("results", [])
            if not results:
                return None
            artist_data = results[0]
            return CatalogArtist(
                id=f"itunes:artist:{raw_id}",
                name=artist_data.get("artistName", "Unknown"),
                genres=[artist_data.get("primaryGenreName")] if artist_data.get("primaryGenreName") else []
            )
        except Exception as e:
            logger.error(f"iTunes get_artist failed for '{artist_id}': {e}")
            return None

    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        raw_id = album_id.replace("itunes:album:", "")
        try:
            async with self._get_client() as client:
                res = await client.get("/lookup", params={"id": raw_id, "entity": "song"})
                res.raise_for_status()
                data = res.json()
            results = data.get("results", [])
            if not results:
                return None

            album_item = results[0]
            album_art = self._upgrade_art(album_item.get("artworkUrl100"))
            album = CatalogAlbum(
                id=f"itunes:album:{raw_id}",
                title=album_item.get("collectionName", "Unknown"),
                artist_name=album_item.get("artistName", "Unknown"),
                artist_id=f"itunes:artist:{album_item.get('artistId', '')}",
                release_date=album_item.get("releaseDate", "")[:10] if album_item.get("releaseDate") else None,
                total_tracks=album_item.get("trackCount", 1),
                artwork_url=album_art
            )

            tracks: list[CatalogTrack] = []
            for item in results[1:]:
                if item.get("wrapperType") == "track":
                    tracks.append(CatalogTrack(
                        id=f"itunes:track:{item.get('trackId')}",
                        title=item.get("trackName", ""),
                        artist_name=item.get("artistName", album.artist_name),
                        artist_id=album.artist_id,
                        album_title=album.title,
                        album_id=album.id,
                        duration_ms=item.get("trackTimeMillis", 0),
                        disc_number=item.get("discNumber", 1),
                        track_number=item.get("trackNumber", 1),
                        artwork_url=album_art,
                        explicit=(item.get("trackExplicitness") == "explicit")
                    ))

            return album, tracks
        except Exception as e:
            logger.error(f"iTunes get_album failed for '{album_id}': {e}")
            return None

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        raw_id = track_id.replace("itunes:track:", "")
        try:
            async with self._get_client() as client:
                res = await client.get("/lookup", params={"id": raw_id})
                res.raise_for_status()
                data = res.json()
            results = data.get("results", [])
            if not results:
                return None
            item = results[0]
            art = self._upgrade_art(item.get("artworkUrl100"))
            return CatalogTrack(
                id=f"itunes:track:{raw_id}",
                title=item.get("trackName", ""),
                artist_name=item.get("artistName", ""),
                artist_id=f"itunes:artist:{item.get('artistId', '')}",
                album_title=item.get("collectionName", ""),
                album_id=f"itunes:album:{item.get('collectionId', '')}",
                duration_ms=item.get("trackTimeMillis", 0),
                disc_number=item.get("discNumber", 1),
                track_number=item.get("trackNumber", 1),
                artwork_url=art,
                explicit=(item.get("trackExplicitness") == "explicit")
            )
        except Exception as e:
            logger.error(f"iTunes get_track failed for '{track_id}': {e}")
            return None
