param(
    [ValidateSet("status", "rapidocr-ppocrv5", "easyocr", "paddleocr", "pix2text", "pp-structure-v3", "paddleocr-vl", "dots-ocr", "all")]
    [string]$Engine = "status",

    [switch]$Install,

    [switch]$InstallTesseract,

    # Provision per-language rapidocr-net rec models into ocr-models\ (the Api csproj globs them into
    # the build/dist, so end users get them too). Auto-runs when installing rapidocr-ppocrv5/all;
    # -DownloadRecModels forces it standalone. -RecModels: none | recommended | all | comma keys (ko,th).
    [switch]$DownloadRecModels,

    [string]$RecModels = "recommended"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Venv = Join-Path $Root ".ocr-set\venv"
$VenvPython = Join-Path $Venv "Scripts\python.exe"
$Wrapper = Join-Path $Root "scripts\local_ocr_json.py"
$Tessdata = Join-Path $Root ".ocr-set\tessdata"
$Requirements = @{
    "rapidocr-ppocrv5" = Join-Path $Root "ocr-set\requirements-rapidocr.txt"
    easyocr = Join-Path $Root "ocr-set\requirements-easyocr.txt"
    paddleocr = Join-Path $Root "ocr-set\requirements-paddleocr.txt"
    "pp-structure-v3" = Join-Path $Root "ocr-set\requirements-paddleocr.txt"
    "paddleocr-vl" = Join-Path $Root "ocr-set\requirements-paddleocr.txt"
    pix2text = Join-Path $Root "ocr-set\requirements-pix2text.txt"
    "dots-ocr" = Join-Path $Root "ocr-set\requirements-dots-ocr.txt"
}

function Resolve-Python {
    if (Test-Path -LiteralPath $VenvPython) {
        return $VenvPython
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        return $python.Source
    }

    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        return $py.Source
    }

    throw "Python was not found. Install Python 3.10+ first."
}

function Ensure-Venv {
    if (Test-Path -LiteralPath $VenvPython) {
        return
    }

    Write-Host "Creating OCR SET venv: $Venv"
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        & $py.Source -3.11 -m venv $Venv
    }
    else {
        $python = Resolve-Python
        & $python -m venv $Venv
    }
    & $VenvPython -m pip install --upgrade pip
}

function Install-PaddleRuntime {
    Ensure-Venv
    Write-Host "Installing PaddlePaddle CPU runtime..."
    & $VenvPython -m pip install paddlepaddle -i https://www.paddlepaddle.org.cn/packages/stable/cpu/
}

function Install-PythonEngine {
    param([Parameter(Mandatory = $true)][string]$Name)

    Ensure-Venv
    $req = $Requirements[$Name]
    if (-not (Test-Path -LiteralPath $req)) {
        throw "Missing requirements file: $req"
    }

    if ($Name -eq "paddleocr" -or $Name -eq "pp-structure-v3" -or $Name -eq "paddleocr-vl") {
        Install-PaddleRuntime
    }

    Write-Host "Installing $Name requirements..."
    & $VenvPython -m pip install -r $req
}

function Test-Engine {
    param([Parameter(Mandatory = $true)][string]$Name)

    $python = Resolve-Python
    if (-not (Test-Path -LiteralPath $Wrapper)) {
        throw "Missing local OCR wrapper: $Wrapper"
    }

    & $python $Wrapper --engine $Name --check
    Write-Host ""
}

function Test-Tesseract {
    $command = Get-Command tesseract -ErrorAction SilentlyContinue
    if ($command) {
        Write-Host (@{ engine = "tesseract"; available = $true; path = $command.Source } | ConvertTo-Json -Compress)
        return
    }

    $common = "C:\Program Files\Tesseract-OCR\tesseract.exe"
    if (Test-Path -LiteralPath $common) {
        Write-Host (@{ engine = "tesseract"; available = $true; path = $common } | ConvertTo-Json -Compress)
        return
    }

    Write-Host (@{ engine = "tesseract"; available = $false; note = "Install Tesseract OCR and Japanese/Chinese language data." } | ConvertTo-Json -Compress)
}

function Install-Tesseract {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Host "winget was not found. Install Tesseract manually from UB Mannheim builds, then add it to PATH."
        return
    }

    Write-Host "Installing Tesseract OCR with winget..."
    & $winget.Source install --id UB-Mannheim.TesseractOCR --source winget --accept-package-agreements --accept-source-agreements --silent
    Install-TesseractLanguages
}

function Install-TesseractLanguages {
    $languages = @("eng", "jpn", "chi_tra", "chi_sim")
    New-Item -ItemType Directory -Force -Path $Tessdata | Out-Null

    foreach ($language in $languages) {
        $target = Join-Path $Tessdata "$language.traineddata"
        if (Test-Path -LiteralPath $target) {
            continue
        }

        $url = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/$language.traineddata"
        Write-Host "Downloading Tesseract language data: $language"
        Invoke-WebRequest -Uri $url -OutFile $target
    }
}

function Install-RapidOcrNetRecModels {
    param([string]$Set = "recommended")

    if ($Set -eq "none") {
        return
    }

    $python = Resolve-Python
    $fetch = Join-Path $Root "scripts\fetch_ocr_rec_model.py"
    $outDir = Join-Path $Root "src\Verbeam.Api\ocr-models"
    if (-not (Test-Path -LiteralPath $fetch)) {
        Write-Host "Skipping rapidocr-net rec models: fetch script not found ($fetch)."
        return
    }

    Write-Host "Provisioning rapidocr-net per-language rec models ($Set) -> $outDir"
    switch ($Set) {
        "recommended" { & $python $fetch --recommended --out $outDir }
        "all" { & $python $fetch --all --out $outDir }
        default { & $python $fetch --lang $Set --out $outDir }
    }
}

if ($InstallTesseract) {
    Install-Tesseract
}

if ($Install) {
    if ($Engine -eq "status") {
        throw "Use -Engine rapidocr-ppocrv5|easyocr|paddleocr|pix2text|pp-structure-v3|paddleocr-vl|dots-ocr|all with -Install."
    }

    $targets = if ($Engine -eq "all") { @("rapidocr-ppocrv5", "easyocr", "paddleocr", "pp-structure-v3", "paddleocr-vl", "pix2text", "dots-ocr") } else { @($Engine) }
    $installedRequirementFiles = @{}
    foreach ($target in $targets) {
        $req = $Requirements[$target]
        if ($req -and $installedRequirementFiles.ContainsKey($req)) {
            Write-Host "Skipping duplicate requirements for $target."
            continue
        }

        Install-PythonEngine $target
        if ($req) {
            $installedRequirementFiles[$req] = $true
        }
    }
}

# Auto-provision the rapidocr-net per-language rec models when setting up rapidocr/all (rapidocr-net
# is the cross-OS realtime engine), or on demand via -DownloadRecModels. Skip with -RecModels none.
$installingRapid = $Install -and ($Engine -eq "rapidocr-ppocrv5" -or $Engine -eq "all")
if ($DownloadRecModels -or $installingRapid) {
    Install-RapidOcrNetRecModels $RecModels
}

Write-Host "OCR SET status:"
Test-Tesseract
foreach ($target in @("rapidocr-ppocrv5", "easyocr", "paddleocr", "pp-structure-v3", "paddleocr-vl", "pix2text", "dots-ocr")) {
    Test-Engine $target
}

Write-Host ""
Write-Host "API engines are listed only and are intentionally not installed here: Google Cloud Vision, DeepSeek-OCR / VLM OCR, Mathpix."
