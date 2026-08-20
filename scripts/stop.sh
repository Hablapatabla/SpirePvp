#!/usr/bin/env bash
#
# Kills every running Slay the Spire 2 instance. macOS counterpart of stop.ps1.
#
# Ctrl+C in the launching tab is not reliable here: the scripts start a GUI process, and
# interrupting the script does not necessarily take the game down with it. This does.
#
# **It escalates, because SIGTERM alone hangs.** Reported 2026-08-20: `stop.sh` left the client
# frozen and it had to be force-quit through macOS. A plain `kill` asks Godot to shut down
# gracefully, and a duel client has reasons not to — an open ENet socket with a peer that is gone,
# and a mod teardown that runs on the way out. Waiting on a clean exit that is not coming is
# indistinguishable from a script that did nothing. So: ask nicely, give it a moment, then insist.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=sts2.sh
. "$SCRIPT_DIR/sts2.sh"

pattern="Contents/MacOS/Slay the Spire 2"

pids=$(pgrep -f "$pattern" || true)
if [ -z "$pids" ]; then
    echo "No running instances."
    exit 0
fi

for pid in $pids; do
    kill "$pid" 2>/dev/null && echo "Asked pid $pid to stop"
done

# Up to 3s for a graceful exit, checked ten times a second so a well-behaved instance costs
# nothing.
for _ in $(seq 1 30); do
    remaining=$(pgrep -f "$pattern" || true)
    [ -z "$remaining" ] && echo "All instances stopped." && exit 0
    sleep 0.1
done

for pid in $(pgrep -f "$pattern" || true); do
    kill -9 "$pid" 2>/dev/null && echo "Force-killed pid $pid (it ignored SIGTERM)"
done

sleep 0.2
if pgrep -f "$pattern" >/dev/null 2>&1; then
    echo "WARNING: instances still running after SIGKILL — check Activity Monitor." >&2
    exit 1
fi

echo "All instances stopped."
