# Manga OCR Bench

Experimental Python tooling for manga-style OCR geometry research. This is not
the primary Verbeam desktop app; Avalonia v2 remains the main client. The bench
keeps detector proposal logic, columnization, and preview scripts together so
reviewers can inspect the OCR detector work without committing local models,
videos, ROI crops, or virtual environments.

## Tracked Scope

- `block_detector.py` proposes full-frame text blocks and confirms them on raw
  frame crops with `columnizer.columnize()`.
- `columnizer.py` turns a text ROI into ordered vertical columns or rejects it.
- `detector_preview.py` visualizes proposal, confirm, and OCR stages.
- `realtime_preview.py`, `reader_routes.py`, and `cases.py` provide known ROI
  preview and Japanese manga-ocr routing helpers.
- `robustness.py` and `preprocess_probe.py` are local validation probes.

Generated files belong under `inputs/`, `outputs/`, `rois/`, or `venv/`; those
paths are intentionally ignored.

## Setup

```powershell
cd tools\manga-ocr-bench
python -m venv venv
.\venv\Scripts\python -m pip install -r requirements.txt
```

Place the source video at `inputs\source_1080p.mp4`, or pass `--src` explicitly.

## Checks

Geometry-only robustness check:

```powershell
.\venv\Scripts\python robustness.py --no-ocr --src inputs\source_1080p.mp4
```

Detector preview:

```powershell
.\venv\Scripts\python detector_preview.py --src inputs\source_1080p.mp4 --stage confirm --scorer cc
```

OCR routing requires `manga-ocr` and its model cache:

```powershell
.\venv\Scripts\python detector_preview.py --src inputs\source_1080p.mp4 --stage ocr --ocr
```
