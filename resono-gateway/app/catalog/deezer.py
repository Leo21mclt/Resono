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

    async def search_artists(self, query: str, limit: int = 10) -> list[CatalogArtist]:
        try:
            async with self._get_client() as client:
                res = await client.get("/search/artist", params={"q": query, "limit": 25})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])
            
            spam_terms = {"piano", "tribute", "cover", "karaoke", "lullaby", "instrumental", "relaxing", "sleep", "orchestra", "love"}
            clean_q = query.strip().lower()
            valid_artists: list[dict[str, Any]] = []

            for a in items:
                name = a.get("name", "").strip()
                name_lower = name.lower()
                nb_fan = a.get("nb_fan", 0)
                pic = a.get("picture_xl") or a.get("picture_big") or a.get("picture_medium")

                # Filter out broken or missing avatar MD5 hashes
                if not pic or "/artist//1000x1000" in pic or "/artist//500x500" in pic or "/artist//250x250" in pic:
                    continue

                # Filter out obvious tribute/piano/lullaby accounts
                words = set(name_lower.split())
                if any(term in words for term in spam_terms):
                    continue

                is_exact = (name_lower == clean_q)
                # If not exact match, require at least 5000 fans to prevent spam/typos
                if not is_exact and nb_fan < 5000:
                    continue

                valid_artists.append({
                    "artist": CatalogArtist(
                        id=f"deezer:artist:{a.get('id')}",
                        name=name,
                        artwork_url=pic
                    ),
                    "nb_fan": nb_fan,
                    "is_exact": is_exact,
                    "raw_id": str(a.get("id"))
                })

            # Sort exact match first, then by nb_fan descending
            valid_artists.sort(key=lambda x: (not x["is_exact"], -x["nb_fan"]))
            results = [x["artist"] for x in valid_artists[:limit]]

            # If fewer than 4 artists and the top artist is a major verified star (>= 50,000 fans),
            # append related artists matching Deezer/Spotify UI behavior
            if len(results) < 4 and valid_artists and valid_artists[0]["nb_fan"] >= 50000:
                top_id = valid_artists[0]["raw_id"]
                try:
                    related = await self.get_artist_related(top_id, limit=limit - len(results) + 2)
                    existing_names = {r.name.lower() for r in results}
                    for rel in related:
                        if rel.name.lower() not in existing_names and rel.artwork_url and "/artist//" not in rel.artwork_url:
                            results.append(rel)
                            existing_names.add(rel.name.lower())
                            if len(results) >= limit:
                                break
                except Exception as e:
                    logger.debug(f"Could not append related artists for {top_id}: {e}")

            return results
        except Exception as e:
            logger.error(f"Deezer search_artists failed for '{query}': {e}")
            return []

    async def _resolve_artist_id(self, artist_id: str, name: str | None = None) -> str | None:
        if artist_id.startswith("deezer:artist:"):
            return artist_id.replace("deezer:artist:", "").strip()
        if name and name.strip():
            clean_name = name.strip()
            try:
                async with self._get_client() as client:
                    res = await client.get("/search/artist", params={"q": clean_name, "limit": 1})
                    if res.is_success:
                        items = res.json().get("data", [])
                        if items:
                            return str(items[0]["id"])
            except Exception:
                pass
        clean = artist_id.replace("itunes:artist:", "").replace("spotify:artist:", "").strip()
        if clean.isdigit():
            return clean
        return None

    async def get_artist_top_tracks(self, artist_id: str, name: str | None = None, limit: int = 10) -> list[CatalogTrack]:
        resolved_id = await self._resolve_artist_id(artist_id, name)
        if not resolved_id:
            return []
        try:
            async with self._get_client() as client:
                res = await client.get(f"/artist/{resolved_id}/top", params={"limit": limit})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])
            tracks: list[CatalogTrack] = []
            for it in items:
                al = it.get("album", {})
                cover = al.get("cover_xl") or al.get("cover_big")
                art_name = it.get("artist", {}).get("name") or name or "Unknown Artist"
                alb_id = f"deezer:album:{al.get('id')}" if al.get("id") else f"deezer:album:top_{resolved_id}"
                tracks.append(CatalogTrack(
                    id=f"deezer:track:{it.get('id')}",
                    title=it.get("title", "Unknown Track"),
                    artist_name=art_name,
                    artist_id=f"deezer:artist:{resolved_id}",
                    album_title=al.get("title") or "Top Tracks",
                    album_id=alb_id,
                    duration_ms=it.get("duration", 0) * 1000,
                    track_number=it.get("track_position", 1),
                    artwork_url=cover
                ))
            return tracks
        except Exception as e:
            logger.error(f"Deezer get_artist_top_tracks failed for '{artist_id}': {e}")
            return []

    async def get_artist_albums(self, artist_id: str, name: str | None = None, limit: int = 50) -> list[CatalogAlbum]:
        resolved_id = await self._resolve_artist_id(artist_id, name)
        if not resolved_id:
            return []
        try:
            async with self._get_client() as client:
                res = await client.get(f"/artist/{resolved_id}/albums", params={"limit": limit})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])
            albums: list[CatalogAlbum] = []
            for it in items:
                cover = it.get("cover_xl") or it.get("cover_big")
                art_name = it.get("artist", {}).get("name") or name or "Unknown Artist"
                albums.append(CatalogAlbum(
                    id=f"deezer:album:{it.get('id')}",
                    title=it.get("title", "Unknown Album"),
                    artist_name=art_name,
                    artist_id=f"deezer:artist:{resolved_id}",
                    release_date=it.get("release_date"),
                    total_tracks=it.get("nb_tracks", 1),
                    artwork_url=cover
                ))
            return albums
        except Exception as e:
            logger.error(f"Deezer get_artist_albums failed for '{artist_id}': {e}")
            return []

    async def get_artist_related(self, artist_id: str, name: str | None = None, limit: int = 10) -> list[CatalogArtist]:
        resolved_id = await self._resolve_artist_id(artist_id, name)
        if not resolved_id:
            return []
        try:
            async with self._get_client() as client:
                res = await client.get(f"/artist/{resolved_id}/related", params={"limit": limit})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])
            artists: list[CatalogArtist] = []
            for it in items:
                pic = it.get("picture_xl") or it.get("picture_big")
                artists.append(CatalogArtist(
                    id=f"deezer:artist:{it.get('id')}",
                    name=it.get("name", "Unknown Artist"),
                    artwork_url=pic
                ))
            return artists
        except Exception as e:
            logger.error(f"Deezer get_artist_related failed for '{artist_id}': {e}")
            return []

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

    async def get_chart_tracks(self, chart_type: str = "global", limit: int = 50) -> list[CatalogTrack]:
        try:
            async with self._get_client() as client:
                if chart_type == "global":
                    url = f"/chart/0/tracks?limit={limit}"
                elif chart_type.isdigit():
                    url = f"/playlist/{chart_type}/tracks?limit={limit}"
                else:
                    # Search playlist for country (e.g. Top Peru, Top USA)
                    s = await client.get("/search/playlist", params={"q": f"Top {chart_type}", "limit": 1})
                    s_data = s.json().get("data", [])
                    if s_data:
                        url = f"/playlist/{s_data[0]['id']}/tracks?limit={limit}"
                    else:
                        url = f"/chart/0/tracks?limit={limit}"

                res = await client.get(url)
                res.raise_for_status()
                data = res.json()

            items = data.get("data", [])
            tracks: list[CatalogTrack] = []
            for it in items:
                art_data = it.get("artist", {})
                alb_data = it.get("album", {})
                cover = alb_data.get("cover_xl") or alb_data.get("cover_big")
                tracks.append(CatalogTrack(
                    id=f"deezer:track:{it.get('id')}",
                    title=it.get("title", ""),
                    artist_name=art_data.get("name", "Unknown Artist"),
                    artist_id=f"deezer:artist:{art_data.get('id', '')}",
                    album_title=alb_data.get("title", "Top Chart"),
                    album_id=f"deezer:album:{alb_data.get('id', '')}",
                    duration_ms=it.get("duration", 0) * 1000,
                    disc_number=1,
                    track_number=len(tracks) + 1,
                    isrc=it.get("isrc"),
                    artwork_url=cover,
                    explicit=bool(it.get("explicit_lyrics", False))
                ))
            return tracks
        except Exception as e:
            logger.error(f"Deezer get_chart_tracks failed for '{chart_type}': {e}")
            return []
