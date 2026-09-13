import uuid
from typing import Any
from pydantic import BaseModel, Field

# Fixed Namespace UUID for Resono deterministic GUID generation
RESONO_NAMESPACE = uuid.UUID("6ba7b810-9dad-11d1-80b4-00c04fd430c8") # DNS namespace base

def deterministic_guid(identifier: str) -> str:
    """Generate a deterministic UUIDv5 GUID from an identifier string."""
    return str(uuid.uuid5(RESONO_NAMESPACE, identifier))

class CanonicalArtist(BaseModel):
    canonical_id: str
    spotify_id: str
    name: str
    sort_name: str | None = None
    artwork_url: str | None = None
    genres: list[str] = Field(default_factory=list)
    popularity: int = 0
    metadata: dict[str, Any] = Field(default_factory=dict)

class CanonicalAlbum(BaseModel):
    canonical_id: str
    spotify_id: str
    artist_id: str
    title: str
    artist_name: str
    release_date: str | None = None
    release_type: str = "album" # album, single, compilation
    total_tracks: int = 1
    artwork_url: str | None = None
    metadata: dict[str, Any] = Field(default_factory=dict)

class CanonicalTrack(BaseModel):
    canonical_id: str
    spotify_id: str
    artist_id: str
    album_id: str
    title: str
    artist_name: str
    album_title: str
    duration_ms: int
    track_number: int = 1
    disc_number: int = 1
    isrc: str | None = None
    release_date: str | None = None
    artwork_url: str | None = None
    explicit: bool = False
    metadata: dict[str, Any] = Field(default_factory=dict)

class AudioCandidate(BaseModel):
    """
    Backend-neutral candidate model.
    Represents an audio file discovered on a peer network (slskr, slskd, etc.)
    without exposing daemon-specific schemas to the matcher.
    """
    backend: str = "slskr" # "slskr" | "slskd"
    peer_id: str
    remote_path: str
    filename: str
    folder: str = ""
    size_bytes: int
    duration_ms: int | None = None
    duration_seconds: int | None = None
    bitrate: int | None = None
    codec: str = "mp3"
    sample_rate: int | None = None
    is_locked: bool = False
    slots_free: bool = True
    queue_length: int = 0
    upload_speed: int = 0
    source_metadata: dict[str, Any] = Field(default_factory=dict)

    @property
    def duration_sec(self) -> int | None:
        if self.duration_seconds is not None:
            return self.duration_seconds
        if self.duration_ms is not None:
            return round(self.duration_ms / 1000)
        return None
