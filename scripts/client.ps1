<#
.SYNOPSIS
Launches the JOINING client windowed on the right half of the screen. Run this in tab 2,
after the host window appears.

Does not build: host.ps1 already did, and two concurrent builds fight over the same
output files.

.PARAMETER Join
Join automatically on launch, as this used to. Only useful when the host is already sitting
in a lobby.

.PARAMETER Setup
Accepted and ignored - it used to mean "launch without --fastmp", which is now the default.

.PARAMETER ClientId
Net id and save profile for this instance. Default 1001.

.PARAMETER Fullscreen
Leave the display setting alone instead of forcing a tiled window.

.PARAMETER Width
Window width in pixels. Default: half the primary monitor, 16:9.
#>
param(
    [switch]$Setup,
    [switch]$Join,
    [int]$ClientId = 1001,
    [switch]$Fullscreen,
    [int]$Width = 0
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Sts2Path.ps1"

if (-not $Fullscreen) { [void](Set-Sts2DevProfile -ClientId $ClientId -Role client -Width $Width) }

# Per-instance log; see host.ps1.
$log = Join-Path $PSScriptRoot "..\logs\client.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Move-Sts2Log $log

$gameArgs = @("--force-steam=off", "--clientId=$ClientId", "--log-file", $log)
# Title screen by default; see host.ps1. The client's half: --fastmp=join re-fires every time
# the main menu is rebuilt, so ending a match sends it straight back into a join against a host
# that has gone, which times out and raises vanilla's "internal error" popup. Joining by hand
# also removes the ordering trap where an automatic join lands before the host reaches its
# lobby and fails as a bare "[ENetClient] Connection timed out!".
if ($Join) { $gameArgs += "--fastmp=join" }

Write-Host "Launching CLIENT (log: $log)" -ForegroundColor Green
& (Get-Sts2Exe) @gameArgs
