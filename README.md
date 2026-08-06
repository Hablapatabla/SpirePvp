# Spire PvP

1v1 competitive Slay the Spire 2. Two players get the same map, same path options, and same Neow bonus; race through Act 1 (and maybe grab the Act 2 ancient bonus), then duel. Chess-clock turns for blitz-style play.

## Status

**The full loop works** (playtested 2026-08-06, two local clients): lobby modifiers → race Act 1 → arena node → rendezvous → deck review → duel → result screen, with desync detection live. No blocking issues. The clock is now two banks — a race deadline and a fresh duel bank — and M6's remaining work is presentation: race progress HUD, a real duel result screen, rematch. See `docs/HANDOFF.md`.

## Agent handover prompt

Paste this to start a fresh agent session on this project.

> You're picking up SpirePvp, a 1v1 PvP mod for Slay the Spire 2, at `<repo path>` (developed on a Windows desktop and a MacBook — see `docs/MAC_SETUP.md`).
>
> Read first, in this order: `docs/HANDOFF.md` → `CLAUDE.md` → `docs/DESIGN.md`. HANDOFF's "Open issues" section is your starting point. Decompiled game source lives outside the repo (regenerate per README if missing; game is v0.110.1).
>
> **State:** the whole loop is playable and was played end to end several times on 2026-08-06. Two players configure the match with custom-run modifiers (turn model, race clock, duel clock), race the same seeded map independently with mirrored RNG, converge on an arena node placed back-to-back after the Act 1 boss, review each other's decks, and duel. Checksums and pre-combat state sync are live through the duel; matches run without a desync, and back-to-back matches in one process reconfigure cleanly.
>
> **Your first task — verify four fixes made after the last playtest, all unplaytested.** They are small and all in HANDOFF's open-issues section; one race-timeout run plus one HP-win duel covers every one:
> 1. The `duel over` NullReferenceException, open for three sessions, was `DuelEndCombatPatch.Prefix` skipping an `async Task` without assigning `__result`. Expect a clean log on an HP win now.
> 2. A race clock running out is now a **draw**, not a win for whoever the service ticked second. Expect a `DRAW` banner.
> 3. The result screen after a race timeout showed `YOU 0:00 · OPP 0:00` for a duel nobody played; it should now show the single expired race clock.
> 4. Abandoning a run left the host broadcasting `ClockSyncMessage` into a dead service for 21 seconds (46 error lines). Abandon a run mid-race and expect silence.
>
> **Then M6's remaining work, which is all presentation and none of it risky:** `RaceProgressHud` (the messages already flow and the opponent's portrait moves on your map — what is missing is their position, HP and deck size while you wait at the arena), then `DuelResultScreen` (vanilla's game-over screen currently reports run-score lines that mean nothing for a duel; **rematch lives here**, and its absence is why the only way out of a finished duel is abandoning, which tells the other player "the host abandoned the game"), then M7's dedicated PvP menu entry.
>
> **Rules that cost this project real time — don't relearn them:**
>
> * Read the logs yourself (`logs/host.log`, `logs/client.log`) rather than asking for symptoms. Check the log's timestamp against the installed DLL — a stale log looks identical to a patch that stopped applying. When two clients disagree, the host dumps *both* full state dumps on divergence: diff them and the answer is the lines that differ. The launch scripts rotate logs, so the previous five runs are still there as `host.<timestamp>.log`.
> * Confirm `N patch classes applied cleanly` after every change (35 as of this handoff). Never use `Harmony.PatchAll`.
> * Arm all message handlers at run start, never lazily on first local use — the peer can announce something before you act, and the message is silently dropped. This bit three separate times. Release them on run teardown for the same reason (`DuelRunCleanupPatch`). The same ordering trap bit again in a new place: `DuelFlag.Arm()` ran *before* the clocks existed, subscribed to nothing, and set `_armed` anyway, so nobody could lose on time at all.
> * A prefix that skips an async method must assign `__result = Task.CompletedTask`, or the caller NREs on `await null`. **And the stack will name the caller, with no frame for the method you patched** — which reads exactly like inlining and has now sent two multi-session hunts into reading the wrong function. A missing frame under an `await` means check the patch, not the callee.
> * **Guard on the condition, not on each route.** A run can end without a duel result — abandoning, the host quitting, a disconnect — and none of those reach `DuelResult`. Anything that must stop when the run stops should ask whether the run is still in progress, because there is always another route out.
> * `RunManager.EnterRoom` is the *last step* of entering a room, not the whole thing. Vanilla's real entry points run a preamble in front of it; skipping any of it fails silently and differently each time. `DuelArena.EnterRoom` mirrors `EnterMapPointInternal` step for step — keep the two in sync.
> * Never kill the user's running game processes; `host.ps1` stops instances itself before building.
> * The recurring root cause of the whole race phase: the engine assumes the party is co-located, and each assumption fails differently — a hang, a silent freeze, a crash. Its content-level twin: the engine reads `Players.Count > 1` as "co-op", so a PvP run gets offered co-op-only cards and relics.
>
> **Build/test (Windows):** `.\scripts\host.ps1` (pwsh 7 — stops instances, builds, re-exports the `.pck` if assets changed, launches), then `.\scripts\client.ps1` in a second tab. macOS has `./scripts/*.sh` equivalents (no pwsh there); see `docs/MAC_SETUP.md`. A match is configured in a **Custom** lobby — the only one exposing the modifier list — so pass `-Custom` (`host.ps1`) or `--custom` (`host.sh`). Console opens with `'`; `travel` unlocks clicking any map node; `unlock all` is needed per dev profile. `kill` does not work in a duel by design — use `damage <amount> <index>`.
>
> Lucas playtests every change — hand him one specific thing to try with specific things to watch, then read the logs.

## Toolchain

- **.NET SDK 9** (installed via winget)
- **Godot 4.5.1 mono** at `C:\Users\lucas\Tools\Godot\Godot_v4.5.1-stable_mono_win64\` — must stay 4.5.1; the game rejects `.pck` files exported by newer Godot ("Megadot" is Mega Crit's fork of 4.5.1)
- **Game**: `D:\SteamLibrary\steamapps\common\Slay the Spire 2` (v0.110.1). Referenced DLLs: `sts2.dll`, `0Harmony.dll` from `data_sts2_windows_x86_64\`
- **Decompiled game source** (ilspycmd): `D:\modding\sts2\decompiled\` — regenerate after game updates:
  ```
  ilspycmd -o D:\modding\sts2\decompiled "D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll" --nested-directories -p
  ```
  (needs `$env:DOTNET_ROLL_FORWARD="Major"`; ilspycmd 9.1.0.7988 is pinned for the .NET 9 SDK)

## Build

```
dotnet build
```
Post-build target copies `SpirePvp.dll` + `SpirePvp.json` + `.pdb` into the game's `mods\SpirePvp\` folder automatically.

Asset changes (anything Godot-side) also need a `.pck` re-export:
```
& "C:\Users\lucas\Tools\Godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --export-pack "Windows Desktop" "D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SpirePvp\SpirePvp.pck"
```
(run from the project directory; run `--headless --import` once after adding new assets)

Game logs: `%APPDATA%\SlayTheSpire2\logs\godot.log`.

## How StS2 modding works (research notes)

- StS2 is **Godot 4.5.1 .NET** (not Unity, not Java). Official built-in mod loader + Steam Workshop support since v0.107.1. Mods = folder under `mods\` with `<Name>.json` manifest, `<Name>.dll` (C# assembly), `<Name>.pck` (Godot assets).
- Entry point: `[ModInitializer("MethodName")]` attribute on a static class (`MegaCrit.Sts2.Core.Modding`). The game **ships HarmonyLib** (`0Harmony.dll`) + MonoMod — patch freely with `new Harmony(id).PatchAll()`.
- Community ecosystem: **BaseLib** (`Alchyr.Sts2.BaseLib` on NuGet / Workshop id 3737335127) is the StS2 BaseMod equivalent — node factories, config UI, hooks. **Krafs.Publicizer** exposes the game's private members at compile time (used here).
- Reference mods studied:
  - **Minty Spire 2** (erasels/Minty-Spire-2) — QoL patches, model for csproj/packaging/publicizer setup.
  - **STS2FirstMod** (jiegec/STS2FirstMod) — minimal hello-world recipe this project follows.
  - **Minty Spire 1** (erasels/mintySpire, Java) — `FrozenEyePreviewPatches` renders draw-pile cards on screen by hooking the panel render, repositioning card objects, drawing, restoring. Same pattern applies for showing opponent state overlays in StS2 (patch a node's render/`_Process`).

## Multiplayer architecture (from decompiled source, `Core/Multiplayer`)

The game has full co-op netcode we can piggyback for PvP:

- **Transports**: Steamworks.NET (Steam lobbies) and ENet (LAN/direct-connect). Host-authoritative topology: `NetHostGameService` / `NetClientGameService` / `NetSingleplayerGameService` behind `INetGameService`; typed messages via `NetMessageBus`.
- **Determinism model**: before each combat, `CombatStateSynchronizer` broadcasts every player's serialized state and the host's RNG set (`SyncRngMessage`) so all peers simulate identically. `ChecksumTracker` + `StateDivergenceException` detect desyncs.
- **In-combat**: `ActionQueueSynchronizer` (Core/GameActions/Multiplayer) — clients send `RequestEnqueueActionMessage`, host orders them deterministically, broadcasts `ActionEnqueuedMessage`, everyone executes the same action stream. Turn end flows through `EndPlayerTurnAction` / `EndTurnSignal`.
- **Out-of-combat sync**: per-screen synchronizers — `MapSelectionSynchronizer` (+`MapVote`), `RestSiteSynchronizer`, `RewardSynchronizer`, `EventSynchronizer`, `ActChangeSynchronizer`, etc.
- **Run structure**: co-op already gives each player their own deck/relics/gold/energy on a shared seeded map — exactly the "same map, same options" property the 1v1 needs.

### PvP design implications

- "Same map / same Neow / race Act 1" ≈ vanilla co-op run with combats made per-player instead of shared — likely patch encounter setup so each player fights their own copy of the encounter (RNG is already synced, so same enemies/rolls for both).
- The duel is the novel part: a custom combat room where the "enemy" is the other player's character. State for both players is already replicated on both machines each combat via `CombatStateSynchronizer`; the action relay can carry both players' plays.
- Chess clock: client-side timers keyed off `EndTurnSignal` / turn-state messages; enforcement via a message when a clock hits zero (forced end turn or loss).
- Turn model open question: simultaneous-turn (both plan, resolve together — "competitive Pokémon" style) vs. real-time alternating with shared clock pressure (blitz). The action-queue design (host orders all actions deterministically) supports either.
