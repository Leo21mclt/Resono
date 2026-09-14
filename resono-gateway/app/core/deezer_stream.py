import asyncio
import hashlib
import logging
import time
from pathlib import Path
import httpx
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

from app.config import settings

logger = logging.getLogger(__name__)

DEEZER_BLOWFISH_SECRET = b"g4el58wc0zvf9na1"
DEEZER_IV = b"\x00\x01\x02\x03\x04\x05\x06\x07"
DEEZER_GW_URL = "https://www.deezer.com/ajax/gw-light.php"
DEEZER_MEDIA_URL = "https://media.deezer.com/v1/get_url"
CHUNK_SIZE = 2048


def get_blowfish_key(track_id: str | int) -> bytes:
    """Derive 16-byte Blowfish CBC key from track ID and Deezer master secret."""
    md5_id = hashlib.md5(str(track_id).encode("utf-8")).hexdigest()
    return bytes([ord(md5_id[i]) ^ ord(md5_id[i + 16]) ^ DEEZER_BLOWFISH_SECRET[i] for i in range(16)])


class DeezerStreamer:
    """Manages authenticated Deezer CDN audio acquisition using ARL."""

    def __init__(self, arl: str | None = None):
        self.arl = arl or getattr(settings, "DEEZER_ARL", None)
        self._license_token: str | None = None
        self._user_token: str | None = None
        self._session_expires: float = 0.0
        self._session_lock = asyncio.Lock()

    def update_arl(self, new_arl: str):
        if new_arl and new_arl.strip() != (self.arl or ""):
            self.arl = new_arl.strip()
            self._license_token = None
            self._user_token = None
            self._session_expires = 0.0

    async def _ensure_session(self, client: httpx.AsyncClient) -> bool:
        """Authenticate session using ARL and obtain license_token."""
        current_arl = self.arl or getattr(settings, "DEEZER_ARL", None)
        if not current_arl:
            return False

        now = time.time()
        if self._license_token and now < self._session_expires:
            return True

        async with self._session_lock:
            if self._license_token and now < self._session_expires:
                return True

            try:
                logger.info("[DEEZER-ARL] Authenticating with Deezer session API...")
                resp = await client.post(
                    DEEZER_GW_URL,
                    params={
                        "api_version": "1.0",
                        "api_token": "null",
                        "input": "3",
                        "method": "deezer.getUserData"
                    },
                    cookies={"arl": current_arl.strip()},
                    headers={
                        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
                        "Accept-Language": "en-US,en;q=0.9",
                        "Content-Type": "application/json"
                    },
                    timeout=10.0
                )
                resp.raise_for_status()
                data = resp.json()

                results = data.get("results", {})
                self._user_token = results.get("checkForm")
                user_options = results.get("USER", {}).get("OPTIONS", {})
                self._license_token = user_options.get("license_token")

                if not self._license_token:
                    logger.warning("[DEEZER-ARL] No license_token returned from Deezer. Check ARL validity.")
                    return False

                self._session_expires = now + 1800
                logger.info("[DEEZER-ARL] Authenticated successfully with Deezer CDN.")
                return True
            except Exception as e:
                logger.error(f"[DEEZER-ARL] Authentication failed: {e}")
                return False

    async def get_track_token(self, track_id: str | int, client: httpx.AsyncClient) -> str | None:
        """Fetch track_token for a given Deezer track ID."""
        try:
            resp = await client.get(
                f"https://api.deezer.com/track/{track_id}",
                headers={"User-Agent": "Mozilla/5.0"},
                timeout=8.0
            )
            resp.raise_for_status()
            data = resp.json()
            return data.get("track_token")
        except Exception as e:
            logger.warning(f"[DEEZER-ARL] Failed to get track_token for {track_id}: {e}")
            return None

    async def get_media_stream_url(
        self,
        track_token: str,
        client: httpx.AsyncClient
    ) -> tuple[str, str, str] | None:
        """Query media.deezer.com for direct CDN audio URL. Returns (url, format, cipher)."""
        if not self._license_token:
            return None

        formats_to_try = [
            {"cipher": "BF_CBC_STRIPE", "format": "MP3_320"},
            {"cipher": "BF_CBC_STRIPE", "format": "MP3_128"},
            {"cipher": "BF_CBC_STRIPE", "format": "FLAC"},
        ]

        payload = {
            "license_token": self._license_token,
            "media": [
                {
                    "type": "FULL",
                    "formats": formats_to_try
                }
            ],
            "track_tokens": [track_token]
        }

        try:
            resp = await client.post(
                DEEZER_MEDIA_URL,
                json=payload,
                headers={"User-Agent": "Mozilla/5.0"},
                timeout=8.0
            )
            resp.raise_for_status()
            res_data = resp.json()

            data_list = res_data.get("data", [])
            if not data_list:
                return None

            media_item = data_list[0].get("media", [])
            if not media_item:
                return None

            chosen = media_item[0]
            sources = chosen.get("sources", [])
            if not sources:
                return None

            stream_url = sources[0].get("url")
            chosen_format = chosen.get("format", "MP3_320")
            raw_cipher = chosen.get("cipher", "BF_CBC_STRIPE")
            if isinstance(raw_cipher, dict):
                chosen_cipher = raw_cipher.get("type", "BF_CBC_STRIPE")
            else:
                chosen_cipher = str(raw_cipher)

            return stream_url, chosen_format, chosen_cipher
        except Exception as e:
            logger.error(f"[DEEZER-ARL] Failed to resolve media stream URL: {e}")
            return None

    async def download_track(
        self,
        track_id: str | int,
        target_path: Path,
        arl_override: str | None = None
    ) -> bool:
        """Downloads and decrypts Deezer track directly to target_path in MP3/FLAC."""
        if arl_override:
            self.update_arl(arl_override)

        current_arl = self.arl or getattr(settings, "DEEZER_ARL", None)
        if not current_arl:
            logger.debug("[DEEZER-ARL] No ARL configured. Skipping Deezer CDN acquire.")
            return False

        async with httpx.AsyncClient(follow_redirects=True, timeout=30.0) as client:
            ok = await self._ensure_session(client)
            if not ok:
                return False

            track_token = await self.get_track_token(track_id, client)
            if not track_token:
                try:
                    song_resp = await client.post(
                        DEEZER_GW_URL,
                        params={
                            "api_version": "1.0",
                            "api_token": self._user_token or "null",
                            "input": "3",
                            "method": "song.getData"
                        },
                        json={"sng_id": str(track_id)},
                        cookies={"arl": current_arl.strip()},
                        timeout=8.0
                    )
                    song_data = song_resp.json()
                    track_token = song_data.get("results", {}).get("TRACK_TOKEN")
                except Exception:
                    pass

            if not track_token:
                logger.warning(f"[DEEZER-ARL] Could not obtain track_token for track {track_id}")
                return False

            media_info = await self.get_media_stream_url(track_token, client)
            if not media_info:
                logger.warning(f"[DEEZER-ARL] Could not obtain media stream URL for track {track_id}")
                return False

            stream_url, fmt, cipher_type = media_info
            logger.info(f"[DEEZER-ARL] Streaming track {track_id} ({fmt}, {cipher_type}) from CDN...")

            target_path.parent.mkdir(parents=True, exist_ok=True)
            temp_path = target_path.with_suffix(".tmp")

            cipher_str = cipher_type.get("type", "BF_CBC_STRIPE") if isinstance(cipher_type, dict) else str(cipher_type)
            is_encrypted = "BF" in cipher_str.upper() or "STRIPE" in cipher_str.upper()

            try:
                bf_key = get_blowfish_key(track_id)
                start_time = time.time()
                total_bytes = 0

                async with client.stream("GET", stream_url) as stream_resp:
                    stream_resp.raise_for_status()

                    with open(temp_path, "wb") as f_out:
                        buffer = bytearray()
                        chunk_index = 0

                        async for raw_bytes in stream_resp.aiter_bytes(chunk_size=CHUNK_SIZE):
                            buffer.extend(raw_bytes)

                            while len(buffer) >= CHUNK_SIZE:
                                block = bytes(buffer[:CHUNK_SIZE])
                                del buffer[:CHUNK_SIZE]

                                if is_encrypted and chunk_index % 3 == 0:
                                    cipher = Cipher(algorithms.Blowfish(bf_key), modes.CBC(DEEZER_IV))
                                    decryptor = cipher.decryptor()
                                    decrypted = decryptor.update(block)
                                    f_out.write(decrypted)
                                else:
                                    f_out.write(block)

                                total_bytes += CHUNK_SIZE
                                chunk_index += 1

                        if buffer:
                            f_out.write(buffer)
                            total_bytes += len(buffer)

                elapsed = time.time() - start_time
                mb = total_bytes / (1024 * 1024)
                rate = mb / max(elapsed, 0.001)
                logger.info(f"[DEEZER-ARL] Acquired {mb:.2f} MB in {elapsed:.2f}s ({rate:.2f} MB/s) -> {target_path.name}")

                if temp_path.exists() and is_valid_audio_file(temp_path):
                    temp_path.replace(target_path)
                    logger.info(f"[DEEZER-ARL] Verified valid audio stream for track {track_id} -> {target_path.name}")
                    return True
                else:
                    logger.error(f"[DEEZER-ARL] Track {track_id} failed audio validation (corrupt or encrypted)")
                    if temp_path.exists():
                        temp_path.unlink()
                    return False
            except Exception as e:
                logger.error(f"[DEEZER-ARL] Failed streaming/decrypting track {track_id}: {e}")
                if temp_path.exists():
                    temp_path.unlink()
                return False


def is_valid_audio_file(path: Path) -> bool:
    """Verify that file exists, has sufficient size, and begins with a valid audio sync frame or container tag."""
    if not path.exists() or path.stat().st_size < 10000:
        return False
    try:
        with open(path, "rb") as f:
            header = f.read(16)
        if not header or len(header) < 2:
            return False
        # ID3 header, FLAC magic, RIFF (WAV), OggS
        if header.startswith(b"ID3") or header.startswith(b"fLaC") or header.startswith(b"RIFF") or header.startswith(b"OggS"):
            return True
        # MP3 sync frame (0xFF 0xFB, 0xFA, 0xF3, 0xF2, etc.)
        if header[0] == 0xFF and (header[1] & 0xE0) == 0xE0:
            return True
        return False
    except Exception:
        return False


deezer_streamer = DeezerStreamer()
