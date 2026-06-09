# OCR SET

Verbeam separates OCR engines into three lanes:

| Lane | Engines | Status |
| --- | --- | --- |
| Available built-in | Windows OCR, Mock OCR | Ready now |
| Local installable | Tesseract, EasyOCR, PaddleOCR text, Pix2Text | Provider wired; dependencies must be installed locally |
| Local structure installable | PP-StructureV3, PaddleOCR-VL, dots.ocr-compatible client | Provider wired; dependencies/model cache must be installed locally |
| Planned structure | RapidOCR + PP-OCRv5, Snipping Tool OCR | Listed for routing/design; not wired yet |
| API listed | Google Cloud Vision, DeepSeek-OCR / VLM OCR, Mathpix | Listed only; no API integration yet |

## Installable Local Engines

Run status without installing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine status
```

Install one Python engine into the isolated OCR venv:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-ocr-set.ps1 -Engine easyocr -Install
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
