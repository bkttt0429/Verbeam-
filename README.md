# Verbeam App

This directory contains the main Verbeam application code.

Verbeam is a local-first translation workspace for screen and game OCR, web page translation, subtitles, audio transcription, document translation, glossary/memory workflows, and local or API-backed model routing.

## Main Direction

The primary desktop client is the Avalonia v2 app:

```text
src/Verbeam.Desktop.Avalonia
```

The older API-hosted WebView workbench still exists and is useful for runtime compatibility and reference behavior:

```text
src/Verbeam.Api/Pages/AppWorkbenchPage.cs
```

Do new desktop UI work in Avalonia first unless the change is specifically about the web workbench, browser extension, backend API, or packaged tray runtime.

## Project Layout

- `src/Verbeam.Desktop.Avalonia/` - primary native desktop UI.
- `src/Verbeam.Api/` - local API, tray shell, WebView workbench, viewer/projector pages, static assets, and HTTP/WebSocket endpoints.
- `src/Verbeam.Core/` - translation, OCR, ASR, memory, model catalog, runtime, storage, and provider services.
- `tests/Verbeam.Tests/` - unit and integration tests.
- `extensions/verbeam-web-translator/` - unpacked Chrome/Edge extension.
- `presets/` - translation prompt presets.
- `glossaries/` - sample glossary data.
- `docs/` - design notes and implementation plans.
- `scripts/` - build, publish, OCR, PDF, model, and local runtime helpers.

## Do Not Commit Local Assets

Large runtime and model files are intentionally local-only. Keep these out of Git:

- `dist/`
- `runtimes/`
- `models/`
- `src/Verbeam.Api/models/`
- `src/Verbeam.Api/ocr-models/`
- `src/Verbeam.Api/runtimes/`
- `.codex-run/`, `.verify/`, `.pdf2zh/`, browser profiles, screenshots, and other generated test output.

The repository tracks catalogs, configuration, source code, tests, scripts, and small checked-in UI assets. It should not track GGUF, ONNX, downloaded runtime archives, packaged builds, local databases, or secrets.

## Build

Run commands from this `app` directory.

```powershell
dotnet build .\Verbeam.sln -c Release --no-restore
```

For a first build on a clean machine, omit `--no-restore`:

```powershell
dotnet build .\Verbeam.sln -c Release
```

## Run

Run the primary Avalonia desktop app:

```powershell
dotnet run --project .\src\Verbeam.Desktop.Avalonia\Verbeam.Desktop.Avalonia.csproj
```

Run the API/workbench manually:

```powershell
dotnet run --project .\src\Verbeam.Api\Verbeam.Api.csproj --urls http://localhost:5768
```

Useful local checks when the API is running:

```powershell
Invoke-RestMethod http://localhost:5768/health
Invoke-RestMethod http://localhost:5768/ocr/engines
```

## Test

Run the test project:

```powershell
dotnet test .\tests\Verbeam.Tests\Verbeam.Tests.csproj -c Release
```

Some OCR/runtime integration tests depend on local GPU, DirectML, model, and process state. For quick UI/catalog/core checks, prefer targeted test filters that match the area you changed.

## Packaging

The packaged daily-runtime output is generated under:

```text
dist/Verbeam
```

That folder is generated output and must stay out of Git. Use the publish script when producing a local packaged build:

```powershell
.\scripts\publish-exe.ps1
```
