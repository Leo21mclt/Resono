# Resono 🎵

Resono is a high-fidelity virtual music streaming & discovery system for Jellyfin. It delivers instantaneous on-demand audio streaming, lossless background acquisition via Soulseek, synchronized karaoke lyrics, and rich metadata powered by Apple Music and Deezer catalogs.

```text
Jellyfin Web / Mobile Clients (Finamp, Discrete, Manet)
                       │
                       ▼
             Resono Jellyfin Plugin
                       │ (HTTP 8080)
                       ▼
                 Resono Gateway
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
Catalog Providers  Live Stream     Soulseek (slskd)
 (Apple / Deezer) (Instant Cache) (Lossless FLAC/MP3)
```

## Features

- **Multi-Provider Metadata**: Lightning-fast, zero-credential music catalog powered by Apple Music (iTunes API) and Deezer API with high-resolution portraits and album artwork (up to 1400x1400).
- **Pure Lossless & Hi-Fi Streaming**: Direct audio acquisition from the decentralized Soulseek network via `slskd`, cached on-disk and served via seekable HTTP 206 Partial Content streams.
- **Strict Anti-Cover & Match Scoring**: High-precision track duration matching (±4s) and automatic disqualification of covers, karaoke, remixes, live recordings, and locked peer shares.
- **Synchronized Karaoke Lyrics**: Millisecond-accurate synchronized lyrics powered by LrcLib rendered in real-time in Jellyfin Web and mobile apps.
- **Spotify-Like Artist Profiles**: Full artist view with top played songs, complete discography, albums, and related artists.
- **Virtual Discovery Playlists**: Configurable Top 50 Global, Top 50 Country (e.g. USA, Peru, Spain, Mexico, Argentina), and Trending charts injected directly into your library.
- **Forensic Audio Validation**: Inspection using `mutagen` for header integrity, bitrates, audio channels, and duration tolerance before caching.
- **Native Jellyfin Integration**: Zero core modifications. Installs via Jellyfin Plugin Repository (`manifest.json`) or standalone DLL.

---

## Repository Structure

```text
Resono/
├── resono-gateway/              # FastAPI Gateway service
│   ├── app/                     # Gateway application code
│   │   ├── catalog/             # Spotify / SpotAPI & iTunes metadata providers
│   │   ├── core/                # Acquisition, caching, streaming, and validation
│   │   ├── db/                  # SQLite WAL models and database sessions
│   │   ├── matcher/             # Exact audio matching & scoring algorithms
│   │   ├── slskd/               # slskd Soulseek daemon API client
│   │   ├── config.py            # Pydantic settings
│   │   └── main.py              # FastAPI endpoints & lifecycle
│   ├── tests/                   # Pytest test suite
│   ├── Dockerfile               # Production Python 3.12 container definition
│   ├── requirements.txt         # Gateway Python dependencies
│   └── pytest.ini               # Pytest configuration
├── resono-jellyfin-plugin/      # Jellyfin C# Plugin (.NET 8.0)
│   ├── Configuration/           # Plugin settings & web admin configuration page
│   ├── Providers/               # IMediaSourceProvider & IRemoteSearchProvider
│   ├── Plugin.cs                # Jellyfin plugin entrypoint
│   └── Resono.Plugin.csproj     # C# Project definition
├── docker-compose.yml           # Compose service definition (for existing stack)
├── .env.example                 # Production environment variable template
├── .dockerignore                # Docker build exclusions
├── .gitignore                   # Git repository exclusions
└── README.md                    # Project documentation
```

---

## Production Deployment (Dokploy)

The root `docker-compose.yml` integrates the entire production stack into a single Dokploy Compose project (`music-stack-jellyfinmediaserver-q8vogu`):

- `jellyfin`: Jellyfin media server (`8096:8096`)
- `slskd`: Soulseek acquisition daemon (`5030:5030`, `2234:2234`)
- `resono-gateway`: Resono virtual gateway and cache manager (`8080:8080`)

### Architecture & Coexistence Guarantees
- **No Duplicate slskd**: Resono communicates directly with the single production `slskd` container via internal Compose networking at `http://slskd:5030`.
- **Shared Volume Access**: The Gateway mounts the project's `slskd-downloads` volume **read-only**:
  ```yaml
  volumes:
    - slskd-downloads:/downloads:ro
  ```
- **Gateway Persistence**: The Gateway maintains its own isolated persistent database and audio cache:
  ```yaml
  volumes:
    - resono-data:/app/data
    - resono-cache:/app/cache
  ```
- **Acquisition Lifecycle**: `slskd` downloads audio into `slskd-downloads` (`/app/downloads`); Resono Gateway reads completed files from `/downloads:ro`, forensically validates audio integrity via `mutagen`, and atomically copies verified audio into `resono-cache`.
- **Zero Disruption**: Existing services (`jellyfin`, `slskd`) are preserved and continue operating normally.

### Configuration
In Dokploy (or your `.env` file):
- `RESONO_SLSKD_URL`: `http://slskd:5030`
- `SLSKD_SLSK_USERNAME`: Your Soulseek username
- `SLSKD_SLSK_PASSWORD`: Your Soulseek password
- `RESONO_PORT`: `8080` (default)

---

## Jellyfin Plugin Setup

1. Copy the compiled plugin DLL (`Resono.Plugin.dll` located in `resono-jellyfin-plugin/bin/Release/net8.0/` or built from source) to your Jellyfin server:
   ```bash
   mkdir -p /config/plugins/Resono
   cp Resono.Plugin.dll /config/plugins/Resono/
   ```
2. Restart your Jellyfin container:
   ```bash
   docker restart jellyfin
   ```
3. In Jellyfin Web UI, navigate to **Dashboard** -> **Plugins** -> **Resono**.
4. Configure the **Gateway URL** (e.g., `http://<gateway-ip>:8080` or `http://resono-gateway:8080`) and save.

---

## Running Tests Locally

```powershell
cd resono-gateway
.\.venv\Scripts\Activate.ps1
pytest
```
