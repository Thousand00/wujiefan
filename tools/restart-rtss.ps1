# restart-rtss.ps1
# Restarts RTSS so it re-reads the OverlayEditor layout (.ovl).
# The layout is loaded once when the OverlayEditor plugin starts;
# RTSS has no hot reload, so a .ovl edit only takes effect after a restart.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\restart-rtss.ps1
#
# NOTE: keep this file pure ASCII. PowerShell 5.1 decodes .ps1 files as ANSI
#       and non-ASCII bytes can break parsing.

$ErrorActionPreference = "Continue"
$log = Join-Path $env:TEMP "zephyr-restart-rtss.log"

function Note($msg) {
    "$(Get-Date -Format o)  $msg" | Out-File -FilePath $log -Append -Encoding utf8
}

Note "--- restart begin ---"

$before = Get-Process RTSS -ErrorAction SilentlyContinue
if ($before) {
    Note "before: pid=$($before.Id) started=$($before.StartTime)"
} else {
    Note "before: not running"
}

Stop-Process -Name RTSS -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$still = Get-Process RTSS -ErrorAction SilentlyContinue
if ($still) {
    Note "WARN: still alive after kill, pid=$($still.Id)"
} else {
    Note "killed ok"
}

# RTSS runs elevated, so starting the scheduled task only succeeds from an
# elevated shell.
Start-ScheduledTask -TaskName "RTSS"
Start-Sleep -Seconds 6

$after = Get-Process RTSS -ErrorAction SilentlyContinue
if ($after) {
    Note "after: pid=$($after.Id) started=$($after.StartTime)"
    Write-Host "RTSS restarted, pid=$($after.Id)"
    Write-Host "log: $log"
} else {
    Note "ERROR: RTSS did not come back"
    Write-Host "ERROR: RTSS did not come back"
    exit 1
}
