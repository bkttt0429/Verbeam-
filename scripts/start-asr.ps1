param(
    [ValidateSet("cpu", "cuda")]
    [string]$Device = "cpu",
    [int]$Port = 8000,
    [string]$HostName = "127.0.0.1",
    [string]$Model = "iic/SenseVoiceSmall",
    [string]$PythonFileName = "python",
    [switch]$ForceInstall,
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Workspace = Split-Path $Root -Parent
$AsrDir = Join-Path $Root ".asr-funasr"
$VenvDir = Join-Path $AsrDir "venv"
$VenvPython = Join-Path $VenvDir "Scripts\python.exe"
$InstalledMarker = Join-Path $AsrDir "installed.txt"
$ServerScript = Join-Path $Root "scripts\funasr_openai_server.py"
$DataDir = Join-Path $Root "data"
$ModelRoot = Join-Path $Workspace "models\funasr"
$ModelScopeCache = Join-Path $ModelRoot "modelscope"
$HfHome = Join-Path $ModelRoot "huggingface"
$TorchHome = Join-Path $ModelRoot "torch"
$AsrLog = Join-Path $DataDir "asr-server.log"
$AsrErr = Join-Path $DataDir "asr-server.err.log"

New-Item -ItemType Directory -Force -Path `
    $AsrDir, $DataDir, $ModelRoot, $ModelScopeCache, $HfHome, $TorchHome | Out-Null

if (-not (Test-Path $ServerScript)) {
    throw "ASR server script not found: $ServerScript"
}

function Test-PortListening {
    param([int]$PortToCheck)

    $listeners = Get-NetTCPConnection -LocalPort $PortToCheck -State Listen -ErrorAction SilentlyContinue
    return $null -ne $listeners
}

function Invoke-Step {
    param(
        [string]$Title,
        [string]$FileName,
        [string[]]$Arguments
    )

    Write-Host $Title
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Title failed with exit code $LASTEXITCODE."
    }
}

function Test-RequiredPackagesInstalled {
    if (-not (Test-Path $VenvPython)) {
        return $false
    }

    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $VenvPython -c "import torch, torchaudio, funasr, fastapi, uvicorn, multipart, soundfile, more_itertools, einops" *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }
}

if (Test-PortListening $Port) {
    Write-Host "FunASR server is already listening on http://localhost:$Port"
    return
}

$existingServer = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -like "*funasr_openai_server.py*" -and
        $_.CommandLine -like "*--port $Port*"
    } |
    Select-Object -First 1

if ($existingServer) {
    Write-Host "FunASR server is already starting/loading on port $Port (PID $($existingServer.ProcessId))."
    Write-Host "Check logs:"
    Write-Host "  $AsrLog"
    Write-Host "  $AsrErr"
    return
}

if (-not (Test-Path $VenvPython)) {
    Invoke-Step `
        -Title "Creating FunASR Python venv at $VenvDir" `
        -FileName $PythonFileName `
        -Arguments @("-m", "venv", $VenvDir)
}

$packagesInstalled = Test-RequiredPackagesInstalled

if (-not $SkipInstall -and ($ForceInstall -or (-not (Test-Path $InstalledMarker) -and -not $packagesInstalled))) {
    Invoke-Step `
        -Title "Upgrading pip" `
        -FileName $VenvPython `
        -Arguments @("-m", "pip", "install", "--upgrade", "pip")

    if ($Device -eq "cpu") {
        Invoke-Step `
            -Title "Installing CPU PyTorch and torchaudio" `
            -FileName $VenvPython `
            -Arguments @("-m", "pip", "install", "torch", "torchaudio", "--index-url", "https://download.pytorch.org/whl/cpu")
    }
    else {
        Invoke-Step `
            -Title "Installing PyTorch and torchaudio" `
            -FileName $VenvPython `
            -Arguments @("-m", "pip", "install", "torch", "torchaudio")
    }

    Invoke-Step `
        -Title "Installing FunASR server dependencies" `
        -FileName $VenvPython `
        -Arguments @("-m", "pip", "install", "funasr", "modelscope", "fastapi", "uvicorn", "python-multipart", "soundfile", "more-itertools", "einops")

    "Installed $(Get-Date -Format o) for $Device" | Set-Content -Encoding UTF8 $InstalledMarker
}
elseif ($packagesInstalled -and -not (Test-Path $InstalledMarker)) {
    "Detected existing install $(Get-Date -Format o) for $Device" | Set-Content -Encoding UTF8 $InstalledMarker
}

$env:MODELSCOPE_CACHE = $ModelScopeCache
$env:HF_HOME = $HfHome
$env:TORCH_HOME = $TorchHome
$env:FUNASR_MODEL_CACHE = $ModelRoot
$env:PYTHONUTF8 = "1"

Start-Process `
    -FilePath $VenvPython `
    -ArgumentList @($ServerScript, "--host", $HostName, "--port", "$Port", "--device", $Device, "--model", $Model) `
    -WorkingDirectory $Root `
    -WindowStyle Hidden `
    -RedirectStandardOutput $AsrLog `
    -RedirectStandardError $AsrErr

Write-Host "Starting FunASR on http://localhost:$Port"
Write-Host "  Model cache: $ModelRoot"
Write-Host "  Log:         $AsrLog"
Write-Host "  Error log:   $AsrErr"
Write-Host ""
Write-Host "First launch downloads SenseVoiceSmall and may take several minutes."
