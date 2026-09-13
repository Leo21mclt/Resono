import mimetypes
import logging
from pathlib import Path
from fastapi import HTTPException
from fastapi.responses import FileResponse
from app.core.cache import cache_manager

logger = logging.getLogger("resono.stream")

MIME_MAP = {
    ".mp3": "audio/mpeg",
    ".flac": "audio/flac",
    ".m4a": "audio/mp4",
    ".ogg": "audio/ogg",
    ".opus": "audio/opus",
    ".wav": "audio/wav",
}

def get_audio_file_response(file_path: Path) -> FileResponse:
    """
    Return a FileResponse with Accept-Ranges support for Jellyfin audio streaming.
    Starlette automatically handles HTTP 206 Partial Content for byte-range requests.
    """
    if not file_path.exists() or file_path.stat().st_size == 0:
        raise HTTPException(status_code=404, detail="Audio file not found or empty")

    ext = file_path.suffix.lower()
    media_type = MIME_MAP.get(ext, "application/octet-stream")

    return FileResponse(
        path=file_path,
        media_type=media_type,
        headers={
            "Accept-Ranges": "bytes",
            "Content-Disposition": f'inline; filename="{file_path.name}"',
            "Cache-Control": "public, max-age=31536000, immutable"
        }
    )
