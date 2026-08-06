#!/usr/bin/env bash
# Shared helpers for the macOS dev scripts — the counterpart to Sts2Path.ps1, kept
# deliberately parallel to it so the two stay easy to diff. Source it, don't run it.
#
# Override the game location for a session with:  export STS2_PATH="/path/to/Slay the Spire 2"

sts2_path() {
    # ${VAR:-} throughout: the launchers run under `set -u`, where a bare $STS2_PATH is a
    # hard error rather than an empty string when the override is not set.
    if [ -n "${STS2_PATH:-}" ] && [ -d "${STS2_PATH:-}" ]; then
        printf '%s' "$STS2_PATH"
        return 0
    fi

    local candidates=(
        "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
        "/Applications/Slay the Spire 2"
    )
    local c
    for c in "${candidates[@]}"; do
        if [ -x "$c/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2" ]; then
            printf '%s' "$c"
            return 0
        fi
    done

    echo "Slay the Spire 2 not found. Set STS2_PATH to the game folder." >&2
    return 1
}

# The binary inside the bundle, not the .app. Launching the bundle with `open` would
# detach the process, drop our command-line flags, and refuse to start a second copy —
# all three of which this workflow depends on.
sts2_exe() { printf '%s/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2' "$(sts2_path)"; }

sts2_log_path() { printf '%s/Library/Application Support/SlayTheSpire2/logs/godot.log' "$HOME"; }

# With --force-steam=off the platform layer is NullPlatformUtilStrategy, whose LocalPlayerId
# is 1 by default or whatever --clientId says. That id names the save profile directory, so
# the host and the joining client keep entirely separate settings.
sts2_settings_file() {
    printf '%s/Library/Application Support/SlayTheSpire2/default/%s/settings.save' "$HOME" "${1:-1}"
}

# Logical desktop size in points, which is the coordinate space both macOS window placement
# and Godot's window_size/window_position use on a Retina display. Asking the Finder for the
# desktop bounds is the only way to get this without extra tooling.
sts2_screen() {
    local bounds
    bounds=$(osascript -e 'tell application "Finder" to get bounds of window of desktop' 2>/dev/null)
    if [ -z "$bounds" ]; then
        echo "1440 900"
        return
    fi
    echo "$bounds" | awk -F', *' '{print $3, $4}'
}

# Backing-pixels per point on the main display — 2 on a Retina panel, 1 otherwise.
#
# This is not a detail: **Godot's window_size and window_position are in backing pixels, not
# points.** `DisplayServerMacOS::window_set_size` divides by `screen_get_max_scale()` before
# handing the size to AppKit, so a settings file saying 852 produces a 426-point window — a
# quarter of the screen's width, which is exactly the "why is it so small" this was first
# written with. Everything user-facing here stays in points and gets multiplied on the way in.
#
# Override with STS2_SCALE if display detection ever gets it wrong.
sts2_scale() {
    if [ -n "${STS2_SCALE:-}" ]; then
        printf '%s' "$STS2_SCALE"
        return
    fi

    local native logical
    native=$(system_profiler SPDisplaysDataType 2>/dev/null | awk '/Resolution:/ {print $2; exit}')
    read -r logical _ <<< "$(sts2_screen)"

    if [ -n "$native" ] && [ "$logical" -gt 0 ] 2>/dev/null; then
        awk -v n="$native" -v l="$logical" 'BEGIN { s = n / l; print (s < 1 ? 1 : s) }'
    else
        echo 1
    fi
}

# Configures a save profile for two-client dev: windowed, tiled to one half of the screen,
# and mod loading pre-agreed.
#
# The game ignores Godot's own --windowed/--resolution flags: NGame reapplies the display
# mode from settings.save at startup, so the settings file is the only thing that decides.
# That is also why fullscreen is the thing to fix here rather than at launch — and on macOS
# a fullscreen window is worse than on Windows, because each instance takes over its own
# Space and you cannot see both at once at all.
#
# "mods_enabled" is the JSON name for ModSettings.PlayerAgreedToModLoading. Without it the
# game logs "user has not yet seen the mods warning" and silently loads no mods.
#
# Targeted regex replacements rather than a JSON round-trip, so the rest of the file
# (keybinds, controller mappings) is byte-for-byte untouched.
#
# Args: <client-id> <host|client> [width] [WxH] [X,Y]
# Width tiles at 16:9; an explicit WxH or X,Y overrides the tiling for that axis of the
# decision, so you can say "this size, wherever you tiled it" or the reverse.
sts2_set_dev_profile() {
    local client_id="${1:-1}" role="${2:-host}" width="${3:-0}" size="${4:-}" pos="${5:-}"
    local file
    file="$(sts2_settings_file "$client_id")"

    if [ ! -f "$file" ]; then
        printf '  No settings for profile %s yet — launch once with --setup to create it.\n' "$client_id" >&2
        return 1
    fi

    local screen_w screen_h scale
    read -r screen_w screen_h <<< "$(sts2_screen)"
    scale="$(sts2_scale)"

    SPIREPVP_FILE="$file" \
    SPIREPVP_ROLE="$role" \
    SPIREPVP_WIDTH="$width" \
    SPIREPVP_SIZE="$size" \
    SPIREPVP_POS="$pos" \
    SPIREPVP_SCREEN_W="$screen_w" \
    SPIREPVP_SCREEN_H="$screen_h" \
    SPIREPVP_SCALE="$scale" \
    python3 - <<'PY'
import os, re, shutil

path = os.environ["SPIREPVP_FILE"]
role = os.environ["SPIREPVP_ROLE"]
screen_w = int(os.environ["SPIREPVP_SCREEN_W"])
screen_h = int(os.environ["SPIREPVP_SCREEN_H"])
width = int(os.environ["SPIREPVP_WIDTH"])

if width <= 0:
    width = screen_w // 2 - 12
height = width * 9 // 16

# Leave room for the menu bar above and the Dock below.
max_h = screen_h - 90
if height > max_h:
    height = max_h
    width = height * 16 // 9

x = 6 if role == "host" else screen_w // 2 + 6
y = 40

# Explicit --size / --pos win over the tiling, and are taken at face value: asking for a
# window bigger than the screen or hanging off the edge is a legitimate thing to want on a
# multi-monitor desk, and second-guessing it here would be the more annoying failure.
size = os.environ.get("SPIREPVP_SIZE", "").strip().lower()
if size:
    m = re.fullmatch(r"(\d+)\s*[x,]\s*(\d+)", size)
    if not m:
        raise SystemExit(f"  --size must look like 1280x720, got {size!r}")
    width, height = int(m.group(1)), int(m.group(2))

pos = os.environ.get("SPIREPVP_POS", "").strip().lower()
if pos:
    m = re.fullmatch(r"(-?\d+)\s*[x,]\s*(-?\d+)", pos)
    if not m:
        raise SystemExit(f"  --pos must look like 0,40, got {pos!r}")
    x, y = int(m.group(1)), int(m.group(2))

backup = path + ".spirepvp-bak"
if not os.path.exists(backup):
    shutil.copyfile(path, backup)

# Everything above is in points, which is what the Finder, the flags and a human all mean.
# The settings file is in backing pixels — see sts2_scale — so convert on the way in.
scale = float(os.environ.get("SPIREPVP_SCALE", "1") or 1)
px_width, px_height = int(width * scale), int(height * scale)
px_x, px_y = int(x * scale), int(y * scale)

with open(path, "r", encoding="utf-8") as f:
    text = f.read()

def sub(pattern, replacement, text):
    new, n = re.subn(pattern, replacement, text)
    if n == 0:
        print(f"  warning: {pattern} not found in settings.save — left alone")
    return new

text = sub(r'"fullscreen"\s*:\s*(?:true|false)', '"fullscreen": false', text)
text = sub(r'"mods_enabled"\s*:\s*(?:true|false)', '"mods_enabled": true', text)
text = sub(r'"window_size"\s*:\s*\{[^}]*\}',
           '"window_size": {{ "X": {0}, "Y": {1} }}'.format(px_width, px_height), text)
text = sub(r'"window_position"\s*:\s*\{[^}]*\}',
           '"window_position": {{ "X": {0}, "Y": {1} }}'.format(px_x, px_y), text)

with open(path, "w", encoding="utf-8") as f:
    f.write(text)

scale_note = f" [{px_width}x{px_height} px @{scale:g}x]" if scale != 1 else ""
print(f"  profile -> windowed {width}x{height} pt at ({x},{y}){scale_note}, mods enabled")
PY
}

# Moves an existing log aside before a launch, keeping the last few.
#
# `--log-file` truncates on open, so every relaunch destroyed the previous run's evidence.
# That is not hypothetical: the `duel over` NRE was diagnosed from a client log that had
# thirty seconds left to live, and the host's half of the same run was already gone — which
# is precisely the pairing you need when two clients disagree. Rotating costs nothing and the
# logs are gitignored.
sts2_rotate_log() {
    local path="$1"
    [ -f "$path" ] || return 0

    local dir base
    dir="$(dirname "$path")"
    base="$(basename "$path" .log)"
    mv "$path" "$dir/$base.$(date -r "$path" '+%Y%m%dT%H%M%S').log"

    # Keep the five most recent; older runs stop being useful once the DLL has moved on.
    ls -1t "$dir/$base."*.log 2>/dev/null | tail -n +6 | while read -r old; do rm -f "$old"; done
}

# Kills every running instance. macOS does not lock an open dylib the way Windows locks a
# DLL, so this is about relaunching rather than about the build succeeding.
sts2_stop_all() {
    local pids
    pids=$(pgrep -f "Contents/MacOS/Slay the Spire 2" || true)
    if [ -z "$pids" ]; then
        return 1
    fi
    echo "$pids" | xargs kill 2>/dev/null || true
    sleep 0.7
    return 0
}
