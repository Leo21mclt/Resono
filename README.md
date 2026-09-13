# Resono

Resono is a seamless virtual music streaming integration for Jellyfin that resolves, acquires, caches, and streams audio on-demand via decentralized networks while cataloging through Spotify metadata.

```text
Jellyfin Web / Mobile Clients
              │
              ▼
    Resono Jellyfin Plugin
              │ (HTTP 8080)
              ▼
        Resono Gateway
       ┌──────┴──────┐
       ▼             ▼
 Spotify Metadata   slskd (Same Compose Project)
    (SpotAPI)     (Soulseek Network)
```

## Architecture & Features

- **Metadata Provider**: Zero-credential Spotify catalog search, artist metadata, album tracklists, and artwork powered by `spotapi 1.2.8` (with optional iTunes fallback).
- **Decentralized Audio Backend**: Integrates directly with the existing `slskd` service in the same Docker Compose project.
- **Acquisition Engine**: Asynchronous state machine: `QUEUED` -> `SEARCHING` -> `MATCHED` -> `DOWNLOADING` -> `VALIDATING` -> `COMPLETED`.
- **Audio Validation**: Rigorous forensic inspection using `mutagen` for header integrity, minimum audio length, and metadata duration tolerance matching.
- **Atomic Operations**: Downloads written to `.part` files and moved atomically upon verification.
- **Cache & Streaming**: Persistent SQLite WAL source mappings, local LRU cache, and HTTP 206 byte-range streaming for instantaneous audio seeking.
- **Native Jellyfin Integration**: C# .NET 8 plugin (`Resono.Plugin.dll`) providing virtual search and direct media streaming without modifying Jellyfin's core.

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
- `ytmusic-stream-server`: Legacy discovery stream server (`8081:8081`)
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
- **Zero Disruption**: Existing services (`jellyfin`, `slskd`, `ytmusic-stream-server`) are preserved and continue operating normally.

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
