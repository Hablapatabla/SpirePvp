#!/usr/bin/env bash
#
# Kills every running Slay the Spire 2 instance. macOS counterpart of stop.ps1.
#
# Ctrl+C in the launching tab is not reliable here: the scripts start a GUI process, and
# interrupting the script does not necessarily take the game down with it. This does.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=sts2.sh
. "$SCRIPT_DIR/sts2.sh"

pids=$(pgrep -f "Contents/MacOS/Slay the Spire 2" || true)
if [ -z "$pids" ]; then
    echo "No running instances."
    exit 0
fi

for pid in $pids; do
    kill "$pid" 2>/dev/null && echo "Stopped pid $pid"
done
