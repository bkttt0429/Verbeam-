# OCR SET

Verbeam separates OCR engines into three lanes:

| Lane | Engines | Status |
| --- | --- | --- |
| Available built-in | Windows OCR, Mock OCR | Ready now |
| Local installable | Tesseract, RapidOCR + PP-OCRv5, EasyOCR, PaddleOCR text, Pix2Text | Provider wired; dependencies must be installed locally |
| Local structure installable | PP-StructureV3, PaddleOCR-VL, dots.ocr-compatible client | Provider wired; dependencies/model cache must be installed locally |
| Planned structure | Snipping Tool OCR | Listed for routing/design; not wired yet |
| API listed | Google Cloud Vision, DeepSeek-OCR / VLM OCR, Mathpix | Listed only; no API integration yet |

## Installable Local Engines

Run status without installing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine status
```

Install one Python engine into the isolated OCR venv:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine easyocr -Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine rapidocr-ppocrv5 -Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine paddleocr -Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine pix2text -Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine pp-structure-v3 -Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine paddleocr-vl -Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine dots-ocr -Install
```

Install all Python engines and local structure clients:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine all -Install
```

`PP-StructureV3` and `PaddleOCR-VL` share PaddleOCR's document parser dependency set. `dots.ocr` is not a normal PyPI OCR package; this project wires a Hugging Face/Transformers-compatible local client and reports missing runtime dependencies or model cache issues through `/ocr/engines`.

Tesseract is an external Windows program. The installer script can call `winget`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -InstallTesseract
```

After installation, restart the API and check:

```powershell
Invoke-RestMethod http://localhost:5758/ocr/engines
```

## Structure OCR Rule

Math and tables are structure OCR, not line OCR:

- Formulas should bypass translation after OCR and be re-rendered as LaTeX/structure.
- Tables should keep the grid and translate only textual cells.
- Rapid line OCR is for dialogue/prose, not formulas or tables.

## OCR Optimization Todo

### P0 - Immediate Flow Fixes

- [x] Add OCR engine routing profiles: speed class, content type, runtime kind, and expected latency.
- [x] Add automatic OCR routing:
  - realtime dialogue/prose -> `external` or `rapidocr-ppocrv5`
  - CJK screenshot text -> `paddleocr`
  - formula/table/document region -> `pp-structure-v3`
  - manual high-accuracy structure OCR -> `paddleocr-vl`
  - `dots-ocr` -> only when model cache exists or user explicitly chooses it
- [x] Add OCR result cache keyed by `imageHash + provider + language + normalizeWhitespace + correctionVersion + engineModelVersion`.
- [x] Show OCR engine cost in UI: realtime, local text, structure, slow VLM, model missing.
- [x] Mark `dots-ocr` as `model_missing` when runtime dependencies exist but model weights are not cached.

### P1 - Structure-Aware OCR and Translation

- [x] Extend OCR result shape with `segments`, not only flat `text`:
  - `text` segment: prose/dialogue
  - `formula` segment: LaTeX/structure, bypass translation
  - `table` segment: cells/grid, translate textual cells only
  - `image/chart` segment: preserve or summarize later
- [x] Update `/ocr/translate` to route by segment:
  - prose -> normal translation
  - formula -> pass-through
  - table -> cell-by-cell translation, keep row/column layout
- [x] Preserve structured OCR metadata from `pp-structure-v3`, `paddleocr-vl`, and `pix2text`.
- [x] Add manual UI mode and cancel control for structure OCR so long-running jobs do not look frozen.

### P2 - Latency and Reliability

- [x] Add a persistent Python OCR worker for heavy Python engines to avoid loading models on every request.
- [x] Add async OCR jobs for slow engines: `/ocr/jobs`, job status, cancel, and result retrieval.
- [x] Add OCR history/job list UI backed by `/ocr/events` and `/ocr/jobs`.
- [x] Add image preprocessing presets: `none`, `upscale`, `contrast`, `threshold`, `denoise`, `crop-padding`, `text-line`, `screenshot`, `document`, `table`, and `formula` through API/cache/local Python wrapper.
- [x] Add OCR correction fuzzy matching: NFKC, punctuation/space normalization, edit distance 1-2, and CJK/kana glyph confusion table.
- [x] Add OCR correction management UI backed by `/ocr/corrections`.
- [x] Add OCR correction deactivate/reactivate flow so wrong correction memory can be safely retired.
- [x] Add per-engine concurrency limits so `paddleocr-vl` cannot block fast OCR requests.
- [x] Add model cache health checks: model directory exists, expected large files exist, and model source is not just runtime deps.

### P3 - Future Engine Work

- [x] Wire RapidOCR + PP-OCRv5 as a low-latency text-line path.
- [x] Add OCR smoke test metrics to compare engines before deciding on C++/ONNX realtime work.
- [x] Persist recent OCR smoke test results for engine comparison.
- [ ] Wire Snipping Tool OCR only if it adds value beyond Windows OCR.
- [ ] Consider C++/ONNX route for realtime OCR after the Python path proves the target accuracy.

## Python vs C++ Fit

| OCR Engine | Current Runtime | Best Fit | Why |
| --- | --- | --- | --- |
| Windows OCR (`external`) | PowerShell/WinRT wrapper | C#/WinRT, not C++ | Already local and light. Keep as Windows API path unless replacing the wrapper. |
| Tesseract (`tesseract`) | Native executable called from Python wrapper | C++ or CLI | Tesseract is native C++. Good candidate for direct native/CLI use. |
| EasyOCR (`easyocr`) | Python/PyTorch | Python | Library ecosystem and model loading are Python-first. Not worth porting for this app. |
| PaddleOCR text (`paddleocr`) | Python/PaddleOCR | Python first, C++ possible later | Current API is easiest in Python. C++/Paddle Inference or ONNX is useful only for a realtime optimized path. |
| RapidOCR + PP-OCRv5 (`rapidocr-ppocrv5`) | Python/ONNX provider wired | C++/ONNX or Python | Low-latency text-line OCR path. Python/ONNX is wired first; C++/ONNX remains a later optimization if this proves accurate. |
| PP-StructureV3 (`pp-structure-v3`) | Python/PaddleOCR pipeline | Python | Structure pipeline orchestration is Python-first. C++ would require rebuilding several modules. |
| PaddleOCR-VL (`paddleocr-vl`) | Python/PaddleOCR VLM | Python or external serving | Heavy VLM path. Keep Python/serving; not a good C++ app-embedded target. |
| Pix2Text (`pix2text`) | Python | Python | Math/table extraction stack is Python-first. |
| dots.ocr (`dots-ocr`) | Python/Transformers client | Python or external serving | Large VLM model. Use Python/Transformers or a separate inference server. |
| Google Cloud Vision | API listed | HTTP client | No local runtime. |
| DeepSeek-OCR / VLM OCR | API listed | HTTP client or external serving | Treat as external VLM/OCR service. |
| Mathpix | API listed | HTTP client | Cloud API; no local C++ runtime. |

Rule of thumb:

- Use Python for accuracy-first OCR, structure OCR, VLM OCR, and model experimentation.
- Use C++/ONNX/native only for a narrow realtime OCR lane where startup time, per-frame latency, and memory pressure matter.
- Keep formulas/tables in the Python structure lane until the intermediate representation is stable.
