import logging
from pathlib import Path
from pydantic import BaseModel
import mutagen

logger = logging.getLogger("resono.validate")

class AudioValidationResult(BaseModel):
    is_valid: bool
    duration_seconds: float = 0.0
    bitrate: int | None = None
    codec: str = ""
    file_size: int = 0
    error_message: str | None = None

def validate_audio_file(file_path: Path, expected_duration_ms: int | None = None) -> AudioValidationResult:
    """
    Forensically validates an acquired audio file.
    - Ensures file exists and has content
    - Uses Mutagen to parse audio container headers and verify stream integrity
    - Checks that duration is within reasonable bounds (not truncated or 0 seconds)
    """
    if not file_path.exists():
        return AudioValidationResult(
            is_valid=False,
            error_message=f"File not found on disk: {file_path}"
        )

    file_size = file_path.stat().st_size
    if file_size < 10240: # Less than 10 KB is definitely not a real full song
        return AudioValidationResult(
            is_valid=False,
            file_size=file_size,
            error_message=f"File size too small ({file_size} bytes); likely incomplete or corrupted"
        )

    try:
        audio = mutagen.File(str(file_path))
        if audio is None:
            return AudioValidationResult(
                is_valid=False,
                file_size=file_size,
                error_message=f"Mutagen could not parse audio container for format {file_path.suffix}"
            )

        info = getattr(audio, "info", None)
        if info is None:
            return AudioValidationResult(
                is_valid=False,
                file_size=file_size,
                error_message="Audio headers contain no stream info"
            )

        duration = getattr(info, "length", 0.0)
        bitrate = getattr(info, "bitrate", None)
        codec = file_path.suffix.lstrip(".").lower()

        if duration <= 10.0:
            return AudioValidationResult(
                is_valid=False,
                duration_seconds=duration,
                file_size=file_size,
                codec=codec,
                error_message=f"Duration too short ({duration:.1f}s); file is truncated"
            )

        # Check against expected duration if provided (tolerance ±15 seconds or ±10%)
        if expected_duration_ms and expected_duration_ms > 0:
            expected_sec = expected_duration_ms / 1000.0
            diff = abs(duration - expected_sec)
            max_allowed_diff = max(15.0, expected_sec * 0.10)
            if diff > max_allowed_diff:
                return AudioValidationResult(
                    is_valid=False,
                    duration_seconds=duration,
                    file_size=file_size,
                    codec=codec,
                    error_message=f"Duration discrepancy: expected ~{expected_sec:.1f}s, but file is {duration:.1f}s (diff {diff:.1f}s)"
                )

        logger.info(f"[VALIDATE] OK format={codec} duration={duration:.1f}s size={file_size} bytes")
        return AudioValidationResult(
            is_valid=True,
            duration_seconds=duration,
            bitrate=bitrate,
            codec=codec,
            file_size=file_size
        )

    except Exception as e:
        logger.error(f"[VALIDATE] Failed for {file_path.name}: {e}")
        return AudioValidationResult(
            is_valid=False,
            file_size=file_size,
            error_message=f"Failed to decode audio: {str(e)}"
        )
