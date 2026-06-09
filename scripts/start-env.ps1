$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Workspace = Split-Path $Root -Parent
$OllamaExe = Join-Path $Workspace "tools\ollama\bin\ollama.exe"
$ModelDir = Join-Path $Workspace "models\ollama"
$ApiProject = Join-Path $Root "src\YomiBridge.Api\YomiBridge.Api.csproj"
$CreateOllamaProfiles = Join-Path $Root "scripts\create-ollama-profiles.ps1"
$StartAsr = Join-Path $Root "scripts\start-asr.ps1"
$DataDir = Join-Path $Root "data"
$OllamaLog = Join-Path $Workspace "tools\ollama\ollama-serve.log"
$OllamaErr = Join-Path $Workspace "tools\ollama\ollama-serve.err.log"
$ApiLog = Join-Path $DataDir "server.log"
$ApiErr = Join-Path $DataDir "server.err.log"

New-Item -ItemType Directory -Force -Path $ModelDir, $DataDir | Out-Null

if (-not (Test-Path $OllamaExe)) {
    throw "Ollama executable not found: $OllamaExe"
}

function Test-PortListening {
    param([int]$Port)

    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    return $null -ne $listeners
}

$env:OLLAMA_HOST = "127.0.0.1:11434"
$env:OLLAMA_MODELS = $ModelDir
$env:OLLAMA_FLASH_ATTENTION = "1"
$env:OLLAMA_KEEP_ALIVE = "30m"

if (Test-PortListening 11434) {
    Write-Host "Ollama is already listening on http://127.0.0.1:11434"
    Write-Host "Restart Ollama to apply OLLAMA_FLASH_ATTENTION or OLLAMA_KEEP_ALIVE changes."
}
else {
    Start-Process `
        -FilePath $OllamaExe `
        -ArgumentList "serve" `
        -WorkingDirectory (Split-Path $OllamaExe -Parent) `
        -WindowStyle Hidden `
        -RedirectStandardOutput $OllamaLog `
        -RedirectStandardError $OllamaErr

    Start-Sleep -Seconds 2
    Write-Host "Started Ollama on http://127.0.0.1:11434"
}

if (Test-Path $CreateOllamaProfiles) {
    & $CreateOllamaProfiles
}

if ($env:YB_SKIP_ASR -eq "1") {
    Write-Host "Skipping FunASR startup because YB_SKIP_ASR=1"
}
elseif (Test-Path $StartAsr) {
    & $StartAsr
}

if (Test-PortListening 5757) {
    Write-Host "YomiBridge API is already listening on http://localhost:5757"
}
else {
    Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $ApiProject, "--no-build") `
        -WorkingDirectory $Root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $ApiLog `
        -RedirectStandardError $ApiErr

    Start-Sleep -Seconds 3
    Write-Host "Started YomiBridge API on http://localhost:5757"
}

Write-Host ""
Write-Host "Environment ready:"
Write-Host "  API:    http://localhost:5757"
Write-Host "  Health: http://localhost:5757/health"
Write-Host "  Ollama: http://127.0.0.1:11434"
Write-Host "  ASR:    http://localhost:8000"
Write-Host "  Models: $ModelDir"
