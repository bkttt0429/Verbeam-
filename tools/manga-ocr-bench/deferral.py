"""Explicit OCR deferral regions (per-game / per-profile config, NOT a CV classifier).

Measured (SEED-UTILITY-MEASURED.md): the hard_mixed art region (既視感/視感) PASSES confirm — cheap-CV has
no signal that separates it from real text (same colour, size ladder, position, density). Don't try to
auto-detect it. Instead suppress a configured screen region before it reaches the cache/OCR — cheap, exact,
and it is the biggest cost lever measured (15 OCR -> ~5 on the 48.0s clip when the right-side art band is
deferred). Long-term automatic separation is a learned detector / low-frequency VLM fallback, not this file.

A region is either a plain (x0,y0,x1,y1) rect tuple (legacy, still accepted), or a dict:
    {"rect": (x0,y0,x1,y1), ...}
    {"polygon": [(x,y), ...], ...}
`allow_regions` always win over `deferral_regions` (NEXT-STEPS-ROADMAP.md P2): the blunt x>1200 realtime
band also covers a legit caption (何がそんな不満なんだ, a white text panel at x≈1208-1437) that Full Recall
reads fine; a `right_white_text_panel` allow region carves it back out without touching the art band.
"""
import json


def _overlap_ratio(bbox, rect):
    ix0, iy0 = max(bbox[0], rect[0]), max(bbox[1], rect[1])
    ix1, iy1 = min(bbox[2], rect[2]), min(bbox[3], rect[3])
    inter = max(0, ix1 - ix0) * max(0, iy1 - iy0)
    area = max(1, (bbox[2] - bbox[0]) * (bbox[3] - bbox[1]))
    return inter / float(area)


def _point_in_polygon(x, y, poly):
    """Ray-casting point-in-polygon test. poly: list/tuple of (x, y) vertices."""
    inside = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            x_at_y = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if x < x_at_y:
                inside = not inside
    return inside


def _region_hit(bbox, region, min_overlap):
    if isinstance(region, dict):
        if "polygon" in region:
            cx, cy = (bbox[0] + bbox[2]) * 0.5, (bbox[1] + bbox[3]) * 0.5
            return _point_in_polygon(cx, cy, region["polygon"])
        region = region["rect"]
    return _overlap_ratio(bbox, region) > min_overlap


def apply_deferral_regions(blocks, deferral_regions, allow_regions=None, min_overlap=0.5):
    """Split blocks into (kept, deferred). `allow_regions` win over `deferral_regions`: a block hit by any
    allow region is always kept, regardless of deferral. Otherwise a block more than `min_overlap` inside
    any deferral region (rect: area overlap; polygon: center-point test) is deferred — held back from the
    cache/OCR. Returns (kept, deferred) so a caller can report a deferred_count metric."""
    allow_regions = allow_regions or []
    if not deferral_regions and not allow_regions:
        return list(blocks), []
    kept, deferred = [], []
    for b in blocks:
        if any(_region_hit(b.bbox, r, min_overlap) for r in allow_regions):
            kept.append(b)
        elif any(_region_hit(b.bbox, r, min_overlap) for r in deferral_regions):
            deferred.append(b)
        else:
            kept.append(b)
    return kept, deferred


def regions_for_mode(deferral_regions, mode="realtime"):
    """A region with allow_accuracy_mode=True is suppressed only in "realtime" mode; in "accuracy" mode
    (Full Recall) it is skipped here so the region is not deferred at all. Regions given as plain rect
    tuples (no allow_accuracy_mode) are unaffected by mode."""
    if mode != "accuracy":
        return deferral_regions
    return [r for r in deferral_regions
            if not (isinstance(r, dict) and r.get("allow_accuracy_mode"))]


def load_profile_regions(path, profile="default"):
    """Load deferral_regions/allow_regions for one profile from the product config format (§3c,
    SEED-ADMISSION-IMPL-SPEC.md): {"MangaOcrBench": {"Profiles": {<profile>: {...}}}}. Returns
    (deferral_regions, allow_regions), each a list of region dicts as above."""
    with open(path, encoding="utf-8") as fh:
        cfg = json.load(fh)
    prof = cfg["MangaOcrBench"]["Profiles"][profile]
    return prof.get("deferral_regions", []), prof.get("allow_regions", [])


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

    # the real P2 bug: x>1200 deferral also kills the white caption panel -> allow_region rescues it.
    whitebox = B((1215, 112, 1435, 506))          # measured, real_run_results.md frame 3
    art_blob = B((1463, 418, 1703, 776))          # measured, 視感 art region, same frame
    allow = [{"name": "right_white_text_panel", "rect": (1200, 90, 1440, 520)}]
    kept3, deferred3 = apply_deferral_regions([whitebox, art_blob], [art_rect], allow_regions=allow)
    assert kept3 == [whitebox], kept3
    assert deferred3 == [art_blob], deferred3

    # polygon deferral region (umbrella_character_art from SEED-ADMISSION-IMPL-SPEC.md §3c example)
    poly_region = [{"name": "umbrella_character_art",
                    "polygon": [(1320, 300), (1720, 300), (1720, 850), (1320, 850)]}]
    center_in = B((1400, 400, 1500, 500))    # center (1450,450) inside the polygon -> deferred
    center_out = B((100, 400, 200, 500))     # far outside -> kept
    kept4, deferred4 = apply_deferral_regions([center_in, center_out], poly_region)
    assert deferred4 == [center_in], deferred4
    assert kept4 == [center_out], kept4

    # mode gate: allow_accuracy_mode region is deferred in realtime, skipped (not deferred) in accuracy
    mode_region = [{"name": "right_art_region", "rect": (1200, 0, 1920, 1080), "allow_accuracy_mode": True}]
    realtime_rects = regions_for_mode(mode_region, mode="realtime")
    accuracy_rects = regions_for_mode(mode_region, mode="accuracy")
    assert apply_deferral_regions([inside], realtime_rects)[1] == [inside]
    assert apply_deferral_regions([inside], accuracy_rects)[1] == []

    print("deferral self-check OK")


if __name__ == "__main__":
    _demo()
