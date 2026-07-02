# Next-steps roadmap — manga-ocr vertical-JP bench (post seed-admission)

Date: 2026-07-01. Audience: continuing work (incl. a web-side model). This is the forward plan; what is
already DONE is summarised first, then the open items in the owner's decided priority order.

## Status — what is SHIPPED and verified

Seed-admission "optimal A/B" is implemented and verified end-to-end (real manga-ocr on the test clip, not a
prototype). The 12-feature utility-scorer idea was measured down to **two knobs**:

1. **column_seed matching = center-linked, gate = count 3** (`temporal_cache.py`: `center_r`,
   `_best_center`, `update()` center fallback for column_seed only, `stable_by_kind={"column_seed":3}`).
   A faint sparse caption (これ, detected only **3/15 frames**, center spread 26px) recurs near one center
   but its recall-bbox extent varies so IoU fragments its hits; center-linking accumulates them to count-3.
2. **hard art region = explicit deferral ROI** (`deferral.py`, wired into `temporal_stream.py`). Cheap-CV
   cannot separate art from text (it passes confirm); a configured ignore-region does it exactly.

Measured operating points (current detector):

| point | mechanism | OCR | これ | 語っといて |
|---|---|---|---|---|
| old A | seed_stable=2 (IoU) | 23 | ✓ | ✓ |
| old B | seed_stable=4 (IoU) | 13 | ✗ | ✓ |
| **Full Recall** | center-match R20 + count 3 | **15** | ✓ | ✓ |
| **Realtime** | + deferral ROI | **5** | ✓ | ✓ |

Gate `robustness.py --no-ocr` = **raw 16/20 | core 15/15** unchanged; `temporal_cache.py`/`deferral.py`
self-checks pass. Two column char-drops also fixed: **散** (grouping-drop → `_seed_supersedes` in NMS),
**て** (detection-drop → `_extend_column_tails`, CLAHE + polarity-matched tophat). Real run:
`real_run_results.md`, `rois/real_run_realtime.{png,mp4}`. Full detail: `SEED-UTILITY-MEASURED.md`,
`SEED-ADMISSION-IMPL-SPEC.md`, `CHAR-DROP-FINDINGS.md`.

Perf (1080p, measured): the two knobs add **~1.1 ms/frame** (negligible). The standing cost is the
experimental full-frame detector at **~540 ms/frame** (group + confirm ≈ 400 ms). OCR (~570 ms/col CPU,
~240 MiB VRAM iGPU-DML) is amortised by the cache (96 frames → 19 OCR calls in the rendered clip).

**P2 + P2b + P2c SHIPPED (2026-07-02), real manga-ocr run, not synthetic.** `deferral.py` now takes
polygon regions (ray-cast point test) and an `allow_regions` list that wins over `deferral_regions`
(`load_profile_regions()` reads the §3c JSON config format). `ocr_quality.py` is the dot-ratio/jp-ratio
gate (reuses `reader_routes.jp_ratio`, doesn't reimplement it). `eval_captions.py` scores
`expected_captions.json` (all-caption recall, not just KORE/KATATTOITE). `real_run.py` now runs a third
operating point, **RealtimeAllow** (defer x>1200 + allow the white text panel `[1200,90,1440,520]`), and
measured the exact bug P2 targets:

| point | OCR | これ | 語っといて | whitebox (何がそんな不満なんだ) | garbage cached |
|---|---|---|---|---|---|
| Full Recall | 15 | ✓ | ✓ | ✓ | 5 (pre-gate) |
| Realtime (old, x>1200 only) | 5 | ✓ | ✓ | **✗ killed** | 0 |
| **Realtime+Allowlist** | **10** | ✓ | ✓ | **✓ rescued** | 0 |

Realtime+Allowlist keeps the deferral savings (10 vs 15, still cuts the art-region OCR) while no longer
dropping the white-panel caption. `python deferral.py` / `ocr_quality.py` / `eval_captions.py` self-checks
pass; `robustness.py --no-ocr` unchanged at `raw 16/20 | core 15/15`; `temporal_cache.py` self-check
unchanged. Not yet done: wiring `ocr_quality`'s `OCR_REJECTED` behaviour into `temporal_cache.py`'s state
machine itself (left for whoever does P1 — same function, avoid a second touch to the admission core).

---

## Open items — owner-prioritised

### P1 — Cross-frame temporal state: Kalman track + crop-diff [SHIPPED — see `P1-KALMAN-CROPDIFF.md`]
**Implemented 2026-07-02.** Alpha-beta (steady-state Kalman) center prediction per column_seed track feeds
`_best_center` (drift-gap re-link, self-checked with a stripped-filter control); measured crop-diff stale
guard (tracked-bbox thumbs, per-kind thresholds `column_seed` 7.5 / others 12.0, 2-frame confirm); P2b
`quality_fn` wired into the cache (garbage read HOLDs previous good text). Production Realtime point
**unchanged at 5 OCR** with zero false re-fires on real text; Full Recall 19 (+4 = genuine art-region
animation, quality-gated). ⚠ thresholds are one-clip calibrated — re-check on more footage. The roadmap's
original guessed design (fixed box, 32×32, MAD 18) was **refuted by measurement twice**; the report has
the distributions.

Original plan (kept for context):
**These are one workstream (cross-frame change logic), not two.** The subtitles MOVE (animated scene), and
the per-frame rescues are noisy:
- `_extend_column_tails` re-detects て with a fresh CLAHE probe every frame → measured: it hits て on
  frames 0/4/13 (y≈792) but misses on 7/8/12 (y=744), while the column x-drifts 843→872 (the caption moves).
  **DOWN-PAYMENT DONE:** the cache now keeps a column_seed track's max observed Y-extent and OCRs that
  (`temporal_cache._extend_seed_y`), so the firing frame no longer drops a tail glyph an earlier frame
  proved — real run reads 語っといて in full. Full cross-frame smoothing (below) still wanted for the drift.
- Center-linking makes a track persist across more frames, so a same-position content change would reuse
  stale cached text (no re-OCR trigger today).

**Approach — a per-caption Kalman filter carrying the track state:**
- Track each caption's bbox (position + extent, and velocity) with a **Kalman filter**. The predicted,
  smoothed bbox — not the raw per-frame detection — feeds admission and the tail-extension, so て (and any
  column-end glyph) is stabilised across frames instead of flickering in/out with the CLAHE probe.
- The same track state holds a **crop thumbnail**; on a matched track compute crop-diff MAD vs the stored
  thumb and, with a 2-frame DIRTY confirm (avoid motion-blur/rain), re-OCR only on genuine content change.
- This subsumes the current center-link matching (Kalman prediction replaces / augments the center-distance
  match) and gives the moving-subtitle handling the current IoU/center matcher lacks.
- Guardrail: keep it column_seed-scoped first; measure `center_match_collision_count` so prediction never
  fuses adjacent columns. Pick the MAD threshold from measured static-vs-changed crops, not a guess.

### P2 — Deferral ROI → profile system: polygon + allowlist (item "deferral ROI") [SHIPPED]
The blunt `x>1200` band used in the demo is a **stand-in** and it **kills a legit caption**: 何がそんな
不満なんだ (white card, x≈1208–1437) sits in the same right-side band as the 視感 art and is wrongly
deferred (it IS read in Full Recall). A vertical line can't separate them. Ship a **per-game/profile
config**, not a hardcoded rect:

```json
{ "deferral_regions": [ { "name": "umbrella_character_art",
    "polygon": [[1320,300],[1720,300],[1720,850],[1320,850]],
    "mode": "suppress_realtime", "allow_accuracy_mode": true } ],
  "allow_regions": [ { "name": "right_white_text_panel", "rect": [1200,90,1440,520] } ] }
```

Precedence: **allow_region wins over deferral_region** (a block in an allow rect is always kept), then
deferral suppresses, then default keep. `allow_accuracy_mode` lets Full Recall optionally still read a
deferred region. Metrics to add: `deferred_count`, `dropped_by_deferral_count`,
`deferred_useful_text_count` (a deferred block whose Full-Recall OCR was quality-OK = a config bug signal).
Config format drafted in `SEED-ADMISSION-IMPL-SPEC.md` §3c.

### P2b — OCR output quality gate (garbage filter) [SHIPPED — `ocr_quality.py`]
The Full-Recall real run still caches garbage from the art region: `．．．．．．`, `«`, `．．．` etc.
These must not enter the final overlay / translation / cache-as-final-text. Post-OCR gate:

```python
def ocr_quality(text):
    if not text.strip(): return "empty"
    dot_ratio = sum(ch in "．.・…" for ch in text) / len(text)
    jp_ratio  = sum(is_japanese(ch) for ch in text) / len(text)
    if dot_ratio > 0.45: return "garbage_dots"
    if jp_ratio < 0.35 and len(text) <= 4: return "low_jp_ratio"
    return "ok"
```

Behaviour: `ok` → cache/overlay/translate; garbage → mark track `OCR_REJECTED`, do NOT cache as final text
(optionally HOLD the previous good text if the track had one). This cleans the art-region dot-noise out of
Full Recall **even before** any learned detector exists. Thresholds are first-guess — verify against the
real-run outputs (all current garbage lines hit `dot_ratio>0.45`; the legit captions don't).

### P2c — Evaluation upgrade: all-caption recall (`expected_captions.json`) [SHIPPED — `eval_captions.py`]
Current acceptance tracks only two tags (KORE/KATATTOITE) — good for seed admission, not enough for
productization: a config could score perfect OCR counts while deferral silently kills real right-side text.
Add a manifest of every caption in the test window:

```json
[ { "id": "kore",       "text": "これ",        "must_have": true, "region": [740,150,830,270] },
  { "id": "katattoite", "text": "語っといて",   "must_have": true, "region": [830,520,910,820] },
  { "id": "whitebox",   "text_contains": ["何がそんな","不満なんだ"], "must_have": true },
  { "id": "art_shikan", "deferred": true } ]
```

Metrics: `all_caption_recall`, `must_have_recall`, `garbage_output_count`, `ocr_calls`,
`useful_ocr_per_call`, `dropped_by_deferral_count`. Acceptance becomes: **must_have_recall = 100% AND
garbage_output_count = 0 AND ocr_calls ≤ target** — which catches exactly the `x>1200` white-card kill.

### P3 — Detector latency optimisation (item "detector 540ms") — "下下個目標" (next-next)
~540 ms/frame is not realtime; **group (~191 ms) + confirm (~204 ms)** dominate. Candidates: spatial index
for the O(N²) graph pair-scan; cache/skip confirm on stable tracks (once a track is HOLD, trust its bbox and
skip re-columnize); coarse-locate → ROI-tile two-stage. Not urgent — deferred to after P1/P2.

### P4 — hard_mixed (視感 / 既視感): learned detector / VLM (item "hard_mixed")
cheap-CV has no separating signal (same colour/size/position/density; art passes confirm — measured). Two
paths, both later: (a) a small learned detector that only *classifies* `{vertical_text | hard_mixed_art |
ignore_art | ruby}` (no reading), or (b) a low-frequency VLM (Qwen2-VL / PaddleOCR-VL) fallback run only
when a hard_mixed block is stable N frames AND accuracy mode is on — never per-frame, never realtime default.

### P5 — Wire into the live region engine (item "live engine") — "最後做" (do last)
Everything above is still bench-only (Problem 5). The productization step — detector → deferral → Kalman
cache → reader behind the existing Region tab — is scheduled last, after the pipeline is stable.

---

## Priority summary

| # | item | decision | model tier |
|---|---|---|---|
| **P1** | Kalman track + crop-diff (cross-frame logic) | **next** — unifies て-stability + stale-guard | **strong model** (algorithm design; touches admission core; measure-first discipline required) |
| **P2** | deferral profile: polygon + **allowlist** | **SHIPPED** — measured fix for 何がそんな不満なんだ collateral | done |
| **P2b** | OCR quality gate (dot/jp ratio) | **SHIPPED** | done |
| **P2c** | all-caption recall eval (`expected_captions.json`) | **SHIPPED** | done |
| **P3** | detector latency (group+confirm ~400ms) | 下下個目標 (next-next) | profiling + judgment |
| **P4** | hard_mixed learned-detector / VLM | long-term / parallel | training-pipeline experience, separate workstream |
| **P5** | live region engine integration | 最後做 (last) | codebase familiarity |
