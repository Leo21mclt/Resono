from __future__ import annotations
import asyncio
import httpx
import logging
import time
from typing import Any
from app.catalog.models import CatalogSearchResult, CatalogArtist, CatalogAlbum, CatalogTrack
from app.catalog.provider import CatalogProvider
from app.catalog.deezer_query import SEARCH_FULL_QUERY

logger = logging.getLogger("resono.catalog.deezer")

class DeezerProvider(CatalogProvider):
    """
    Fast public Deezer catalog provider.
    Powered by Deezer Web SearchFull GraphQL engine for 1:1 parity with Deezer Web.
    """
    def __init__(self):
        self.base_url = "https://api.deezer.com"
        self._jwt_token: str | None = None
        self._jwt_expires_at: float = 0.0
        self._jwt_lock = asyncio.Lock()

    @property
    def name(self) -> str:
        return "deezer"

    def _get_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            timeout=10.0,
            headers={"User-Agent": "Resono/0.1.0 (Deezer Public Catalog)"}
        )

    async def _get_anonymous_jwt(self) -> str | None:
        now = time.time()
        if self._jwt_token and now < self._jwt_expires_at:
            return self._jwt_token

        async with self._jwt_lock:
            if self._jwt_token and now < self._jwt_expires_at:
                return self._jwt_token

            try:
                async with httpx.AsyncClient(timeout=8.0) as client:
                    r = await client.post(
                        "https://auth.deezer.com/login/anonymous?jo=p&rto=c",
                        json={},
                        headers={
                            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
                            "Origin": "https://www.deezer.com",
                            "Referer": "https://www.deezer.com/"
                        }
                    )
                    if r.status_code == 200:
                        token = r.json().get("jwt")
                        if token:
                            self._jwt_token = token
                            self._jwt_expires_at = now + 3000
                            return token
            except Exception as e:
                logger.warning(f"Failed to fetch anonymous Deezer JWT: {e}")
            return None

    async def _search_graphql(self, query: str, limit: int = 20) -> CatalogSearchResult | None:
        token = await self._get_anonymous_jwt()
        if not token:
            return None

        payload = {
            "operationName": "SearchFull",
            "variables": {
                "query": query,
                "firstGrid": limit,
                "firstList": limit,
                "includeRelatedContent": False,
                "channelPlaylistFirst": 5
            },
            "query": SEARCH_FULL_QUERY
        }
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
            "Origin": "https://www.deezer.com",
            "Referer": "https://www.deezer.com/"
        }

        try:
            async with httpx.AsyncClient(timeout=8.0) as client:
                r = await client.post("https://pipe.deezer.com/api", json=payload, headers=headers)
                if r.status_code != 200:
                    return None
                data = r.json()
        except Exception as e:
            logger.debug(f"Deezer GraphQL request error: {e}")
            return None

        instant = data.get("data", {}).get("instantSearch", {})
        if not instant:
            return None

        best = instant.get("bestResult") or {}
        results = instant.get("results") or {}

        albums: list[CatalogAlbum] = []
        artists: list[CatalogArtist] = []
        tracks: list[CatalogTrack] = []
        seen_albums: set[str] = set()
        seen_artists: set[str] = set()
        seen_tracks: set[str] = set()

        def clean_cover(url: str | None) -> str | None:
            if not url:
                return None
            if "/500x500" in url:
                return url.replace("/500x500", "/1000x1000")
            return url

        best_type = best.get("__typename")
        best_result_type = None

        if best_type == "InstantSearchAlbumBestResult":
            best_result_type = "album"
            node = best.get("album") or {}
            al_id = str(node.get("id", ""))
            if al_id:
                cover = clean_cover(((node.get("cover") or {}).get("large") or [None])[0])
                contribs = (node.get("contributors") or {}).get("edges") or []
                art_node = contribs[0].get("node") or {} if contribs else {}
                art_id = str(art_node.get("id", ""))
                art_name = art_node.get("name") or "Unknown Artist"
                full_id = f"deezer:album:{al_id}"
                albums.append(CatalogAlbum(
                    id=full_id,
                    title=node.get("displayTitle") or "",
                    artist_name=art_name,
                    artist_id=f"deezer:artist:{art_id}" if art_id else "",
                    artwork_url=cover
                ))
                seen_albums.add(full_id)
        elif best_type == "InstantSearchArtistBestResult":
            best_result_type = "artist"
            node = best.get("artist") or {}
            ar_id = str(node.get("id", ""))
            if ar_id:
                pic = clean_cover(((node.get("picture") or {}).get("large") or [None])[0])
                full_id = f"deezer:artist:{ar_id}"
                artists.append(CatalogArtist(
                    id=full_id,
                    name=node.get("name") or "",
                    artwork_url=pic
                ))
                seen_artists.add(full_id)
        elif best_type == "InstantSearchTrackBestResult":
            best_result_type = "track"
            node = best.get("track") or {}
            tr_id = str(node.get("id", ""))
            if tr_id:
                alb_node = node.get("album") or {}
                alb_id = str(alb_node.get("id", ""))
                alb_title = alb_node.get("displayTitle") or "Unknown Album"
                cover = clean_cover(((alb_node.get("cover") or {}).get("large") or [None])[0])
                contribs = (node.get("contributors") or {}).get("edges") or []
                art_node = contribs[0].get("node") or {} if contribs else {}
                art_id = str(art_node.get("id", ""))
                art_name = art_node.get("name") or "Unknown Artist"
                full_id = f"deezer:track:{tr_id}"
                tracks.append(CatalogTrack(
                    id=full_id,
                    title=node.get("title") or "",
                    artist_name=art_name,
                    artist_id=f"deezer:artist:{art_id}" if art_id else "",
                    album_title=alb_title,
                    album_id=f"deezer:album:{alb_id}" if alb_id else "",
                    duration_ms=(node.get("duration") or 0) * 1000,
                    artwork_url=cover,
                    explicit=bool(node.get("isExplicit", False))
                ))
                seen_tracks.add(full_id)

        # Process albums
        for edge in (results.get("albums") or {}).get("edges") or []:
            node = edge.get("node") or {}
            al_id = str(node.get("id", ""))
            full_id = f"deezer:album:{al_id}"
            if al_id and full_id not in seen_albums:
                seen_albums.add(full_id)
                cover = clean_cover(((node.get("cover") or {}).get("large") or [None])[0])
                contribs = (node.get("contributors") or {}).get("edges") or []
                art_node = contribs[0].get("node") or {} if contribs else {}
                art_id = str(art_node.get("id", ""))
                art_name = art_node.get("name") or "Unknown Artist"
                albums.append(CatalogAlbum(
                    id=full_id,
                    title=node.get("displayTitle") or "",
                    artist_name=art_name,
                    artist_id=f"deezer:artist:{art_id}" if art_id else "",
                    artwork_url=cover
                ))

        # Process artists
        for edge in (results.get("artists") or {}).get("edges") or []:
            node = edge.get("node") or {}
            ar_id = str(node.get("id", ""))
            full_id = f"deezer:artist:{ar_id}"
            if ar_id and full_id not in seen_artists:
                seen_artists.add(full_id)
                pic = clean_cover(((node.get("picture") or {}).get("large") or [None])[0])
                artists.append(CatalogArtist(
                    id=full_id,
                    name=node.get("name") or "",
                    artwork_url=pic
                ))

        # Process tracks
        for edge in (results.get("tracks") or {}).get("edges") or []:
            node = edge.get("node") or {}
            tr_id = str(node.get("id", ""))
            full_id = f"deezer:track:{tr_id}"
            if tr_id and full_id not in seen_tracks:
                seen_tracks.add(full_id)
                alb_node = node.get("album") or {}
                alb_id = str(alb_node.get("id", ""))
                alb_title = alb_node.get("displayTitle") or "Unknown Album"
                cover = clean_cover(((alb_node.get("cover") or {}).get("large") or [None])[0])
                contribs = (node.get("contributors") or {}).get("edges") or []
                art_node = contribs[0].get("node") or {} if contribs else {}
                art_id = str(art_node.get("id", ""))
                art_name = art_node.get("name") or "Unknown Artist"
                tracks.append(CatalogTrack(
                    id=full_id,
                    title=node.get("title") or "",
                    artist_name=art_name,
                    artist_id=f"deezer:artist:{art_id}" if art_id else "",
                    album_title=alb_title,
                    album_id=f"deezer:album:{alb_id}" if alb_id else "",
                    duration_ms=(node.get("duration") or 0) * 1000,
                    artwork_url=cover,
                    explicit=bool(node.get("isExplicit", False))
                ))

        if not artists and not albums and not tracks:
            return None

        return CatalogSearchResult(
            artists=artists[:limit],
            albums=albums[:limit],
            tracks=tracks[:limit],
            best_result_type=best_result_type
        )

    async def search(self, query: str, limit: int = 20) -> CatalogSearchResult:
        clean_q = query.strip()
        if not clean_q:
            return CatalogSearchResult()

        # 1. Primary: Deezer Web SearchFull GraphQL (exact 1:1 match with Deezer Web, bestResult intent, priority ordering)
        try:
            gql_res = await self._search_graphql(clean_q, limit=limit)
            if gql_res and (gql_res.artists or gql_res.albums or gql_res.tracks):
                return gql_res
        except Exception as e:
            logger.warning(f"Deezer GraphQL search failed for '{clean_q}', falling back to REST: {e}")

        try:
            # 1. Run smart artist search (filters spam/piano/tributes and enriches with related artists)
            artist_task = self.search_artists(clean_q, limit=limit)

            async with self._get_client() as client:
                async def fetch_endpoint(path: str, p: dict):
                    try:
                        r = await client.get(path, params=p)
                        if r.status_code == 200:
                            return r.json().get("data", [])
                    except Exception as e:
                        logger.debug(f"Deezer search subquery {path} error: {e}")
                    return []

                # Query tracks and albums concurrently alongside smart artist search
                track_task = fetch_endpoint("/search", {"q": clean_q, "limit": limit, "order": "RANKING"})
                album_task = fetch_endpoint("/search/album", {"q": clean_q, "limit": limit, "order": "RANKING"})

                artists_list, track_items, album_items = await asyncio.gather(
                    artist_task, track_task, album_task
                )

            tracks: list[CatalogTrack] = []
            albums_map: dict[str, CatalogAlbum] = {}
            artists_map: dict[str, CatalogArtist] = {}

            # 1. Process smart Artist search results first (preserving curated order & related artists)
            for art in artists_list:
                aid = art.id.replace("deezer:artist:", "")
                if aid and aid not in artists_map:
                    artists_map[aid] = art

            # 2. Process dedicated Album search results (ensuring matching albums appear prominently)
            album_spam_terms = {"tribute", "karaoke", "piano cover", "instrumental version", "relaxing", "sleep music"}
            top_artist_name = artists_list[0].name.lower() if artists_list else ""
            is_artist_query = (clean_q.lower() == top_artist_name)

            filtered_albums = []
            for it in album_items:
                al_title = it.get("title", "")
                if any(term in al_title.lower() for term in album_spam_terms):
                    continue
                filtered_albums.append(it)

            def album_rank(a):
                title = (a.get("title") or "").lower()
                rec_type = (a.get("record_type") or "").lower()
                nb_tr = a.get("nb_tracks") or 1
                
                # Priority: full albums first (0), EPs second (4), 1-track singles last (10)
                type_penalty = 0
                if rec_type == "single" or nb_tr == 1:
                    type_penalty = 10
                elif rec_type == "ep":
                    type_penalty = 4

                spam_penalty = 0
                if "track by track" in title or "karaoke" in title or "acoustic" in title:
                    spam_penalty = 20

                # Match bonus if query matches album title
                match_bonus = 0
                if clean_q.lower() in title or title in clean_q.lower():
                    match_bonus = -15
                
                return type_penalty + spam_penalty + match_bonus

            if is_artist_query and top_artist_name:
                artist_albums = [a for a in filtered_albums if a.get("artist", {}).get("name", "").lower() == top_artist_name]
                other_albums = [a for a in filtered_albums if a.get("artist", {}).get("name", "").lower() != top_artist_name]
                artist_albums.sort(key=album_rank)
                other_albums.sort(key=album_rank)
                filtered_albums = artist_albums + other_albums
            else:
                filtered_albums.sort(key=album_rank)

            for it in filtered_albums:
                al_id = str(it.get("id", ""))
                al_title = it.get("title", "")
                if al_id and al_title and al_id not in albums_map:
                    art_data = it.get("artist", {})
                    art_id = str(art_data.get("id", ""))
                    art_name = art_data.get("name", "Unknown Artist")
                    cover = it.get("cover_xl") or it.get("cover_big") or it.get("cover_medium")
                    if art_id and art_name and art_id not in artists_map:
                        artists_map[art_id] = CatalogArtist(
                            id=f"deezer:artist:{art_id}",
                            name=art_name,
                            artwork_url=art_data.get("picture_xl") or art_data.get("picture_big")
                        )
                    albums_map[al_id] = CatalogAlbum(
                        id=f"deezer:album:{al_id}",
                        title=al_title,
                        artist_name=art_name,
                        artist_id=f"deezer:artist:{art_id}" if art_id else "",
                        artwork_url=cover
                    )

            # 3. Process Track search results
            for it in track_items:
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
                        disc_number=it.get("disk_number") or 1,
                        track_number=it.get("track_position") or 1,
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
            logger.error(f"Deezer search failed for '{query}': {e}", exc_info=True)
            return CatalogSearchResult()

    async def get_artist(self, artist_id: str) -> CatalogArtist | None:
        raw_id = artist_id.split(":")[-1].strip()
        if not raw_id.isdigit():
            artists = await self.search_artists(raw_id, limit=1)
            if artists:
                return artists[0]
            return None
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
                query_words = [w for w in clean_q.split() if len(w) > 2]
                contains_query = (clean_q in name_lower) or (len(query_words) > 0 and all(w in name_lower for w in query_words))

                # Require that artist actually matches the query:
                # 1. Exact match or name contains query / all query words -> keep!
                # 2. If it does not contain query words, reject it (avoids Deezer fuzzy typos like Halestorm for Hadestown)
                if not (contains_query or is_exact):
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

            # Only append related artists if:
            # 1. We have results, and the top artist is an EXACT match (e.g. searching "taylor swift" or "coldplay")
            # 2. Top artist is a major superstar (>= 50,000 fans)
            # 3. We have fewer than 4 artists
            if len(results) < 4 and valid_artists and valid_artists[0]["is_exact"] and valid_artists[0]["nb_fan"] >= 50000:
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
        clean = artist_id.split(":")[-1].strip()
        if clean.isdigit():
            return clean
        query = name or (clean.split(":")[-1] if ":" in clean else clean)
        try:
            async with self._get_client() as client:
                res = await client.get("/search/artist", params={"q": query, "limit": 1})
                if res.is_success:
                    items = res.json().get("data", [])
                    if items:
                        return str(items[0]["id"])
        except Exception:
            pass
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
                tracks.append(CatalogTrack(
                    id=f"deezer:track:{it.get('id')}",
                    title=it.get("title", "Unknown Track"),
                    artist_name=it.get("artist", {}).get("name", ""),
                    artist_id=f"deezer:artist:{resolved_id}",
                    album_title=al.get("title"),
                    album_id=f"deezer:album:{al.get('id')}" if al.get("id") else None,
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
                albums.append(CatalogAlbum(
                    id=f"deezer:album:{it.get('id')}",
                    title=it.get("title", "Unknown Album"),
                    artist_name=it.get("artist", {}).get("name", ""),
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
        raw_id = album_id.split(":")[-1].strip()
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

            genres = [g.get("name") for g in data.get("genres", {}).get("data", []) if g.get("name")]
            if not genres and data.get("genre_id"):
                gid = data.get("genre_id")
                genre_map = {
                    132: "Pop", 116: "Rap/Hip Hop", 152: "Rock", 113: "Dance", 165: "R&B",
                    85: "Alternative", 98: "Classical", 464: "Metal", 129: "Jazz", 173: "Latin",
                    106: "Electro", 84: "Country", 2: "African", 16: "Asian Pop", 144: "Reggae",
                    12: "Latin", 52: "World", 71: "Indie", 153: "Blues", 169: "Soul"
                }
                if gid in genre_map:
                    genres = [genre_map[gid]]

            total_tracks = data.get("nb_tracks", 1)
            album = CatalogAlbum(
                id=f"deezer:album:{raw_id}",
                title=data.get("title", "Unknown Album"),
                artist_name=artist_name,
                artist_id=f"deezer:artist:{artist_id}",
                release_date=data.get("release_date"),
                total_tracks=total_tracks,
                artwork_url=cover,
                genres=genres
            )

            tracks_data = data.get("tracks", {}).get("data", [])
            # Deezer caps embedded album tracks at 25. If total_tracks > len(tracks_data), fetch the complete tracklist.
            if total_tracks > len(tracks_data):
                try:
                    async with self._get_client() as client:
                        tr_res = await client.get(f"/album/{raw_id}/tracks", params={"limit": 100})
                        if tr_res.status_code == 200:
                            fetched_tracks = tr_res.json().get("data", [])
                            if len(fetched_tracks) > len(tracks_data):
                                tracks_data = fetched_tracks
                except Exception as e:
                    logger.debug(f"Could not fetch full tracklist for album {raw_id}: {e}")

            tracks: list[CatalogTrack] = []
            for idx, t in enumerate(tracks_data, 1):
                track_artist = t.get("artist", {})
                t_artist_name = track_artist.get("name") or artist_name
                t_artist_id = f"deezer:artist:{track_artist.get('id')}" if track_artist.get("id") else f"deezer:artist:{artist_id}"

                tracks.append(CatalogTrack(
                    id=f"deezer:track:{t.get('id')}",
                    title=t.get("title", ""),
                    artist_name=t_artist_name,
                    artist_id=t_artist_id,
                    album_title=album.title,
                    album_id=album.id,
                    duration_ms=t.get("duration", 0) * 1000,
                    disc_number=t.get("disk_number") or 1,
                    track_number=t.get("track_position") or idx,
                    isrc=t.get("isrc"),
                    artwork_url=cover,
                    explicit=bool(t.get("explicit_lyrics", False))
                ))

            return album, tracks
        except Exception as e:
            logger.error(f"Deezer get_album failed for '{album_id}': {e}")
            return None

    async def get_track(self, track_id: str) -> CatalogTrack | None:
        raw_id = track_id.split(":")[-1].strip()
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

    async def get_chart_albums(self, country: str = "0", limit: int = 30) -> list[CatalogAlbum]:
        """Fetch real top trending and new albums from Deezer."""
        try:
            async with self._get_client() as client:
                cid = country if country.isdigit() else "0"
                res = await client.get(f"/chart/{cid}/albums", params={"limit": limit})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])
            albums: list[CatalogAlbum] = []
            for it in items:
                aid = str(it.get("id", ""))
                title = it.get("title", "Unknown Album")
                artist_data = it.get("artist", {})
                artist_name = artist_data.get("name", "Unknown Artist")
                artist_id = str(artist_data.get("id", ""))
                cover = it.get("cover_xl") or it.get("cover_big") or it.get("cover_medium")
                if aid and title:
                    albums.append(CatalogAlbum(
                        id=f"deezer:album:{aid}",
                        title=title,
                        artist_name=artist_name,
                        artist_id=f"deezer:artist:{artist_id}" if artist_id else "",
                        release_date=it.get("release_date"),
                        total_tracks=it.get("nb_tracks", 1),
                        artwork_url=cover
                    ))
            return albums
        except Exception as e:
            logger.error(f"Deezer get_chart_albums failed for country '{country}': {e}")
            return []

    async def get_chart_playlists(self, country: str = "0", limit: int = 30) -> list[dict[str, Any]]:
        """Fetch live rotating curated playlists from Deezer."""
        try:
            async with self._get_client() as client:
                cid = country if country.isdigit() else "0"
                res = await client.get(f"/chart/{cid}/playlists", params={"limit": limit})
                res.raise_for_status()
                data = res.json()
            items = data.get("data", [])
            playlists: list[dict[str, Any]] = []
            for it in items:
                pid = str(it.get("id", ""))
                title = it.get("title", "Unknown Playlist")
                pic = it.get("picture_xl") or it.get("picture_big") or it.get("picture_medium")
                user_data = it.get("user", {})
                creator = user_data.get("name", "Deezer")
                nb_tracks = it.get("nb_tracks", 0)
                if pid and title:
                    playlists.append({
                        "id": pid,
                        "name": title,
                        "description": f"Curated by {creator} • {nb_tracks} tracks",
                        "imageUrl": pic
                    })
            return playlists
        except Exception as e:
            logger.error(f"Deezer get_chart_playlists failed for country '{country}': {e}")
            return []

    async def get_lyrics(self, track_id: str) -> dict | None:
        raw_id = track_id.split(":")[-1].strip()
        if not raw_id:
            return None
        token = await self._get_anonymous_jwt()
        if not token:
            return None

        query = """
        query GetLyrics($trackId: String!) {
          track(trackId: $trackId) {
            id
            title
            hasSynchronizedLyrics
            lyrics {
              id
              text
              synchronizedLines {
                line
                milliseconds
                duration
              }
            }
          }
        }
        """
        try:
            async with httpx.AsyncClient(timeout=8.0) as client:
                res = await client.post(
                    "https://pipe.deezer.com/api",
                    json={"query": query, "variables": {"trackId": raw_id}},
                    headers={
                        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
                        "Origin": "https://www.deezer.com",
                        "Referer": "https://www.deezer.com/",
                        "Authorization": f"Bearer {token}",
                        "Content-Type": "application/json"
                    }
                )
                if res.status_code != 200:
                    return None
                data = res.json()

            track_data = data.get("data", {}).get("track", {})
            if not track_data:
                return None
            lyrics_data = track_data.get("lyrics")
            if not lyrics_data:
                return None

            plain_text = lyrics_data.get("text") or None
            sync_lines = lyrics_data.get("synchronizedLines") or []
            lrc_lines = []
            for sl in sync_lines:
                ms = int(sl.get("milliseconds", 0))
                m = ms // 60000
                s = (ms % 60000) // 1000
                cs = (ms % 1000) // 10
                lrc_lines.append(f"[{m:02d}:{s:02d}.{cs:02d}]{sl.get('line', '')}")

            synced_lrc = "\n".join(lrc_lines) if lrc_lines else None
            if not synced_lrc and not plain_text:
                return None

            return {
                "trackName": track_data.get("title"),
                "plainLyrics": plain_text,
                "syncedLyrics": synced_lrc,
                "source": "deezer"
            }
        except Exception as e:
            logger.warning(f"Deezer get_lyrics failed for track '{track_id}': {e}")
            return None

