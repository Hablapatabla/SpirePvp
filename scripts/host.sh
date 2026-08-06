#!/usr/bin/env bash
#
# Builds the mod, then launches the HOST client windowed on the left half of the screen.
# Run this in tab 1. macOS counterpart of host.ps1.
#
#   --no-build     Skip the build and just launch (when you only changed the other client).
#   --setup        First-run mode: launch WITHOUT --fastmp, so the game creates this profile
#                  and sits at the main menu. Needed once per profile, to accept the mod
#                  warning, before the settings file exists.
#   --custom       Boot straight into a Custom multiplayer host, which is the only lobby that
#                  exposes the modifier list — so it is the one you need to configure a match.
#   --fullscreen   Leave the display setting alone instead of forcing a tiled window.
#   --width N      Window width in points; height follows at 16:9. Default: half the screen.
#   --size WxH     Exact window size in points, overriding --width.
#   --pos X,Y      Exact window position in points, overriding the tiling.
#
# Sizes and positions are given in *points* — the units the Finder and macOS window management
# use, so 1728x1117 is a full screen here rather than 3456x2234. settings.save itself stores
# backing pixels; sts2_scale does the conversion. See its comment for why that matters.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=sts2.sh
. "$SCRIPT_DIR/sts2.sh"

no_build=0; setup=0; fullscreen=0; width=0; size=""; pos=""; custom=0
while [ $# -gt 0 ]; do
    case "$1" in
        --no-build)   no_build=1 ;;
        --setup)      setup=1 ;;
        --custom)     custom=1 ;;
        --fullscreen) fullscreen=1 ;;
        --width)      width="${2:-}"; shift ;;
        --size)       size="${2:-}"; shift ;;
        --pos)        pos="${2:-}"; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ "$no_build" -eq 0 ]; then
    if sts2_stop_all; then
        echo "Stopped running instance(s) — relaunching is the point of this script."
    fi

    echo "Building..."
    dotnet build "$SCRIPT_DIR/../SpirePvp.csproj" --nologo -v minimal

    # Godot assets (the modifier names, the map node art) live in the .pck, which dotnet does
    # not produce. Re-export when anything under SpirePvp/ is newer than the installed pack —
    # a stale .pck shows modifier names as raw loc keys rather than failing loudly.
    pck="$(sts2_path)/SlayTheSpire2.app/Contents/MacOS/mods/SpirePvp/SpirePvp.pck"
    newest="$(find "$SCRIPT_DIR/../SpirePvp" -type f -newer "$pck" -print -quit 2>/dev/null || true)"
    if [ ! -f "$pck" ] || [ -n "$newest" ]; then
        godot="/Applications/Godot_mono.app/Contents/MacOS/Godot"
        if [ -x "$godot" ]; then
            echo "Assets changed — re-exporting .pck..."
            # Export to a sibling temp file and rename into place. client.sh does not build,
            # so the client's startup read lands seconds after the host begins exporting, and
            # writing "$pck" directly lets that read catch a half-written pack — which
            # surfaces as `LocException: Failed to parse language file` on the client and
            # looks exactly like malformed JSON in the repo. See host.ps1 for the measured
            # timeline. mv within a directory is atomic, so a reader sees old or new, never
            # partial.
            ( cd "$SCRIPT_DIR/.." \
              && "$godot" --headless --import >/dev/null 2>&1 \
              && "$godot" --headless --export-pack "Windows Desktop" "$pck.new" 2>&1 | grep -i error || true )
            if [ -f "$pck.new" ]; then
                mv -f "$pck.new" "$pck"
            else
                echo "Export produced no pack — keeping the existing one."
            fi
        else
            echo "Godot not found — .pck may be stale (modifier names will show as loc keys)."
        fi
    fi
fi

if [ "$fullscreen" -eq 0 ]; then
    sts2_set_dev_profile 1 host "$width" "$size" "$pos" || true
fi

# Per-instance log. Both instances otherwise write to the same
# ~/Library/Application Support/SlayTheSpire2/logs/godot.log and interleave mid-line, which
# has already cost real debugging time.
log="$SCRIPT_DIR/../logs/host.log"
mkdir -p "$(dirname "$log")"
sts2_rotate_log "$log"

args=(--force-steam=off --log-file "$log")
if [ "$setup" -eq 0 ]; then
    if [ "$custom" -eq 1 ]; then
        args+=(--fastmp=host_custom)
    else
        args+=(--fastmp=host_standard)
    fi
fi

echo "Launching HOST (log: $log)"
exec "$(sts2_exe)" "${args[@]}"
