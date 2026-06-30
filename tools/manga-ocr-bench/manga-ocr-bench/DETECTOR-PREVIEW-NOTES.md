# Detector Preview Notes

Date: 2026-06-29

This documents the first experimental full-frame block candidate layer.

## What Was Added

Files:

```text
block_detector.py
detector_preview.py
```

This is bench-only and not connected to the app.

The detector is not a trained detector. It scans the full frame with several window sizes, runs the current cheap-CV `columnize()` on each window, then converts accepted column bboxes back into tighter full-frame block proposals. Multiple overlapping proposals vote into a final candidate rank.

2026-06-29 speed update: `block_detector.py` now supports two scorers.

```text
fast  = precompute full-frame blackhat/top-hat masks once, score window crops from those masks, then re-columnize only selected candidates in preview
exact = old behavior; run full columnize() on every sliding window
```

`detector_preview.py` defaults to `--scorer fast`; use `--scorer exact` only for comparison.

2026-06-30 suppression update:

```text
raw confirm     = re-run cheap CV columnize() on each ranked raw crop before keeping it
require vertical = default for this Japanese route; unknown/horizontal candidates are dropped
line reject     = drop crops dominated by long border/character-line components
min_vote=5      = do not fill top-N with weak clusters seen by only a few windows
```

This still does not OCR. It is only a second cheap-CV confirmation step on a small
ranked set, not on every sliding window.

2026-06-30 CC-proposal update:

```text
cc     = full-frame blackhat/tophat masks -> frame-scale CC -> dilation grouping -> raw confirm
window = old sliding-window fast scorer, kept only for debug comparison
```

`columnizer.py` now exposes additive `components` in the result dict and a
`component_filter_global()` sibling for detector proposal only. Existing
`component_filter()`, `layout_gate()`, and `columnize()` callers keep their old
fields and behavior.

`detector_preview.py` now supports:

```text
--stage proposal  draw proposal bboxes only
--stage confirm   proposal + raw-crop columnize confirm, no OCR
--stage ocr       confirm + manga-ocr route
```

It writes `metrics.tsv/json` with mask/CC/group/window/confirm/OCR timings.

The OCR contract remains unchanged:

```text
mask/window = geometry evidence
raw crop = OCR input
```

## Output

Full candidate preview with OCR:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview\contact_sheet.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview\frames\detector_42.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview\frames\detector_48.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview\events.tsv
```

Cleaner top-8 box preview:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_top8\contact_sheet.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_top8\frames\detector_42.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_top8\frames\detector_48.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_top8\events.tsv
```

Faster top-8 box preview:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_top8\contact_sheet.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_top8\frames\detector_42.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_top8\frames\detector_48.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_top8\events.tsv
```

Fast top-8 with raw confirm + vote floor:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_confirm_vote5_top8\contact_sheet.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_confirm_vote5_top8\frames\detector_42.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_confirm_vote5_top8\frames\detector_48.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_fast_confirm_vote5_top8\events.tsv
```

CC proposal/confirm outputs:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_cc_proposal_v3\contact_sheet.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_cc_proposal_v3\metrics.tsv
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_cc_confirm_v3\contact_sheet.png
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\detector_preview_cc_confirm_v3\metrics.tsv
```

## Result

The detector now finds more regions on 42s / 48s than the known-ROI preview, but it is still experimental.

42s top-8 candidates:

```text
[794,168,893,303]      gray text block, good
[1348,121,1540,522]    red/character area, unknown
[510,548,652,754]      purple text block partial/good
[628,239,892,672]      broad mixed candidate
[1348,561,1552,783]    horizontal/character area
[1178,336,1342,606]    noisy character/panel candidate
[969,68,1232,522]      noisy mixed candidate
[408,388,572,772]      purple-side partial candidate
```

48s top-8 candidates:

```text
[201,338,403,512]      teal block fragment / horizontal after re-columnize
[1205,165,1512,379]    white box / nearby character, unknown
[546,68,712,332]       red top text candidate
[647,327,999,806]      purple/pink text block candidate
[228,327,692,784]      teal text block, broad
[1528,468,1692,719]    視感 block candidate
[931,632,1432,878]     lower mixed candidate
[948,165,1305,602]     whitebox / red-top mixed candidate
```

## Current Failure Modes

- False positives on character outlines, umbrella edges, clothing, and panel borders.
- Some text blocks are broad mixed candidates rather than tight semantic blocks.
- Some candidates re-columnize as `unknown` or `horizontal_ltr`, so the Japanese route correctly skips OCR.
- A candidate can contain multiple logical text blocks; multi-block split is still not solved.
- Sliding-window scan is slow. It is a diagnostic detector, not realtime implementation.

Observed runtime:

```text
42s + 48s, max-blocks=14, exact scorer + CPU manga-ocr: ~100s
42s + 48s, max-blocks=8, exact scorer, no OCR: ~30s
42s + 48s, max-blocks=8, fast scorer, no OCR: ~9s
42s + 48s, max-blocks=8, fast scorer + raw confirm + min_vote=5, no OCR: ~16s preview run
42s + 48s, window proposal only, no OCR: 5.69s + 4.99s
42s + 48s, cc proposal only, no OCR: 86ms + 71ms
42s + 48s, cc proposal + confirm, no OCR: 205ms + 88ms
```

The speedup comes from avoiding per-window morphology. The fast scorer computes full-frame blackhat/top-hat masks once, then uses mask crops for candidate scoring. It is still not realtime, but it is much better for visual iteration.

The CC scorer removes the sliding-window loop entirely. Current bottleneck in
the CC path is confirm on ambiguous 42s proposals; it is still under 250ms/frame
on the tested frames.

Full-frame Lab proposal was measured but left disabled: 1080p `_lab_delta_mask`
took ~224-346ms/frame and did not recover the missing 42s red text candidate.

## Why This Still Helps

Before this layer, preview only drew hand-written `cases.py` ROIs. The new detector proves that the existing cheap-CV columnizer can be reused as a block proposal scorer and can recover additional candidates from a whole frame.

It also confirms what needs work before app integration:

```text
1. temporal stability before OCR
2. block grouping/splitting
3. 42s red light-on-color proposal recall
4. candidate false-positive suppression for character/diamond-edge leftovers
5. app integration only after frame-loop stability exists
```

## Next Work

Do not tune manga-ocr or OCR preprocessing for this. The failure is still geometry.

Recommended next steps:

```text
1. Add temporal voting: keep only boxes that remain stable for 2-3 adjacent frames.
2. Add multi-block split inside broad candidates before Columnizer.
3. Test candidate generation from edge/gradient components to reduce sliding-window cost.
4. Replace sliding windows with a proposal source before app integration.
5. Keep OCR disabled until geometry is stable; then OCR only selected vertical_rl raw crops.
```
