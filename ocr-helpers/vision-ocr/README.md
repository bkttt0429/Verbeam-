# Apple Vision OCR helper (`verbeam-vision-ocr`)

macOS-only OCR helper that wraps Apple's **Vision** framework (`VNRecognizeTextRequest`) and is
driven by `AppleVisionOcrProvider` over stdout JSON. It is the macOS analog of OneOCR on Windows:
NPU-accelerated, ~tens of ms, and on macOS 13+ it natively reads
`ja-JP, zh-Hans, zh-Hant, yue-Hant, ko-KR` plus `ru-RU, uk-UA, th-TH, vi-VT` and the Latin set.

## Build (on a Mac, macOS 13+)

```sh
cd app/ocr-helpers/vision-ocr
swiftc -O main.swift -o verbeam-vision-ocr
chmod +x verbeam-vision-ocr
```

No third-party dependencies — only the system `Vision` / `ImageIO` / `CoreGraphics` frameworks.

## How the app finds it

`AppleVisionOcrProvider.TryProbeAvailability` resolves the binary in this order (macOS only):

1. an explicit configured path (currently unused — reserved),
2. the `VERBEAM_VISION_OCR_PATH` environment variable,
3. `<ContentRoot>/ocr-helpers/vision-ocr/verbeam-vision-ocr`,
4. `<ContentRoot>/ocr-helpers/verbeam-vision-ocr`.

For a dev run, the simplest path is to export the env var to the built binary:

```sh
export VERBEAM_VISION_OCR_PATH="$PWD/app/ocr-helpers/vision-ocr/verbeam-vision-ocr"
```

When the binary resolves, the provider registers under the name **`apple-vision`** and the realtime
router prefers it on macOS (falling back to `rapidocr-net`). Off-macOS or when the binary is missing
the provider is simply not registered.

## Contract

```
verbeam-vision-ocr --image <path> --language <ja|zh-TW|zh|ko|en|...>
```

Prints to stdout:

```json
{
  "text": "first line\nsecond line",
  "blocks": [
    { "text": "first line", "confidence": 0.97,
      "boundingBox": { "x": 10, "y": 8, "width": 220, "height": 36 } }
  ],
  "engine": "apple-vision"
}
```

Bounding boxes are top-left pixel coordinates (Vision's normalized bottom-left rects are converted).

## Known limitations

- macOS only.
- Vision's Chinese recognition handles **vertical / 縦書き** text poorly (known Apple limitation).
- No Arabic / Hebrew / Hindi / Greek (use a Python engine for those scripts).
