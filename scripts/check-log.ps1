<#
.SYNOPSIS
Reports mod load status and errors for both clients.

The scripts give each instance its own log (logs\host.log, logs\client.log) because two
instances sharing %APPDATA%\SlayTheSpire2\logs\godot.log interleave mid-line.

Per HANDOFF: a failed patch leaves the mod loaded and logging "loaded" while an arbitrary
subset of its behaviour is silently missing, so check this after every launch. It also
compares the log against the installed DLL's timestamp — a log older than the build means
the game never relaunched, which otherwise looks identical to a patch that stopped applying.

.PARAMETER Errors
Show full error and exception lines rather than a count.
#>
param([switch]$Errors)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Sts2Path.ps1"

$dll = Join-Path (Get-Sts2Path) "mods\SpirePvp\SpirePvp.dll"
$dllTime = if (Test-Path $dll) { (Get-Item $dll).LastWriteTime } else { $null }
if ($dllTime) { Write-Host "Installed DLL built: $dllTime" -ForegroundColor DarkGray }

$logs = [ordered]@{
    HOST   = Join-Path $PSScriptRoot "..\logs\host.log"
    CLIENT = Join-Path $PSScriptRoot "..\logs\client.log"
}
# Fall back to the shared log if the per-instance ones are not there (e.g. a manual launch).
if (-not (Test-Path $logs.HOST) -and -not (Test-Path $logs.CLIENT)) {
    $logs = [ordered]@{ SHARED = Get-Sts2LogPath }
}

foreach ($name in $logs.Keys) {
    $path = $logs[$name]
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    if (-not (Test-Path $path)) { Write-Host "  (no log)" -ForegroundColor DarkGray; continue }

    $written = (Get-Item $path).LastWriteTime
    if ($dllTime -and $written -lt $dllTime) {
        Write-Host "  STALE: log ($written) predates the installed DLL - this instance has not been relaunched." -ForegroundColor Red
    }

    Select-String -LiteralPath $path -Pattern "\[SpirePvp\]" | ForEach-Object {
        $color = if ($_.Line -match "FAILED") { "Red" } elseif ($_.Line -match "applied cleanly") { "Green" } else { "Gray" }
        Write-Host "  $($_.Line)" -ForegroundColor $color
    }

    $errs = Select-String -LiteralPath $path -Pattern "\[ERROR\]|Exception|StateDivergence|still \d+ messages"
    if (-not $errs) {
        Write-Host "  no errors" -ForegroundColor Green
    }
    elseif ($Errors) {
        $errs | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
    }
    else {
        Write-Host "  $($errs.Count) error line(s) - rerun with -Errors to see them" -ForegroundColor Yellow
    }
}
