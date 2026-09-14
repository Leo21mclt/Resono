# Resono — Environment & Infrastructure Specification

## 1. Target Production Environment (Home Server)

### 1.1 Hardware Profile
- **Model:** HP ProDesk 600 G5 SFF (Small Form Factor)
- **CPU:** Intel® Core™ i5-8500 (6 Cores, 6 Threads @ 3.00 GHz base, 4.10 GHz Turbo, 9 MB Cache)
- **Architecture:** x86_64 (Intel 64)
- **Integrated GPU:** Intel UHD Graphics 630 (QuickSync available for transcoding if needed)
- **Memory:** 16 GB - 32 GB DDR4-2666 MHz
- **Primary Storage:** NVMe M.2 SSD (System OS, Docker Volumes, Database, Hot Audio Cache)
- **Secondary Storage (Optional):** SATA SSD / HDD (Warm & Long-term audio storage)

### 1.2 Operating System & Platform Stack
- **Operating System:** Ubuntu Server 24.04 LTS (Noble Numbat)
- **Kernel:** Linux 6.8+ generic x86_64
- **Container Engine:** Docker Engine (v26.0+) with Docker Compose v2
- **PaaS / Server Orchestrator:** Dokploy (self-hosted PaaS on top of Docker / Traefik)
- **Process Supervision:** Systemd + Docker restart policies (`restart: unless-stopped`)

---

## 2. Existing Workloads & Side-by-Side Isolation

The home server currently runs:
- **Jellyfin Server:** Main media hub (`8096:8096`).
- **slskd:** Soulseek daemon (`5030:5030`).

### Isolation & Rollback Guarantees:
1. **Zero Disruption to Media Server:** Resono runs in its own containerized gateway and uses dedicated persistent data volumes (`resono-data`, `resono-cache`). Existing services (`jellyfin`, `slskd`) remain completely untouched.
2. **Safe Rollback:** Removing the Resono plugin `.dll` immediately restores Jellyfin to its previous state.

---

## 3. Storage Layout & Volume Mounts

```
/var/lib/resono/
├── data/                  # SQLite Database (resono.db, WAL, SHM) & config files
│   ├── resono.db
│   └── config.yaml
├── cache/                 # Audio cache root (fast NVMe storage)
│   ├── incoming/          # In-flight active downloads from slskr (.part files)
│   ├── audio/             # Verified, atomic playable files ({track_uuid}.flac / .mp3)
│   └── artwork/           # Cached Spotify/MusicBrainz album and artist cover images
└── logs/                  # Rotated JSON/structured application logs

/var/lib/slskr/
├── config/                # slskr application settings, credentials, port bindings
└── downloads/             # slskr internal download staging
```

### Docker Volume Mapping Table:
| Host Directory | Container Path | Purpose | Permissions |
|---|---|---|---|
| `/var/lib/resono/data` | `/app/data` | SQLite DB, state | `rw` |
| `/var/lib/resono/cache` | `/app/cache` | Audio & artwork cache | `rw` |
| `/var/lib/slskr/config` | `/app/config` | slskr config | `rw` |
| `/var/lib/slskr/downloads` | `/app/downloads` | Shared download staging | `rw` |
| `/var/lib/jellyfin/plugins/Resono` | `/jellyfin/plugins/Resono` | Compiled C# Plugin `.dll` | `ro` |

---

## 4. Network Topology & Security Boundaries

```
[ Internet ]
     │
     │  (Future Phase 11: Reverse Proxy / Secure Tunnel)
     ▼
[ Oracle Cloud Free Tier VM (Gateway) ]
     │
     │  WireGuard Tunnel / Cloudflare Tunnel
     ▼
┌────────────────────────────────────────────────────────┐
│ HP ProDesk 600 G5 (Ubuntu Server 24.04 LTS)            │
│                                                        │
│  Dokploy / Traefik                                     │
│  └── 80/443 (HTTPS Web, Jellyfin Client Traffic)       │
│                                                        │
│  Docker Bridge Network: 'resono-internal'              │
│  ┌─────────────────┐       ┌─────────────────┐         │
│  │ Jellyfin Server │<----->│ Resono Gateway  │         │
│  │ (Port 8096)     │  HTTP │ (Port 8080)     │         │
│  └─────────────────┘       └────────┬────────┘         │
│                                     │ HTTP REST        │
│                                     ▼                  │
│                            ┌─────────────────┐         │
│                            │   slskr Daemon  │         │
│                            │   (Port 5030)   │         │
│                            └────────┬────────┘         │
│                                     │ Outbound TCP     │
└─────────────────────────────────────┼──────────────────┘
                                      ▼
                           [ Soulseek P2P Network ]
```

### Critical Security Boundaries:
- **Zero Public Exposure of slskr:** The slskr web UI/API (port 5030) is bound **strictly** to the internal Docker bridge network (`resono-internal`) or `127.0.0.1`. It must NEVER be mapped to `0.0.0.0` on the public host interface.
- **Resono Gateway Authentication:** Internal communications between the Jellyfin plugin and the Resono Gateway are authenticated using a pre-shared internal bearer token (`RESONO_API_KEY`).
- **No Spotify Credential Requirement:** Catalog browsing operates without storing user Spotify credentials, avoiding risk of account bans or credential leakage.

---

## 5. Development Workstation Environment

- **Host OS:** Windows 11 Pro (Local Development & Orchestration)
- **Languages & SDKs Available:**
  - Python 3.12.10 (Local test runner, Gateway service development)
  - Docker Desktop 28.3.2 (Containerized .NET 8 builds, integration test runners)
  - Git 2.52.0
- **Build Strategy:** Multi-stage Docker builds for the .NET 8 C# Jellyfin plugin (avoiding local .NET SDK version conflicts on developer workstations).
