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
#   --errors        Show full error and exception lines rather than a count.
#   --filter REGEX  Only show mod lines matching REGEX. The mod logs a lot now — a draft alone
#                   prints a line per pick per peer — so a full dump buries what you opened it for.
#                   The patch count is never filtered out: it decides whether anything else in the
#                   log means anything.
#   --draft         Shorthand for --filter 'draft|lobby telemetry'.
#   --compare       Print the last lobby roster each peer holds and say whether they agree. That one
#                   line is what five character-mirror fixes failed to establish. The run is seeded
#                   from the host's copy, so on a disagreement the client's screen is the liar.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=sts2.sh
. "$SCRIPT_DIR/sts2.sh"

show_errors=0
filter=""
compare=0
while [ $# -gt 0 ]; do
    case "$1" in
        --errors)  show_errors=1 ;;
        --draft)   filter='draft|lobby telemetry' ;;
        --filter)  shift; filter="${1:-}" ;;
        --compare) compare=1 ;;
        *)         echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

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

    if [ -n "$filter" ]; then
        total=$(grep -cF "[SpirePvp]" "$path" || true)
        shown=$(grep -F "[SpirePvp]" "$path" | grep -cE "$filter|applied cleanly|PATCH FAILED" || true)
        grep -F "[SpirePvp]" "$path" | grep -E "$filter|applied cleanly|PATCH FAILED" | sed 's/^/  /' || true
        hidden=$(( ${total:-0} - ${shown:-0} ))
        [ "$hidden" -gt 0 ] && echo "  ($hidden more mod line(s) hidden by --filter)"
    else
        grep -F "[SpirePvp]" "$path" | sed 's/^/  /' || true
    fi

    errs=$(grep -cE "\[ERROR\]|Exception|StateDivergence" "$path" || true)
    if [ "${errs:-0}" -eq 0 ]; then
        echo "  no errors"
    elif [ "$show_errors" -eq 1 ]; then
        grep -E "\[ERROR\]|Exception|StateDivergence" "$path" | sed 's/^/  /'
    else
        echo "  $errs error line(s) — rerun with --errors to see them"
    fi
done
