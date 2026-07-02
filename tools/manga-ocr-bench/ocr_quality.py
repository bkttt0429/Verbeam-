"""Post-OCR output quality gate (NEXT-STEPS-ROADMAP.md P2b). The Full-Recall real run caches garbage from
the art region (．．．．．．, «, ．．．ぐ（ etc — real_run_results.md); this must not reach the overlay /
translation / cache-as-final-text. Thresholds are first-guess, verified in _demo() against the actual
real_run_results.md output: every current garbage line trips a gate, every legit caption stays "ok".
"""
from reader_routes import jp_ratio

DOT_CHARS = "．.・…"


def ocr_quality(text):
    """Classify OCR'd text as "empty" | "garbage_dots" | "low_jp_ratio" | "ok". Caller behaviour: "ok" ->
    cache/overlay/translate; anything else -> reject (do not cache as final text; optionally hold the
    track's previous good text instead)."""
    if not text.strip():
        return "empty"
    dot_ratio = sum(ch in DOT_CHARS for ch in text) / len(text)
    if dot_ratio > 0.45:
        return "garbage_dots"
    if jp_ratio(text) < 0.35 and len(text) <= 4:
        return "low_jp_ratio"
    return "ok"


def _demo():
    assert ocr_quality("") == "empty"
    assert ocr_quality("   ") == "empty"

    # real garbage from real_run_results.md (Full Recall, art region)
    for garbage in ("．．．．．．", "．．．", "うん．．．．．．",
                    "視感　．．．　．．．．．．　えー",
                    "．．．．．．．．．．．．　ぐ　（",
                    "．．．．．．　．．．　で、　．．．",
                    "«"):
        assert ocr_quality(garbage) != "ok", garbage

    # real legit captions from the same run must all pass
    for legit in ("これ", "語っといて", "何がそ　不満", "不満なんだ",
                  "う　他に何がいる？　以上", "散々ワガ　話",
                  "そんなところも　割　嫌いじゃ無い",
                  "．．．．．．。　何がそんな　不満なんだ",
                  "«　何がそんな　不満なんだ"):
        assert ocr_quality(legit) == "ok", legit

    print("ocr_quality self-check OK")


if __name__ == "__main__":
    _demo()
