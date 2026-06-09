$ErrorActionPreference = "Stop"

param(
    [int]$Port = 8000
)

$connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
$processIds = $connections |
    Where-Object { $_.OwningProcess -gt 0 } |
    Select-Object -ExpandProperty OwningProcess -Unique

if (-not $processIds) {
    Write-Host "No FunASR listener found on port $Port"
    return
}

foreach ($processId in $processIds) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        continue
    }

    Write-Host "Stopping $($process.ProcessName) PID $processId on port $Port"
    Stop-Process -Id $processId -Force
}
