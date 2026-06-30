#Requires -Version 5.1
param(
    [ValidateSet("chrome", "edge")]
    [string]$Browser = "edge",

    [string]$ProfilePath = "",

    [switch]$NoWatch,

    [switch]$KeepProfile
)

$ErrorActionPreference = "Stop"

$extensionRoot = Split-Path -Parent $PSScriptRoot
$defaultProfilePath = Join-Path $extensionRoot ".dev-profile-edge"

function Find-BrowserExecutable {
    param([string]$name)

    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files\Google\Chrome\Application\chrome.exe",
        "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        "C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -like "*$name*" -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Could not find $name executable. Please install $name or add it to PATH."
}

$browserExe = Find-BrowserExecutable -name $Browser
Write-Host "Using browser: $browserExe" -ForegroundColor Cyan

if ($ProfilePath) {
    $profileDir = $ProfilePath
}
else {
    $profileDir = $defaultProfilePath
}

if (-not (Test-Path -LiteralPath $profileDir)) {
    New-Item -ItemType Directory -Path $profileDir | Out-Null
}

$arguments = @(
    "--user-data-dir=`"$profileDir`"",
    "--load-extension=`"$extensionRoot`"",
    "--remote-debugging-port=9222",
    "--no-first-run",
    "--no-default-browser-check",
    "--start-maximized",
    "about:blank"
)

Write-Host "Launching $Browser with Verbeam extension loaded..." -ForegroundColor Cyan
Write-Host "Profile: $profileDir" -ForegroundColor DarkGray
Write-Host "Extension: $extensionRoot" -ForegroundColor DarkGray

$process = Start-Process -FilePath $browserExe -ArgumentList $arguments -PassThru

$watchJob = $null
if (-not $NoWatch) {
    Write-Host "Starting file watcher..." -ForegroundColor Cyan
    $watchScript = Join-Path $PSScriptRoot "watch.mjs"
    $watchJob = Start-Job -ScriptBlock {
        param($node, $script)
        & $node $script
    } -ArgumentList (Get-Command node).Source, $watchScript
}

Write-Host "`nBrowser launched. Press Enter to stop the dev session." -ForegroundColor Green
[void][Console]::ReadLine()

if ($watchJob) {
    Stop-Job $watchJob -ErrorAction SilentlyContinue
    Remove-Job $watchJob -ErrorAction SilentlyContinue
}

if ($process -and -not $process.HasExited) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}

if (-not $KeepProfile -and -not $ProfilePath -and (Test-Path -LiteralPath $defaultProfilePath)) {
    Write-Host "Cleaning up temporary profile..." -ForegroundColor DarkGray
    Remove-Item -Recurse -Force -LiteralPath $defaultProfilePath -ErrorAction SilentlyContinue
}

Write-Host "Dev session ended." -ForegroundColor Cyan
