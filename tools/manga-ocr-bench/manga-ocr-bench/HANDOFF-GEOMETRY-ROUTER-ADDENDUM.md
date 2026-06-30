# Handoff Addendum: Geometry/OCR Contract

Date: 2026-06-29

Read this with `HANDOFF-COLUMNIZER.md`.

Canonical plan:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\GEOMETRY-ROUTER-PLAN.md
```

Reader routing follow-up:

```text
D:\LocalTranslateHub\.codex-run\manga-ocr-bench\READER-ROUTING-PLAN.md
```

Decision after `preprocess_probe.py`:

```text
mask = layout / column / reject evidence
raw crop = OCR input
```

Do not feed these to manga-ocr as the normal path:

```text
mask_bw
mask_bw_dilate
orig_on_white
Lab mask
edge mask
gradient mask
```

Next bench work should follow this order:

1. Refactor mask evaluation into candidate objects without behavior change.
2. Keep GAP-B Lab as fallback only, not forced onto corrected color ROIs.
3. Add multi-block reject before Columnizer.
4. Use edge/gradient only as a GAP-C geometry helper for 37s, with border/long-line cleanup.
5. Add ruby/furigana filtering after column proposal.
6. Before multilingual integration, build a reader matrix bench. Japanese vertical remains Columnizer + manga-ocr; non-Japanese scripts must route to script/layout-specific readers.

2026-06-29 update: Japanese vertical routing is now implemented in the bench. See `reader_routes.py`; `robustness.py` full mode prints `reader=ja_mangaocr` only for `layout=vertical_rl`, and uses `ocr_calls=0` for horizontal, no_text, and unknown layouts.

Current acceptance baseline:

```text
venv\Scripts\python.exe robustness.py --no-ocr
  raw 16/20 | core 15/15

venv\Scripts\python.exe robustness.py
  raw 16/20 | core 15/15
```
