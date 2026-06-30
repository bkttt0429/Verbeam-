# Reader Routing Plan

Date: 2026-06-29

Decision:

```text
Japanese vertical text uses Columnizer + manga-ocr.
Other languages should not look for a "manga-ocr equivalent".
Route by script + layout to different readers.
```

This document records a routing plan, not an implemented bench result. External reader choices below are candidates that must be benchmarked on local crops before being promoted into the realtime path.

Implementation status:

```text
Implemented in bench:
  vertical_ja -> Columnizer -> raw column crops -> manga-ocr

Files:
  reader_routes.py  # Japanese route + raw-crop OCR contract
  robustness.py     # full mode now prints route/ocr_calls and skips non-vertical layouts

Not implemented:
  horizontal readers
  Chinese/Korean vertical readers
  stacked Latin
  fallback/document readers
```

## Contract With Geometry Router

This builds on `GEOMETRY-ROUTER-PLAN.md`:

```text
mask = geometry evidence
raw crop = OCR input
Columnizer = reusable geometry adapter
Reader = script/layout-specific recognizer
```

Columnizer may be shared across CJK-style vertical layouts because it outputs raw column crop bboxes. The reader behind those crops must be selected by script and layout.

Do not feed mask images to readers as the normal path:

```text
mask_bw
orig_on_white
Lab mask
edge mask
gradient mask
```

## High-Level Routing

```text
Layout / script router
  -> horizontal_any_language
       -> RapidOCR / PP-OCRv6 / PP-OCRv5 script-specific

  -> vertical_ja
       -> Columnizer -> raw single-column crop -> manga-ocr

  -> vertical_zh / vertical_ko
       -> Columnizer -> raw single-column crop -> CnOCR / Tesseract *_vert / benchmark fallback

  -> vertical_mongolian / rare vertical scripts
       -> fallback / custom model

  -> stacked_latin
       -> char-cell recognizer / Latin reader

  -> unknown / multi_block / low_confidence
       -> reject, split first, or offline fallback
```

## Japanese Vertical

Primary:

```text
vertical_ja
  -> Columnizer
  -> raw single-column crop
  -> manga-ocr
```

Reason:

- This bench already shows manga-ocr reads vertical Japanese manga-style crops far better than the current PP-OCR route.
- Manga-ocr should receive tight raw single-column crops, not loose multi-column regions and not preprocessed mask images.
- Multi-column vertical Japanese must be split before OCR. Whole loose ROI -> manga-ocr causes hallucination.

Do not use manga-ocr for:

```text
horizontal multilingual text
Chinese/Korean vertical as an assumed default
whole-frame multi-block ROI
```

Current bench behavior:

```text
layout=vertical_rl:
  reader=ja_mangaocr
  OCR each raw column crop

layout=horizontal_ltr:
  reader=None
  ocr_calls=0
  reason=horizontal_reader_not_implemented_in_ja_bench

layout=unknown:
  reader=None
  ocr_calls=0
  reason=hold_unknown_layout

layout=no_text / reject:
  reader=None
  ocr_calls=0
```

## Horizontal Text

Default:

```text
horizontal_ltr / horizontal_cjk / horizontal_latin
  -> RapidOCR / PP-OCRv6 small or medium
```

Script-specific branches to benchmark:

```text
horizontal_arabic / persian / urdu
  -> RapidOCR PP-OCRv5 Arabic or equivalent script-specific reader
  -> bidi-aware postprocess

horizontal_devanagari
  -> RapidOCR PP-OCRv5 Devanagari

horizontal_tamil
  -> RapidOCR PP-OCRv5 Tamil

horizontal_telugu
  -> RapidOCR PP-OCRv5 Telugu

horizontal_thai
  -> RapidOCR PP-OCRv5 Thai

horizontal_greek / cyrillic
  -> RapidOCR PP-OCRv5 script-specific reader
```

Rules:

- Do not run Columnizer for normal horizontal text.
- Do not split Arabic/Indic/Thai into character cells; preserve grapheme clusters, diacritics, matras, tone marks, and ligatures.
- Use extra padding for scripts with marks above/below the baseline.

## Chinese Vertical

Candidate routing:

```text
vertical_zh_sim
  -> Columnizer
  -> raw single-column crop
  -> CnOCR ch_PP-OCRv3
  -> secondary: Tesseract chi_sim_vert

vertical_zh_tra
  -> Columnizer
  -> raw single-column crop
  -> Tesseract chi_tra_vert
  -> secondary: CnOCR / PaddleOCR-VL / Surya after benchmark
```

Notes:

- Do not assume manga-ocr generalizes to Chinese vertical.
- Treat Simplified and Traditional separately in benchmark data.
- Reader decision should be based on CER, garbage rate, latency, RAM, and VRAM on local crops.

## Korean Vertical

Candidate routing:

```text
vertical_ko
  -> Columnizer
  -> raw single-column crop
  -> CnOCR korean_PP-OCRv3
  -> secondary: Tesseract kor_vert
```

Notes:

- Benchmark CnOCR vs Tesseract before choosing a default.
- Korean vertical should not inherit Japanese right-to-left assumptions blindly; keep script/layout metadata explicit.

## Mongolian and Rare Vertical Scripts

Candidate routing:

```text
vertical_mongolian
  -> not realtime core for now
  -> fallback / custom model / offline reader
```

Important:

- Mongolian is not CJK vertical-rl. It may need `vertical_lr` ordering and a script-specific recognizer.
- Do not reuse CJK column order rules without script-specific validation.

## Stacked Latin

Example:

```text
H
O
T
E
L
```

This is not Japanese vertical and not normal horizontal line OCR.

Candidate routing:

```text
stacked_latin
  -> char-cell or connected-component sequence
  -> top-to-bottom sort
  -> Latin reader / classifier
```

Do not send stacked Latin to manga-ocr.

## Unknown, Multi-Block, and Low Confidence

Rules:

```text
unknown
  -> hold previous / wait for stable geometry / fallback

multi_block
  -> split or reject before Columnizer

low_confidence
  -> secondary reader only after primary route fails
```

Large VLM/document OCR systems belong here as fallback, teacher, or offline mode, not the realtime hot path.

Candidate fallback bucket:

```text
PaddleOCR-VL
Surya
other document/layout OCR
```

Do not use these in the default realtime path until latency, RAM, VRAM, and accuracy are locally measured.

## Reader Registry Draft

| Layout / Script | Primary candidate | Secondary / fallback | Notes |
|---|---|---|---|
| horizontal Latin / CJK / Japanese | RapidOCR PP-OCRv6 small/medium | EasyOCR / Tesseract | realtime default candidate |
| horizontal Arabic/Persian/Urdu | RapidOCR PP-OCRv5 Arabic | Tesseract / EasyOCR | needs bidi postprocess |
| horizontal Devanagari | RapidOCR PP-OCRv5 Devanagari | Tesseract / EasyOCR | do not char-split |
| horizontal Thai | RapidOCR PP-OCRv5 Thai | EasyOCR / Tesseract | preserve marks and padding |
| horizontal Korean | RapidOCR PP-OCRv6 or PP-OCRv5 Korean | EasyOCR | realtime candidate |
| vertical Japanese | Columnizer + manga-ocr | CnOCR japan_PP-OCRv3 / fallback | already proven useful in bench |
| vertical Simplified Chinese | Columnizer + CnOCR ch_PP-OCRv3 | Tesseract chi_sim_vert / fallback | benchmark required |
| vertical Traditional Chinese | Columnizer + Tesseract chi_tra_vert | PaddleOCR-VL / Surya | benchmark required |
| vertical Korean | Columnizer + CnOCR korean_PP-OCRv3 | Tesseract kor_vert / Surya | benchmark required |
| vertical Mongolian | custom / fallback | PaddleOCR-VL / Surya | not CJK vertical-rl |
| stacked Latin | char-cell + Latin reader | fallback | special layout |
| unknown / multi_block | reject / split first | PaddleOCR-VL / Surya | do not feed whole ROI to Columnizer |

## Benchmark Before Integrating

Do not wire these readers into the main app first. Create a `reader_matrix.py` style bench using raw crops from the geometry pipeline.

Suggested crop sets:

```text
JA vertical: 20 crops
ZH-sim vertical: 20 crops
ZH-tra vertical: 20 crops
KO vertical: 20 crops
horizontal multilingual: 10-20 crops per script
hard negatives: 20 crops
```

Readers to compare:

```text
ja_mangaocr
cnocr_ch_ppocrv3
cnocr_korean_ppocrv3
tesseract_chi_sim_vert
tesseract_chi_tra_vert
tesseract_kor_vert
rapidocr_ppocrv6
paddleocr_vl_fallback optional
surya optional
```

Metrics:

```text
CER
exact match
empty / garbage rate
latency p50 / p95
RAM / VRAM
fallback rate
```

## Stop Rules

- Do not make manga-ocr the universal vertical reader.
- Do not search for one "manga-ocr replacement" for every language.
- Do not send non-Japanese vertical crops to manga-ocr without benchmark evidence.
- Do not use VLM/document OCR in the realtime hot path without local latency and memory measurements.
- Do not char-split Arabic/Indic/Thai scripts.
- Do not treat stacked Latin as CJK vertical.
- Do not connect this plan to the main app until the bench has a reader matrix.
