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
    # A running instance holds an open handle on the installed DLL, so the post-build copy
    # fails with ~12 lines of MSBuild retry noise that reads like a compile error. Clear it
    # first - relaunching is the whole point of running this script.
    $running = Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping $($running.Count) running instance(s) - they lock the mod DLL." -ForegroundColor Yellow
        $running | ForEach-Object { Stop-Process -Id $_.Id -Force }
        Start-Sleep -Milliseconds 700
    }

    Write-Host "Building..." -ForegroundColor Cyan
    dotnet build "$PSScriptRoot\..\SpirePvp.csproj" --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed - not launching." }

    # Godot assets (localization JSON, images) live in the .pck, which dotnet does not
    # produce. Re-export when anything under SpirePvp/ is newer than the installed pack -
    # a stale .pck shows modifier names as raw loc keys rather than failing loudly.
    $pck = Join-Path (Get-Sts2Path) "mods\SpirePvp\SpirePvp.pck"
    $assets = Get-ChildItem "$PSScriptRoot\..\SpirePvp" -Recurse -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $needsPck = (-not (Test-Path $pck)) -or ($assets -and $assets.LastWriteTime -gt (Get-Item $pck).LastWriteTime)

    if ($needsPck) {
        $godot = "C:\Users\lucas\Tools\Godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe"
        if (Test-Path $godot) {
            Write-Host "Assets changed - re-exporting .pck..." -ForegroundColor Cyan
            Push-Location "$PSScriptRoot\.."
            & $godot --headless --import 2>&1 | Out-Null
            & $godot --headless --export-pack "Windows Desktop" $pck 2>&1 | Select-String -Pattern "ERROR|error" | ForEach-Object { Write-Host $_ -ForegroundColor Red }
            Pop-Location
        }
        else {
            Write-Host "Godot not found - .pck may be stale (modifier names will show as loc keys)." -ForegroundColor Yellow
        }
    }
}

if (-not $Fullscreen) { [void](Set-Sts2DevProfile -ClientId 1 -Role host -Width $Width) }

# Per-instance log. Both instances otherwise write to the same %APPDATA% godot.log and
# interleave mid-line, which has already cost real debugging time.
$log = Join-Path $PSScriptRoot "..\logs\host.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null

$gameArgs = @("--force-steam=off", "--log-file", $log)
if (-not $Setup) { $gameArgs += "--fastmp=host_standard" }

Write-Host "Launching HOST (log: $log)" -ForegroundColor Green
& (Get-Sts2Exe) @gameArgs
