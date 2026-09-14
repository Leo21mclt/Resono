import re
import unicodedata

UNWANTED_TAGS = [
    "karaoke",
    "cover",
    "guitar cover",
    "piano cover",
    "backing track",
    "orchestral cover",
    "synthesia",
    "reaction",
    "parody",
    "8d audio",
    "tribute",
    "live",
    "remix",
    "radio edit",
    "instrumental",
    "acoustic",
    "sped up",
    "slowed",
    "nightcore",
    "demo",
    "re-recorded",
    "mashup",
    "unofficial",
    "bass boosted",
    "ringtone",
]

def normalize_text(text: str) -> str:
    """Normalize text by converting to lowercase, stripping accents, and removing punctuation."""
    if not text:
        return ""
    # Normalize unicode (NFD decomposition)
    text = unicodedata.normalize("NFD", text)
    text = "".join(c for c in text if unicodedata.category(c) != "Mn")
    text = text.lower()
    # Remove common file junk and bracketed info
    text = re.sub(r"[\(\[\{][^\)\]\}]*[\)\]\}]", " ", text)
    # Replace separators with spaces
    text = re.sub(r"[._\-\\/]+", " ", text)
    # Remove non-alphanumeric except whitespace
    text = re.sub(r"[^\w\s]", "", text)
    # Normalize multiple whitespace
    return re.sub(r"\s+", " ", text).strip()

def tokenize(text: str) -> set[str]:
    """Tokenize a normalized string into a set of distinct words."""
    norm = normalize_text(text)
    return set(norm.split())

def extract_track_number(filename: str) -> int | None:
    """Extract leading track number from filename (e.g., '03 - Song.mp3', '11 Drake - Controlla.flac')."""
    # Extract only the file basename without directory
    basename = filename.replace("\\", "/").rsplit("/", 1)[-1]
    match = re.match(r"^(?:cd\s*\d+[\s\-_]*)?(\d{1,3})(?:[\s\.\-_]+)", basename, re.IGNORECASE)
    if match:
        try:
            return int(match.group(1))
        except ValueError:
            pass
    return None

def has_unwanted_tags(candidate_text: str, canonical_title: str) -> str | None:
    """Check if candidate contains unwanted markers (karaoke, remix, etc.) unless requested in canonical title."""
    candidate_lower = candidate_text.lower()
    canonical_lower = canonical_title.lower()

    for tag in UNWANTED_TAGS:
        if tag in candidate_lower and tag not in canonical_lower:
            return tag
    return None
