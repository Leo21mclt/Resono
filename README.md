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
 Spotify Metadata   Existing Production slskd (Host)
    (SpotAPI)     (Soulseek Network)
```

## Architecture & Features

- **Metadata Provider**: Zero-credential Spotify catalog search, artist metadata, album tracklists, and artwork powered by `spotapi 1.2.8` (with optional iTunes fallback).
- **Decentralized Audio Backend**: Integrates with the existing production Soulseek daemon (`slskd`).
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
├── docker-compose.yml           # Production Compose stack (Dokploy compatible)
├── .env.example                 # Production environment variable template
├── .dockerignore                # Docker build exclusions
├── .gitignore                   # Git repository exclusions
└── README.md                    # Project documentation
```

---

## Deployment with Dokploy

This repository deploys the **Resono Gateway** as a standalone service via Docker Compose in [Dokploy](https://dokploy.com/).

### Production Architecture & slskd Integration
- **Existing slskd Daemon**: Resono does **not** deploy a duplicate Soulseek container. It integrates directly with your existing production `slskd` instance in the `music-stack-jellyfinmediaserver-q8vogu` stack.
- **Daemon API Communication**: The Gateway communicates with the host `slskd` API via `http://host.docker.internal:5030` (enabled on Linux using `extra_hosts: ["host.docker.internal:host-gateway"]`).
- **Download Filesystem Access**: The Gateway mounts the existing production `slskd` download volume as a **read-only bind mount**:
  - **Host Path**: `/var/lib/docker/volumes/music-stack-jellyfinmediaserver-q8vogu_slskd-downloads/_data`
  - **Container Mount**: `/downloads:ro`
- **Acquisition Lifecycle**: `slskd` downloads audio into its volume; Resono Gateway reads completed files from `/downloads`, validates audio integrity via `mutagen`, and atomically copies verified audio into its local cache (`resono-cache`).

### 1. In Dokploy Dashboard
1. Create a new **Compose** application pointing to your GitHub repository.
2. In the **Environment** tab, configure the environment variables based on `.env.example`:
   - `RESONO_SLSKD_URL`: `http://host.docker.internal:5030`
   - `RESONO_PORT`: `8080` (default)
3. Click **Deploy**.

### 2. Networking & Ports
- **Resono Gateway**: Port `8080` is published to the host (`8080:8080`) so your existing Jellyfin server can query virtual search and stream audio.

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
4. Configure the **Gateway URL** (e.g., `http://<your-gateway-host>:8080`) and save.

---

## Running Tests Locally

```powershell
cd resono-gateway
.\.venv\Scripts\Activate.ps1
pytest
```
