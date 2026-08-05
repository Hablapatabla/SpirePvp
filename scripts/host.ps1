<#
.SYNOPSIS
Builds the mod, then launches the HOST client windowed on the left half of the screen.
Run this in tab 1.

.PARAMETER NoBuild
Skip the build and just launch (useful when you only changed the other client).

.PARAMETER Setup
First-run mode: launch WITHOUT --fastmp, so the game creates this profile and sits at the
main menu. Needed once per profile before the settings file exists.

.PARAMETER Fullscreen
Leave the display setting alone instead of forcing a tiled window.

.PARAMETER Width
Window width in pixels. Default: half the primary monitor, 16:9.
#>
param(
    [switch]$NoBuild,
    [switch]$Setup,
    [switch]$Fullscreen,
    [int]$Width = 0
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Sts2Path.ps1"

if (-not $NoBuild) {
    Write-Host "Building..." -ForegroundColor Cyan
    dotnet build "$PSScriptRoot\..\SpirePvp.csproj" --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed - not launching." }
}

if (-not $Fullscreen) { [void](Set-Sts2DevProfile -ClientId 1 -Role host -Width $Width) }

$gameArgs = @("--force-steam=off")
if (-not $Setup) { $gameArgs += "--fastmp=host_standard" }

Write-Host "Launching HOST: $gameArgs" -ForegroundColor Green
& (Get-Sts2Exe) @gameArgs
