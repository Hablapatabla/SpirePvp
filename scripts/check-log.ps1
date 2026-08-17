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

.PARAMETER Filter
Only show mod lines matching this regex. The mod logs a lot now - a draft alone prints a line per
pick per peer - so a full dump buries the one line you are looking for.

.PARAMETER Draft
Shorthand for -Filter on the draft and lobby lines.

.PARAMETER Compare
Print the lobby telemetry from both logs together rather than one log after the other.

Per HANDOFF, the two peers disagreeing about the lobby is what five separate character-mirror fixes
failed to establish, and the answer was one diff of these lines. Reading them interleaved is the
whole point: the host's view of the client is the authority, because the run is seeded from it.
#>
param(
    [switch]$Errors,
    [string]$Filter,
    [switch]$Draft,
    [switch]$Compare
)

if ($Draft -and -not $Filter) { $Filter = "draft|lobby telemetry" }

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

if ($Compare) {
    # **The last state each peer holds, not every line.** Dumping the whole history was the first
    # version and it buried the answer: a draft lobby produces a dump per character change per peer,
    # and the question is only ever "do the two agree *now*". The history is still there under
    # -Filter "lobby telemetry" when the order matters.
    Write-Host "`n=== LOBBY, BOTH PEERS ===" -ForegroundColor Cyan

    $final = [ordered]@{}
    foreach ($name in $logs.Keys) {
        $path = $logs[$name]
        if (-not (Test-Path $path)) { continue }
        $last = Select-String -LiteralPath $path -Pattern "lobby telemetry \[" | Select-Object -Last 1
        if ($last) {
            # Everything after "players:" is the roster; that is the part being compared.
            $final[$name] = ($last.Line -replace ".*: (?=\d)", "" -replace "\s+<- diff.*", "")
        }
    }

    if ($final.Count -eq 0) {
        Write-Host "  (no lobby telemetry - was a Duel lobby opened?)" -ForegroundColor DarkGray
    }
    else {
        foreach ($name in $final.Keys) {
            Write-Host ("  {0,-6} {1}" -f $name, $final[$name]) -ForegroundColor Gray
        }

        # Each peer names itself with (me), so the strings never match literally. Comparing the
        # sorted id=character pairs with that marker stripped is the honest test.
        $rosters = $final.Values | ForEach-Object {
            (($_ -replace "\(me\)", "") -split ",\s*" | Sort-Object) -join ","
        }

        if ($final.Count -lt 2) {
            Write-Host "  only one peer logged - cannot compare" -ForegroundColor DarkGray
        }
        elseif (($rosters | Select-Object -Unique).Count -eq 1) {
            Write-Host "  peers AGREE" -ForegroundColor Green
        }
        else {
            Write-Host "  peers DISAGREE - the run is seeded from the host's copy, so the client's screen is the one lying" -ForegroundColor Red
        }
    }

    Write-Host ""
}

foreach ($name in $logs.Keys) {
    $path = $logs[$name]
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    if (-not (Test-Path $path)) { Write-Host "  (no log)" -ForegroundColor DarkGray; continue }

    $written = (Get-Item $path).LastWriteTime
    if ($dllTime -and $written -lt $dllTime) {
        Write-Host "  STALE: log ($written) predates the installed DLL - this instance has not been relaunched." -ForegroundColor Red
    }

    $modLines = Select-String -LiteralPath $path -Pattern "\[SpirePvp\]"
    $shown = 0
    $modLines | ForEach-Object {
        # The patch count is never filtered out: it is the line that decides whether anything else
        # in the log means anything, so hiding it behind a filter would be the wrong economy.
        if ($Filter -and $_.Line -notmatch $Filter -and $_.Line -notmatch "applied cleanly|PATCH FAILED") { return }
        $shown++
        $color = if ($_.Line -match "FAILED") { "Red" } elseif ($_.Line -match "applied cleanly") { "Green" } else { "Gray" }
        Write-Host "  $($_.Line)" -ForegroundColor $color
    }

    if ($Filter -and $modLines.Count -gt $shown) {
        Write-Host "  ($($modLines.Count - $shown) more mod line(s) hidden by -Filter)" -ForegroundColor DarkGray
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
