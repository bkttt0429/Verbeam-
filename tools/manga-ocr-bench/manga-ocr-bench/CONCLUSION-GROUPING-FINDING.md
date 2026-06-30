# Grouping Finding — over-merge vs under-detect are the SAME root cause

Date: 2026-06-30
For: fresh web session pickup. Read this first, then `BLOCK-DETECTOR-CC-PROPOSAL-SPEC.md`.
Bench dir: `.codex-run/manga-ocr-bench/` (source files in this folder).

---

## TL;DR

The cheap-CV full-frame detector (`block_detector.py`, `scorer="cc"`) is fast and correct in
structure, but its **block grouping is a dumb uniform dilation** that cannot tell
"text + neighbouring text" apart from "text + character art". That single weakness produces
**two opposite-looking failures** on the test clip:

- **42s 「既視感」 (red title): OVER-MERGE.** Text gets glued to the red-umbrella character art
  into one big mixed block → confirms as non-vertical → rejected → no box at all.
- **48s 「これ」 (small white caption): UNDER-DETECT.** A weak 2-glyph column gets swallowed into
  the adjacent text block / is too weak to stand → no clean column box of its own.

These pull in **opposite directions**, so a global "tighten / loosen the dilation kernel" knob
**cannot fix both**. The correct fix is **content-aware grouping (component graph)**: connect
components by *size-similarity + alignment*, which (a) refuses to bond a 40px text glyph to a
200px art blob → fixes 既視感, and (b) bonds collinear small text into a proper multi-column
block and never drops a vote-2 column that is collinear with neighbours → fixes これ.

**Do NOT** spend another round tuning `GROUP_KERNEL_*` / `_nearby_block` thresholds.
**Do NOT** add a Lab proposal source to fix 既視感 (see §3 — it is already proposed by gray).

---

## 1. Current pipeline (what already works)

```
full frame
  → _frame_masks            blackhat_full + tophat_full (gray, morphology, Otsu)   ~50–75ms
  → full_frame_components   component_filter_global() per mask, CC once            ~25–30ms
  → group_components_into_blocks   paint comp bboxes → directional cv2.dilate → CC ~37–45ms
  → _merge_proposal         union/nearby merge across sources
  → _confirm_candidate_on_raw   columnize() each raw crop, reject reasons counted  rest
```

Speed (42s/48s): old sliding-window **5688 / 4989 ms** → cc **339 / 158 ms**. ~15–30×.
Core gate intact: `python robustness.py --no-ocr` → `raw 16/20 | core 15/15`.

Patch-1 (this round) added **reject-reason metrics** so we stop guessing:
`detector_preview.py --stage proposal|confirm|ocr` → `metrics.tsv` has
`raw_proposal_count / merged_proposal_count / proposal_count / confirmed_count` and a
`reject_breakdown` column (`right_edge / status / require_vertical / unknown / weak_mask /
wide_col_low_tl / line_dominated / size`).

---

## 2. Failure A — 42s 「既視感」 OVER-MERGE (measured)

`detector_preview.py --scorer cc --times 42 --stage proposal` shows:

```
proposal [1270,220,1577,815] vote=32   ← red text 既視感 GLUED TO the red-umbrella character
proposal [1340, 94,1560,227] vote=2    ← clean top fragment of 既視感, too weak
```

`--stage confirm` → 42s confirmed = only `[794,123,897,405]` and `[510,474,676,872]`.
The big mixed box is **rejected** (`reject_breakdown` 42s = `{require_vertical:1, right_edge:1,
status:1, weak_mask:1}`; the big mixed block columnizes as non-vertical → `require_vertical`).
`right_edge` is a *different* box `[1682,409,1920,792]`, not 既視感.

**Conclusion:** 既視感 IS proposed by the gray masks; it dies because the umbrella-character art
is merged into its block. This is a grouping precision problem, not a mask-recall problem.

## 3. Failure B — 48s 「これ」 UNDER-DETECT (measured)

Probe of the これ region (frame 48s, ROI x[470–600] y[80–260]):

```
[blackhat_full] comps-in-ROI = 1   (just one noise comp)
[tophat_full]   comps-in-ROI = 2   (the これ glyphs)
                  (564,169, 18x23) area=274 fill=0.66
                  (569,228, 13x17) area=116 fill=0.52
[tophat_full]   group block = (525,122,622,293) vote=2   ← これ IS grouped, but vote=2 (minimum)
```

これ is white text on the red diamond → only **tophat** sees it (blackhat is wrong polarity,
sees 1 noise comp). The vote-2 これ block then gets **absorbed by `_merge_proposal` into the
adjacent `[525,16,735,477]` 「他に何がある？」 proposal** → これ never gets its own clean column.

**Conclusion:** これ IS detected (by tophat) but is too weak / mis-merged into a neighbour.
This is a grouping recall problem — the *opposite* of 既視感.

## 4. Why both are one root cause

`group_components_into_blocks` paints every kept component's bbox onto a canvas and applies a
**uniform directional dilation**, then takes connected components of the dilated canvas. Uniform
dilation bonds **anything within kernel reach**, so it cannot distinguish:

- text glyph ↔ character-art blob  (should NOT bond → 既視感)
- text glyph ↔ collinear text glyph (SHOULD bond into multi-column block → これ)

Tightening the kernel helps 既視感 but kills the weak これ block. Loosening does the reverse.
**The knob is the wrong tool.** The grouping must be *content-aware*.

---

## 5. Recommended fix (next implementation)

Replace `group_components_into_blocks` dilation with a **component-graph grouper** (keep dilation
as `--group dilate` for A/B):

```
build graph over component_filter_global() comps:
  edge(c1, c2) iff
      size_similar:  0.5 <= median(c1.h,c1.w)/median(c2.h,c2.w) <= 2.0   (text↔art refused)
      AND aligned & near (vertical mode):
          |c1.cx - c2.cx| < 0.8 * median_glyph_w        (same column)
          0 < gap(c1,c2)  < ~2.5 * median_glyph_h       (stacked, small gap)
      OR adjacent-column (same block):
          |c1.cy band overlap| high AND column gap < ~1.2 * median_glyph_w
connected components of graph = block proposals
do NOT drop a 2-comp column if it is collinear with a neighbour column (multi-column block)
```

Expected: 既視感 art blobs (large, size-dissimilar to glyphs) won't join the text → text block
survives as vertical → confirms. これ (collinear small text) joins 以上/他に何がある？ as one
R→L multi-column sentence-block → boxed, read in order.

Acceptance (A/B on 42s + 48s):
- 42s: a `vertical_rl` block on 既視感 survives confirm (currently 0).
- 48s: これ is inside a clean column box (its own column within the sentence block).
- `python robustness.py --no-ocr` still `raw 16/20 | core 15/15` (grouping change must not
  touch columnizer core).
- detector_ms stays < ~400ms/frame.

Also pending (unchanged): right-edge / wide-col hard rejects in `_confirm_candidate_on_raw`
should become edge+line-feature conditional (they are content-blind today). Temporal cache is the
realtime unlock after grouping is stable (OCR only blocks stable 2–3 frames; see
`HANDOFF-COLUMNIZER.md` / `BLOCK-DETECTOR-CC-PROPOSAL-SPEC.md` §7).

---

## 6. How to run / verify

```bash
# in .codex-run/manga-ocr-bench/venv
python robustness.py --no-ocr                                   # core gate: raw 16/20 | core 15/15
python detector_preview.py --scorer cc --times 42 48 --stage proposal   # all proposal boxes
python detector_preview.py --scorer cc --times 42 48 --stage confirm    # survivors + reject_breakdown
# outputs: rois/<out>/contact_sheet.png, frames/detector_42.png, frames/detector_48.png,
#          metrics.tsv (reject_breakdown), events.tsv
python detector_preview.py --scorer window --times 42 48 --stage proposal   # old baseline for A/B
```

## 7. File map (in this folder)

```
columnizer.py        block→columns cheap-CV core (mask/polarity/component_filter/layout_gate/columnize)
                     + component_filter_global() (frame-scale CC for proposal)
block_detector.py    full-frame detector. scorer="cc" (new) | "window" (old, debug).
                     group_components_into_blocks() = THE function to replace (§5).
detector_preview.py  bench harness: --stage, metrics.tsv reject_breakdown, contact sheets.
reader_routes.py     raw-crop OCR routing (manga-ocr, vertical-JP only). mask=geometry, raw crop=OCR.
robustness.py        core regression gate (raw 16/20 | core 15/15).
cases.py             hand-labelled ROIs.
CONCLUSION-GROUPING-FINDING.md   <- this file
BLOCK-DETECTOR-CC-PROPOSAL-SPEC.md   the CC-proposal rewrite spec (guardrails, phases)
HANDOFF-COLUMNIZER.md   columnizer judgment chain + numbers + dead-ends
DETECTOR-PREVIEW-NOTES.md   detector history/notes
```

**Invariants (do not break):** mask = geometry only; raw crop = OCR input; detector outputs
blocks (not columns — columns are `columnize()`'s job); never regress `raw 16/20 | core 15/15`.
