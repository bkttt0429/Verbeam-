param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Workspace = Split-Path $Root -Parent
$OllamaExe = Join-Path $Workspace "tools\ollama\bin\ollama.exe"
$ModelDir = Join-Path $Workspace "models\ollama"
$Modelfile = Join-Path $Root "ollama\Modelfile.mort-qwen2.5-0.5b"
$BaseModel = "qwen2.5:0.5b"
$ProfileModel = "yomibridge-mort-qwen2.5-0.5b:latest"

function Test-OllamaModel {
    param([string]$Name)

    $previousErrorActionPreference = $ErrorActionPreference
    $script:ErrorActionPreference = "Continue"
    & $OllamaExe show $Name *> $null
    $exitCode = $LASTEXITCODE
    $script:ErrorActionPreference = $previousErrorActionPreference

    return $exitCode -eq 0
}

if (-not (Test-Path $OllamaExe)) {
    throw "Ollama executable not found: $OllamaExe"
}

if (-not (Test-Path $Modelfile)) {
    throw "Modelfile not found: $Modelfile"
}

if ([string]::IsNullOrWhiteSpace($env:OLLAMA_HOST)) {
    $env:OLLAMA_HOST = "127.0.0.1:11434"
}

if ([string]::IsNullOrWhiteSpace($env:OLLAMA_MODELS)) {
    $env:OLLAMA_MODELS = $ModelDir
}

if (-not (Test-OllamaModel $BaseModel)) {
    throw "Base model '$BaseModel' is not installed. Run: $OllamaExe pull $BaseModel"
}

if (Test-OllamaModel $ProfileModel) {
    if ($Force) {
        & $OllamaExe rm $ProfileModel
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to remove existing Ollama profile: $ProfileModel"
        }
    }
    else {
    Write-Host "Ollama profile already exists: $ProfileModel"
    return
    }
}

& $OllamaExe create $ProfileModel -f $Modelfile
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create Ollama profile: $ProfileModel"
}

Write-Host "Created Ollama profile: $ProfileModel"
