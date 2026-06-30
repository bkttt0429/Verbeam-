<#
.SYNOPSIS
  Sets up the pdf2zh_next venv used by the high-fidelity "auto" PDF export engine and
  generates the offline-assets bundle so exports never download at runtime.

.DESCRIPTION
  -Install            Create the venv (.pdf2zh\venv) and pip install pdf2zh-next.
  -OfflineAssets      Generate the offline-assets zip into .pdf2zh\offline-assets
                      (DocLayout-YOLO ONNX + fonts + cmaps, ~213 MB). Requires the venv.
  (no switch)         Print status.

  pdf2zh-next uses ONNX Runtime (no torch). The export pipeline (DocumentJobService.
  ExportPdf2zhAsync) auto-passes --restore-offline-assets when the bundle is present.
#>
param(
    [switch]$Install,
    [switch]$OfflineAssets
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Venv = Join-Path $Root ".pdf2zh\venv"
$VenvPython = Join-Path $Venv "Scripts\python.exe"
$Pdf2zh = Join-Path $Venv "Scripts\pdf2zh.exe"
$BabelDoc = Join-Path $Venv "Scripts\babeldoc.exe"
$AssetsDir = Join-Path $Root ".pdf2zh\offline-assets"

$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"

function Resolve-BasePython {
    foreach ($name in @("python", "py")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    throw "Python 3.10-3.12 was not found. Install it first."
}

function Ensure-Venv {
    if (Test-Path -LiteralPath $VenvPython) { return }
    Write-Host "Creating pdf2zh venv: $Venv"
    & (Resolve-BasePython) -m venv $Venv
    & $VenvPython -m pip install --upgrade pip
}

function Show-Status {
    Write-Host "venv:           $((Test-Path $VenvPython))  ($Venv)"
    if (Test-Path $VenvPython) {
        $ver = (& $VenvPython -m pip show pdf2zh-next 2>$null | Select-String '^Version').ToString()
        Write-Host "pdf2zh-next:    $ver"
    }
    $zip = Get-ChildItem $AssetsDir -Filter 'offline_assets_*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($zip) {
        Write-Host ("offline bundle: {0} ({1:N0} MB)" -f $zip.Name, ($zip.Length / 1MB))
    } else {
        Write-Host "offline bundle: (none) — run with -OfflineAssets"
    }
}

if ($Install) {
    Ensure-Venv
    Write-Host "Installing pdf2zh-next..."
    & $VenvPython -m pip install pdf2zh-next
}

if ($OfflineAssets) {
    if (-not (Test-Path $BabelDoc)) { throw "babeldoc not found; run with -Install first." }
    New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null
    Write-Host "Generating offline-assets bundle into $AssetsDir ..."
    & $BabelDoc --generate-offline-assets $AssetsDir
}

Show-Status
