from pydantic import BaseModel, Field

class CatalogArtist(BaseModel):
    id: str
    name: str
    artwork_url: str | None = None
    genres: list[str] = Field(default_factory=list)
    popularity: int = 0

class CatalogAlbum(BaseModel):
    id: str
    title: str
    artist_name: str
    artist_id: str
    release_date: str | None = None
    release_type: str = "album" # album, single, compilation
    total_tracks: int = 1
    artwork_url: str | None = None

class CatalogTrack(BaseModel):
    id: str
    title: str
    artist_name: str
    artist_id: str
    album_title: str
    album_id: str
    duration_ms: int
    disc_number: int = 1
    track_number: int = 1
    isrc: str | None = None
    artwork_url: str | None = None
    explicit: bool = False

class CatalogSearchResult(BaseModel):
    artists: list[CatalogArtist] = Field(default_factory=list)
    albums: list[CatalogAlbum] = Field(default_factory=list)
    tracks: list[CatalogTrack] = Field(default_factory=list)
