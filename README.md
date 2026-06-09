# Verbeam

Verbeam is a local translation gateway for MORT Custom API, OCR tools, and reading workflows. It exposes a MORT-compatible `POST /translate` endpoint, calls a local Ollama model, and adds prompt presets, glossary terms, and a SQLite translation cache.

No API key. No cloud upload. No subscription by default.

Product direction: Verbeam is evolving toward an "即時螢幕語境翻譯器" that remembers terms, story context, character voice, and user corrections. See the Chinese [product roadmap](docs/product-roadmap.md).

## Quick Start

Requirements:

- .NET 9 SDK or runtime
- Ollama running at `http://localhost:11434`
- A local translation-capable model, for example `verbeam-mort-qwen2.5-0.5b:latest`

Run:

```powershell
dotnet run --project .\src\Verbeam.Api\Verbeam.Api.csproj
```

The API listens on `http://localhost:5757` by default.

To start the prepared local environment, including the standalone Ollama server and Verbeam API:

```powershell
.\scripts\start-env.ps1
```

To stop both local services:

```powershell
.\scripts\stop-env.ps1
```

Smoke test with the mock provider:

```powershell
$json = '{"text":"こんにちは、勇者さん。","source":"ja","target":"zh-TW","provider":"mock"}'
$body = [System.Text.Encoding]::UTF8.GetBytes($json)
Invoke-RestMethod http://localhost:5757/translate -Method Post -ContentType 'application/json; charset=utf-8' -Body $body
```

Smoke test with Ollama:

```powershell
$json = '{"text":"おはようございます。今日はいい天気ですね。","source":"ja","target":"zh-TW","provider":"ollama","mode":"game_dialogue"}'
$body = [System.Text.Encoding]::UTF8.GetBytes($json)
Invoke-RestMethod http://localhost:5757/translate -Method Post -ContentType 'application/json; charset=utf-8' -Body $body
```

MORT should point its Custom API URL at:

```text
http://localhost:5757/translate
```

## Endpoints

- `POST /translate` returns MORT-compatible JSON: `result`, `errorCode`, `errorMessage`.
- `GET /health` shows configuration and data paths.
- `GET /providers` lists available providers.
- `GET /ocr/providers` lists available OCR providers.
- `POST /ocr` runs OCR on a base64 image payload.
- `POST /ocr/translate` runs OCR, then translates the recognized text.
- `GET /asr/providers` lists available ASR providers.
- `GET /asr/engines` lists configured ASR engines.
- `POST /asr` transcribes a base64 audio payload or a YouTube/audio URL.
- `POST /asr/translate` transcribes audio, then translates each timed segment.
- `GET /asr/live` accepts a WebSocket stream of PCM16 mono 16 kHz chunks.
- `GET /presets` lists prompt presets.
- `GET /glossaries` lists glossary files.
- `GET /app` opens the OpenCode desktop-style local workbench.
- `GET /viewer` opens the mobile/tablet translation viewer.
- `GET /broadcast` accepts WebSocket clients for live translation events.
- `GET /broadcast/latest` returns the latest broadcast translation, if any.

## OCR API

The first OCR slice is backend-only. It accepts a base64 image payload and returns recognized text blocks. The built-in `mock` OCR provider is for tests and wiring checks; configure `external` to call a local RapidOCR/Python command.

The `/app` workbench also supports choosing, dragging, or pasting an image. It auto-runs OCR, lets you edit the OCR text, then translates the corrected text with `Translate OCR Text`. It also includes Audio and Audio + Translate panels for audio files or YouTube/audio URLs, with segment output and SRT/VTT copy actions.

OCR smoke test with the mock provider:

```powershell
$bytes = [System.Text.Encoding]::UTF8.GetBytes("こんにちは OCR")
$image = [Convert]::ToBase64String($bytes)
$json = @{ imageBase64 = $image; provider = "mock"; language = "ja" } | ConvertTo-Json
Invoke-RestMethod http://localhost:5757/ocr -Method Post -ContentType 'application/json; charset=utf-8' -Body $json
```

OCR then translate:

```powershell
$json = @{
  imageBase64 = $image
  ocrProvider = "mock"
  translationProvider = "mock"
  source = "ja"
  target = "zh-TW"
  mode = "game_dialogue"
} | ConvertTo-Json
Invoke-RestMethod http://localhost:5757/ocr/translate -Method Post -ContentType 'application/json; charset=utf-8' -Body $json
```

## ASR API

The ASR pipeline mirrors OCR: `mock` is for wiring tests, `funasr-http` calls a local FunASR/SenseVoice OpenAI-compatible server, and `external` can run a configured command.

Start the local FunASR/SenseVoice server:

```powershell
.\scripts\start-asr.ps1
```

The script creates a Python venv under `app\.asr-funasr`, stores model caches under `models\funasr`, and starts an OpenAI-compatible ASR endpoint at `http://localhost:8000/v1/audio/transcriptions`. The first launch downloads SenseVoiceSmall and can take several minutes. `.\scripts\start-env.ps1` also starts ASR unless `VB_SKIP_ASR=1` is set.

ASR smoke test with the mock provider:

```powershell
$bytes = [System.Text.Encoding]::UTF8.GetBytes("hello audio`nsecond line")
$audio = [Convert]::ToBase64String($bytes)
$json = @{ audioBase64 = $audio; provider = "mock"; language = "en" } | ConvertTo-Json
Invoke-RestMethod http://localhost:5757/asr -Method Post -ContentType 'application/json; charset=utf-8' -Body $json
```

ASR then translate:

```powershell
$json = @{
  audioBase64 = $audio
  speechProvider = "mock"
  translationProvider = "mock"
  source = "en"
  target = "zh-TW"
  mode = "subtitle"
} | ConvertTo-Json
Invoke-RestMethod http://localhost:5757/asr/translate -Method Post -ContentType 'application/json; charset=utf-8' -Body $json
```

YouTube URLs can be passed through `sourceUrl`; Verbeam tries captions first, then falls back to `yt-dlp` + `ffmpeg` audio extraction when those tools are configured.

## Pro Broadcast Mode

Open `http://localhost:5757/viewer` on a second screen to see translations without a game overlay on the main display.

For a phone or tablet on the same LAN, start the API on a reachable interface:

```powershell
$env:VB_Urls='http://0.0.0.0:5757'
dotnet run --project .\src\Verbeam.Api\Verbeam.Api.csproj
```

Then open:

```text
http://<your-pc-lan-ip>:5757/viewer
```

The viewer connects to `/broadcast` over WebSocket and updates whenever `POST /translate` returns a successful translation.

## Configuration

Edit `src/Verbeam.Api/appsettings.json` or use environment variables with the `VB_` prefix.

Defaults:

- Provider: `ollama`
- Ollama URL: `http://localhost:11434`
- Model: `verbeam-mort-qwen2.5-0.5b:latest`
- Ollama low-latency options: `num_ctx=1024`, `num_predict=64`, `temperature=0`, `keep_alive=30m`
- Mode: `game_dialogue`
- Source/target: `ja` to `zh-TW`

## License Notes

This project is MIT licensed. The `upstream/` repositories are reference checkouts only. Do not copy GPL code from Read Frog into this project unless the license strategy changes.

## Sponsor Note

If this tool helps you play untranslated games or read foreign content privately, consider sponsoring development once a public project page exists.
