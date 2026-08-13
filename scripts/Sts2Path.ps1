# Shared helpers for the dev scripts. Path discovery mirrors the csproj logic.
# Override for a session with:  $env:STS2_PATH = "C:\path\to\Slay the Spire 2"

function Get-Sts2Path {
    if ($env:STS2_PATH -and (Test-Path $env:STS2_PATH)) { return $env:STS2_PATH }

    $candidates = @(
        "D:\SteamLibrary\steamapps\common\Slay the Spire 2",
        "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
        "$env:ProgramFiles\Steam\steamapps\common\Slay the Spire 2"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "SlayTheSpire2.exe")) { return $c }
    }
    throw "Slay the Spire 2 not found. Set `$env:STS2_PATH to the game folder."
}

function Get-Sts2Exe { Join-Path (Get-Sts2Path) "SlayTheSpire2.exe" }

function Get-Sts2LogPath { Join-Path $env:APPDATA "SlayTheSpire2\logs\godot.log" }

<#
Split the running instances into the ones this rig started and the ones it did not.

host.ps1 stops what it finds before building, because a running instance holds an open
handle on the installed mod DLL and the post-build copy then fails with a dozen lines of
MSBuild retry noise that reads like a compile error. It used to find them with
`Get-Process SlayTheSpire2` - which also matches a game launched through Steam, so starting
a test killed a real match Lucas was in the middle of with a friend (2026-08-13).

`--force-steam=off` is the discriminator, and a reliable one in both directions: a dev client
cannot run without it (a direct launch otherwise fails SteamAPI_Init with "No appID found"
and quits), and a Steam launch never passes it. Win32_Process is the only way to read another
process's command line from PowerShell; Get-Process cannot.

A CIM query that fails - permissions, the WMI service, a locked-down box - returns *nothing*
rather than throwing, so the caller stops nothing. That is the safe direction to fail in: the
cost is a build error naming the locked DLL, against the cost of ending someone's match.
#>
function Get-Sts2Process {
    param([switch]$Foreign)

    try {
        $procs = @(Get-CimInstance Win32_Process -Filter "Name = 'SlayTheSpire2.exe'" -ErrorAction Stop)
    }
    catch {
        Write-Host "Could not read process command lines - assuming no instances are running." -ForegroundColor Yellow
        return @()
    }

    @($procs | Where-Object {
        $isDev = $_.CommandLine -and $_.CommandLine -match '--force-steam=off'
        if ($Foreign) { -not $isDev } else { $isDev }
    })
}

<#
With --force-steam=off the platform layer is NullPlatformUtilStrategy, whose LocalPlayerId
is 1 by default or whatever --clientId says. That id names the save profile directory, so
the host and the joining client keep entirely separate settings.
#>
function Get-Sts2SettingsFile {
    param([int]$ClientId = 1)
    Join-Path $env:APPDATA "SlayTheSpire2\default\$ClientId\settings.save"
}

function Get-PrimaryScreenSize {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        return @{ W = $b.Width; H = $b.Height }
    } catch {
        return @{ W = 1920; H = 1080 }
    }
}

<#
Moves an existing log aside before a launch, keeping the last five.

--log-file truncates on open, so every relaunch destroyed the previous run's evidence. That is
not hypothetical: the `duel over` NRE was diagnosed 2026-08-06 from a client log that had
seconds left to live, and the host's half of the same run was already gone - which is exactly
the pairing you need when two clients disagree. Rotating costs nothing; logs/ is gitignored.
#>
function Move-Sts2Log {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return }

    $dir = Split-Path $Path
    $base = [IO.Path]::GetFileNameWithoutExtension($Path)
    $stamp = (Get-Item $Path).LastWriteTime.ToString("yyyyMMddTHHmmss")
    Move-Item $Path (Join-Path $dir "$base.$stamp.log") -Force

    Get-ChildItem (Join-Path $dir "$base.*.log") -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 5 | Remove-Item -Force
}

<#
Configures a save profile for two-client dev: windowed, tiled to one half of the primary
monitor, and mod loading pre-agreed.

The game ignores Godot's own --windowed/--resolution flags: NGame reapplies the display
mode from settings.save at startup, so the setting file is the only thing that decides.
(The vanilla -wpos X Y flag also forces windowed, but takes its size from this same file,
so we may as well set both here.)

"mods_enabled" is the JSON name for ModSettings.PlayerAgreedToModLoading. Without it the
game logs "user has not yet seen the mods warning" and silently loads no mods at all -
which on Windows is a fresh gate, because --force-steam=off uses a different profile than
the Steam launch that was already consented.

Edits are targeted regex replacements rather than a JSON round-trip, so the rest of the
file (keybinds, controller mappings) is byte-for-byte untouched.
#>
function Set-Sts2DevProfile {
    param(
        [int]$ClientId = 1,
        [ValidateSet("host", "client")] [string]$Role = "host",
        [int]$Width = 0
    )

    $file = Get-Sts2SettingsFile -ClientId $ClientId
    if (-not (Test-Path $file)) {
        Write-Host "  No settings for profile $ClientId yet - launch once with -Setup to create it." -ForegroundColor Yellow
        return $false
    }

    $screen = Get-PrimaryScreenSize
    if ($Width -le 0) { $Width = [Math]::Floor($screen.W / 2) - 20 }
    $height = [Math]::Floor($Width * 9 / 16)
    $maxH = $screen.H - 90
    if ($height -gt $maxH) {
        $height = $maxH
        $Width = [Math]::Floor($height * 16 / 9)
    }

    $x = if ($Role -eq "host") { 8 } else { [Math]::Floor($screen.W / 2) + 4 }
    $y = 40

    $backup = "$file.spirepvp-bak"
    if (-not (Test-Path $backup)) { Copy-Item $file $backup }

    $json = Get-Content $file -Raw
    $json = $json -replace '"fullscreen"\s*:\s*(true|false)', '"fullscreen": false'
    $json = $json -replace '"mods_enabled"\s*:\s*(true|false)', '"mods_enabled": true'
    $json = $json -replace '"window_size"\s*:\s*\{[^}]*\}', ('"window_size": {{ "X": {0}, "Y": {1} }}' -f $Width, $height)
    $json = $json -replace '"window_position"\s*:\s*\{[^}]*\}', ('"window_position": {{ "X": {0}, "Y": {1} }}' -f $x, $y)
    Set-Content -Path $file -Value $json -NoNewline -Encoding UTF8

    Write-Host "  Profile $ClientId -> windowed ${Width}x${height} at ($x,$y), mods enabled" -ForegroundColor DarkGray
    return $true
}
