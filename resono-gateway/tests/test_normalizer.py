from app.matcher.normalizer import normalize_text, tokenize, extract_track_number, has_unwanted_tags

def test_normalize_text():
    assert normalize_text("Drake - Take Care (Deluxe Edition)") == "drake take care"
    assert normalize_text("11. Controlla [Explicit].mp3") == "11 controlla mp3"
    assert normalize_text("Beyoncé - Déjà Vu") == "beyonce deja vu"

def test_tokenize():
    tokens = tokenize("Headlines Drake")
    assert "headlines" in tokens
    assert "drake" in tokens

def test_extract_track_number():
    assert extract_track_number("03 - Headlines.mp3") == 3
    assert extract_track_number("11 Drake - Controlla.flac") == 11
    assert extract_track_number("CD 01 - 05 - Song.mp3") == 5
    assert extract_track_number("Music/Drake/Views/11 - Controlla.mp3") == 11
    assert extract_track_number("JustAFilename.mp3") is None

def test_has_unwanted_tags():
    # Karaoke rejection
    assert has_unwanted_tags("Drake - Headlines (Karaoke Version)", "Headlines") == "karaoke"
    # Live bootleg rejection
    assert has_unwanted_tags("Drake - Headlines Live at Wembley", "Headlines") == "live"
    # Remix rejection
    assert has_unwanted_tags("Drake - Headlines (Remix)", "Headlines") == "remix"
    # Cover rejection
    assert has_unwanted_tags("Acoustic Cover - Headlines", "Headlines") == "cover"

    # But if the canonical track explicitly is a remix, do NOT reject!
    assert has_unwanted_tags("Song (Club Remix)", "Song (Club Remix)") is None
