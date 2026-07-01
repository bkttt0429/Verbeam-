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

---

## Open items — owner-prioritised

### P1 — Cross-frame temporal state: Kalman track + crop-diff (items "て instability" + "stale guard")
**These are one workstream (cross-frame change logic), not two.** The subtitles MOVE (animated scene), and
the per-frame rescues are noisy:
- `_extend_column_tails` re-detects て with a fresh CLAHE probe every frame → it catches て on some frames,
  misses it on others (real run OCR'd 語っといて on a frame where the tail wasn't caught → "語っとい").
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

### P2 — Deferral ROI shaping (per-game polygon) (item "deferral ROI")
The blunt `x>1200` band used in the demo is a **stand-in** and it **kills a legit caption**: 何がそんな
不満なんだ (white card, x≈1208–1437) sits in the same right-side band as the 視感 art and is wrongly
deferred (it IS read in Full Recall). A vertical line can't separate them. Ship a **per-game/profile
polygon** ignore-region that encloses only 視感 + the character/umbrella, leaving the white-card caption.
Config format already drafted in `SEED-ADMISSION-IMPL-SPEC.md` §3c (`deferral_regions`, rect or polygon).

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

| # | item | decision |
|---|---|---|
| **P1** | Kalman track + crop-diff (cross-frame logic) | **next** — unifies て-stability + stale-guard |
| **P2** | deferral ROI shaping (polygon) | near-term — fixes 何がそんな不満なんだ collateral |
| **P3** | detector latency (group+confirm) | 下下個目標 (next-next) |
| **P4** | hard_mixed learned-detector / VLM | long-term / parallel |
| **P5** | live region engine integration | 最後做 (last) |
