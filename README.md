# Spire PvP

1v1 competitive Slay the Spire 2. Two players get the same map, same path options, and same Neow bonus; race through Act 1 (and maybe grab the Act 2 ancient bonus), then duel. Chess-clock turns for blitz-style play.

## Status

**The full loop works and is playable end to end** (playtested through 2026-08-11, two local clients): main menu → **Duel** → lobby with presets → race Act 1 on a mirrored seed → arena node → rendezvous → deck review → duel → result screen, with desync detection live. No blocking issues.

The clock is two banks — a race deadline and a fresh duel bank — configured by preset (Blitz 10/1, Rapid 15/3, No clock) or by hand. A match can also end by **resignation** (abandoning is a loss and a win for the opponent) or by **agreed draw**. The result screen shows the match's own statistics and badges, compared against your opponent rather than scored as a run.

Remaining: rematch, and the turn-based turn model (M8). See `docs/HANDOFF.md`.

## Playing with a friend

Both players need the mod, and both must be on the **same commit** — net message ids are
positional and the model database is hashed into the connection handshake, so mismatched builds
are refused by the engine before anything of ours runs.

### Install

1. **Slay the Spire 2** on Steam (built against **v0.110.1**).
2. **[.NET SDK 9](https://dotnet.microsoft.com/download)** and **git**.
3. Then:

   ```
   git clone <repo url>
   cd SpirePvp
   dotnet build
   ```

`dotnet build` installs everything into the game's `mods/SpirePvp/` folder for you — dll, json
and the `.pck`. **No Godot install is needed**: the `.pck` is committed precisely so that a
clone is playable without the editor.

The game is found automatically in the usual Steam locations on Windows, macOS and Linux. If
Steam lives somewhere else, point at it once:

```
dotnet build -p:Sts2Path="E:/Games/steamapps/common/Slay the Spire 2"
```

### Enable mods, once per save profile

**This step is a silent killer.** Without it the game loads *no mods at all* while otherwise
looking completely normal — no error, no missing-mod message, just a game with no Duel entry.

Launch the game, open **Mods** from the main menu, and accept the warning. The log line to look
for if something is wrong is `Skipping loading mod SpirePvp, user has not yet seen the mods
warning`.

### Play

Launch normally through Steam (no command-line flags — those are for the two-local-clients dev
setup and force a LAN transport instead of Steam).

- **Host:** Multiplayer → Host → **Duel**. Pick a preset or set the clocks by hand, then start.
- **Join:** Multiplayer → **Join**, and pick the host from your Steam friends list.

The joining player sees the agreed time control in the lobby before committing.

### Updating

```
git pull
dotnet build
```

Both of you, to the same commit. If one side has pulled and the other has not, the connection is
rejected rather than silently desyncing — which is the good outcome, but it does mean a fix is
only live once you have both pulled it.

### Known: you cannot join an unmodded friend while this is installed

The mod is inert at runtime — every patch is guarded, and normal runs are untouched — but its
mere presence changes the multiplayer handshake, and the engine refuses the connection on
either a mod-list mismatch or a model-database hash mismatch. This is the engine correctly
refusing a configuration that would desync.

To play vanilla co-op with someone who does not have it, disable SpirePvp on the **Mods** screen
(the setting is per profile) and restart.

## Agent handover prompt

Paste this to start a fresh agent session on this project.

**Keep this prompt durable.** Everything volatile — what state the project is in, what to do first, the current patch count — lives in `docs/HANDOFF.md` and is maintained there. The prompt below deliberately points at that file instead of restating it, because an earlier version inlined the state and was wrong within a session: it still named four fixes that had since been verified and a patch count that had moved. One place to update, not two.

> You're picking up **SpirePvp**, a 1v1 PvP mod for Slay the Spire 2, at `<repo path>` (developed on a Windows desktop and a MacBook — see `docs/MAC_SETUP.md`). **Pull first.**
>
> Read in this order: `docs/HANDOFF.md` → `CLAUDE.md` → `docs/DESIGN.md`. **HANDOFF is the living document** — it holds the current state, the patch count, what is playtested and what is not, and your first task. Trust it over anything restated elsewhere, including this prompt. Decompiled game source lives outside the repo (regenerate per README if missing; game is v0.110.1 — check `release_info.json` and re-decompile if Steam moved it underneath you, which it has done mid-session before).
>
> **State, in one line:** the whole loop is playable and has been played end to end many times — two players configure a match with custom-run modifiers (turn model, race clock, duel clock), race the same seeded map independently with mirrored RNG, converge on an arena node placed back-to-back after the Act 1 boss, review each other's decks, and duel. Matches also end by resignation or agreed draw. Checksums and pre-combat state sync are live throughout; back-to-back matches in one process reconfigure cleanly.
>
> **Your first task is whatever HANDOFF's "Immediate next step" says.** It is kept current and usually names something built-but-unplaytested, because that is the state most work lands in here.
>
> **Rules that cost this project real time — don't relearn them:**
>
> * Read the logs yourself (`logs/host.log`, `logs/client.log`) rather than asking for symptoms. Check the log's timestamp against the installed DLL — a stale log looks identical to a patch that stopped applying. When two clients disagree, the host dumps *both* full state dumps on divergence: diff them and the answer is the lines that differ. The launch scripts rotate logs, so the previous five runs are still there as `host.<timestamp>.log`.
> * Confirm `N patch classes applied cleanly` after every change — **HANDOFF has the current N**. Never use `Harmony.PatchAll`.
> * Arm all message handlers at run start, never lazily on first local use — the peer can announce something before you act, and the message is silently dropped. This has now bitten five separate times. Release them on run teardown for the same reason (`DuelRunCleanupPatch`).
> * A prefix that skips an `async Task` must assign `__result`, or the caller NREs on `await null`. **And the stack will name the caller, with no frame for the method you patched** — which reads exactly like inlining and has sent two multi-session hunts into reading the wrong function. A missing frame under an `await` means check the patch, not the callee. Related: a Harmony *postfix* on an async method runs when the state machine is created, not on completion.
> * **Guard on the condition, not on each route.** A run can end without a duel result — abandoning, the host quitting, a disconnect — and none of those reach `DuelResult`. Anything that must stop when the run stops should ask whether the run is still in progress, because there is always another route out.
> * **Ask the condition you mean, not one that merely correlates** — and when you fix a wrong predicate, grep for it. A phase test standing in for "has the duel bank been granted" was fixed in one file and left standing in another, where it decided a match result rather than a label.
> * **"It only logs an error" is a claim worth measuring.** The arena's missing room icon was recorded as one line per run; it was 19 per client per session, because `AssetCache` never caches a failed load and every repaint retried it. Count the lines, and check whether the failure is cached. **Silence is not evidence either:** creating the lobby logs nothing at all — the first `[StartRunLobby]` line in a healthy run is `Client 1001 connected` — so a listening host nobody reaches is indistinguishable from a host that never opened a lobby. Before concluding from an absent log line, confirm the thing you expect would have logged.
> * Mod state is static; the run it belongs to is not. Anything surviving a run must be released in `DuelMatch.OnRunEnded`, or the next match silently fails to re-register it.
> * `RunManager.EnterRoom` is the *last step* of entering a room, not the whole thing. `DuelArena` mirrors `EnterMapPointInternal` step for step — **keep the two in sync**. Six omissions found so far, each failing differently and none loudly; the two most recent were the map coord (which left the clients at different `RunLocation`s, so the host's arbitration messages were buffered forever and the client froze mid-turn while the host played on) and the map-point-history entry (which made an elite killed en route count as zero and become the reported cause of death).
> * **A message the peer needs must be sent, not looked up.** The race decouples the two runs, so your copy of the opponent's `Player`, `MapPointHistory` and deck all stop updating — the pre-combat state sync only fixes it at arena entry, which is *after* the deck review. Anything read about the opponent before that point is stale and will look plausible.
> * Loc filenames are load-bearing: `LocManager` merges a mod's tables only into tables vanilla already has, **by filename**. A new `spirepvp.json` would never be read. New strings must ride in an existing vanilla table.
> * Never kill the user's running game processes; the launch scripts stop instances themselves.
> * The recurring root cause of the whole race phase: the engine assumes the party is co-located, and each assumption fails differently — a hang, a silent freeze, a crash. Its content-level twin: the engine reads `Players.Count > 1` as "co-op", so a PvP run gets offered co-op-only cards and relics.
>
> **Build/test (Windows):** `.\scripts\host.ps1` (pwsh 7 — stops instances, builds, re-exports the `.pck` if assets changed, launches), then `.\scripts\client.ps1` in a second tab. macOS has `./scripts/*.sh` equivalents (no pwsh there); see `docs/MAC_SETUP.md`. A match is configured in a **Custom** lobby — the only one exposing the modifier list — so pass `-Custom` (`host.ps1`) or `--custom` (`host.sh`); a run launched without it cannot configure a match at all, and the only sign is `--fastmp=host_standard` on the log's args line. **Before starting the client, let the host's `.pck` export finish _and_ get the host visibly into the lobby.** A fresh pull triggers a re-export, and reading a half-written pack fails as a `LocException` that looks exactly like malformed JSON in the repo; starting the client while the host is still short of the lobby fails as a bare `[ENetClient] Connection timed out!`, which the host log cannot confirm or deny (see the silence rule above). Console opens with `'`; `travel` unlocks clicking any map node; `unlock all` is needed per dev profile. `kill` does not work in a duel by design — use `damage <amount> <index>`.
>
> Lucas playtests every change — hand him one specific thing to try with specific things to watch, then read the logs yourself.

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
