#!/usr/bin/env bash
# Simulate an accidental disconnect by hard-killing the client instance.
#
# **A hard kill rather than a quit, and the difference decides the test.** Quitting to the menu is a
# *deliberate* departure — the mod scores those as an outright loss for the leaver and never opens a
# rejoin window. A SIGKILL leaves no goodbye packet, which is the accidental path, and on ENet it is
# the only path that exercises the real mechanism: ENetHost.Update answers the transport's own
# disconnect event with a bare `continue`, so the host never *hears* a drop and has to measure
# silence via ConnectionStats.LastReceivedTime instead.
#
# Nothing here touches the host — it stays up holding the run open, which is what a rejoin needs.
set -euo pipefail

pid=$(pgrep -f -- "--clientId=1001" || true)

if [ -z "$pid" ]; then
    echo "No client instance running (looked for --clientId=1001)."
    exit 1
fi

echo "Killing client pid $pid — the host should open its disconnect window."
kill -9 $pid
echo
echo "Now: ./scripts/client.sh   then at the main menu press ' and type:  duel rejoin"
