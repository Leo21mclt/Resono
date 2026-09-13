import logging
from typing import Any
from pathlib import Path
from contextlib import asynccontextmanager
from fastapi import FastAPI, HTTPException, Query, Depends
from fastapi.responses import FileResponse
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import settings
from app.db.database import init_db, get_db
from app.catalog.manager import catalog_manager
from app.core.backend import soulseek_backend
from app.core.cache import cache_manager
from app.core.stream import get_audio_file_response
from app.matcher.scoring import matcher
from app.core.jobs import job_manager
from app.core.acquire import acquisition_manager

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
)
logger = logging.getLogger("resono.main")

@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("Initializing Resono Gateway...")
    await init_db()
    is_backend_up = await soulseek_backend.check_health()
    logger.info(f"Soulseek backend ({soulseek_backend.name}) status: {'CONNECTED' if is_backend_up else 'DISCONNECTED'}")
    yield
    logger.info("Shutting down Resono Gateway...")

app = FastAPI(
    title=settings.APP_NAME,
    version="0.1.0",
    lifespan=lifespan
)

@app.get("/health")
async def health():
    return {
        "status": "healthy",
        "service": settings.APP_NAME,
    }

@app.get("/catalog/search")
async def catalog_search(q: str = Query(..., min_length=1), limit: int = Query(20, ge=1, le=50)):
    result = await catalog_manager.search(q, limit=limit)
    return result

@app.get("/catalog/artist/{artist_id:path}")
async def get_artist(artist_id: str):
    artist = await catalog_manager.get_artist(artist_id)
    if not artist:
        raise HTTPException(status_code=404, detail="Artist not found")
    return artist

@app.get("/catalog/album/{album_id:path}")
async def get_album(album_id: str):
    data = await catalog_manager.get_album(album_id)
    if not data:
        raise HTTPException(status_code=404, detail="Album not found")
    album, tracks = data
    return {"album": album, "tracks": tracks}

@app.get("/catalog/track/{track_id:path}")
async def get_track(track_id: str):
    track = await catalog_manager.get_track(track_id)
    if not track:
        raise HTTPException(status_code=404, detail="Track not found")
    return track

@app.get("/resolve/{track_id:path}")
async def resolve_track(track_id: str, db: AsyncSession = Depends(get_db)):
    """
    Resolve a catalog track to an exact Soulseek recording candidate.
    Uses JobManager to deduplicate concurrent requests for the same track.
    """
    track = await catalog_manager.get_track(track_id)
    if not track:
        raise HTTPException(status_code=404, detail="Track not found in catalog")

    # Persist track in DB with deterministic GUID
    await catalog_manager.sync_track_to_db(track, db)
    canonical = catalog_manager.to_canonical_track(track)

    async def _resolve():
        search_query = f"{track.artist_name} {track.title}"
        logger.info(f"Resolving recording for '{track.title}' by '{track.artist_name}' (query: '{search_query}')")
        candidates = await soulseek_backend.search(search_query, timeout_seconds=6)
        best = matcher.find_best_match(candidates, canonical)
        if not best:
            raise HTTPException(status_code=404, detail="No confident recording match found on Soulseek network")
        return {
            "canonical_id": canonical.canonical_id,
            "track_id": track.id,
            "title": track.title,
            "artist": track.artist_name,
            "album": track.album_title,
            "duration_ms": track.duration_ms,
            "selected_match": {
                "backend": best.candidate.backend,
                "peer": best.candidate.peer_id,
                "filename": best.candidate.filename,
                "remote_path": best.candidate.remote_path,
                "format": best.candidate.codec,
                "bitrate": best.candidate.bitrate,
                "duration_seconds": best.candidate.duration_sec,
                "file_size": best.candidate.size_bytes,
                "score": best.score.total_score,
                "breakdown": best.score.model_dump()
            }
        }

    return await job_manager.get_or_create(track.id, _resolve)

@app.get("/stream")
async def stream_query(
    artist: str | None = Query(None),
    title: str | None = Query(None),
    q: str | None = Query(None),
    db: AsyncSession = Depends(get_db)
):
    """
    Direct stream endpoint compatible with legacy and external players.
    Accepts artist/title or general search query, acquires audio from Soulseek,
    and returns an HTTP 206 stream response.
    """
    query_str = q or f"{artist or ''} {title or ''}".strip()
    if not query_str:
        raise HTTPException(status_code=400, detail="Missing artist/title or query parameter")
    search_res = await catalog_manager.search(query_str, limit=1)
    if not search_res.tracks:
        raise HTTPException(status_code=404, detail=f"Track '{query_str}' not found in catalog")
    track = search_res.tracks[0]
    await catalog_manager.sync_track_to_db(track, db)
    canonical = catalog_manager.to_canonical_track(track)
    try:
        audio_file = await acquisition_manager.acquire_track_audio(canonical, db)
        return get_audio_file_response(audio_file)
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e))
    except Exception as e:
        logger.error(f"[PLAYBACK] Failed acquisition for '{canonical.title}': {e}")
        raise HTTPException(status_code=502, detail=f"Failed to acquire recording: {str(e)}")

@app.get("/playback/{track_id:path}")
@app.get("/stream/{track_id:path}")
async def stream_track(track_id: str, db: AsyncSession = Depends(get_db)):
    """
    Audio playback & streaming endpoint for Jellyfin.
    Supports HTTP 206 Partial Content (byte-range seeking).
    Checks cache first; acquires, validates, and atomically caches on demand if cache miss.
    """
    track = await catalog_manager.get_track(track_id)
    if not track:
        raise HTTPException(status_code=404, detail="Track not found in catalog")

    # Persist track in DB with deterministic GUID
    await catalog_manager.sync_track_to_db(track, db)
    canonical = catalog_manager.to_canonical_track(track)

    try:
        audio_file = await acquisition_manager.acquire_track_audio(canonical, db)
        return get_audio_file_response(audio_file)
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e))
    except Exception as e:
        logger.error(f"[PLAYBACK] Failed acquisition for '{canonical.title}': {e}")
        raise HTTPException(status_code=502, detail=f"Failed to acquire recording: {str(e)}")


# =====================================================================
# Jellyfin Virtual Provider Integration Endpoints
# =====================================================================

@app.get("/jellyfin/search")
async def jellyfin_search(q: str = Query(..., min_length=1), limit: int = Query(20, ge=1, le=50)):
    """Search endpoint formatted for Jellyfin C# RemoteSearchProvider and action filters."""
    results = await catalog_manager.search(q, limit=limit)
    return {
        "artists": [
            {
                "id": a.id,
                "name": a.name,
                "imageUrl": a.artwork_url,
                "providerIds": {"Spotify": a.id.replace("spotify:artist:", "")}
            }
            for a in results.artists
        ],
        "albums": [
            {
                "id": al.id,
                "name": al.title,
                "artistName": al.artist_name,
                "artistId": al.artist_id,
                "releaseDate": al.release_date,
                "imageUrl": al.artwork_url,
                "providerIds": {"Spotify": al.id.replace("spotify:album:", "")}
            }
            for al in results.albums
        ],
        "tracks": [
            {
                "id": t.id,
                "canonicalId": catalog_manager.to_canonical_track(t).canonical_id,
                "name": t.title,
                "artistName": t.artist_name,
                "albumName": t.album_title,
                "durationMs": t.duration_ms,
                "trackNumber": t.track_number,
                "discNumber": t.disc_number,
                "imageUrl": t.artwork_url,
                "streamUrl": f"/playback/{t.id}",
                "providerIds": {"Spotify": t.id.replace("spotify:track:", "")}
            }
            for t in results.tracks
        ]
    }

@app.get("/jellyfin/track/{track_id:path}")
async def jellyfin_track_metadata(track_id: str):
    """Deliver full track metadata and stream URL for Jellyfin MediaSourceProvider."""
    track = await catalog_manager.get_track(track_id)
    if not track:
        raise HTTPException(status_code=404, detail="Track not found")
    canonical = catalog_manager.to_canonical_track(track)
    return {
        "id": track.id,
        "canonicalId": canonical.canonical_id,
        "name": track.title,
        "artistName": track.artist_name,
        "artistId": track.artist_id,
        "albumName": track.album_title,
        "albumId": track.album_id,
        "durationMs": track.duration_ms,
        "trackNumber": track.track_number,
        "discNumber": track.disc_number,
        "imageUrl": track.artwork_url,
        "streamUrl": f"/playback/{track.id}",
        "providerIds": {"Spotify": track.id.replace("spotify:track:", "")}
    }

@app.get("/jellyfin/album/{album_id:path}")
async def jellyfin_album_details(album_id: str):
    """Deliver album metadata and child track list for Jellyfin virtual album views."""
    data = await catalog_manager.get_album(album_id)
    if not data:
        raise HTTPException(status_code=404, detail="Album not found")
    album, tracks = data
    return {
        "id": album.id,
        "name": album.title,
        "artistName": album.artist_name,
        "artistId": album.artist_id,
        "releaseDate": album.release_date,
        "imageUrl": album.artwork_url,
        "providerIds": {"Spotify": album.id.replace("spotify:album:", "")},
        "tracks": [
            {
                "id": t.id,
                "canonicalId": catalog_manager.to_canonical_track(t).canonical_id,
                "name": t.title,
                "artistName": t.artist_name,
                "albumName": t.album_title,
                "durationMs": t.duration_ms,
                "trackNumber": t.track_number,
                "discNumber": t.disc_number,
                "imageUrl": t.artwork_url or album.artwork_url,
                "streamUrl": f"/playback/{t.id}",
                "providerIds": {"Spotify": t.id.replace("spotify:track:", "")}
            }
            for t in tracks
        ]
    }

@app.get("/jellyfin/artist/{artist_id:path}")
async def jellyfin_artist_details(artist_id: str):
    """Deliver artist metadata for Jellyfin virtual artist views."""
    artist = await catalog_manager.get_artist(artist_id)
    if not artist:
        raise HTTPException(status_code=404, detail="Artist not found")
    return {
        "id": artist.id,
        "name": artist.name,
        "imageUrl": artist.artwork_url,
        "providerIds": {"Spotify": artist.id.replace("spotify:artist:", "")}
    }

@app.get("/jellyfin/image")
async def jellyfin_image_proxy(url: str = Query(...)):
    """
    Proxy image requests so mobile clients like Discrete, Finamp, Manet
    receive standard 200 OK responses with caching, avoiding cross-domain 302 drops.
    """
    import httpx
    from fastapi.responses import Response
    try:
        async with httpx.AsyncClient(follow_redirects=True) as client:
            res = await client.get(url, timeout=10.0)
            if res.status_code == 200:
                content_type = res.headers.get("content-type", "image/jpeg")
                return Response(
                    content=res.content,
                    media_type=content_type,
                    headers={"Cache-Control": "public, max-age=31536000, immutable"}
                )
    except Exception as e:
        logger.warning(f"Image proxy failed for '{url}': {e}")
    raise HTTPException(status_code=404, detail="Image not found")

@app.post("/jellyfin/telemetry/playback")
async def jellyfin_playback_telemetry(payload: dict[str, Any], db: AsyncSession = Depends(get_db)):
    """Listen for Jellyfin playback & favorite events to adjust adaptive cache retention score."""
    canonical_id = payload.get("canonicalId") or payload.get("ItemId")
    if not canonical_id:
        return {"status": "ignored", "reason": "no ID provided"}

    is_favorite = payload.get("isFavorite", False)
    play_count = payload.get("playCount", 1)

    # If entry exists, boost retention
    from sqlalchemy import select
    from app.db.models import CacheEntry
    res = await db.execute(select(CacheEntry).where(CacheEntry.track_id == canonical_id))
    entry = res.scalar_one_or_none()
    if entry:
        if is_favorite:
            entry.tier = "PROTECTED"
            entry.retention_score += 1000.0
        entry.play_count += play_count
        await db.commit()
        return {"status": "updated", "tier": entry.tier, "retention_score": entry.retention_score}

    return {"status": "acknowledged"}

@app.get("/plugin/Resono.Plugin.dll")
async def download_plugin_dll():
    """Serve the compiled Jellyfin plugin DLL for one-command installation."""
    candidate_paths = [
        Path("/app/app/plugin/Resono.Plugin.dll"),
        Path("/app/plugin/Resono.Plugin.dll"),
        Path("./plugin/Resono.Plugin.dll"),
        Path("../resono-jellyfin-plugin/dist/Resono.Plugin.dll"),
        Path("../resono-jellyfin-plugin/bin/Release/net8.0/Resono.Plugin.dll")
    ]
    for p in candidate_paths:
        if p.exists():
            return FileResponse(p, filename="Resono.Plugin.dll", media_type="application/octet-stream")
    raise HTTPException(status_code=404, detail="Resono.Plugin.dll not found")

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("app.main:app", host=settings.HOST, port=settings.PORT, reload=settings.DEBUG)


