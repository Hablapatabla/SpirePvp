#!/usr/bin/env bash
#
# Reports mod load status and errors for both clients. macOS counterpart of check-log.ps1.
#
# Per HANDOFF: a failed patch leaves the mod loaded and logging "loaded" while an arbitrary
# subset of its behaviour is silently missing, so check this after every launch. It also
# compares each log against the installed DLL's timestamp — a log older than the build means
# that instance never relaunched, which otherwise looks identical to a patch that stopped
# applying.
#
#   --errors   Show full error and exception lines rather than a count.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=sts2.sh
. "$SCRIPT_DIR/sts2.sh"

show_errors=0
[ "${1:-}" = "--errors" ] && show_errors=1

dll="$(sts2_path)/SlayTheSpire2.app/Contents/MacOS/mods/SpirePvp/SpirePvp.dll"
if [ -f "$dll" ]; then
    echo "Installed DLL built: $(date -r "$dll" '+%Y-%m-%d %H:%M:%S')"
fi

logs=("HOST:$SCRIPT_DIR/../logs/host.log" "CLIENT:$SCRIPT_DIR/../logs/client.log")
# Fall back to the shared log if the per-instance ones are not there (e.g. a manual launch).
if [ ! -f "$SCRIPT_DIR/../logs/host.log" ] && [ ! -f "$SCRIPT_DIR/../logs/client.log" ]; then
    logs=("SHARED:$(sts2_log_path)")
fi

for entry in "${logs[@]}"; do
    name="${entry%%:*}"
    path="${entry#*:}"
    printf '\n=== %s ===\n' "$name"

    if [ ! -f "$path" ]; then
        echo "  (no log)"
        continue
    fi

    if [ -f "$dll" ] && [ "$path" -ot "$dll" ]; then
        echo "  STALE: log predates the installed DLL — this instance has not been relaunched."
    fi

    grep -F "[SpirePvp]" "$path" | sed 's/^/  /' || true

    errs=$(grep -cE "\[ERROR\]|Exception|StateDivergence" "$path" || true)
    if [ "${errs:-0}" -eq 0 ]; then
        echo "  no errors"
    elif [ "$show_errors" -eq 1 ]; then
        grep -E "\[ERROR\]|Exception|StateDivergence" "$path" | sed 's/^/  /'
    else
        echo "  $errs error line(s) — rerun with --errors to see them"
    fi
done
