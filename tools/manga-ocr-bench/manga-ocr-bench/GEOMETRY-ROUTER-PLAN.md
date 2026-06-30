# Geometry Router Plan

Date: 2026-06-29

This plan captures the decision after the preprocessing probe:

```text
CV masks are for geometry only.
OCR images should stay raw crops by default.
```

Reader selection is a separate script/layout decision. See
`D:\LocalTranslateHub\.codex-run\manga-ocr-bench\READER-ROUTING-PLAN.md` for the routing contract: Japanese vertical uses Columnizer + manga-ocr; other languages should be benchmarked with script/layout-specific readers rather than forced through manga-ocr.

Do not continue testing "which preprocessed image should be fed to manga-ocr" as the main path. The probe already showed that `raw`, `gray3`, and `clahe` are roughly tied, while `mask_bw`, `mask_bw_dilate`, and `orig_on_white` often make manga-ocr worse or more hallucinatory.

## Pipeline Contract

```text
ROI
  -> Preprocess candidate builder
       - gray/Otsu mask          primary
       - Lab color-delta mask    fallback only
       - edge/gradient mask      geometry helper only
  -> Geometry scorer
       - no_text
       - horizontal
       - vertical_single_block
       - multi_block
       - unknown
  -> Layout adapter
       - horizontal: raw ROI -> horizontal reader
       - vertical: Columnizer -> raw column crops -> manga-ocr / vertical reader
       - multi_block: split/reject before Columnizer
       - unknown: hold previous / fallback
```

Hard rule:

```text
mask = geometry evidence
raw crop = OCR input
```

Do not use these as the normal manga-ocr input:

```text
mask_bw
mask_bw_dilate
orig_on_white
edge mask
gradient mask
Lab mask
```

## Candidate Model

Future refactor target:

```python
{
    "source": "gray_otsu | lab_delta | edge_grad",
    "mask": mask,
    "components": comps,
    "occ": 0.21,
    "align": 0.76,
    "layout": "horizontal | vertical | unknown | no_text | multi_block",
    "score": 0.83,
    "weak": False,
}
```

Candidate order:

1. `gray_otsu`: always build first.
2. `lab_delta`: build only if gray is weak, `no_text`, `unknown`, or otherwise ambiguous.
3. `edge_grad`: build only as a geometry helper for ambiguous split/layout cases, especially GAP-C.

Do not OR masks together. Score candidates independently and choose one geometry path.

## Scoring Signals

Use a shared scoring vocabulary across mask candidates:

```text
component_count
alignment_score
layout_confidence
occupancy_penalty
noise_penalty
tall_streak_drop
border_line_drop
layout_margin
```

Important caveat: rain can align like text, so `text_likeness` / alignment cannot be the only reject signal. Occupancy remains the main rain/texture guard.

Current good behavior to preserve:

```text
rain-no-text(69s): reject by high occupancy
storm/cloud negatives: reject
69s horizontal white-on-dark: bypass Columnizer
5s / teal / 21s / 132s / color corrected ROIs: vertical ok
```

## Reader Contract

Pseudocode for the future adapter:

```python
if best.layout == "no_text":
    reject()

elif best.layout == "horizontal":
    read_raw_roi_with_horizontal_reader()

elif best.layout == "vertical":
    columns = columnize_from_geometry(best.mask, best.components)
    for col in columns:
        read_raw_column_crop(col.bbox)

elif best.layout == "multi_block":
    split_blocks_or_reject()

else:
    hold_previous_or_fallback()
```

The OCR crop must be sliced from the original BGR ROI:

```python
raw_roi[y0:y1, x0:x1]
```

The selected mask may decide the bbox, but it is not the image sent to manga-ocr.

## Patch Order

### Patch 1: mask candidate structure

Refactor the current single chosen mask into candidate objects without changing behavior.

Target shape:

```python
def build_mask_candidates(roi):
    candidates = []

    gray = run_gray_otsu(roi)
    candidates.append(gray)

    if gray.weak:
        candidates.append(run_lab_delta(roi))

    if all(c.weak for c in candidates):
        candidates.append(run_edge_grad_geometry(roi))

    return candidates
```

Acceptance:

```text
No behavior change on current 20 cases.
robustness.py --no-ocr: raw 16/20, core 15/15
robustness.py:        raw 16/20, core 15/15
```

### Patch 2: GAP-B Lab fallback

Already mostly implemented in `columnizer.py`:

```text
gray/Otsu primary
Lab local color-delta fallback
mask_dbg.source = gray_otsu | lab_delta
```

Keep the important caveat: corrected purple/blue/pink currently pass with `gray_otsu`; old ROIs were wrong or partial. Lab is a fallback for true gray failures, not a path to force onto color cases.

Acceptance:

```text
color-vert 6/6
core 15/15
raw 16/20
no regression on 21s / 132s / 69s / 5s / teal / rain
```

### Patch 3: multi-block reject

Problem:

```text
Whole-frame 42s/48s contain multiple separated text blocks.
All masks find many text-like components.
Columnizer must not receive whole-frame multi-block input.
```

Target:

```python
if detect_multi_block(comps):
    return {
        "status": "reject",
        "reject_reason": "multi_block_roi",
    }
```

Acceptance:

```text
multiblock-whole(42s): reject, reason=multi_block_roi
multiblock-whole(48s): reject, reason=multi_block_roi
single-block vertical/horizontal cases unchanged
```

### Patch 4: GAP-C edge/gradient geometry helper

Problem:

```text
whitebox-2col(37s) is not an OCR-image problem.
Raw crops read useful text when the crop is meaningful.
The failure is column proposal: gray/lab layout=unknown and over-split into 3 crops.
```

Probe result:

```text
gray/lab: unknown, 3 columns
edge/grad: can expose 2 useful columns, but also captures white frame and character outlines
edge/grad occupancy is high, so it cannot be used as the main mask directly
```

Research direction:

```text
Use edge/gradient only as a geometry helper:
  1. Build gray candidate first.
  2. If gray is unknown / over-split, build edge/grad candidate.
  3. Remove border lines / touching-border components.
  4. Use edge/grad components to propose column groups.
  5. Merge or drop tiny junk columns.
  6. Feed raw column crops to OCR.
```

Junk-column heuristic to test:

```python
def remove_junk_columns(cols):
    main = []
    junk = []
    med_area = median(c.ink_area for c in cols)
    med_width = median(c.width for c in cols)

    for c in cols:
        if (
            c.ink_area < 0.25 * med_area
            and c.component_count <= 2
            and c.width < 0.45 * med_width
        ):
            junk.append(c)
        else:
            main.append(c)

    return main, junk
```

Acceptance:

```text
whitebox-2col(37s): vertical_rl, 2 useful main columns
middle tiny junk crop ignored or merged
OCR input remains raw column crop
```

### Patch 5: furigana / ruby filter

Problem:

```text
light-on-color(48s視感) is layout/ruby contamination, not contrast.
raw/gray/clahe read a useful main text fragment.
mask_bw and orig_on_white hallucinate or produce dots.
```

Target:

```text
Classify small adjacent ruby components/columns after column proposal.
Mark them ignored or metadata.
Do not feed ruby crops as main OCR columns.
```

Heuristic to test:

```python
def is_ruby(col, main_col):
    return (
        col.median_glyph_h < 0.65 * main_col.median_glyph_h
        and abs(col.cx - main_col.cx) < 1.5 * main_col.median_glyph_w
        and y_overlap(col, main_col) > 0.45
        and col.ink_area < 0.45 * main_col.ink_area
    )
```

Acceptance:

```text
furigana/ruby components are ignored or attached as metadata
main text output no longer includes dotted junk columns
```

## Output Schema Target

Current `columnize()` returns most of this already. Future fields should make geometry vs OCR explicit:

```json
{
  "status": "ok | reject | bypass_columnizer",
  "reject_reason": null,
  "mask_source": "gray_otsu",
  "layout": "vertical_rl",
  "polarity": "dark_on_light",
  "mask_dbg": {
    "occ": 0.21,
    "tl": 0.75,
    "n": 8,
    "drop_tall": 3,
    "score": 0.84
  },
  "columns": [
    {
      "order": 0,
      "bbox": [0, 0, 80, 360],
      "type": "main",
      "source": "gray_otsu"
    }
  ],
  "ignored": [
    {
      "type": "ruby",
      "bbox": [80, 0, 110, 180],
      "attached_to": 0
    }
  ]
}
```

Reader code should consume:

```text
status
layout
columns where type == main
```

Reader code should not consume:

```text
mask images as OCR input
ignored ruby crops as main text
unknown layout as forced OCR
whole-frame multi-block as a single Columnizer block
```

## Stop Rules

- Do not feed binarized/whitened masks to manga-ocr as the normal path.
- Do not use OCR to decide normal routing.
- Do not force `layout=unknown` through Columnizer as if it were clean vertical text.
- Do not send whole-frame multi-block ROIs to Columnizer.
- Do not broaden OCC/GATE just to make edge/gradient masks pass.
- Do not expand the test set before the current deferred gaps are handled.
- Do not connect this to the main app until the bench contracts are stable.
