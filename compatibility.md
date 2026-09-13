# Resono — Compatibility & Technical Audit

## 1. Jellyfin Server & Plugin Compatibility Audit

### 1.1 Target Jellyfin Versions & Framework Alignment
| Jellyfin Server Version | Release Status | Target .NET Framework | Plugin API Compatibility | Resono Support Status |
|---|---|---|---|---|
| **Jellyfin 10.8.z** | Legacy (Deprecated) | `.NET 6.0` | Old EF Core & API | Not Targeted |
| **Jellyfin 10.9.z** | Stable Production | `.NET 8.0` | Modern `MediaBrowser.Controller` | **Primary Target** |
| **Jellyfin 10.10.z** | Latest Stable | `.NET 8.0` / `.NET 9.0` | Minor schema enhancements | **Primary Target** |
| **Jellyfin 12.x** | In Development | `.NET 9.0`+ | Future modular API | Monitored / Forward-Compatible |

**Target Framework Recommendation:** Build plugin assemblies targeting `net8.0` with references to:
- `Jellyfin.Controller (>= 10.9.0)`
- `Jellyfin.Model (>= 10.9.0)`
- `MediaBrowser.Common (>= 10.9.0)`

This guarantees binary compatibility across Jellyfin 10.9.x through 10.10.x on Ubuntu Server 24.04.

---

## 2. Jellyfin Virtual Item Implementation Mechanisms

We audited four potential mechanisms to represent virtual, on-demand music tracks inside Jellyfin:

| Mechanism | Description | Pros | Cons / Risks | Architectural Verdict |
|---|---|---|---|---|
| **Option A: Custom `BaseItem` Subclass** | Subclassing `BaseItem` or `Audio` with custom virtual behaviors. | Direct control over item lifecycle in memory. | High risk of breaking Jellyfin's SQLite ORM schema during Jellyfin updates; crashes database migration. | ❌ **Rejected** |
| **Option B: Pure In-Memory `IItemProvider`** | Dynamic items injected only into search and collection views. | No files created on disk. | Items vanish upon server restart; cannot be added to standard user playlists or favorites reliably. | ❌ **Rejected** |
| **Option C: Virtual Media Source Provider (`IMediaSourceProvider`)** | Implements `IMediaSourceProvider` hooked to standard `Audio` items. | 100% native playback pipeline; zero hacking of Jellyfin database. | Requires a persistent metadata stub in the Jellyfin library so the item has a permanent GUID. | ⚠️ **Component used for streaming** |
| **Option D: Deterministic Virtual STRM + Dynamic Gateway** | Deterministic directory structure populated with tiny `.strm` pointers containing immutable Gateway playback URLs (`http://resono-gateway:8080/playback/{track_uuid}`). | **100% stable across restarts, updates, and clients.** Fully indexed by native Jellyfin scanner; native favorites, playlists, and user metadata work out of the box. Eviction deletes audio, never the `.strm` stub. | Requires managing minimal disk stubs (few bytes each). | ✅ **Selected Architecture** |

### Why Deterministic Virtual `.strm` + Dynamic Gateway is Superior:
1. **Permanent Identity:** A `.strm` file at `/music/Artists/Drake/Take Care/03 - Headlines [sp_123].strm` contains a single line:
   `http://resono-gateway:8080/playback/sp_123`
2. **Standard Client Compatibility:** All Jellyfin clients (Web, iOS, Android, Swiftfin, Finamp) recognize `.strm` files as native tracks.
3. **Audio Independence:** When audio is evicted from cache, the `.strm` file remains untouched. Playback simply triggers a cache-miss re-resolution in the Gateway.
4. **Resilience:** Jellyfin restarts do not clear the library or lose play history.

---

## 3. Playback & Streaming Compatibility Audit

### 3.1 Can slskr Preview / Mesh-Preview be used directly?
**Technical Audit Finding:** Relying purely on raw Soulseek mesh-preview for client playback is **unsuitable for production**:
- **Soulseek transfers are peer-to-peer over arbitrary residential internet connections.** Transfer rates fluctuate wildly.
- **HTTP Range Requests & Seeking:** Standard Jellyfin players issue `Range: bytes=X-Y` requests when the user seeks. Soulseek P2P transfers are linear downloads. If a client seeks to minute 2:30 (e.g. byte 30,000,000) while slskr has only received byte 2,000,000, a direct preview stream will stall, error out, or terminate playback.
- **Container Duration & Metadata:** Direct preview streams of incomplete FLAC or MP3 files lack correct container headers or accurate duration frames, causing clients to display `0:00 / 0:00` or lock up.

### 3.2 Resono's Dual-Phase Streaming Engine Solution:
To achieve Spotify-like playback responsiveness while maintaining rock-solid stability:
1. **Phase 1 (Instant Buffer Start):**
   - For an uncached track, the Gateway selects the best peer and initiates the download in slskr.
   - Once an initial safety buffer (e.g., 512 KB - 1 MB) is verified and audio headers are parsed, the Gateway begins streaming chunks to Jellyfin with full HTTP 206 `Content-Range` support.
2. **Phase 2 (Cache-Through Write):**
   - Audio data is written into an atomic staging file (`{track_uuid}.part`).
   - Once transfer completes, the file is validated (`ffprobe` / header check) and atomically renamed to `{track_uuid}.flac` in the verified cache.
   - Subsequent requests (and seeking) hit local NVMe cache with zero latency.

---

## 4. slskr API Compatibility & Lifecycle

### 4.1 slskr API Endpoints Used:
- `POST /api/v1/searches`: Initiates a distributed Soulseek search query (`searchText`, `searchTimeout`).
- `GET /api/v1/searches/{id}`: Polling/fetching discovered peer files (returns filename, size, bitrate, duration, peer username, queue length, upload slots free).
- `POST /api/v1/transfers/downloads`: Enqueues download for a specific peer and remote path.
- `GET /api/v1/transfers/downloads`: Queries download speed, bytes transferred, status (`Queued`, `Initializing`, `InProgress`, `Completed`, `Failed`).
- `DELETE /api/v1/transfers/downloads/{id}`: Cancels obsolete prefetch or abandoned jobs.
- `GET /api/v1/events` (WebSocket / SSE): Real-time transfer notifications.

---

## 5. Client Compatibility Matrix

| Client Platform | Direct Play Codecs | Seeking & Range Support | Favorites & Playlists | Notes |
|---|---|---|---|---|
| **Jellyfin Web** (Chrome/Firefox/Safari) | MP3, AAC, FLAC (Wasm/WebAudio) | Full Range Request Support | Native Server Sync | Plays seamlessly via Gateway HTTP stream. |
| **Jellyfin iOS / Swiftfin** | MP3, AAC, ALAC, FLAC | AVPlayer Range Request Support | Native Server Sync | Requires valid `Content-Type` header (`audio/flac`, `audio/mpeg`). |
| **Jellyfin Android / Finamp** | MP3, AAC, FLAC, OGG | ExoPlayer Chunked Buffer | Native Server Sync | Finamp supports offline download of Jellyfin tracks. |
| **Android Auto / Apple CarPlay** | MP3, AAC, FLAC | Standard media session | Synchronized via host app | Seamless background buffering. |

---

## 6. Catalog Provider Compatibility (Spotify / SpotAPI)

- **Official Spotify API:** Requires developer registration, OAuth credentials, and enforces strict rate limits.
- **SpotAPI / Public Token Scraping:** Allows anonymous querying of artist discographies, album tracklists, and metadata using public web client tokens.
- **Resilient Fallback Design:**
  - If Spotify public endpoints change or rate limit:
    - Cached metadata in `resono.db` serves known items.
    - Automatic fallback to **MusicBrainz API** and **Apple iTunes Search API** ensures the search and catalog pipeline never encounters a total outage.
