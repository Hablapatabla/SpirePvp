#!/usr/bin/env bash
#
# Launches the JOINING client windowed on the right half of the screen. Run this in tab 2,
# after the host window appears. macOS counterpart of client.ps1.
#
# Does not build: host.sh already did, and two concurrent builds fight over the same
# output files.
#
#   --setup        First-run mode: launch WITHOUT --fastmp, so the game creates this profile
#                  and sits at the main menu. Needed once, because --clientId is a different
#                  save profile from the host's.
#   --client-id N  Net id and save profile for this instance. Default 1001.
#   --fullscreen   Leave the display setting alone instead of forcing a tiled window.
#   --width N      Window width in points; height follows at 16:9. Default: half the screen.
#   --size WxH     Exact window size in points, overriding --width.
#   --pos X,Y      Exact window position in points, overriding the tiling.
#
# Sizes and positions are in *points*, not pixels — see host.sh.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=sts2.sh
. "$SCRIPT_DIR/sts2.sh"

setup=0; client_id=1001; fullscreen=0; width=0; size=""; pos=""
while [ $# -gt 0 ]; do
    case "$1" in
        --setup)      setup=1 ;;
        --client-id)  client_id="${2:-}"; shift ;;
        --fullscreen) fullscreen=1 ;;
        --width)      width="${2:-}"; shift ;;
        --size)       size="${2:-}"; shift ;;
        --pos)        pos="${2:-}"; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ "$fullscreen" -eq 0 ]; then
    sts2_set_dev_profile "$client_id" client "$width" "$size" "$pos" || true
fi

# Per-instance log; see host.sh.
log="$SCRIPT_DIR/../logs/client.log"
mkdir -p "$(dirname "$log")"
sts2_rotate_log "$log"

args=(--force-steam=off "--clientId=$client_id" --log-file "$log")
[ "$setup" -eq 1 ] || args+=(--fastmp=join)

echo "Launching CLIENT (log: $log)"
exec "$(sts2_exe)" "${args[@]}"
