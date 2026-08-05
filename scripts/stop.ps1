<#
.SYNOPSIS
Kills every running Slay the Spire 2 instance.

Ctrl+C in the launching tab is not reliable here: the scripts start a GUI process, and
interrupting the script does not necessarily take the game down with it. This does.

.PARAMETER Id
Kill only one process id, instead of all instances.
#>
param([int]$Id = 0)

$procs = if ($Id -gt 0) { Get-Process -Id $Id -ErrorAction SilentlyContinue }
         else { Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue }

if (-not $procs) {
    Write-Host "No running instances." -ForegroundColor DarkGray
    return
}

foreach ($p in $procs) {
    Stop-Process -Id $p.Id -Force
    Write-Host "Stopped pid $($p.Id)" -ForegroundColor Yellow
}
