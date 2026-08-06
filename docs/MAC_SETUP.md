# macOS setup (primary dev machine)

Verified recipe adapted from jiegec/STS2FirstMod (built on Apple Silicon) and
erasels/Minty-Spire-2's path discovery. The mod DLL is MSIL/AnyCPU — one build runs on both
Windows and Mac; only the *local paths* differ. Cross-platform multiplayer (Mac client vs
Windows client) works over Steam or ENet.

## Prerequisites

1. **Slay the Spire 2** installed via Steam. Everything lives inside the app bundle:
   - Game root: `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2`
   - Game DLLs (`sts2.dll`, `0Harmony.dll`):
     `Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/`
     (Apple Silicon; Intel Macs have `data_sts2_macos_x86_64` — the csproj auto-detects both)
   - Mods folder (create it): `Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`
2. **.NET SDK 9**: `brew install dotnet-sdk@9` (or the pkg from dotnet.microsoft.com).
   Any SDK ≥ 9 that can target `net9.0` is fine.
3. **Godot 4.5.1 mono** (exact version — the game rejects .pck files exported by newer Godot):
   download `Godot_v4.5.1-stable_mono_macos.universal.zip` from
   https://godotengine.org/download/archive/4.5.1-stable/ and put `Godot_mono.app` in
   `/Applications`. First launch: right-click → Open (Gatekeeper), or
   `xattr -dr com.apple.quarantine /Applications/Godot_mono.app`.
4. **Rider** (or VS Code + C# Dev Kit).
5. **ilspycmd** for the decompiled game source (not in the repo — it's Mega Crit's code):
   ```
   dotnet tool install ilspycmd -g --version 9.1.0.7988
   export DOTNET_ROLL_FORWARD=Major
   ilspycmd -o ~/Code/sts2-decompiled "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/sts2.dll" --nested-directories -p
   ```
   (If a newer ilspycmd matches your SDK, use it; 9.1.0.7988 is what's pinned on the
   Windows box. Keep the output out of the repo — it sits next to it at
   `~/Code/sts2-decompiled`, 3493 files / 26 MB, mirroring `D:\modding\sts2\decompiled`.)

## Build & install

```
git clone https://github.com/Hablapatabla/SpirePvp
cd SpirePvp
mkdir -p "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods"
dotnet build
```

`dotnet build` discovers the game path per-OS (see csproj) and auto-copies
`SpirePvp.dll` + `SpirePvp.json` + `.pdb` into the mods folder. If Steam is somewhere else:
`dotnet build -p:Sts2Path="/path/to/Slay the Spire 2"`.

`.pck` export (only needed when Godot-side assets change; the .pck in the mods folder
otherwise carries over — copy it from another machine or re-export):

```
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --import
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --export-pack "Windows Desktop" \
  "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/SpirePvp/SpirePvp.pck"
```

(The preset is named "Windows Desktop" but a .pck is platform-agnostic — STS2FirstMod uses
the same preset on macOS. Run from the repo root.)

## Verify

Launch the game, then:

```
grep -i "SpirePvp" "$HOME/Library/Application Support/SlayTheSpire2/logs/godot.log"
```

Expect `Found mod manifest`, `Loading assembly DLL`, and the init line
`[SpirePvp] loaded — hello from the PvP mod`. (If the log dir isn't there, check
`~/Library/Application Support/Godot/app_userdata/` — Windows puts it in
`%APPDATA%\SlayTheSpire2\logs`.)

Verified on macOS 2026-08-04 against game v0.109.0: manifest found, DLL + PCK loaded,
init line present, `Loaded 5 mods`. Two things will make this silently fail:

- **Steam must be running.** Without it the game burns its startup in a Steamworks init
  retry loop and never reaches `ModManager.Initialize` — you get a log with no mod lines
  at all, which looks identical to "the mods folder is wrong".
- **No stale instance.** `steam://rungameid/2868840` is a no-op if a copy is already
  running, so you end up reading an old log. Check with
  `pgrep -fl "Contents/MacOS/Slay the Spire 2"` first.

The live session writes to `logs/godot.log`; it only rotates to a timestamped file on the
*next* launch, so grep `godot.log` while the game is still up.

## Two local clients, tiled side by side

`scripts/*.sh` are the macOS counterparts of the PowerShell dev scripts (there is no pwsh on
this machine). Same workflow, same per-instance logs:

```
./scripts/host.sh --custom      # tab 1: builds, re-exports the .pck if assets changed, launches left
./scripts/client.sh             # tab 2: launches right
./scripts/check-log.sh --errors # after the run
./scripts/stop.sh               # kill both
```

**Why this exists:** the two instances launched fullscreen, and on macOS a fullscreen window
takes over its own Space — so you cannot see both at once, which is unworkable for testing a
two-client mod. The fix is not a launch flag. **The game ignores Godot's `--windowed` and
`--resolution`:** `NGame` reapplies the display mode from `settings.save` at startup, so the
settings file is the only thing that decides. `sts2_set_dev_profile` edits it per profile —
`fullscreen: false`, `window_size`, `window_position`, and `mods_enabled` — with targeted
regex replacements rather than a JSON round-trip, so keybinds and controller mappings stay
byte-for-byte untouched. It keeps a one-time `settings.save.spirepvp-bak` beside each file.

Placement flags on both launchers:

| Flag | Effect |
|---|---|
| *(default)* | Tiles to half the screen at 16:9 — host left, client right |
| `--width N` | Window width; height follows at 16:9 |
| `--size WxH` | Exact size, overriding `--width` |
| `--pos X,Y` | Exact position, overriding the tiling |
| `--fullscreen` | Leave the display setting alone |

**The flags are in points; `settings.save` is in backing pixels.** The scripts convert, and
the conversion is the whole reason the first attempt produced a window a quarter of the screen
wide: `DisplayServerMacOS::window_set_size` divides by `screen_get_max_scale()` before handing
the size to AppKit, so a settings file saying `852` yields a 426-point window on a 2× display.
`sts2_scale` derives the factor from the native resolution over the Finder's desktop bounds
(3456/1728 = 2 here); override with `STS2_SCALE` if it ever guesses wrong. The default tiling
is half the screen each — 852×479 points, written as 1704×958 pixels.

`--custom` on the host boots into a **Custom** multiplayer lobby rather than the standard one.
That matters: Custom is the only lobby exposing the modifier list, so it is the only way to
configure a match (turn model + the two clocks). `--setup` launches with no `--fastmp` at all,
which is what you need once per profile to accept the mod-loading warning.

Profiles `1` (host) and `1001` (client) are separate save directories under
`~/Library/Application Support/SlayTheSpire2/default/`, selected by `--clientId`.

## Gotchas

- **Game updates**: Steam updating the game may not touch `mods/`, but a "verify integrity"
  might. Keep builds reproducible from the repo; never keep the only copy of anything in
  the mods folder. After a game update, re-run ilspycmd (APIs move; see README) and rebuild.
- **Two-instance testing on one Mac**: same story as Windows (DESIGN §8 / I7) — second
  instance + ENet direct connect. Steam normally allows one running instance per account;
  launching the .app binary directly (`SlayTheSpire2.app/Contents/MacOS/SlayTheSpire2`) for
  the second instance is the first thing to try. Cross-machine (MacBook vs the Windows
  desktop) over Steam works and is the easiest real test once both machines build.
- **Case sensitivity**: macOS is usually case-insensitive but don't rely on it — match
  exact casing in paths/`res://` references so Linux/Windows stay happy.
- **Line endings**: repo has no .gitattributes; Windows-side git converts LF→CRLF on
  checkout (harmless). If it gets annoying, add `* text=auto` in .gitattributes.

## Agent takeover checklist (Opus, start here)

1. Read `README.md`, then `docs/DESIGN.md` fully. Current milestone: **M1** (duel spike) —
   see DESIGN §7 for scope and acceptance criteria, §10 for its investigation tasks (I1, I7).
2. Run the Prerequisites + Build steps above; confirm the Verify log line before writing
   any code.
3. Decompile the game locally (Prerequisites step 5) — DESIGN's file references
   (`Core/...`) resolve against that tree. Re-verify any referenced API against the
   *current* game version; the design was written against v0.110.1.
4. Work in small commits on `master` (or branch per milestone), keep `dotnet build` green,
   and update DESIGN.md when an investigation (I#) resolves an open question — the doc is
   the shared source of truth between machines/agents.
5. The Windows box (this repo's origin machine) has: game at
   `D:\SteamLibrary\steamapps\common\Slay the Spire 2`, decompiled source at
   `D:\modding\sts2\decompiled`, Godot at `C:\Users\lucas\Tools\Godot\`. Useful as the
   second client for cross-machine multiplayer tests.
