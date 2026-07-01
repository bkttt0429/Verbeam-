# Implementation spec — optimal A/B seed admission (for a fresh implementer)

Date: 2026-07-01. Target: **another model implements this cold.** Bench dir: `tools/manga-ocr-bench/`
(locally `.codex-run/manga-ocr-bench/`). Runs on Windows via `venv/Scripts/python.exe`. Test clip is
hardcoded in the scripts (48.0s window of a 1080p mp4). Evidence & rationale: `SEED-UTILITY-MEASURED.md`
(read it if you need the "why"; this doc is the "what to build").

## 0. Context you need

A cheap-CV detector (`block_detector.py::detect_text_blocks(frame, scorer="cc", group="graph",
emit_seeds=True, confirm_raw=True)`) emits confirmed text **blocks** per frame; each block has `.bbox`
(x0,y0,x1,y1), `.kind` (`"column_seed"` | `"block_merged"` | `"broad_split"`), `.layout`,
`.column_boxes_abs`. `TemporalBlockCache` (`temporal_cache.py`) tracks blocks across frames by bbox-IoU and
fires OCR once a track is "stable", then HOLDs (reuse). manga-ocr has **no KV-cache**, so each OCR call is
the cost we minimise. Two reference captions in the test window (tagged by bbox in the scripts):
**KORE = これ** (faint micro-caption) and **KATATTOITE = 語っといて**.

**Measured problem (do not re-derive):** これ is detected in only **3/15 frames**; its center is *not* more
stable than flicker (26px spread). So a "same bbox for N consecutive frames" gate (the current
`stable_frames`/IoU model) can never admit it without also admitting flicker. The separating signal is
**detection count at a location** (これ = 3 hits, singleton flicker = 1 hit), and these's bbox *extent*
varies between hits so IoU-matching fragments its 3 hits into age-1 stubs.

**Guardrails (must hold):**
- Do NOT change `columnize.py` contracts. Do NOT touch the detector's mask/grouping for this task.
- Core regression gate must stay **`python robustness.py --no-ocr` → raw 16/20 | core 15/15**.
- `block_detector.py` already contains `_seed_supersedes` and `_extend_column_tails` (unrelated char-drop
  fixes). Leave them. All numbers below are measured on that current detector.
- Do NOT build any of: 12-feature utility scorer, logistic/hand-weights, log-odds decay, budgeted top-K,
  correlation-clustering / set-packing, `recall_source_penalty`, extra aspect/occ/line scoring. They were
  measured not to separate これ from flicker (recall_source_penalty actively *hurts* これ). Keep it to the
  two knobs below.

## 1. The two knobs (the whole change)

1. **Knob 1 — column_seed matching = center-linked, gate = count 3** (not consecutive-age / IoU-only).
2. **Knob 2 — hard art/text region = explicit deferral ROI** (config or, later, learned detector/VLM). Never
   try to separate art from text with cheap-CV — art blocks pass confirm; there is no CV signal.

Target operating points (**measured on current detector**, `measure_seed_spread.py`):

| point | mechanism | OCR | これ | 語っといて |
|---|---|---|---|---|
| old A | `seed_stable=2` (IoU) | 23 | ✓ | ✓ |
| old B | `seed_stable=4` (IoU) | 13 | ✗ | ✓ |
| **new A** | center-match R=20 + column_seed count=3 | **15** | ✓ | ✓ |
| **new B** | new A + deferral ROI (right art band) | **5** | ✓ | ✓ |

new A strictly dominates old A (same full recall, 23→15). new B beats old B (13, drops これ) on both cost
and recall. **Retire old B (`seed_stable=4`); it is the recall-sacrificing branch.**

---

## 2. PATCH 1 — center-linked matching + count 3 (`temporal_cache.py`)

### 2a. `__init__` — add `center_r`
```python
def __init__(self, stable_frames=2, expire_frames=3, match_iou=0.5, stable_by_kind=None, center_r=20.0):
    self.stable_frames = stable_frames
    self.stable_by_kind = stable_by_kind or {}
    self.expire_frames = expire_frames
    self.match_iou = match_iou
    self.center_r = center_r          # column_seed center-link radius (see _best_center)
    self.tracks = {}
    self._next_id = 0
```

### 2b. add `_best_center` (column_seed only)
```python
def _best_center(self, bbox):
    """Nearest COLUMN_SEED track whose center is within center_r. A faint sparse caption (これ, 3/15
    frames) recurs at ~the same center, but its recall bbox EXTENT varies so IoU<0.5 between hits and
    IoU-matching fragments it into age-1 stubs. Center-linking lets its hits accumulate to the count gate.
    Gated to column_seed AND center_r < min inter-column gap (~54px here) so adjacent columns never fuse."""
    cx, cy = (bbox[0] + bbox[2]) * 0.5, (bbox[1] + bbox[3]) * 0.5
    best_id, best_d = None, self.center_r
    for tid, t in self.tracks.items():
        if t.get("kind") != "column_seed":
            continue
        tcx, tcy = (t["bbox"][0] + t["bbox"][2]) * 0.5, (t["bbox"][1] + t["bbox"][3]) * 0.5
        d = ((cx - tcx) ** 2 + (cy - tcy) ** 2) ** 0.5
        if d < best_d:
            best_id, best_d = tid, d
    return best_id
```

### 2c. `update` — use center fallback when IoU fails, for column_seed candidates
Replace the current spawn decision:
```python
#  OLD:
#  cand_id, best_iou = self._best(b.bbox)
#  spawned = cand_id is None or best_iou < self.match_iou
#  NEW:
cand_id, best_iou = self._best(b.bbox)
matched = cand_id is not None and best_iou >= self.match_iou
if not matched and getattr(b, "kind", None) == "column_seed":
    cid = self._best_center(b.bbox)     # IoU failed -> try center-link (column_seed only)
    if cid is not None:
        cand_id, matched = cid, True
spawned = not matched
tid = None if spawned else cand_id
```
Everything else in `update` stays. (`best_iou` is still reported in the output dict for diagnostics.)

### 2d. default `stable_by_kind`
Callers should construct the cache with:
```python
TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                   stable_by_kind={"block_merged": 2, "broad_split": 2, "column_seed": 3})
```
Semantically the column_seed `3` is a **center-linked hit count**, not 3 consecutive frames (gaps ≤
`expire_frames` are tolerated by the existing age/missed logic).

### 2e. extend `_demo()` self-check
Add two asserts:
- **Center-linked count admits a sparse caption:** feed a `column_seed` block on 3 frames that share a
  center within `center_r` but whose **extents vary enough that pairwise IoU < match_iou** (mimics これ).
  With `center_r=20, column_seed count=3` → exactly **one** `ocr_called==True` across the 3 frames.
- **Control:** the same 3 boxes through an IoU-only cache (`center_r=0`, `column_seed`:4) → **zero**
  `ocr_called`. (Proves center-link + count-3, not IoU/consecutive-age, is what admits it.)

---

## 3. PATCH 2 — deferral ROI (art region), before the cache

Art (既視感/視感 character-art titles) **passes confirm** — cheap-CV cannot separate it. Do not try. Instead
suppress configured screen regions before they reach the cache/OCR.

### 3a. new file `deferral.py`
```python
"""Explicit OCR deferral regions (per-game / per-profile config, NOT a CV classifier). Art blocks pass
confirm, so cheap-CV can't auto-separate them; a configured 'ignore region' is the shippable answer."""

def _overlap_ratio(bbox, rect):
    ix0, iy0 = max(bbox[0], rect[0]), max(bbox[1], rect[1])
    ix1, iy1 = min(bbox[2], rect[2]), min(bbox[3], rect[3])
    inter = max(0, ix1 - ix0) * max(0, iy1 - iy0)
    area = max(1, (bbox[2] - bbox[0]) * (bbox[3] - bbox[1]))
    return inter / float(area)

def apply_deferral_regions(blocks, rects, min_overlap=0.5):
    """Split blocks into (kept, deferred). A block whose bbox is >min_overlap inside any rect is deferred
    (not sent to the cache/OCR). rects = list of (x0,y0,x1,y1). Returns (kept, deferred) so callers can
    report a deferred_count metric."""
    kept, deferred = [], []
    for b in blocks:
        if any(_overlap_ratio(b.bbox, r) > min_overlap for r in rects):
            deferred.append(b)
        else:
            kept.append(b)
    return kept, deferred
```

### 3b. wiring — bench (verifiable now)
In `temporal_stream.py` (and `mode_compare.py` if you want it there): add `--defer-region x0,y0,x1,y1`
(repeatable), and after `detect_text_blocks(...)` and **before** `cache.update(...)`:
```python
blocks, deferred = apply_deferral_regions(blocks, defer_rects)
# metric: sum(len(deferred)) over frames
```
Insert AFTER the detector emits, never before mask/grouping (keeps detector debug output intact).

### 3c. product config (target format; wire when the live region engine is integrated — Problem 5)
Per-game/profile `deferral_regions` (rect or polygon), e.g.:
```json
{ "MangaOcrBench": { "Profiles": { "default": { "deferral_regions": [
  { "name": "right_art_region", "rect": [1200,0,1920,1080], "mode": "suppress_realtime",
    "allow_accuracy_mode": false } ] } } } }
```
Name it `deferral_regions` / `ignore_regions` — NOT `hard_mixed_cv_gate` (it is config, not a classifier).

---

## 4. PATCH 3 — product modes (rename A/B)
```
Mode A / "Full Recall":   center-match R=20 + column_seed count=3,  no deferral ROI   -> ~15 OCR, full recall
Mode B / "Realtime":      Mode A + per-game deferral ROI                               -> ~5 OCR,  full recall
```
Both keep これ + 語っといて. Do not keep `seed_stable=4` as a mode — it is strictly dominated.

---

## 5. Follow-ups (do AFTER Patches 1–3; ordered)

### 5.1 Stale-content guard (recommended BEFORE live) — center-match raises stale-reuse risk
Center-linking makes a track persist across more frames, so a same-location content change would reuse
stale cached text. Add a crop-diff on matched tracks:
```python
# on a track that MATCHED (not spawned): compare a 32x32 gray thumb of the new crop to the stored one
mad = mean(abs(curr_thumb.astype(int16) - track.thumb.astype(int16)))
# 2-frame confirm: mark DIRTY if mad > ~18; re-OCR only if still high next frame (avoid motion-blur/rain)
```
Store `thumb` per track on OCR. This is the crop-diff trigger already flagged in `temporal_cache.py`'s
module docstring. Pick the MAD threshold by measuring known-static vs known-changed crops (don't guess).

### 5.2 `center_r` generalization — ship fixed 20 first + metrics, adapt later
`center_r=20` is tuned on one 48s clip; it must stay below the min inter-column gap (~54px here) or adjacent
columns fuse. Ship fixed 20. Add metrics: `center_match_distance`, `center_match_collision_count`,
`seed_track_count`. Only if multi-clip footage shows column-fusion, switch to
`center_r = clamp(0.6 * median_seed_width, 12, 20)`.

### 5.3 Hard mixed (既視感/視感) — deferral ROI now, learned detector/VLM later
cheap-CV cannot separate (same colour/size/position/density; art passes confirm). Short term = 5.a the
deferral ROI (Patch 2). Long term = a small learned detector that only classifies
`{vertical_text | hard_mixed_art | ignore_art | ruby}` (no reading), or a VLM (Qwen2-VL / PaddleOCR-VL)
fallback run **low-frequency** (only when a hard_mixed block is stable N frames AND accuracy mode is on) —
never per-frame, never in the realtime default.

---

## 6. Acceptance criteria (exact)

Reproducer already exists: `measure_seed_spread.py` (Sections 1/3b/5). After Patch 1+2, running the bench
should show:
```
Mode A (center R20 + column_seed count=3, no defer):   OCR = 15,  tags = [KATATTOITE, KORE]   (これ ✓)
Mode B (+ defer region x>1200):                        OCR = 5,   tags = [KATATTOITE, KORE]   (これ ✓)
old A (seed_stable=2) still 23; old B (seed_stable=4) still 13 & drops KORE  (as a before/after check)
```
And unconditionally:
```
python robustness.py --no-ocr   ->   raw 16/20 | core 15/15    (unchanged)
python temporal_cache.py         ->   "temporal_cache self-check OK"   (with the 2 new asserts in 2e)
```
Numbers ±1 are fine (scene has animation; see SEED-UTILITY-MEASURED.md caveat). The **hard** criteria are:
new A keeps BOTH captions at fewer OCR than old A, and new B keeps BOTH captions.

## 7. What NOT to do (guardrail restated)
No utility scorer, no logistic, no log-odds, no budgeted top-K, no correlation-clustering/set-packing, no
`recall_source_penalty`, no extra geometry/aspect scoring, no cheap-CV art classifier. If you feel the urge
to add a feature, re-read §1: the measurement says only *center-linked count* + *deferral ROI* move the
needle.
