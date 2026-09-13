import asyncio
import base64
import time
import httpx
import logging
from typing import Any
from app.config import settings
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider

logger = logging.getLogger("resono.catalog.spotify")

# Try importing spotapi for zero-credential Spotify catalog access
try:
    from spotapi import Song as SpotSong, PublicAlbum as SpotPublicAlbum, Artist as SpotArtist
    SPOTAPI_AVAILABLE = True
except Exception as e:
    logger.warning(f"spotapi library not available: {e}")
    SPOTAPI_AVAILABLE = False

class SpotifyProvider(CatalogProvider):
    """
    Primary catalog provider communicating with Spotify.
    - Zero-Credential Mode (Default): Uses SpotAPI to query Spotify's public GraphQL catalog directly.
    - Official API Mode (Optional): Uses Client Credentials flow if SPOTIFY_CLIENT_ID / SPOTIFY_CLIENT_SECRET are set.
    """
    def __init__(self, client_id: str | None = None, client_secret: str | None = None):
        self.client_id = client_id or getattr(settings, "SPOTIFY_CLIENT_ID", None)
        self.client_secret = client_secret or getattr(settings, "SPOTIFY_CLIENT_SECRET", None)
        self.base_url = "https://api.spotify.com/v1"
        self.auth_url = "https://accounts.spotify.com/api/token"
        
        self._access_token: str | None = None
        self._token_expires_at: float = 0.0

    @property
    def name(self) -> str:
        return "spotify"

    def is_configured(self) -> bool:
        """Returns True if official credentials are provided or if spotapi is available."""
        return bool(self.client_id and self.client_secret) or SPOTAPI_AVAILABLE

    def _extract_id(self, item_id_or_uri: str, expected_type: str = "track") -> str:
        """
        Extract clean Spotify base62 ID from:
        - URI: spotify:track:3NdDpSvN911NVWqzAC7VFs -> 3NdDpSvN911NVWqzAC7VFs
        - URL: https://open.spotify.com/track/3NdDpSvN911NVWqzAC7VFs?si=... -> 3NdDpSvN911NVWqzAC7VFs
        - Bare ID: 3NdDpSvN911NVWqzAC7VFs
        """
        clean = item_id_or_uri.strip()
        if clean.startswith("spotify:"):
            parts = clean.split(":")
            return parts[-1]
        if "spotify.com/" in clean:
            path_part = clean.split("spotify.com/")[-1].split("?")[0]
            parts = path_part.strip("/").split("/")
            return parts[-1]
        return clean

    # ==========================================
    # Official Spotify Web API Client Credentials
    # ==========================================

    async def _get_access_token(self) -> str | None:
        if not (self.client_id and self.client_secret):
            return None

        if self._access_token and time.time() < (self._token_expires_at - 60):
            return self._access_token

        auth_str = f"{self.client_id}:{self.client_secret}"
        encoded_auth = base64.b64encode(auth_str.encode()).decode()

        headers = {
            "Authorization": f"Basic {encoded_auth}",
            "Content-Type": "application/x-www-form-urlencoded"
        }
        data = {"grant_type": "client_credentials"}

        try:
            async with httpx.AsyncClient(timeout=10.0) as client:
                res = await client.post(self.auth_url, headers=headers, data=data)
                if res.status_code == 200:
                    token_data = res.json()
                    self._access_token = token_data.get("access_token")
                    expires_in = token_data.get("expires_in", 3600)
                    self._token_expires_at = time.time() + expires_in
                    logger.info("Successfully refreshed Spotify client access token")
                    return self._access_token
                else:
                    logger.warning(f"Spotify token request returned status {res.status_code}")
                    return None
        except Exception as e:
            logger.warning(f"Failed to authenticate with Spotify API: {e}")
            return None

    def _get_auth_client(self, token: str) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            headers={
                "Authorization": f"Bearer {token}",
                "User-Agent": "Resono/0.1.0 (Self-Hosted Virtual Music Catalog)"
            },
            timeout=10.0
        )

    def _best_image(self, images: list[dict[str, Any]] | None) -> str | None:
        if not images:
            return None
        return images[0].get("url")

    # ==========================================
    # Public SpotAPI Queries (Zero-Credential Mode)
    # ==========================================

    def _sync_spotapi_search(self, query: str, limit: int) -> CatalogSearchResult:
        song = SpotSong()
        raw = song.query_songs(query, limit=limit)
        items = raw.get("data", {}).get("searchV2", {}).get("tracksV2", {}).get("items", [])

        tracks: list[CatalogTrack] = []
        albums_map: dict[str, CatalogAlbum] = {}
        artists_map: dict[str, CatalogArtist] = {}

        for it in items:
            data = it.get("item", {}).get("data", {})
            uri = data.get("uri", "")
            track_id = self._extract_id(uri)
            if not track_id:
                continue

            artists_data = data.get("artists", {}).get("items", [])
            artist_names = [a.get("profile", {}).get("name") for a in artists_data if a.get("profile", {}).get("name")]
            artist_name = ", ".join(artist_names) if artist_names else "Unknown Artist"
            primary_artist_uri = artists_data[0].get("uri", "") if artists_data else ""
            primary_artist_id = self._extract_id(primary_artist_uri)

            album_data = data.get("albumOfTrack", {})
            album_uri = album_data.get("uri", "")
            album_id = self._extract_id(album_uri)
            album_title = album_data.get("name", "Unknown Album")
            cover_sources = album_data.get("coverArt", {}).get("sources", [])
            artwork_url = cover_sources[0].get("url") if cover_sources else None

            duration_ms = data.get("duration", {}).get("totalMilliseconds", 0)

            tracks.append(CatalogTrack(
                id=f"spotify:track:{track_id}",
                title=data.get("name", ""),
                artist_name=artist_name,
                artist_id=f"spotify:artist:{primary_artist_id}",
                album_title=album_title,
                album_id=f"spotify:album:{album_id}",
                duration_ms=duration_ms,
                disc_number=1,
                track_number=1,
                artwork_url=artwork_url,
                explicit=bool(data.get("contentRating", {}).get("label") == "EXPLICIT")
            ))

            if album_id and album_id not in albums_map:
                albums_map[album_id] = CatalogAlbum(
                    id=f"spotify:album:{album_id}",
                    title=album_title,
                    artist_name=artist_name,
                    artist_id=f"spotify:artist:{primary_artist_id}",
                    artwork_url=artwork_url
                )

            if primary_artist_id and primary_artist_id not in artists_map:
                artists_map[primary_artist_id] = CatalogArtist(
                    id=f"spotify:artist:{primary_artist_id}",
                    name=artist_name,
                    artwork_url=artwork_url
                )

        return CatalogSearchResult(
            artists=list(artists_map.values()),
            albums=list(albums_map.values()),
            tracks=tracks
        )

    def _sync_spotapi_track(self, clean_id: str) -> CatalogTrack | None:
        song = SpotSong()
        info = song.get_track_info(clean_id)
        data = info.get("data", {}).get("trackUnion", {})
        if not data or not data.get("name"):
            return None

        artists_data = data.get("firstArtist", {}).get("items", []) or data.get("otherArtists", {}).get("items", [])
        artist_names = [a.get("profile", {}).get("name") for a in artists_data if a.get("profile", {}).get("name")]
        artist_name = ", ".join(artist_names) if artist_names else "Unknown Artist"
        primary_artist_uri = artists_data[0].get("uri", "") if artists_data else ""
        primary_artist_id = self._extract_id(primary_artist_uri)

        album_data = data.get("albumOfTrack", {})
        album_id = self._extract_id(album_data.get("uri", ""))
        cover_sources = album_data.get("coverArt", {}).get("sources", [])
        artwork_url = cover_sources[0].get("url") if cover_sources else None
        duration_ms = data.get("duration", {}).get("totalMilliseconds", 0)

        return CatalogTrack(
            id=f"spotify:track:{clean_id}",
            title=data.get("name", ""),
            artist_name=artist_name,
            artist_id=f"spotify:artist:{primary_artist_id}",
            album_title=album_data.get("name", ""),
            album_id=f"spotify:album:{album_id}",
            duration_ms=duration_ms,
            disc_number=data.get("discNumber", 1),
            track_number=data.get("trackNumber", 1),
            artwork_url=artwork_url,
            explicit=bool(data.get("contentRating", {}).get("label") == "EXPLICIT")
        )

    def _sync_spotapi_album(self, clean_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        album_client = SpotPublicAlbum(clean_id)
        info = album_client.get_album_info()
        data = info.get("data", {}).get("albumUnion", {})
        if not data or not data.get("name"):
            return None

        artists_data = data.get("artists", {}).get("items", [])
        artist_names = [a.get("profile", {}).get("name") for a in artists_data if a.get("profile", {}).get("name")]
        artist_name = ", ".join(artist_names) if artist_names else "Unknown Artist"
        primary_artist_id = self._extract_id(artists_data[0].get("uri", "")) if artists_data else ""

        cover_sources = data.get("coverArt", {}).get("sources", [])
        artwork_url = cover_sources[0].get("url") if cover_sources else None

        album = CatalogAlbum(
            id=f"spotify:album:{clean_id}",
            title=data.get("name", ""),
            artist_name=artist_name,
            artist_id=f"spotify:artist:{primary_artist_id}",
            release_date=data.get("date", {}).get("isoString"),
            release_type=data.get("type", "album").lower(),
            total_tracks=data.get("tracksV2", {}).get("totalCount", 1),
            artwork_url=artwork_url
        )

        tracks: list[CatalogTrack] = []
        track_items = data.get("tracksV2", {}).get("items", [])
        for item in track_items:
            t = item.get("track", {})
            t_id = self._extract_id(t.get("uri", ""))
            if not t_id:
                continue
            tracks.append(CatalogTrack(
                id=f"spotify:track:{t_id}",
                title=t.get("name", ""),
                artist_name=artist_name,
                artist_id=f"spotify:artist:{primary_artist_id}",
                album_title=album.title,
                album_id=album.id,
                duration_ms=t.get("duration", {}).get("totalMilliseconds", 0),
                disc_number=t.get("discNumber", 1),
                track_number=t.get("trackNumber", 1),
                artwork_url=artwork_url,
                explicit=bool(t.get("contentRating", {}).get("label") == "EXPLICIT")
            ))

        return album, tracks

    def _sync_spotapi_artist(self, clean_id: str) -> CatalogArtist | None:
        art_client = SpotArtist()
        info = art_client.get_artist(clean_id)
        data = info.get("data", {}).get("artistUnion", {})
        profile = data.get("profile", {})
        if not profile or not profile.get("name"):
            return None

        visuals = data.get("visuals", {}).get("avatarImage", {}).get("sources", [])
        artwork_url = visuals[0].get("url") if visuals else None

        return CatalogArtist(
            id=f"spotify:artist:{clean_id}",
            name=profile.get("name", ""),
            artwork_url=artwork_url,
            genres=[],
            popularity=0
        )

    # ==========================================
    # Unified Public API Methods
    # ==========================================

    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        # 1. If official API token available, use official API
        token = await self._get_access_token()
        if token:
            try:
                async with self._get_auth_client(token) as client:
                    res = await client.get("/search", params={
                        "q": query,
                        "type": "track,album,artist",
                        "limit": limit
                    })
                    if res.status_code == 200:
                        data = res.json()
                        tracks = [
                            CatalogTrack(
                                id=f"spotify:track:{item['id']}",
                                title=item.get("name", ""),
                                artist_name=", ".join(a.get("name", "") for a in item.get("artists", [])),
                                artist_id=f"spotify:artist:{item.get('artists', [{}])[0].get('id', '')}",
                                album_title=item.get("album", {}).get("name", ""),
                                album_id=f"spotify:album:{item.get('album', {}).get('id', '')}",
                                duration_ms=item.get("duration_ms", 0),
                                disc_number=item.get("disc_number", 1),
                                track_number=item.get("track_number", 1),
                                isrc=item.get("external_ids", {}).get("isrc"),
                                artwork_url=self._best_image(item.get("album", {}).get("images")),
                                explicit=bool(item.get("explicit", False))
                            )
                            for item in data.get("tracks", {}).get("items", [])
                        ]
                        return CatalogSearchResult(tracks=tracks)
            except Exception as e:
                logger.warning(f"Official Spotify search failed: {e}")

        # 2. Use SpotAPI (Zero-Credential Mode)
        if SPOTAPI_AVAILABLE:
            try:
                logger.info(f"Searching Spotify catalog via SpotAPI for '{query}'...")
                return await asyncio.to_thread(self._sync_spotapi_search, query, limit)
            except Exception as e:
                logger.warning(f"SpotAPI search failed: {e}")

        return CatalogSearchResult()

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        clean_id = self._extract_id(track_id, "track")

        token = await self._get_access_token()
        if token:
            try:
                async with self._get_auth_client(token) as client:
                    res = await client.get(f"/tracks/{clean_id}")
                    if res.status_code == 200:
                        item = res.json()
                        return CatalogTrack(
                            id=f"spotify:track:{clean_id}",
                            title=item.get("name", ""),
                            artist_name=", ".join(a.get("name", "") for a in item.get("artists", [])),
                            artist_id=f"spotify:artist:{item.get('artists', [{}])[0].get('id', '')}",
                            album_title=item.get("album", {}).get("name", ""),
                            album_id=f"spotify:album:{item.get('album', {}).get('id', '')}",
                            duration_ms=item.get("duration_ms", 0),
                            disc_number=item.get("disc_number", 1),
                            track_number=item.get("track_number", 1),
                            isrc=item.get("external_ids", {}).get("isrc"),
                            artwork_url=self._best_image(item.get("album", {}).get("images")),
                            explicit=bool(item.get("explicit", False))
                        )
            except Exception as e:
                logger.warning(f"Official Spotify get_track error: {e}")

        if SPOTAPI_AVAILABLE:
            try:
                return await asyncio.to_thread(self._sync_spotapi_track, clean_id)
            except Exception as e:
                logger.warning(f"SpotAPI get_track failed: {e}")

        return None

    async def get_album(self, album_id: str) -> tuple[CatalogAlbum, list[CatalogTrack]] | None:
        clean_id = self._extract_id(album_id, "album")

        token = await self._get_access_token()
        if token:
            try:
                async with self._get_auth_client(token) as client:
                    res = await client.get(f"/albums/{clean_id}")
                    if res.status_code == 200:
                        item = res.json()
                        album = CatalogAlbum(
                            id=f"spotify:album:{clean_id}",
                            title=item.get("name", ""),
                            artist_name=", ".join(a.get("name", "") for a in item.get("artists", [])),
                            artist_id=f"spotify:artist:{item.get('artists', [{}])[0].get('id', '')}",
                            release_date=item.get("release_date"),
                            release_type=item.get("album_type", "album"),
                            total_tracks=item.get("total_tracks", 1),
                            artwork_url=self._best_image(item.get("images"))
                        )
                        tracks = [
                            CatalogTrack(
                                id=f"spotify:track:{t['id']}",
                                title=t.get("name", ""),
                                artist_name=album.artist_name,
                                artist_id=album.artist_id,
                                album_title=album.title,
                                album_id=album.id,
                                duration_ms=t.get("duration_ms", 0),
                                disc_number=t.get("disc_number", 1),
                                track_number=t.get("track_number", 1),
                                artwork_url=album.artwork_url,
                                explicit=bool(t.get("explicit", False))
                            )
                            for t in item.get("tracks", {}).get("items", [])
                        ]
                        return album, tracks
            except Exception as e:
                logger.warning(f"Official Spotify get_album error: {e}")

        if SPOTAPI_AVAILABLE:
            try:
                return await asyncio.to_thread(self._sync_spotapi_album, clean_id)
            except Exception as e:
                logger.warning(f"SpotAPI get_album failed: {e}")

        return None

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        clean_id = self._extract_id(artist_id, "artist")

        token = await self._get_access_token()
        if token:
            try:
                async with self._get_auth_client(token) as client:
                    res = await client.get(f"/artists/{clean_id}")
                    if res.status_code == 200:
                        item = res.json()
                        return CatalogArtist(
                            id=f"spotify:artist:{clean_id}",
                            name=item.get("name", ""),
                            artwork_url=self._best_image(item.get("images")),
                            genres=item.get("genres", []),
                            popularity=item.get("popularity", 0)
                        )
            except Exception as e:
                logger.warning(f"Official Spotify get_artist error: {e}")

        if SPOTAPI_AVAILABLE:
            try:
                return await asyncio.to_thread(self._sync_spotapi_artist, clean_id)
            except Exception as e:
                logger.warning(f"SpotAPI get_artist failed: {e}")

        return None
