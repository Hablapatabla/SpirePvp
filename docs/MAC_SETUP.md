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
   ilspycmd -o ~/sts2-decompiled "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/sts2.dll" --nested-directories -p
   ```
   (If a newer ilspycmd matches your SDK, use it; 9.1.0.7988 is what's pinned on the
   Windows box. Keep the output out of the repo.)

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
