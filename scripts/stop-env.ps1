$ErrorActionPreference = "Stop"

function Stop-ListeningPort {
    param([int]$Port)

    $connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    $processIds = $connections |
        Where-Object { $_.OwningProcess -gt 0 } |
        Select-Object -ExpandProperty OwningProcess -Unique

    if (-not $processIds) {
        Write-Host "No listener found on port $Port"
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
}

Stop-ListeningPort 5757
Stop-ListeningPort 11434
Stop-ListeningPort 8000

Write-Host "LocalTranslateHub environment stopped."
