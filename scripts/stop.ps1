<#
.SYNOPSIS
Kills the Slay the Spire 2 instances this rig started.

Ctrl+C in the launching tab is not reliable here: the scripts start a GUI process, and
interrupting the script does not necessarily take the game down with it. This does.

A game launched through Steam is NOT touched by default - it is not this rig's to close, and
killing one cost a real match on 2026-08-13. Dev instances are the ones carrying
--force-steam=off; see Get-Sts2Process.

.PARAMETER Id
Kill only one process id, instead of all dev instances. Not filtered - an explicit pid is an
explicit instruction.

.PARAMETER All
Kill every instance, including one launched through Steam. The old behaviour.
#>
param(
    [int]$Id = 0,
    [switch]$All
)

. "$PSScriptRoot\Sts2Path.ps1"

$procs =
    if ($Id -gt 0) { @(Get-Process -Id $Id -ErrorAction SilentlyContinue | Select-Object @{n='ProcessId';e={$_.Id}}) }
    elseif ($All)  { @(Get-Sts2Process) + @(Get-Sts2Process -Foreign) }
    else           { @(Get-Sts2Process) }

if (-not $procs -or $procs.Count -eq 0) {
    Write-Host "No running instances." -ForegroundColor DarkGray
    if (-not $All -and -not $Id -and (Get-Sts2Process -Foreign).Count -gt 0) {
        Write-Host "(A non-dev instance is running and was left alone. -All includes it.)" -ForegroundColor DarkGray
    }
    return
}

foreach ($p in $procs) {
    Stop-Process -Id $p.ProcessId -Force
    Write-Host "Stopped pid $($p.ProcessId)" -ForegroundColor Yellow
}
