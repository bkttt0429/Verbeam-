# Manga-OCR realtime detector — STATUS & OPEN PROBLEMS

Date: 2026-07-01. Concise handoff for a fresh (web) session. Full measured log:
`CONCLUSION-GROUPING-FINDING.md` (UPDATE 3→9). Bench dir: `tools/manga-ocr-bench/`.

---

## What this is

A cheap-CV full-frame vertical-Japanese text-block detector (`block_detector.py`, `scorer="cc"`) that
proposes blocks, confirms each by cropping the raw frame and running `columnize()`, then routes only raw
crops to manga-ocr. Goal: realtime subtitle/caption OCR on anime with vertical text, low VRAM, few OCR calls.

Pipeline:
```
frame → masks (blackhat/tophat + blackhat_close recall) → component_filter_global
      → graph grouping → column_seed + block_merged proposals → kind-aware merge
      → raw-crop columnize confirm → broad-split (cols>4) → parent/child NMS
      → TemporalBlockCache (OCR only stable blocks) → manga-ocr on raw crops
```
Guardrails held throughout: mask = geometry only; raw crop = OCR input; detector emits blocks (columns are
`columnize()`'s job); core regression gate `robustness.py --no-ocr` = **raw 16/20 | core 15/15** unchanged.

---

## What's shipped (all measured on the 42s/48s test clip)

| Step | Result |
|---|---|
| **#1 column_seed → confirm + parent/child NMS** | Weak single columns survive the proposal lifecycle. Recovered **語っといて** (was dropped). Trust-confirm for faint columns `columnize` reads as no_text, guarded by occ/line/aspect. |
| **#2 fragment-recall source** (`blackhat_close_full`, vertical morph-close) | Merges sub-threshold glyph fragments. Recovered **これ** (a faint 2-glyph gray micro-caption). |
| **48s column-level acceptance** | **A これ · B 他に何がある/以上 · C 何がそんな/不満なんだ · D 散々ワガママ + 語っといて · E teal 3-col — ALL pass** (accuracy mode). F 視感/character = deferred. |
| **#3 temporal cache** (`temporal_cache.py`) | NEW→OCR_DONE→HOLD→EXPIRE. OCR fires once per stable block, then reuse. |
| **Phase 4 (4A only)** | Instrumented jitter. Finding: residual OCR is column_seed *precision*, not bbox wobble → a region stabilizer would NOT help (see Open Problems). |
| **Patch 5 seed admission** (kind-tiered `stable_frames`) | Flickery seeds never persist long enough to earn OCR; stable captions do. Gives two modes. |

### Two modes (measured, 48.0s × 15 frames; naive = OCR every block every frame = 108)

| Mode | `seed_stable` | OCR calls | captions read |
|---|---|---|---|
| **Accuracy (A)** | 2 | 23 (79% fewer) | これ + 語っといて |
| **Realtime (B)** | 4 | **15 (86% fewer)** | 語っといて (これ dropped) |

---

## OPEN PROBLEMS (current)

1. **これ vs realtime gating is a hard tradeoff, not a bug.** これ is a faint gray micro-caption recovered
   only via the intermittent recall source; it never persists 3+ consecutive frames, so any
   seed-persistence gating (realtime Mode B) sacrifices it. **Accuracy mode keeps it; realtime mode cannot.**
   No cheap fix — it is inherently low-confidence intermittent detection.

2. **Detector temporal jitter (the residual OCR cost).** column_seeds flicker: 9 of 16 per-frame seed
   spawns land at `best_iou < 0.1` (a NEW position each frame, not the same box wobbling). This is a
   detection-*precision* problem. A track-matching stabilizer / canonical-bbox (the intuitive "Phase 4B/4C")
   was measured to only rescue ~5 near-miss spawns and was **deliberately not built**. Seed admission
   (Patch 5) is the accepted answer: gate low-value flicker out of OCR rather than try to track it.

3. **`hard_mixed_art_text` (既視感 / 視感 region) is unsolved by design.** Where red title text overlaps
   red-umbrella character art, text and art share colour, a continuous size ladder, position, and density —
   cheap-CV size/alignment/fill has **no separating signal** (measured). It is tagged and withheld from
   realtime, never OCR'd. The only real path is a **learned detector or VLM** (e.g. Qwen2-VL / PaddleOCR-VL)
   run at low frequency on stable hard_mixed blocks. **Not started.**

4. **Recall-source CPU cost not yet gated.** #2's `blackhat_close_full` adds 1 morphology + CC pass per
   frame. Cheap vs OCR, but for true realtime it should be gated (cold-start / periodic / on lost-track)
   rather than run every frame. Low priority (CPU, not OCR). Not done.

5. **Still bench-only.** All of the above runs in `detector_preview.py` / `temporal_stream.py` on discrete
   frames or short windows. It is **not yet wired into the live region engine / WebView app**. Integrating
   the detector→cache→reader chain behind the existing Region tab is the productization step.

6. **Scene-motion caveat on the numbers.** 48.0–48.6s is animation; some of the frame-to-frame "flicker"
   may be genuine content change (correct re-OCR), so the 15/23 counts are an upper bound on true jitter.

---

## Files

```
block_detector.py     detector: proposals, seeds, confirm, broad-split, parent/child NMS
columnizer.py         block→columns cheap-CV core (unchanged contracts)
temporal_cache.py     TemporalBlockCache (state machine + kind-tiered stable_frames) + self-check
temporal_stream.py    stream harness: detector→cache, OCR-call metrics, mode switch (--seed-stable)
detector_preview.py   single-frame bench: --stage proposal|confirm|ocr, overlays, metrics.tsv
robustness.py         core regression gate (raw 16/20 | core 15/15)
CONCLUSION-GROUPING-FINDING.md   full measured log, UPDATE 3→9
```

## Suggested next decision (not yet chosen)

- **Productize**: wire detector→cache→reader into the live region engine, Mode B default, Mode A toggle.
- **Or** start the learned-detector/VLM line for the hard_mixed region (Problem 3).
- **Or** gate the recall-source CC (Problem 4) if realtime CPU headroom is tight.
