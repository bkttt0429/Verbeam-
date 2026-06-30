param(
    [switch]$Help
)

if ($Help) {
    Write-Host "Verbeam Edge ADB Deployment Script"
    Write-Host "Usage: .\deploy-edge.ps1"
    Write-Host "This script forwards the local port 5757 to the connected Android device"
    Write-Host "and opens the edge UI directly on the device."
    exit
}

Write-Host "Checking for ADB devices..." -ForegroundColor Cyan
$adbPath = "adb"

# Test if adb exists
try {
    $null = Get-Command $adbPath -ErrorAction Stop
} catch {
    Write-Host "ERROR: adb is not found in your PATH." -ForegroundColor Red
    Write-Host "Please install Android Platform Tools and add adb to your PATH variable." -ForegroundColor Yellow
    exit 1
}

# Check devices
$devices = & $adbPath devices
if ($devices -notmatch "\bdevice\b") {
    Write-Host "ERROR: No authorized Android device connected over USB." -ForegroundColor Red
    Write-Host "Please connect your phone and ensure USB debugging is enabled." -ForegroundColor Yellow
    exit 1
}

Write-Host "Setting up Reverse Port Forwarding (5757 -> 5757)..." -ForegroundColor Cyan
$reverseResult = & $adbPath reverse tcp:5757 tcp:5757
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to setup port forwarding." -ForegroundColor Red
    exit 1
}
Write-Host "Reverse port forwarding active. Mobile localhost:5757 -> PC localhost:5757" -ForegroundColor Green

Write-Host "Opening Edge Interface on the Android device..." -ForegroundColor Cyan
# Launch default browser to the edge UI endpoint
$intentResult = & $adbPath shell am start -a android.intent.action.VIEW -d "http://localhost:5757/edge/index.html"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to launch browser on device." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "SUCCESS!" -ForegroundColor Green
Write-Host "The Verbeam Edge UI should now be open on your phone." -ForegroundColor Green
Write-Host "To disconnect later, run: adb reverse --remove tcp:5757" -ForegroundColor Gray
Write-Host "==============================================" -ForegroundColor Green
