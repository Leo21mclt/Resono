from pydantic_settings import BaseSettings, SettingsConfigDict
from pathlib import Path

class Settings(BaseSettings):
    # Service settings
    APP_NAME: str = "Resono Gateway"
    DEBUG: bool = False
    PORT: int = 8080
    HOST: str = "0.0.0.0"

    # Soulseek daemon connection settings
    SOULSEEK_BACKEND: str = "slskd"  # "slskr" | "slskd"
    SLSKD_URL: str = "http://slskd:5030"
    SLSKD_API_KEY: str | None = None
    SLSKR_URL: str = "http://slskd:5030"
    SLSKR_API_KEY: str | None = None
    SLSKD_TIMEOUT_SECONDS: int = 15
    SLSKD_SEARCH_TIMEOUT_SECONDS: int = 6

    # Catalog Provider settings
    CATALOG_PROVIDER: str = "deezer"  # "deezer" | "apple" | "spotify" | "musicbrainz"
    FALLBACK_CATALOG_PROVIDER: str = "apple"
    SPOTIFY_CLIENT_ID: str | None = None
    SPOTIFY_CLIENT_SECRET: str | None = None

    # Playback Acquisition settings
    PRIMARY_PLAYBACK_SOURCE: str = "deezer"  # "deezer" | "soulseek"
    FALLBACK_PLAYBACK_SOURCE: str = "none"  # "none" | "soulseek" | "deezer"
    DEEZER_ARL: str | None = None

    # Storage paths
    DATA_DIR: Path = Path("./data")
    CACHE_DIR: Path = Path("./cache/audio")
    ARTWORK_DIR: Path = Path("./cache/artwork")
    SLSKD_DOWNLOADS_PATH: Path = Path("./downloads")
    DATABASE_URL: str = "sqlite+aiosqlite:///./data/resono.db"

    # Cache limits
    MAX_CACHE_SIZE_GB: float = 15.0
    CACHE_INACTIVITY_DAYS: int = 14
    MIN_FREE_DISK_GB: float = 10.0
    MAX_CONCURRENT_DOWNLOADS: int = 3
    PREFETCH_BUFFER_TRACKS: int = 2

    # Matcher thresholds
    MATCHER_CONFIDENCE_THRESHOLD: float = 0.80
    MAX_DURATION_DIFFERENCE_MS: int = 6000

    model_config = SettingsConfigDict(
        env_prefix="RESONO_",
        env_file=".env",
        extra="ignore"
    )

settings = Settings()

