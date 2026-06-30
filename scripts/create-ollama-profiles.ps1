param(
    [switch]$Force,
    [string]$ModelsDirectory = ""
)

$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Workspace = Split-Path $Root -Parent
$ModelDir = if ([string]::IsNullOrWhiteSpace($ModelsDirectory)) {
    Join-Path $Root "data\ollama-models"
}
else {
    if ([System.IO.Path]::IsPathRooted($ModelsDirectory)) {
        $ModelsDirectory
    }
    else {
        Join-Path $Root $ModelsDirectory
    }
}
$Modelfile = Join-Path $Root "ollama\Modelfile.mort-qwen2.5-0.5b"
$BaseModel = "qwen2.5:0.5b"
$ProfileModel = "verbeam-mort-qwen2.5-0.5b:latest"

function Resolve-OllamaExe {
    $candidates = @(
        $env:VB_Verbeam__Ollama__ExecutablePath,
        (Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"),
        (Join-Path $Workspace "tools\ollama\bin\ollama.exe"),
        (Join-Path $env:ProgramFiles "Ollama\ollama.exe"),
        "ollama"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }

        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    throw "Ollama executable not found. Install Ollama or set VB_Verbeam__Ollama__ExecutablePath."
}

$OllamaExe = Resolve-OllamaExe

function Test-OllamaModel {
    param([string]$Name)

    $previousErrorActionPreference = $ErrorActionPreference
    $script:ErrorActionPreference = "Continue"
    & $OllamaExe show $Name *> $null
    $exitCode = $LASTEXITCODE
    $script:ErrorActionPreference = $previousErrorActionPreference

    return $exitCode -eq 0
}

if (-not (Test-Path $Modelfile)) {
    throw "Modelfile not found: $Modelfile"
}

if ([string]::IsNullOrWhiteSpace($env:OLLAMA_HOST)) {
    $env:OLLAMA_HOST = "http://127.0.0.1:11434"
}

New-Item -ItemType Directory -Path $ModelDir -Force | Out-Null
$env:OLLAMA_MODELS = $ModelDir

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
