"""Explicit OCR deferral regions (per-game / per-profile config, NOT a CV classifier).

Measured (SEED-UTILITY-MEASURED.md): the hard_mixed art region (既視感/視感) PASSES confirm — cheap-CV has
no signal that separates it from real text (same colour, size ladder, position, density). Don't try to
auto-detect it. Instead suppress a configured screen region before it reaches the cache/OCR — cheap, exact,
and it is the biggest cost lever measured (15 OCR -> ~5 on the 48.0s clip when the right-side art band is
deferred). Long-term automatic separation is a learned detector / low-frequency VLM fallback, not this file.
"""


def _overlap_ratio(bbox, rect):
    ix0, iy0 = max(bbox[0], rect[0]), max(bbox[1], rect[1])
    ix1, iy1 = min(bbox[2], rect[2]), min(bbox[3], rect[3])
    inter = max(0, ix1 - ix0) * max(0, iy1 - iy0)
    area = max(1, (bbox[2] - bbox[0]) * (bbox[3] - bbox[1]))
    return inter / float(area)


def apply_deferral_regions(blocks, rects, min_overlap=0.5):
    """Split blocks into (kept, deferred). A block whose bbox is more than `min_overlap` fraction inside
    ANY rect in `rects` (each an (x0,y0,x1,y1) tuple) is deferred — held back from the cache/OCR. Returns
    (kept, deferred) so a caller can report a deferred_count metric."""
    if not rects:
        return list(blocks), []
    kept, deferred = [], []
    for b in blocks:
        if any(_overlap_ratio(b.bbox, r) > min_overlap for r in rects):
            deferred.append(b)
        else:
            kept.append(b)
    return kept, deferred


def _demo():
    class B:
        def __init__(self, bbox): self.bbox = bbox

    art_rect = (1200, 0, 1920, 1080)
    inside = B((1300, 100, 1400, 300))          # fully inside the deferral rect -> deferred
    outside = B((100, 100, 200, 300))           # nowhere near it -> kept
    straddling = B((1150, 100, 1250, 300))      # 50% inside (100/100 px of its 100px width) -> boundary

    kept, deferred = apply_deferral_regions([inside, outside, straddling], [art_rect])
    assert deferred == [inside], deferred
    assert kept == [outside, straddling], kept   # straddling overlap=0.5, gate is "> 0.5" not ">="

    # no rects configured -> everything kept, nothing deferred (deferral is opt-in per profile)
    kept2, deferred2 = apply_deferral_regions([inside, outside], [])
    assert kept2 == [inside, outside] and deferred2 == [], (kept2, deferred2)
    print("deferral self-check OK")


if __name__ == "__main__":
    _demo()
