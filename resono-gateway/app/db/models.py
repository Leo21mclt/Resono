import uuid
from datetime import datetime, timezone
from sqlalchemy import Column, String, Integer, Float, Boolean, DateTime, ForeignKey, Index, Text
from sqlalchemy.orm import relationship
from app.db.database import Base

def generate_uuid() -> str:
    return str(uuid.uuid4())

def utc_now() -> datetime:
    return datetime.now(timezone.utc)

class Artist(Base):
    __tablename__ = "artists"

    id = Column(String, primary_key=True, default=generate_uuid)
    spotify_id = Column(String, unique=True, index=True, nullable=False)
    name = Column(String, index=True, nullable=False)
    sort_name = Column(String, nullable=True)
    bio = Column(Text, nullable=True)
    artwork_url = Column(String, nullable=True)
    popularity = Column(Integer, default=0)
    created_at = Column(DateTime(timezone=True), default=utc_now)
    updated_at = Column(DateTime(timezone=True), default=utc_now, onupdate=utc_now)

    albums = relationship("Album", back_populates="artist", cascade="all, delete-orphan")
    tracks = relationship("Track", back_populates="artist", cascade="all, delete-orphan")

class Album(Base):
    __tablename__ = "albums"

    id = Column(String, primary_key=True, default=generate_uuid)
    spotify_id = Column(String, unique=True, index=True, nullable=False)
    artist_id = Column(String, ForeignKey("artists.id"), nullable=False, index=True)
    title = Column(String, index=True, nullable=False)
    release_date = Column(String, nullable=True)
    release_type = Column(String, default="album") # album, single, compilation
    total_tracks = Column(Integer, default=1)
    artwork_url = Column(String, nullable=True)
    created_at = Column(DateTime(timezone=True), default=utc_now)

    artist = relationship("Artist", back_populates="albums")
    tracks = relationship("Track", back_populates="album", cascade="all, delete-orphan")

class Track(Base):
    __tablename__ = "tracks"

    id = Column(String, primary_key=True, default=generate_uuid)
    spotify_id = Column(String, unique=True, index=True, nullable=False)
    artist_id = Column(String, ForeignKey("artists.id"), nullable=False, index=True)
    album_id = Column(String, ForeignKey("albums.id"), nullable=False, index=True)
    title = Column(String, index=True, nullable=False)
    duration_ms = Column(Integer, nullable=False)
    disc_number = Column(Integer, default=1)
    track_number = Column(Integer, default=1)
    isrc = Column(String, nullable=True, index=True)
    explicit = Column(Boolean, default=False)
    created_at = Column(DateTime(timezone=True), default=utc_now)

    artist = relationship("Artist", back_populates="tracks")
    album = relationship("Album", back_populates="tracks")
    sources = relationship("SourceMapping", back_populates="track", cascade="all, delete-orphan")
    cache_entry = relationship("CacheEntry", back_populates="track", uselist=False, cascade="all, delete-orphan")

class SourceMapping(Base):
    __tablename__ = "source_mappings"

    id = Column(String, primary_key=True, default=generate_uuid)
    track_id = Column(String, ForeignKey("tracks.id"), nullable=False, index=True)
    peer = Column(String, nullable=False, index=True)
    remote_path = Column(Text, nullable=False)
    file_size = Column(Integer, nullable=False)
    duration_ms = Column(Integer, nullable=True)
    bitrate = Column(Integer, nullable=True)
    codec = Column(String, nullable=True)
    confidence_score = Column(Float, default=0.0)
    success_count = Column(Integer, default=0)
    failure_count = Column(Integer, default=0)
    last_used_at = Column(DateTime(timezone=True), default=utc_now)

    track = relationship("Track", back_populates="sources")

    __table_args__ = (
        Index("idx_source_track_confidence", "track_id", "confidence_score"),
    )

class CacheEntry(Base):
    __tablename__ = "cache_entries"

    track_id = Column(String, ForeignKey("tracks.id"), primary_key=True)
    file_path = Column(String, nullable=False)
    file_size = Column(Integer, nullable=False)
    codec = Column(String, default="mp3")
    tier = Column(String, default="HOT") # VIRTUAL, HOT, WARM, LONG_TERM, PROTECTED
    play_count = Column(Integer, default=1)
    last_played_at = Column(DateTime(timezone=True), default=utc_now)
    retention_score = Column(Float, default=100.0)
    created_at = Column(DateTime(timezone=True), default=utc_now)

    track = relationship("Track", back_populates="cache_entry")

class DownloadJob(Base):
    __tablename__ = "download_jobs"

    id = Column(String, primary_key=True, default=generate_uuid)
    track_id = Column(String, ForeignKey("tracks.id"), nullable=False, index=True)
    status = Column(String, default="QUEUED") # QUEUED, SEARCHING, DOWNLOADING, VALIDATING, COMPLETED, FAILED
    progress = Column(Float, default=0.0)
    peer = Column(String, nullable=True)
    remote_path = Column(Text, nullable=True)
    error_message = Column(Text, nullable=True)
    created_at = Column(DateTime(timezone=True), default=utc_now)
    updated_at = Column(DateTime(timezone=True), default=utc_now, onupdate=utc_now)
