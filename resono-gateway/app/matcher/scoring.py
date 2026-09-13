import logging
from pydantic import BaseModel
from app.core.models import CanonicalTrack, AudioCandidate
from app.matcher.normalizer import normalize_text, tokenize, extract_track_number, has_unwanted_tags

logger = logging.getLogger("resono.matcher")

class MatchScoreBreakdown(BaseModel):
    artist_score: float = 0.0
    title_score: float = 0.0
    duration_score: float = 0.0
    album_score: float = 0.0
    quality_score: float = 0.0
    track_number_bonus: float = 0.0
    penalties: list[str] = []
    total_score: float = 0.0
    rejected: bool = False
    rejection_reason: str | None = None

class ScoredCandidate(BaseModel):
    candidate: AudioCandidate
    score: MatchScoreBreakdown

def token_similarity(set_a: set[str], set_b: set[str]) -> float:
    """Jaccard similarity between two token sets."""
    if not set_a or not set_b:
        return 0.0
    intersection = len(set_a.intersection(set_b))
    union = len(set_a.union(set_b))
    return intersection / union if union > 0 else 0.0

def score_duration(candidate_seconds: int | None, target_duration_ms: int) -> tuple[float, bool]:
    """Score duration match. Returns (score, is_disqualified)."""
    if candidate_seconds is None or candidate_seconds <= 0:
        # Unknown duration: neutral score, small uncertainty penalty
        return 0.5, False

    target_seconds = round(target_duration_ms / 1000)
    diff = abs(candidate_seconds - target_seconds)

    if diff <= 2:
        return 1.0, False
    elif diff <= 4:
        return 0.8, False
    elif diff <= 6:
        return 0.4, False
    else:
        # Over 6 seconds difference for a studio song is an automatic mismatch
        return 0.0, True

class ExactRecordingMatcher:
    """
    Backend-neutral heuristic scoring engine.
    Evaluates AudioCandidate records against a CanonicalTrack contract.
    """
    def __init__(self, confidence_threshold: float = 0.80):
        self.threshold = confidence_threshold

    def score_candidate(self, candidate: AudioCandidate, track: CanonicalTrack) -> ScoredCandidate:
        breakdown = MatchScoreBreakdown()
        full_path = candidate.remote_path.replace("\\", "/")
        path_parts = full_path.split("/")
        basename = candidate.filename or (path_parts[-1] if path_parts else full_path)

        # 1. Unwanted tag rejection check
        unwanted_tag = has_unwanted_tags(full_path, track.title)
        if unwanted_tag:
            breakdown.rejected = True
            breakdown.rejection_reason = f"Contains unwanted tag '{unwanted_tag}'"
            return ScoredCandidate(candidate=candidate, score=breakdown)

        # 2. Duration check
        cand_sec = candidate.duration_sec
        dur_score, dur_disqualified = score_duration(cand_sec, track.duration_ms)
        breakdown.duration_score = dur_score
        if dur_disqualified:
            breakdown.rejected = True
            target_sec = round(track.duration_ms / 1000)
            breakdown.rejection_reason = f"Duration mismatch: got {cand_sec}s, expected {target_sec}s"
            return ScoredCandidate(candidate=candidate, score=breakdown)

        # 3. Artist similarity
        track_artist_tokens = tokenize(track.artist_name)
        file_tokens = tokenize(full_path)
        artist_sim = token_similarity(track_artist_tokens, file_tokens)
        if track_artist_tokens.issubset(file_tokens):
            artist_sim = 1.0
        breakdown.artist_score = artist_sim

        # 4. Title similarity
        track_title_tokens = tokenize(track.title)
        basename_tokens = tokenize(basename)
        title_sim = token_similarity(track_title_tokens, basename_tokens)
        if track_title_tokens.issubset(basename_tokens):
            title_sim = 1.0
        breakdown.title_score = title_sim

        # 5. Album similarity
        if track.album_title:
            album_tokens = tokenize(track.album_title)
            parent_dir = candidate.folder or (path_parts[-2] if len(path_parts) >= 2 else "")
            parent_tokens = tokenize(parent_dir)
            if album_tokens.issubset(parent_tokens) or album_tokens.issubset(file_tokens):
                breakdown.album_score = 1.0
            else:
                breakdown.album_score = token_similarity(album_tokens, file_tokens)
        else:
            breakdown.album_score = 0.5

        # 6. Track number bonus
        extracted_no = extract_track_number(basename)
        if extracted_no is not None and track.track_number is not None:
            if extracted_no == track.track_number:
                breakdown.track_number_bonus = 0.10

        # 7. Quality & Availability score
        q_score = 0.0
        codec_lower = candidate.codec.lower()
        if codec_lower == "flac" or codec_lower == "alac":
            q_score += 0.08
        elif candidate.bitrate and candidate.bitrate >= 320:
            q_score += 0.06
        elif candidate.bitrate and candidate.bitrate >= 256:
            q_score += 0.04

        if candidate.slots_free and candidate.queue_length == 0:
            q_score += 0.04
        elif candidate.queue_length > 5:
            q_score -= 0.05

        breakdown.quality_score = max(0.0, min(0.12, q_score))

        # 8. Compute weighted composite score
        # Weights: Artist 0.25, Title 0.35, Duration 0.25, Album 0.10, Quality/Bonus 0.05 + bonuses
        total = (
            (breakdown.artist_score * 0.25) +
            (breakdown.title_score * 0.35) +
            (breakdown.duration_score * 0.25) +
            (breakdown.album_score * 0.10) +
            breakdown.quality_score +
            breakdown.track_number_bonus
        )

        breakdown.total_score = round(min(1.0, max(0.0, total)), 3)

        if breakdown.total_score < self.threshold:
            breakdown.rejected = True
            breakdown.rejection_reason = f"Score {breakdown.total_score:.2f} below threshold {self.threshold:.2f}"

        return ScoredCandidate(candidate=candidate, score=breakdown)

    @staticmethod
    def candidate_rank_key(s: ScoredCandidate):
        c = s.candidate
        # 1. Immediate availability: free upload slot AND empty queue is top priority
        immediate = 1 if (c.slots_free and c.queue_length == 0) else 0
        # 2. Total match score (FLAC / high bitrate / exact tags)
        score = s.score.total_score
        # 3. Penalize long queues
        queue_penalty = -c.queue_length if c.queue_length < 50 else -50
        # 4. Upload speed
        speed = c.upload_speed or 0
        return (immediate, score, queue_penalty, speed)

    def find_ranked_matches(self, candidates: list[AudioCandidate], track: CanonicalTrack) -> list[ScoredCandidate]:
        scored = [self.score_candidate(c, track) for c in candidates]
        valid = [s for s in scored if not s.score.rejected]
        valid.sort(key=self.candidate_rank_key, reverse=True)
        return valid

    def find_best_match(self, candidates: list[AudioCandidate], track: CanonicalTrack) -> ScoredCandidate | None:
        ranked = self.find_ranked_matches(candidates, track)
        if not ranked:
            logger.warning(f"No confident match found for '{track.title}' by '{track.artist_name}' ({len(candidates)} candidates evaluated)")
            return None

        best = ranked[0]
        logger.info(f"Selected match: score={best.score.total_score} peer={best.candidate.peer_id} file='{best.candidate.remote_path}' (free_slot={best.candidate.slots_free}, queue={best.candidate.queue_length})")
        return best

matcher = ExactRecordingMatcher()
