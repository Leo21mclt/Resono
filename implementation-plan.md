# Resono — Phased Implementation Plan

## Overview
This document lays out the phased, milestone-driven roadmap for designing, implementing, validating, and deploying **Resono**.
Work proceeds strictly through small, testable increments where each milestone produces working, verified code before subsequent layers are added.

---

## Phase 0: Environment Audit & Architecture Baseline (COMPLETED)
- [x] Audit production server hardware (HP ProDesk 600 G5 SFF, Intel i5-8500, Ubuntu 24.04 LTS, Dokploy).
- [x] Audit development environment (Windows 11, Python 3.12, Docker Desktop, Git).
- [x] Audit Jellyfin server and plugin compatibility across versions 10.9 through 10.10.
- [x] Audit slskr API capabilities and evaluate viability of preview streaming vs. dual-phase cache-through streaming.
- [x] Audit virtual library strategies (settled on Deterministic Virtual `.strm` + Dynamic Gateway for 100% client stability).
- [x] Deliver foundational technical documentation:
  - `architecture.md`
  - `environment.md`
  - `compatibility.md`
  - `implementation-plan.md`

---

## Phase 1: slskr Integration & Audio Pipeline Proof of Concept
**Objective:** Validate live programmatic communication with slskr, search query execution, result parsing, peer transfer tracking, and file integrity verification.

- **Milestone 1.1:** Build `slskr_client.py` async client library.
  - Implement authentication and connection health checks (`/api/v1/health`, `/api/v1/version`).
  - Implement search initiation (`POST /api/v1/searches`) and polling retrieval (`GET /api/v1/searches/{id}`).
  - Implement download enqueuing (`POST /api/v1/transfers/downloads`) and status tracking.
- **Milestone 1.2:** Verification with mock / live test queries.
  - Test searching known public domain / test recordings.
  - Measure search latency, response parsing time, and candidate collection stability.
  - Document precise failure scenarios (peer offline, queues full, download timeouts).

---

## Phase 2: Resono Gateway Skeleton & Job Orchestration
**Objective:** Scaffold the FastAPI backend service with durable SQLite persistence, job deduplication, and baseline REST endpoints.

- **Milestone 2.1:** Project structure and configuration management.
  - `resono-gateway/` folder with `pydantic-settings` configuration (`RESONO_PORT`, `SLSKR_URL`, `CACHE_DIR`, `DB_PATH`).
  - Database layer with SQLite, WAL mode, and Alembic migrations.
- **Milestone 2.2:** Core schema definition:
  - `artists`, `albums`, `tracks`, `source_mappings`, `cache_entries`, `playback_jobs`.
- **Milestone 2.3:** Request Deduplication & Coalescing Engine:
  - `JobManager`: If multiple requests arrive for `track_id`, bind them to an existing `asyncio.Future` / download job rather than spawning duplicate transfers.
- **Milestone 2.4:** Standard health & system API endpoints:
  - `GET /health`
  - `GET /status`
  - `GET /metrics`

---

## Phase 3: Spotify & Metadata Catalog Provider
**Objective:** Deliver rich Spotify catalog browsing (Artists, Albums, Tracklists, Artwork) without requiring user credentials.

- **Milestone 3.1:** Abstract `CatalogProvider` interface:
  - `search(query: str, types: list[str]) -> SearchResult`
  - `get_artist(artist_id: str) -> ArtistDetails`
  - `get_album(album_id: str) -> AlbumDetails`
  - `get_track(track_id: str) -> TrackDetails`
- **Milestone 3.2:** `SpotifyProvider` implementation:
  - Anonymous public token fetching and caching (1-hour auto-renewal).
  - Web client endpoints for artist discography and album tracks.
  - High-res image URL extraction.
- **Milestone 3.3:** Fallback Providers:
  - `MusicBrainzProvider` for metadata enrichment and fallback.
  - `ITunesProvider` for cover art and fallback lookup.
- **Milestone 3.4:** Local Catalog Caching:
  - Cache all fetched artist/album/track metadata into SQLite to reduce external HTTP calls.
- **Milestone 3.5:** Artist Deduplication:
  - Strict Spotify ID canonicalization to prevent name-collision clutter in search results.

---

## Phase 4: Exact Recording Matcher & Heuristic Scoring Engine
**Objective:** Eliminate false positives (karaoke, covers, live bootlegs, wrong artists) using multi-factor evidence scoring.

- **Milestone 4.1:** Heuristic Scorer implementation:
  - Token-set Levenshtein distance on artist and title.
  - Folder path and parent directory parsing.
  - Duration comparison ($\le 2$s tolerance).
  - Bitrate and codec prioritization (FLAC $\ge 320\text{k MP3} > \text{low bitrate}$).
- **Milestone 4.2:** Disqualification filter:
  - Rejection list: `karaoke`, `cover`, `tribute`, `live`, `remix`, `instrumental`, `nightcore`, `sped up`.
- **Milestone 4.3:** Source Mapping Persistence:
  - Store winning peer, path, size, and hash in `source_mappings` table.
  - Quick-path: on cache miss, test previous successful source before launching a wide search.
- **Milestone 4.4:** Unit test suite:
  - Test against a battery of 50+ challenging edge cases (deluxe editions, same-name artists, remasters).

---

## Phase 5: Playback & Streaming Engine
**Objective:** Build high-performance, low-latency audio streaming for Jellyfin clients with full range request and seeking capabilities.

- **Milestone 5.1:** HTTP Range Request & Chunked Streaming handler (`GET /playback/{track_id}`).
  - Full `206 Partial Content`, `Accept-Ranges: bytes`, and `Content-Range` header compliance.
- **Milestone 5.2:** Cache Hit streaming:
  - Fast kernel zero-copy / chunked read from local NVMe cache.
- **Milestone 5.3:** Cache Miss / Dual-Phase Cache-Through streaming:
  - Begin background download via slskr.
  - Buffer initial safety window (512 KB - 1 MB) to parse audio headers.
  - Stream chunks to client while simultaneously appending to `.part` file.
  - On transfer completion, validate integrity (`ffprobe` / size check) and perform atomic rename to verified cache.
- **Milestone 5.4:** Incomplete / Aborted download cleanup & retry policy.

---

## Phase 6: Resono Jellyfin Plugin
**Objective:** Deliver seamless Jellyfin integration with native client UI support.

- **Milestone 6.1:** Scaffolding the C# `.NET 8` Plugin (`Resono.Plugin`).
  - `Plugin.cs`, `Configuration/PluginConfiguration.cs`.
- **Milestone 6.2:** Search & Metadata Provider:
  - Intercept user search in Jellyfin; route to Resono Gateway `/catalog/search`.
  - Provide rich metadata (artists, albums, cover art) directly to Jellyfin items.
- **Milestone 6.3:** Virtual Library Generator / Synchronization:
  - Generate deterministic `.strm` references for catalog items pointing to Gateway playback URLs.
- **Milestone 6.4:** Client compatibility validation:
  - Test playback on Jellyfin Web, iOS (Swiftfin), and Android (Finamp).
  - Verify favorites sync, playlist addition, play count increments, and scrobbling.

---

## Phase 7: Adaptive Cache System & Eviction Policy Engine
**Objective:** Intelligently manage local disk storage without ever losing virtual catalog state.

- **Milestone 7.1:** Cache tier lifecycle (`VIRTUAL`, `HOT`, `WARM`, `LONG_TERM`, `PROTECTED`).
- **Milestone 7.2:** Retention scoring algorithm:
  - Incorporates play counts, recency, favorite state, playlist memberships, and file size.
- **Milestone 7.3:** Background Eviction Daemon:
  - Runs periodically or triggers on disk pressure thresholds (`max_cache_size_gb`, `minimum_free_disk_gb`).
  - Evicts lowest-scoring audio files (`unlink()`); updates cache state to `VIRTUAL`.
  - Validates that catalog metadata, favorites, and playlists remain 100% intact after eviction.

---

## Phase 8: Intelligent Prefetch Manager
**Objective:** Pre-buffer upcoming tracks to provide instant track transitions without overloading the server or Soulseek peers.

- **Milestone 8.1:** Queue anticipation:
  - When Track $N$ starts playing, identify tracks $N+1$ through $N+3$ from the album or playlist.
- **Milestone 8.2:** Budgeted prefetch scheduler:
  - Max concurrent prefetch downloads (default: 1-2).
  - Prioritize immediate next track ($N+1$).
- **Milestone 8.3:** Cancellation & Skip handling:
  - If user skips to a different album/track, immediately cancel pending prefetch jobs.

---

## Phase 9: Hardening, Fault Recovery, & Observability
**Objective:** Ensure production-grade reliability, telemetry, and self-healing.

- **Milestone 9.1:** Structured logging (JSON format) with distributed `request_id` tracing.
- **Milestone 9.2:** Metrics exporter (`/metrics` Prometheus format):
  - Cache hit/miss rates, resolution times, transfer speeds, matcher confidence scores.
- **Milestone 9.3:** Restart recovery:
  - Clean up lingering `.part` files on startup.
  - Resume or cleanly re-enqueue interrupted downloads.
- **Milestone 9.4:** Concurrency & load testing:
  - Simulate multiple concurrent streams and simultaneous identical requests.

---

## Phase 10: Production Docker / Dokploy Rollout
**Objective:** Deploy Resono side-by-side with existing home server infrastructure.

- **Milestone 10.1:** `docker-compose.yml` for Dokploy:
  - `resono-gateway`, `slskr`, and persistent volumes.
- **Milestone 10.2:** Deploy Jellyfin plugin to `/var/lib/jellyfin/plugins/Resono`.
- **Milestone 10.3:** Side-by-side validation:
  - Confirm existing YouTube Music system remains fully functional while Resono is tested.
  - Validate mobile client playback on home LAN.
- **Milestone 10.4:** Retirement plan for legacy setup once Resono is fully validated.

---

## Phase 11: Secure External Access
**Objective:** Enable secure out-of-home streaming without exposing sensitive ports.

- **Milestone 11.1:** Evaluate tunnel topologies (Oracle Cloud Free Tier VM reverse proxy vs. Cloudflare Tunnel vs. Tailscale/WireGuard subnet router).
- **Milestone 11.2:** Configure reverse proxy with TLS termination and security headers.
- **Milestone 11.3:** Test mobile playback over 4G/5G cellular connections.
