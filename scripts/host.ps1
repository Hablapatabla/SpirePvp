<#
.SYNOPSIS
Builds the mod, then launches the HOST client windowed on the left half of the screen.
Run this in tab 1.

.PARAMETER NoBuild
Skip the build and just launch (useful when you only changed the other client).

.PARAMETER Setup
Accepted and ignored. It used to mean "launch without --fastmp", which is now the default;
kept so older notes and habits still work.

.PARAMETER Custom
Boot straight into a Custom multiplayer lobby - the plain modifier list, without the
duel-first presentation the Duel menu entry gives you.

.PARAMETER Fast
Boot straight into a Standard multiplayer host. Quickest way to a lobby.

.PARAMETER Fullscreen
Leave the display setting alone instead of forcing a tiled window.

.PARAMETER Width
Window width in pixels. Default: half the primary monitor, 16:9.
#>
param(
    [switch]$NoBuild,
    [switch]$Setup,
    [switch]$Custom,
    [switch]$Fast,
    [switch]$Fullscreen,
    [int]$Width = 0
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\Sts2Path.ps1"

if (-not $NoBuild) {
    # A running instance holds an open handle on the installed DLL, so the post-build copy
    # fails with ~12 lines of MSBuild retry noise that reads like a compile error. Clear it
    # first - relaunching is the whole point of running this script.
    #
    # Only the instances this rig started: see Get-Sts2Process. A game launched through Steam
    # is left alone, so you can test while playing.
    $running = @(Get-Sts2Process)
    if ($running.Count -gt 0) {
        Write-Host "Stopping $($running.Count) dev instance(s) - they lock the mod DLL." -ForegroundColor Yellow
        $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        Start-Sleep -Milliseconds 700
    }

    # A Steam instance survives the line above, but it still holds the DLL open if SpirePvp is
    # *enabled* in that profile - and then the build fails on a file lock rather than on
    # anything wrong with the code. Mod enablement is per profile, so disabling SpirePvp on the
    # Steam profile costs the dev profiles nothing and is required anyway to play with an
    # unmodded friend (JoinFlow refuses a mod mismatch outright). Say so before MSBuild does.
    $foreign = @(Get-Sts2Process -Foreign)
    if ($foreign.Count -gt 0) {
        Write-Host "$($foreign.Count) non-dev instance(s) running (Steam?) - left alone." -ForegroundColor Cyan
        Write-Host "  If the build fails on a locked SpirePvp.dll, disable the mod on that profile's" -ForegroundColor DarkGray
        Write-Host "  Mods screen, or re-run with -NoBuild." -ForegroundColor DarkGray
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

            # Export to a sibling temp file and rename into place, rather than writing the
            # live pack directly.
            #
            # client.ps1 does not build, so the usual workflow is "start the host, then start
            # the client" - which puts the client's startup read a couple of seconds after the
            # host's export begins. Writing $pck in place means that read can land mid-write.
            # Measured 2026-08-06: pack written 11:07:15, client launched 11:07:18, and the
            # client died on `LocException: Failed to parse language file` with the filename
            # itself truncated inside the pack's directory. It looked exactly like malformed
            # JSON in the repo, which is where the investigation started; the JSON was fine.
            #
            # A fresh clone or a pull makes this *likely* rather than rare: it refreshes the
            # mtimes under SpirePvp/, so the next host launch always re-exports.
            #
            # Move-Item -Force is a rename within the same directory, so a reader sees either
            # the old pack or the new one and never a partial file.
            #
            # The temp name must still end in .pck - Godot rejects any other extension with
            # "Export path doesn't end with a supported extension" and exports nothing, which
            # then silently keeps the stale pack. It must also not BE SpirePvp.pck: ModManager
            # loads exactly Path.Combine(mod.path, modId + ".pck"), so "SpirePvp.new.pck" sits
            # in the same folder without ever being loaded.
            $pckTmp = Join-Path (Split-Path $pck) "SpirePvp.new.pck"
            & $godot --headless --export-pack "Windows Desktop" $pckTmp 2>&1 | Select-String -Pattern "ERROR|error" | ForEach-Object { Write-Host $_ -ForegroundColor Red }
            Pop-Location

            if (Test-Path $pckTmp) {
                Move-Item -Path $pckTmp -Destination $pck -Force
            }
            else {
                Write-Host "Export produced no pack - keeping the existing one." -ForegroundColor Yellow
            }
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
Move-Sts2Log $log

$gameArgs = @("--force-steam=off", "--log-file", $log)
# Title screen by default - no --fastmp. The flag auto-clicks into a lobby and keeps doing
# so: returning to the main menu after a run re-runs the auto-navigation, shoving the host
# straight back into a lobby the moment a match ends, which reads as the mod mishandling the
# end of a match. M7's Duel menu entry also removed the reason to shortcut into Custom.
# --force-steam=off already selects ENet, so dropping --fastmp does not restore Steam lobbies.
if ($Custom -or $Fast) {
    $gameArgs += if ($Custom) { "--fastmp=host_custom" } else { "--fastmp=host_standard" }
}

Write-Host "Launching HOST (log: $log)" -ForegroundColor Green
& (Get-Sts2Exe) @gameArgs
