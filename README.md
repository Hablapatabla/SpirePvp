# Spire PvP

1v1 competitive Slay the Spire 2. Two players get the same map, same path options, and same Neow bonus; race through Act 1 (and maybe grab the Act 2 ancient bonus), then duel. Chess-clock turns for blitz-style play.

## Status

**The full loop works** (playtested 2026-08-05, two local clients): lobby modifiers → race Act 1 → arena node → rendezvous → deck review → duel → result screen, with desync detection live. One blocking regression open (Neow offers no blessings) — see `docs/HANDOFF.md`.

## Agent handover prompt

Paste this to start a fresh agent session on this project.

> You're picking up SpirePvp, a 1v1 PvP mod for Slay the Spire 2, at `<repo path>` (developed on a Windows desktop and a MacBook — see `docs/MAC_SETUP.md`).
>
> Read first, in this order: `docs/HANDOFF.md` → `CLAUDE.md` → `docs/DESIGN.md`. HANDOFF's "Open issues" section is your starting point. Decompiled game source lives outside the repo (regenerate per README if missing; game is v0.110.1).
>
> **State:** the whole loop is playable. Two players configure the match with custom-run modifiers (turn model + clock), race the same seeded map independently with mirrored RNG, converge on an arena node placed back-to-back after the Act 1 boss, review each other's decks, and duel. Checksums and pre-combat state sync are live through the duel, and a match now runs end to end without a desync.
>
> **Your first task — the blocking regression:** Neow presents no blessings, so the room looks skipped. HANDOFF's open-issues section has the log evidence and four specific things to check, in order. The short version: `Neow.GenerateInitialOptions` returns an empty list when the run carries modifiers, `DuelNeowOptionsPatch` exists to hide our modifiers for the duration of that call, and on the last run it did not. It gained a `DuelMatch.MaskedModifiers` interaction this session — suspect that first.
>
> **Then the next feature, already specified in DESIGN §9:** split the clock into a separate race bank and duel bank (e.g. a 10-minute race followed by a 2-minute duel). Two lobby modifier groups; the duel starts on a fresh bank, not the race's remainder.
>
> **Rules that cost this project real time — don't relearn them:**
>
> * Read the logs yourself (`logs/host.log`, `logs/client.log`) rather than asking for symptoms. Check the log's timestamp against the installed DLL — a stale log looks identical to a patch that stopped applying. When two clients disagree, the host dumps *both* full state dumps on divergence: diff them and the answer is the lines that differ.
> * Confirm `N patch classes applied cleanly` after every change. Never use `Harmony.PatchAll`.
> * Arm all message handlers at run start, never lazily on first local use — the peer can announce something before you act, and the message is silently dropped. This bit three separate times. Release them on run teardown for the same reason (`DuelRunCleanupPatch`).
> * A prefix that skips an async method must assign `__result = Task.CompletedTask`, or the caller NREs on `await null`.
> * `RunManager.EnterRoom` is the *last step* of entering a room, not the whole thing. Vanilla's real entry points run a preamble in front of it; skipping any of it fails silently and differently each time. `DuelArena.EnterRoom` mirrors `EnterMapPointInternal` step for step — keep the two in sync.
> * Never kill the user's running game processes; `host.ps1` stops instances itself before building.
> * The recurring root cause of the whole race phase: the engine assumes the party is co-located, and each assumption fails differently — a hang, a silent freeze, a crash. Its content-level twin: the engine reads `Players.Count > 1` as "co-op", so a PvP run gets offered co-op-only cards and relics.
>
> **Build/test:** `.\scripts\host.ps1` (pwsh 7 — stops instances, builds, re-exports the `.pck` if assets changed, launches), then `.\scripts\client.ps1` in a second tab. Console opens with `'`; `travel` unlocks clicking any map node; `unlock all` is needed per dev profile.
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
