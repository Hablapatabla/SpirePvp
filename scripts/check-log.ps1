<#
.SYNOPSIS
Shows whether the mod loaded and every patch class applied on the last launch.

Per HANDOFF: a failed patch leaves the mod loaded and logging "loaded" while an arbitrary
subset of its behaviour is silently missing, so in-game results mean nothing until this
says "applied cleanly". Check it after every launch.
#>
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Sts2Path.ps1"

$log = Get-Sts2LogPath
if (-not (Test-Path $log)) { throw "No log at $log - has the game been launched?" }

Write-Host "--- SpirePvp lines from $log ---" -ForegroundColor Cyan
Select-String -Path $log -Pattern "SpirePvp|PATCH FAILED" | ForEach-Object {
    $color = "Gray"
    if ($_.Line -match "FAILED|Error") { $color = "Red" }
    elseif ($_.Line -match "applied cleanly") { $color = "Green" }
    Write-Host $_.Line -ForegroundColor $color
}

Write-Host "`n--- Game version ---" -ForegroundColor Cyan
Get-Content (Join-Path (Get-Sts2Path) "release_info.json") | Select-String "version"
