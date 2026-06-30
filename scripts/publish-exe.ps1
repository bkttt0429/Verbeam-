$ErrorActionPreference = "Stop"

# Publishes Verbeam.Api as a self-contained single-file exe into app\dist\Verbeam.
# The output folder sits exactly two levels under app\ so every ContentRootPath
# relative dependency (..\..\scripts, ..\..\.ocr-set\venv, ..\..\data, ollama
# auto-start candidates) resolves the same way as a dev run from src\Verbeam.Api.

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Project = Join-Path $Root "src\Verbeam.Api\Verbeam.Api.csproj"
$Output = Join-Path $Root "dist\Verbeam"

# Preserve runtime data: `dotnet publish -o` copies the project's data\ over dist\Verbeam\data\,
# which would wipe configured API suppliers + their DPAPI keys + the active route (StorePath /
# SecretsPath / RoutesPath default to data\... = inside this output dir; only the translations DB
# at ..\..\data survives because it lives outside the output). Back it up here, restore after publish.
$DataDir = Join-Path $Output "data"
$HadData = Test-Path $DataDir
$DataBackup = Join-Path ([System.IO.Path]::GetTempPath()) ("verbeam-data-" + [Guid]::NewGuid().ToString("N"))
if ($HadData) {
    New-Item -ItemType Directory -Path $DataBackup -Force | Out-Null
    Copy-Item -Path (Join-Path $DataDir "*") -Destination $DataBackup -Recurse -Force
}

dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    "-p:OutputPath=bin\publish-verify\" `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Restore preserved runtime data over the template copies publish just wrote.
if ($HadData) {
    Copy-Item -Path (Join-Path $DataBackup "*") -Destination $DataDir -Recurse -Force
    Remove-Item $DataBackup -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Restored runtime data (api-suppliers / secrets / routes) into $DataDir"
}

$PresetsSource = Join-Path $Root "presets"
$PresetsOutput = Join-Path $Output "presets"
if (-not (Test-Path $PresetsSource)) {
    throw "Required presets source directory was not found: $PresetsSource"
}
New-Item -ItemType Directory -Path $PresetsOutput -Force | Out-Null
Copy-Item -Path (Join-Path $PresetsSource "*") -Destination $PresetsOutput -Recurse -Force

$GlossariesSource = Join-Path $Root "glossaries"
$GlossariesOutput = Join-Path $Output "glossaries"
if (-not (Test-Path $GlossariesSource)) {
    throw "Required glossaries source directory was not found: $GlossariesSource"
}
New-Item -ItemType Directory -Path $GlossariesOutput -Force | Out-Null
Copy-Item -Path (Join-Path $GlossariesSource "*") -Destination $GlossariesOutput -Recurse -Force

$PresetCount = @(Get-ChildItem $PresetsOutput -Recurse -File -Filter "*.json").Count
$GlossaryCount = @(Get-ChildItem $GlossariesOutput -Recurse -File -Filter "*.json").Count
if ($PresetCount -le 0 -or $GlossaryCount -le 0) {
    throw "Published translation payload is incomplete. Presets: $PresetCount, glossaries: $GlossaryCount"
}

Write-Host "Verified translation payload in $PresetsOutput ($PresetCount presets) and $GlossariesOutput ($GlossaryCount glossaries)"

$OcrModelsSource = Join-Path (Split-Path $Project) "ocr-models"
$OcrModelsOutput = Join-Path $Output "ocr-models"
if (-not (Test-Path $OcrModelsSource)) {
    throw "Required OCR model source directory was not found: $OcrModelsSource"
}

New-Item -ItemType Directory -Path $OcrModelsOutput -Force | Out-Null
Copy-Item -Path (Join-Path $OcrModelsSource "*") -Destination $OcrModelsOutput -Recurse -Force

$RequiredOcrModels = @(
    "ch_PP-OCRv5_server_det.onnx",
    "ch_PP-OCRv5_rec_mobile_infer.onnx",
    "ppocrv5_dict.txt",
    "japan_PP-OCRv4_rec_infer.onnx",
    "japan_dict.txt",
    "ocr_rec_catalog.json",
    "ppocrv6\PP-OCRv6_medium_det_inference.onnx",
    "ppocrv6\PP-OCRv6_small_rec_inference.onnx",
    "ppocrv6\ppocrv6_small_dict.txt"
)

$MissingOcrModels = @()
foreach ($RelativePath in $RequiredOcrModels) {
    $ModelPath = Join-Path $OcrModelsOutput $RelativePath
    if (-not (Test-Path $ModelPath)) {
        $MissingOcrModels += $RelativePath
    }
}

if ($MissingOcrModels.Count -gt 0) {
    throw "Published OCR model payload is incomplete. Missing: $($MissingOcrModels -join ', ')"
}

Write-Host "Verified OCR model payload in $OcrModelsOutput"

$Launcher = Join-Path $Output "Verbeam.cmd"
@"
@echo off
rem ContentRootPath follows the working directory; pin it to this folder so the
rem ..\..\ relative paths (scripts, .ocr-set, data) resolve no matter where the
rem launcher is invoked from.
cd /d "%~dp0"
start "" "%~dp0Verbeam.Api.exe" --tray
"@ | Out-File -FilePath $Launcher -Encoding ascii

$ExePath = Join-Path $Output "Verbeam.Api.exe"
$SizeMb = [Math]::Round((Get-Item $ExePath).Length / 1MB, 1)
Write-Host ""
Write-Host "Published: $ExePath ($SizeMb MB)"
Write-Host "Tray launcher: $Launcher (runs with --tray; pin or drop a shortcut into shell:startup)"
Write-Host "Note: keep dist\Verbeam inside the app folder so ..\..\ paths (scripts, .ocr-set, data) keep working."
