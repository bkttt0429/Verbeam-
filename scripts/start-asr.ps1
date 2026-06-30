param(
    [ValidateSet("cpu", "cuda")]
    [string]$Device = "cpu",
    [int]$Port = 8000,
    [string]$HostName = "127.0.0.1",
    [string]$Model = "iic/SenseVoiceSmall",
    [string]$PythonFileName = "python",
    [int]$HealthTimeoutSeconds = 900,
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

function Get-FunAsrHealth {
    $healthHost = if ($HostName -eq "0.0.0.0" -or $HostName -eq "::") { "127.0.0.1" } else { $HostName }
    $healthUrl = "http://$healthHost`:$Port/health"

    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3 -ErrorAction Stop
        $status = if ($response.status) { [string]$response.status } else { "unknown" }
        return [pscustomobject]@{
            Reachable = $true
            Ready = $status -eq "ok"
            Status = $status
            Error = ""
            Url = $healthUrl
        }
    }
    catch {
        return [pscustomobject]@{
            Reachable = $false
            Ready = $false
            Status = "unreachable"
            Error = $_.Exception.Message
            Url = $healthUrl
        }
    }
}

$existingServer = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -like "*funasr_openai_server.py*" -and
        $_.CommandLine -like "*--port $Port*"
    } |
    Select-Object -First 1

function Wait-FunAsrHealth {
    param([int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds([Math]::Max(1, $TimeoutSeconds))
    $lastHealth = $null
    while ((Get-Date) -lt $deadline) {
        $lastHealth = Get-FunAsrHealth
        if ($lastHealth.Ready) {
            Write-Host "FunASR health is ready at $($lastHealth.Url)"
            return
        }

        if ($lastHealth.Reachable) {
            Write-Host "FunASR health reachable but not ready yet (status: $($lastHealth.Status))."
        }
        else {
            Write-Host "Waiting for FunASR health at $($lastHealth.Url): $($lastHealth.Error)"
        }

        Start-Sleep -Seconds 2
    }

    $message = if ($lastHealth) {
        "FunASR health did not become ready within $TimeoutSeconds seconds. Last status: $($lastHealth.Status). $($lastHealth.Error)"
    }
    else {
        "FunASR health did not become ready within $TimeoutSeconds seconds."
    }
    throw $message
}

if (Test-PortListening $Port) {
    $health = Get-FunAsrHealth
    if ($health.Ready) {
        Write-Host "FunASR server is already healthy at $($health.Url)"
        return
    }

    if (-not $existingServer) {
        throw "Port $Port is listening, but FunASR health is not ready at $($health.Url): $($health.Error)"
    }
}

if ($existingServer) {
    Write-Host "FunASR server is already starting/loading on port $Port (PID $($existingServer.ProcessId))."
    Write-Host "Check logs:"
    Write-Host "  $AsrLog"
    Write-Host "  $AsrErr"
    Wait-FunAsrHealth -TimeoutSeconds $HealthTimeoutSeconds
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
Wait-FunAsrHealth -TimeoutSeconds $HealthTimeoutSeconds
