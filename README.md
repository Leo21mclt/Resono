<div align="center">

# 🎵 Resono

### The Self-Hosted Virtual Music Streaming Engine for Jellyfin

[![GitHub Release](https://img.shields.io/github/v/release/Leo21mclt/Resono?style=for-the-badge&color=blue)](https://github.com/Leo21mclt/Resono/releases)
[![Docker Image](https://img.shields.io/badge/Docker-GHCR-2496ED?style=for-the-badge&logo=docker&logoColor=white)](https://github.com/Leo21mclt/Resono/pkgs/container/resono)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9%20%7C%2010.10-00A4DC?style=for-the-badge&logo=jellyfin&logoColor=white)](https://jellyfin.org/)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Python 3.12](https://img.shields.io/badge/Python-3.12-3776AB?style=for-the-badge&logo=python&logoColor=white)](https://www.python.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=for-the-badge)](LICENSE)

<p align="center">
  <b>Stream millions of songs instantly inside Jellyfin without downloading your library beforehand.</b><br>
  Rich metadata • Native iOS 18+ artwork • Millisecond-synced lyrics • Lossless FLAC & Hi-Fi audio
</p>

[Features](#-key-features) •
[Compatibility](#-client-compatibility-matrix) •
[Installation](#-installation-options) •
[Architecture](#-architecture) •
[Configuration](#-configuration-reference) •
[Troubleshooting](#-troubleshooting--faq)

</div>

---

## 🌟 Overview

**Resono** transforms your Jellyfin media server into an on-demand virtual music streaming service (comparable to Spotify or Apple Music). It combines the catalog and discovery powers of public metadata APIs (Apple Music & Deezer) with instantaneous streaming and smart on-disk LRU caching.

Unlike traditional Jellyfin libraries that require gigabytes or terabytes of locally pre-downloaded music files, Resono presents tracks, albums, and artists virtually. Audio is acquired and cached on the fly the moment you press play, with full support for native iOS/Android music players.

---

## ✨ Key Features

- ⚡ **Instant On-Demand Virtual Streaming**: Play any song, album, or artist immediately with zero wait time.
- 🎨 **Flawless Client Artwork**: Native artwork routing with full compatibility for strict iOS 18+ clients (including **Discrete**, **Finamp**, and **Manet**).
- 🎤 **Synchronized Karaoke Lyrics**: Millisecond-accurate synchronized lyrics powered by LRCLIB and Deezer, rendered seamlessly in Jellyfin Web and mobile apps.
- 💿 **Comprehensive Artist Discographies**: Full artist profiles featuring top played songs, complete discography, chronological albums, EPs, and singles.
- 💎 **Hi-Fi & Lossless Audio**: Stream up to 320kbps MP3 or lossless 16-bit / 44.1kHz FLAC audio.
- 🗄️ **Smart LRU Cache Management**: Automatically manages local storage with configurable limits (e.g. 25 GB limit, minimum 10 GB free disk protection), evicting least-recently-played tracks automatically.
- 🛡️ **Zero Core Jellyfin Modifications**: Built as a clean C# ActionFilter plugin and a standalone FastAPI gateway.

---

## 📱 Client Compatibility Matrix

Resono has been rigorously tested against major Jellyfin client applications:

| Client | Platform | Status | Features Verified | Notes |
|:---|:---|:---:|:---|:---|
| **Discrete** | iOS 18+ | 🟢 Full Support | High-res covers, Now Playing, Discographies, Lyrics, Search hints | Full virtual item compatibility |
| **Finamp** | iOS & Android | 🟢 Full Support | Instant playback, artwork, offline caching, artist view | Excellent mobile daily driver |
| **Manet** | iOS / iPadOS | 🟢 Full Support | Fluid playback, album browser, full-screen player | Native Swift experience |
| **Feishin** | macOS / Windows / Linux | 🟢 Full Support | Desktop Hi-Fi listening, synced lyrics, full queue control | Best desktop experience |
| **Jellyfin Web** | All Browsers | 🟢 Full Support | Playback, dashboard management, repository install | Official client |
| **Swiftfin** | iOS / tvOS | 🟢 Full Support | Apple TV and mobile playback | Full audio streaming |

---

## 🚀 Installation Options

Choose the deployment method that best fits your infrastructure:

```text
                      ┌─────────────────────────────────────────┐
                      │    Which setup fits you best?           │
                      └────────────────────┬────────────────────┘
                                           │
                ┌──────────────────────────┴──────────────────────────┐
                ▼                                                     ▼
     [ Option 1: Existing Jellyfin ]                        [ Option 2: Turnkey All-in-One ]
     "I already have a running server"                      "I want a complete fresh stack"
        • Run Resono Gateway container                         • 1-command installer script
        • Install plugin via Repository                        • Jellyfin + Gateway + Auto-Plugin
        • Zero disruption to current setup                     • Pre-configured volumes and ports
```

---

### Option 1: Existing Jellyfin Server (Standalone)

If you already have a Jellyfin server running on Docker, Unraid, TrueNAS, or bare metal, you only need to run the Resono Gateway and add the plugin.

#### Step 1: Deploy Resono Gateway

Deploy the lightweight Gateway container alongside your existing Jellyfin instance:

**Via Docker Compose (`docker-compose.standalone.yml`):**
```bash
# Download standalone compose file
curl -fsSL https://raw.githubusercontent.com/Leo21mclt/Resono/main/docker-compose.standalone.yml -o docker-compose.yml

# Start the gateway
docker compose up -d
```

**Or via Docker Run:**
```bash
docker run -d \
  --name resono-gateway \
  --restart unless-stopped \
  -p 8080:8080 \
  -v resono-data:/app/data \
  -v resono-cache:/app/cache \
  ghcr.io/leo21mclt/resono:latest
```

#### Step 2: Install the Resono Plugin in Jellyfin

You can install the plugin directly from the Jellyfin Dashboard (Recommended) or by downloading the DLL:

##### Method A: 1-Click Plugin Repository (Recommended)
1. In Jellyfin Web, go to **Dashboard** ➔ **Plugins** ➔ **Repositories** ➔ Click **"+"**.
2. Enter:
   - **Repository Name**: `Resono`
   - **Repository URL**: `https://raw.githubusercontent.com/Leo21mclt/Resono/main/manifest.json`
3. Click **Save**, then switch to the **Catalog** tab.
4. Select **Resono Virtual Music** and click **Install**.
5. Restart your Jellyfin server.

##### Method B: Manual DLL Installation
1. Download [`Resono.Plugin.dll`](https://raw.githubusercontent.com/Leo21mclt/Resono/main/resono-gateway/plugin/Resono.Plugin.dll).
2. Place the DLL inside your Jellyfin plugins directory:
   ```bash
   mkdir -p /path/to/jellyfin/config/plugins/Resono
   cp Resono.Plugin.dll /path/to/jellyfin/config/plugins/Resono/
   ```
3. Restart your Jellyfin server:
   ```bash
   docker restart jellyfin
   ```

#### Step 3: Configure the Gateway URL
1. In Jellyfin Web, open **Dashboard** ➔ **Plugins** ➔ **Resono**.
2. Enter your **Gateway URL**:
   - If in the same Docker network: `http://resono-gateway:8080`
   - If on the same host: `http://<HOST_IP>:8080`
3. Click **Save**.
4. (Optional) Create or designate a **Music** library in Jellyfin (**Dashboard** ➔ **Libraries** ➔ **Add Media Library** ➔ Content type: **Music**).

---

### Option 2: Turnkey All-in-One Full Stack (Fresh Install)

For a brand-new, completely automated deployment including Jellyfin, Resono Gateway, and the auto-bootstrapped plugin.

#### Method A: Automated One-Line Installer (Linux / macOS)

Run the turnkey installation script:

```bash
curl -fsSL https://raw.githubusercontent.com/Leo21mclt/Resono/main/install.sh | bash
```

The script will:
- Check Docker & Compose prerequisites.
- Create all configuration, cache, and media folders.
- Download `docker-compose.yml` and `.env`.
- Bootstrap `Resono.Plugin.dll` directly into Jellyfin's plugin volume.
- Start all services with a single confirmation.

#### Method B: Manual Docker Compose

1. Clone or download the repository:
   ```bash
   git clone https://github.com/Leo21mclt/Resono.git
   cd Resono
   ```

2. (Optional) Copy and adjust environment variables:
   ```bash
   cp .env.example .env
   ```

3. Start the entire stack:
   ```bash
   docker compose up -d
   ```

The init container (`resono-plugin-init`) automatically installs the plugin into Jellyfin before Jellyfin boots!

#### Step-by-Step Initial Setup:
1. Open **`http://<YOUR-SERVER-IP>:8096`** in your browser.
2. Complete the initial Jellyfin setup wizard (create your admin user).
3. Add a Music library pointing to `/media/music`.
4. Navigate to **Dashboard** ➔ **Plugins** ➔ **Resono**.
5. Verify the Gateway URL is set to `http://resono-gateway:8080` and click **Save**.
6. Connect any music client (Discrete, Finamp, Manet, Feishin) and enjoy!

---

## 🏗️ Architecture

```text
┌───────────────────────────────────────────────────────────┐
│                     Client Ecosystem                      │
│   Discrete (iOS 18+) • Finamp • Manet • Feishin • Web     │
└─────────────────────────────┬─────────────────────────────┘
                              │ HTTP / REST API
                              ▼
┌───────────────────────────────────────────────────────────┐
│                      Jellyfin Server                      │
│  ┌─────────────────────────────────────────────────────┐  │
│  │               Resono C# Action Filter               │  │
│  │   • Intercepts item, search & artwork requests      │  │
│  │   • Enriches virtual items with metadata & covers   │  │
│  │   • Injects direct streaming URLs to Gateway        │  │
│  └──────────────────────────┬──────────────────────────┘  │
└─────────────────────────────┼─────────────────────────────┘
                              │ HTTP (Port 8080)
                              ▼
┌───────────────────────────────────────────────────────────┐
│                   Resono Gateway (FastAPI)                │
│  ┌─────────────────────────────────────────────────────┐  │
│  │  Catalog / Metadata     Streaming Engine    LRU     │  │
│  │  (Apple / Deezer)       (Audio Streamer)   Cache    │  │
│  └──────────────┬───────────────────┬────────────┬─────┘  │
└─────────────────┼───────────────────┼────────────┼────────┘
                  ▼                   ▼            ▼
          [Public Catalogs]      [LRCLIB]     [Audio Cache]
          Apple Music / Deezer   Lyrics API   (Fast SSD / Disk)
```

---

## ⚙️ Configuration Reference

All settings can be configured via environment variables or a `.env` file:

| Variable | Default | Description |
|:---|:---:|:---|
| `RESONO_PORT` | `8080` | Port for the Resono Gateway API |
| `RESONO_PRIMARY_PLAYBACK_SOURCE` | `deezer` | Primary audio streaming engine (`deezer`, `slskd`, `none`) |
| `RESONO_DEEZER_ARL` | *(empty)* | Optional Deezer ARL token for 320kbps MP3 and Lossless FLAC |
| `RESONO_MAX_CACHE_SIZE_GB` | `25.0` | Maximum disk size for cached audio tracks before LRU eviction |
| `RESONO_MIN_FREE_DISK_GB` | `10.0` | Minimum free disk space preserved on host storage |
| `RESONO_MATCHER_CONFIDENCE_THRESHOLD` | `0.80` | Audio matching confidence score (0.0 to 1.0) |
| `RESONO_MAX_DURATION_DIFFERENCE_MS` | `6000` | Max duration difference (ms) allowed between catalog and stream |
| `SPOTIFY_CLIENT_ID` | *(empty)* | Optional Spotify Web API Client ID |
| `SPOTIFY_CLIENT_SECRET` | *(empty)* | Optional Spotify Web API Client Secret |
| `JELLYFIN_CONFIG_DIR` | `./config/jellyfin` | Jellyfin configuration directory (Turnkey stack) |
| `JELLYFIN_MEDIA_DIR` | `./media/music` | Jellyfin music media directory (Turnkey stack) |

### Obtaining a Deezer ARL Token (Optional for Hi-Fi / Lossless)
1. Open [Deezer.com](https://www.deezer.com) in your browser and log in.
2. Open Developer Tools (`F12` or `Right Click` ➔ `Inspect`).
3. Go to **Application** (Chrome/Edge) or **Storage** (Firefox) ➔ **Cookies** ➔ `https://www.deezer.com`.
4. Copy the value of the cookie named **`arl`**.
5. Paste it into `RESONO_DEEZER_ARL` in your `.env` file.

---

## ❓ Troubleshooting & FAQ

### Why did iOS clients (like Discrete) previously not show artwork for virtual tracks?
iOS 18 music clients such as Discrete employ native Swift CoreData models that inspect the `LocationType` field in Jellyfin's item DTOs. When an item had `LocationType: "Virtual"`, the client skipped requesting artwork and local caching. Resono's action filters present virtual items with `LocationType: "FileSystem"` at the API boundary, providing full artwork rendering without creating physical files on disk.

### How do I check if Resono Gateway is running healthy?
Visit `http://<gateway-ip>:8080/health` in your browser or run:
```bash
curl -f http://localhost:8080/health
# Response: {"status":"healthy"}
```

### How can I view real-time logs?
```bash
# View Gateway logs:
docker logs -f resono-gateway

# View Jellyfin logs:
docker logs -f jellyfin
```

---

## 🛠️ Development & Building from Source

### Building the Jellyfin Plugin (.NET 8.0)
```bash
cd resono-jellyfin-plugin
dotnet build -c Release -o dist
```

### Running Gateway Tests (Python 3.12)
```bash
cd resono-gateway
python -m venv .venv
# On Windows:
.\.venv\Scripts\Activate.ps1
# On Linux/macOS:
source .venv/bin/activate

pip install -r requirements.txt
pytest
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

