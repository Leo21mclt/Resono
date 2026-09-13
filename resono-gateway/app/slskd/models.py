from pydantic import BaseModel, Field

class SlskdFileCandidate(BaseModel):
    peer: str
    filename: str
    file_size: int
    bitrate: int | None = None
    sample_rate: int | None = None
    duration_seconds: int | None = None
    extension: str = ""
    slots_free: bool = True
    queue_length: int = 0
    upload_speed: int = 0
    is_locked: bool = False

class SlskdSearch(BaseModel):
    id: str
    query: str
    is_complete: bool = False
    file_count: int = 0
    files: list[SlskdFileCandidate] = Field(default_factory=list)

class SlskdTransferStatus(BaseModel):
    id: str
    username: str
    filename: str
    size: int
    bytes_transferred: int = 0
    speed: float = 0.0
    state: str = "Queued" # Queued, Initializing, InProgress, Succeeded, Completed, Errored, Cancelled
    percent_complete: float = 0.0
    local_path: str | None = None
