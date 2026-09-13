from app.matcher.scoring import ExactRecordingMatcher
from app.core.models import AudioCandidate, CanonicalTrack, deterministic_guid

def test_exact_recording_match():
    matcher = ExactRecordingMatcher(confidence_threshold=0.80)
    target = CanonicalTrack(
        canonical_id=deterministic_guid("spotify:track:123"),
        spotify_id="spotify:track:123",
        title="Controlla",
        artist_name="Drake",
        artist_id="spotify:artist:456",
        album_title="Views",
        album_id="spotify:album:789",
        duration_ms=245000,
        track_number=11
    )

    good_candidate = AudioCandidate(
        backend="slskr",
        peer_id="superpeer",
        remote_path="Music/Drake/Views/11 - Drake - Controlla.flac",
        filename="11 - Drake - Controlla.flac",
        folder="Music/Drake/Views",
        size_bytes=28000000,
        duration_seconds=245,
        codec="flac",
        slots_free=True
    )

    scored = matcher.score_candidate(good_candidate, target)
    assert not scored.score.rejected
    assert scored.score.total_score >= 0.90
    assert scored.score.artist_score == 1.0
    assert scored.score.title_score == 1.0
    assert scored.score.duration_score == 1.0
    assert scored.score.track_number_bonus == 0.10

def test_reject_duration_mismatch():
    matcher = ExactRecordingMatcher(confidence_threshold=0.80)
    target = CanonicalTrack(
        canonical_id=deterministic_guid("spotify:track:123"),
        spotify_id="spotify:track:123",
        title="Controlla",
        artist_name="Drake",
        artist_id="spotify:artist:456",
        album_title="Views",
        album_id="spotify:album:789",
        duration_ms=245000
    )

    # 180 seconds instead of 245s (over 6s mismatch)
    bad_duration = AudioCandidate(
        backend="slskr",
        peer_id="randompeer",
        remote_path="Drake - Controlla.mp3",
        filename="Drake - Controlla.mp3",
        size_bytes=5000000,
        duration_seconds=180,
        codec="mp3"
    )

    scored = matcher.score_candidate(bad_duration, target)
    assert scored.score.rejected
    assert "Duration mismatch" in scored.score.rejection_reason

def test_reject_unwanted_versions():
    matcher = ExactRecordingMatcher(confidence_threshold=0.80)
    target = CanonicalTrack(
        canonical_id=deterministic_guid("spotify:track:123"),
        spotify_id="spotify:track:123",
        title="Headlines",
        artist_name="Drake",
        artist_id="spotify:artist:456",
        album_title="Take Care",
        album_id="spotify:album:789",
        duration_ms=236000
    )

    remix_candidate = AudioCandidate(
        backend="slskr",
        peer_id="peer1",
        remote_path="Drake - Headlines (Club Remix).mp3",
        filename="Drake - Headlines (Club Remix).mp3",
        size_bytes=8000000,
        duration_seconds=236,
        codec="mp3"
    )
    assert matcher.score_candidate(remix_candidate, target).score.rejected

    karaoke_candidate = AudioCandidate(
        backend="slskr",
        peer_id="peer2",
        remote_path="Drake - Headlines Karaoke.mp3",
        filename="Drake - Headlines Karaoke.mp3",
        size_bytes=8000000,
        duration_seconds=236,
        codec="mp3"
    )
    assert matcher.score_candidate(karaoke_candidate, target).score.rejected

