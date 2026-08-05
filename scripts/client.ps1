<#
.SYNOPSIS
Launches the JOINING client windowed on the right half of the screen. Run this in tab 2,
after the host window appears.

Does not build: host.ps1 already did, and two concurrent builds fight over the same
output files.

.PARAMETER Setup
First-run mode: launch WITHOUT --fastmp, so the game creates this profile and sits at the
main menu. Needed once, because --clientId is a different save profile from the host's.

.PARAMETER ClientId
Net id and save profile for this instance. Default 1001.

.PARAMETER Fullscreen
Leave the display setting alone instead of forcing a tiled window.

.PARAMETER Width
Window width in pixels. Default: half the primary monitor, 16:9.
#>
param(
    [switch]$Setup,
    [int]$ClientId = 1001,
    [switch]$Fullscreen,
    [int]$Width = 0
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Sts2Path.ps1"

if (-not $Fullscreen) { [void](Set-Sts2DevProfile -ClientId $ClientId -Role client -Width $Width) }

$gameArgs = @("--force-steam=off", "--clientId=$ClientId")
if (-not $Setup) { $gameArgs += "--fastmp=join" }

Write-Host "Launching CLIENT: $gameArgs" -ForegroundColor Green
& (Get-Sts2Exe) @gameArgs
