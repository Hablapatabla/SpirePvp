# Handoff — state of the mod as of 2026-08-05

Written for someone (human or agent) picking this up cold, on any OS. Everything below was
built and playtested against **Slay the Spire 2 v0.110.1**, on two local clients connected
over ENet.

Read order: this file → `CLAUDE.md` → `README.md` → `docs/DESIGN.md`.
Platform setup: `docs/MAC_SETUP.md` is macOS-specific but its *reasoning* is portable — the
flags, console commands and gotchas below are OS-neutral unless marked.

---

## Where the project is

| Milestone | State |
|---|---|
| **M0** toolchain | done |
| **M1** duel spike | **done**, playtested |
| **M2** round loop | **done**, playtested |
| **M3** chess clock | **done**, playtested |
| **M4** information rules | **done**, playtested |
| **M5** race phase | **working, playtested 2026-08-05.** Two clients race the same seeded map independently — own combats, own rewards, advancing at their own pace — with mirrored RNG and a run-long clock |
| **M6** full loop | **working, playtested 2026-08-05.** Lobby modifiers → race → arena node → rendezvous → deck review → duel → result screen, with checksums live. Remaining: progress HUD, result screen, rematch |
| M7 polish | not started |

A duel is fully playable end to end today: enter the arena, fight with real cards and
statuses, win or lose on HP or on the clock, and land on a victory/defeat screen.

---

## The one idea that explains most of the code

**The duel never breaks card logic. It breaks every place the engine encodes "enemy" as a
*side* rather than a *relationship*.**

Both duelists sit on `CombatSide.Player` with an empty enemy side (DESIGN §3.1). Damage,
block, powers and damage-over-time all operate on `Creature` and needed no changes at all.
Every single bug in M1–M4 was a side comparison somewhere. DESIGN §7 has the full
symptom → cause table; consult it before suspecting a mechanic.

Two rules that follow from this, and that cost real time to learn:

- **`CombatState.HittableEnemies` is not patchable.** It is a bare property with no idea who
  is attacking, so it cannot know whose opponent to return. `CombatState.GetOpponentsOf` is
  the correct chokepoint — it is handed the attacker.
- **Targeting is validated more than once, independently.** `CardModel.IsValidTarget`,
  `PotionModel.IsValidTarget` and `NTargetManager.AllowedToTargetCreature` all check sides
  separately. Fixing one and testing is how you conclude, wrongly, that nothing happened.

---

## Things that will bite you

**Patches fail silently if you use `Harmony.PatchAll`.** It throws on the first bad target and
abandons the rest, so one typo disables an arbitrary subset while the mod still loads and
still logs "loaded". `SpirePvpInit` therefore applies each patch class independently and logs
a count. **On every launch, confirm the log says `N patch classes applied cleanly`** — if it
says `PATCH FAILED`, some of the mod is not running and in-game results mean nothing.

**Harmony resolves `[HarmonyPatch(typeof(X))]` against methods declared on `X` only.** Naming
an inherited method throws "Undefined target method". This caused the above.

**Verify in game after every patch change.** Several sessions' worth of confusing symptoms
were patches that had never applied.

**Mod state is static; the run it belongs to is not.** Every `_armed` flag, the clocks and
`DuelSession` all outlive a run, while the net service they were bound to is disposed with it.
Play a second match in the same process and handlers silently fail to re-register (the flag
still says armed) while the old match's clocks keep ticking — the host was caught broadcasting
`ClockSyncMessage` twice a second into an unrelated co-op run. `DuelRunCleanupPatch` hooks
`RunManager.CleanUp` and lets go of everything; add to `DuelMatch.OnRunEnded` when you add
state. It is a **prefix**, because CleanUp disconnects the net service and nulls the run state,
so a postfix would have nothing left to unregister from.

**`DuelNeowOptionsPatch` blanks `RunState.Modifiers` while Neow rolls its blessings**, so for the
duration of that call the run does not look like a PvP match to its own mod. Anything asking
`DuelMatch.IsPvpRun` from inside Neow's option generation gets the wrong answer unless it goes
through `DuelMatch` (which consults `MaskedModifiers`). This is why the co-op-only Massive
Scroll blessing survived a filter that was working perfectly everywhere else.

**With this mod installed you cannot join an unmodded friend's multiplayer game.** Confirmed
2026-08-05. The mod is inert at *runtime* — every patch is guarded behind `DuelSession`, which
stays `Inactive` in normal play — but its mere presence changes the multiplayer handshake, and
`JoinFlow` rejects the connection before any of that matters. Two independent gates, either
one sufficient:

1. **Mod list mismatch** → `ConnectionFailureReason.ModMismatch`. `JoinFlow` compares
   `PeerVersionInfo.gameplayAffectingMods` and refuses if either side has one the other
   lacks. Our manifest declares `"affects_gameplay": true`, so SpirePvp is on that list.
2. **Model database hash mismatch** → `ConnectionFailureReason.VersionMismatch`.
   `ModelIdSerializationCache.Hash` is an xxHash over `ModelDb.All`, and `DuelEncounter` is
   registered into it automatically by the mod-assembly scan.

**So flipping `affects_gameplay` to `false` would not fix it** — gate 2 still fires, and the
manifest would then be lying about a mod that genuinely alters combat. This is the engine
correctly refusing a configuration that would desync. For real games, disable the mod on the
Mods screen (`is_enabled` per mod, stored per profile, so the dev profiles are unaffected) or
rename the `mods/SpirePvp` folder, then restart.

Not a problem for shipping: SpirePvp is a PvP mod, so both players will have it anyway. It
only bites when a developer plays vanilla co-op on the same install.

**Steam updates the game silently.** A pending update landed mid-session and moved the codebase
from v0.109.0 to v0.110.1 underneath a decompile, producing an investigation that was entirely
wrong (a method that "did not exist" was added in the update). After any launch through Steam,
check `release_info.json` and re-run the decompile if the version moved.

---

## Running two local clients (any OS)

No second machine, no Steam lobby, no second account. Vanilla command-line flags do it — this
is I7, and it needed no mod code.

```
<game binary> --force-steam=off --fastmp=host_standard
<game binary> --force-steam=off --clientId=1001 --fastmp=join
```

macOS (tab 1 = host, tab 2 = client):
```
"$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/SlayTheSpire2" --force-steam=off --fastmp=host_standard
"$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/SlayTheSpire2" --force-steam=off --clientId=1001 --fastmp=join
```

**Windows: use the scripts in `scripts/`** — they wrap the same flags and also handle the
build, the windowing and the mod-consent gate (below). Tab 1 then tab 2:
```
.\scripts\host.ps1
.\scripts\client.ps1
```
Run them in **PowerShell 7 (`pwsh`)**, not Windows PowerShell 5.1. The two keep separate
execution policies, and 5.1 commonly defaults to `Restricted`, which refuses the scripts with
"running scripts is disabled on this system" — nothing to do with the scripts themselves.
Either switch shells, or `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` for 5.1.
`host.ps1` builds first and aborts the launch if the build fails; `client.ps1` never builds,
because two concurrent builds fight over the same output files. Flags: `-NoBuild`,
`-Fullscreen`, `-Width <px>`, `-ClientId <n>`. Verify the run with `.\scripts\check-log.ps1`.

- `--force-steam=off` skips Steamworks entirely (`NGame.InitializePlatform`). Required: a
  direct launch otherwise fails `SteamAPI_Init` with "No appID found" and the game quits. It
  also sidesteps Steam's one-instance-per-account limit, which is what makes two local clients
  possible at all.
- `--fastmp=<host_standard|join>` is a vanilla dev flag that auto-clicks through the menus
  **and** forces `PlatformType.None`, i.e. the ENet transport on `127.0.0.1:33771` instead of
  Steam lobbies.
- `--clientId=N` sets the net id *and* selects the save profile, so each instance needs its
  own.

**Mod consent is per save profile, and it is a silent killer.** Without it the game logs
`Skipping loading mod SpirePvp, user has not yet seen the mods warning` and loads *no mods at
all*, while otherwise looking completely normal. Two ways to clear it:

- By hand: launch with no `--fastmp`, accept the warning on the Mods screen, quit.
- By file: the flag is `mod_settings.mods_enabled` in the profile's `settings.save`, which is
  plain JSON. The Windows scripts set it automatically.

Note that `--force-steam=off` selects a *different* profile than a Steam launch
(`NullPlatformUtilStrategy.LocalPlayerId`, which is `1` or whatever `--clientId` says, names
the directory: `%APPDATA%\SlayTheSpire2\default\<id>\`). So consenting once through Steam does
nothing for the dev clients — each `--clientId` needs clearing separately.

**Windowing: Godot's `--windowed` / `--resolution` flags do not work.** `NGame` reapplies the
display mode from `settings.save` during startup and overrides them, so the game launches
fullscreen no matter what you pass — which makes two clients unusable side by side. The
setting file is the only thing that decides: `fullscreen`, `window_size`, `window_position`.
`scripts/Sts2Path.ps1` patches those per profile before launch, tiling the host left and the
client right on the primary monitor (`-Fullscreen` opts out, `-Width` overrides the size). The
vanilla `-wpos X Y` flag also forces windowed and is a lighter alternative, but it still takes
its *size* from the settings file, so editing the file covers both anyway.

The client window retitles itself to "Slay The Spire 2 (Client)", which is how you tell them
apart.

---

## Clock rules (settled 2026-08-05, after trying it both ways)

**Race — a global countdown.** Both clocks run continuously and never pause: reach the arena
before the bank empties. They start together and never stop, so the two values stay identical.

A chess clock was tried here first and is *wrong* for this phase: the players are in separate
combats and never wait on each other, so stopping your clock while theirs runs measures
nothing. Time spent racing is simply time you will not have in the duel.

**Duel — a real chess clock.** Now the players do wait on each other, so ending your turn
stops your clock while your opponent's keeps running.

The top-bar display follows the phase: a single countdown during the race (both clocks are
identical there by construction, so two numbers would say the same thing twice), and
`YOU 2:31 · OPP 1:47` once the duel starts and they actually diverge.

Host-authoritative in both phases: nothing pauses during the race, and in the duel the host
sees both players' end-turn state directly. Sync carries each clock's paused flag so a client's
prediction stops when the owner's does, instead of counting a stopped clock down and snapping
it back twice a second.

## Starting a PvP match

Configured in the lobby, before the run exists (DESIGN §5b) — not by a console command.

Host: **Multiplayer → host → Custom run**, then tick one turn model and one clock in the
modifier list:

- `1v1 Duel: Real-Time` **or** `1v1 Duel: Turn-Based` — picking either marks the run as PvP
- `Duel Clock: 3 / 5 / 10 min` **or** `Off`

Both groups are mutually exclusive (radio-button behaviour, via vanilla's
`MutuallyExclusiveModifiers`), and the joining player sees the choices in the lobby before
starting. Custom mode also exposes the seed field, which is useful for rematches on a known
seed. `--fastmp=host_custom` boots straight into a custom multiplayer host.

Custom runs are gated behind `CustomAndSeedsEpoch`; `unlock all` clears it on a dev profile.

**If the modifiers show up as raw keys like `DUEL_BLITZ.title`, the `.pck` is stale.** Names
come from `SpirePvp/localization/en/modifiers.json`, which ships in the pack, not the DLL.
`host.ps1` re-exports the pack when anything under `SpirePvp/` is newer, but a manual
`dotnet build` alone will not.

## Console commands

The dev console opens with **backtick** (also `'`, `*`, `^`, or Shift+8). **Running any mod
unlocks the full vanilla debug command set** (`ModManager.IsRunningModded()` feeds
`shouldAllowDebugCommands`), so you already have everything below without writing tooling.

Mod commands:

| Command | Effect |
|---|---|
| `duel start` | Opens the opponent's decklist as the duel entry screen. Both players confirm, then the arena loads. |
| `duel now` | Skips the entry screen, straight into the arena. Debug shortcut. |
| ~~`duel clock <minutes>`~~ | **Removed.** The clock is part of the match agreement, picked in the lobby, and runs from run creation. A mid-run command could only hand someone a bank they never agreed to or reset one already spent — either silently invalidates the match. |
| `duel on` / `duel off` | Converts the combat you are already in into a duel, and back. Legacy path from M1; `duel start` is the real flow. |
| `race on` / `race off` | **Debug shortcut only.** A real match is configured in the lobby (below); this forces race mode onto an already-running co-op run, which is useful for exercising the patches but leaves Neow and pre-existing seeds un-mirrored. |

Useful vanilla ones for testing:

| Command | Notes |
|---|---|
| `unlock all` | **Run this on a fresh dev profile before testing anything reward-related.** A profile with no runs and no epoch unlocks playing Ironclad gets *hardcoded* tutorial rewards with no RNG at all (`RewardsSet.TryGenerateTutorialRewards`), which silently masks real reward generation — it once looked exactly like working RNG mirroring. Unlocking epochs clears the `EpochUnlockCount() == 0` half of that condition. **Not networked: run it on both clients.** |
| `card <ID> [pile]` | Screaming snake case (`BODY_SLAM`). Piles: `Draw Hand Discard Exhaust Play Deck`. **`Deck` is the run-level pile** the entry screen reads. |
| `power <id> <amount> <target-index>` | Index is into `state.Creatures` — `0` is you, `1` is the opponent. Works fine despite the empty enemy side. |
| `damage <amount> <index>` | **Always pass the index.** Bare `damage 10` targets `Enemies`, which is empty in a duel, and silently does nothing. |
| `energy`, `draw`, `block`, `heal`, `potion`, `relic`, `kill` | As labelled. |

Known vanilla quirk, not a bug in this mod: the top-bar deck counter caches its value and only
refreshes on the pile's `CardAddFinished`/`CardRemoveFinished`, which the console's add into
the run-level Deck pile does not raise. Cards added by console are really there; the label is
just stale.

---

## Architecture tour

`src/duel/`

| File | Role |
|---|---|
| `DuelSession` | Client-local phase state. Every patch is inert unless a phase is active, so the mod does nothing in normal play. |
| `DuelEntry` | The entry flow: opponent's decklist, revocable confirm, both-ready gate. |
| `DuelArena` | Enters the arena room. **Ordering is load-bearing** — `DuelSession` must be active *before* `EnterRoom`, or the empty enemy side ends combat instantly. |
| `DuelEncounter` | A combat encounter with no monsters. Registered automatically: `ModelDb.AllAbstractModelSubtypes` scans mod assemblies, so custom models need **no BaseLib**. |
| `DuelLayout` | Draws the opponent on the enemy side and mirrors their art. Presentation only — `CombatSide` is untouched. |
| `DuelClockService` / `DuelClock` | Chess clocks. Wall-clock based, run-scoped by design. |
| `DuelFlag` | Losing on time. Host-authoritative. |
| `DuelResult` | Ends the duel on a victory/defeat screen. |

`src/duel/patches/` — one class per concern, each documenting *why* the patch exists and what
the engine does that requires it. Those comments are the real documentation; read them before
changing behaviour.

`src/net/DuelMessages.cs` — `INetMessage` types. **Auto-registered** by `MessageTypes.Initialize`
scanning mod assemblies; no registration call needed. **Message ids are positional**, so both
clients must run the same build. `ForcedEndTurnMessage` is dead (sudden death replaced it) but
retained deliberately, because removing it renumbers the rest.

### Determinism, and why the netcode looks the way it does

The engine is a host-authoritative deterministic simulation: clients request actions, the host
orders them, everyone executes the same stream. Anything that decides an outcome must have
exactly one decision-maker, or the two sims disagree.

So: **the host alone decides losing on time** (clients' clocks are display-only), and **the host
alone decides when the duel starts** (`DuelStartMessage`; two clients independently entering a
room is a race). Clock display is synced separately and cosmetically via `ClockSyncMessage`.
Keep that split if you extend this.

---

## Immediate next step

Fix the Neow regression in Open Issues first — it is the only thing currently broken. Then, in
the order Lucas asked for (2026-08-05):

0. **Split the clock into a race bank and a duel bank** (DESIGN §9). The single shared bank is
   wrong: an act needs far more time than one duel. Two lobby modifier groups, and the duel
   starts on a *fresh* bank rather than the race's remainder. This is the next feature.

Then content and polish, none of it risky:

1. **`RaceProgressHud`** — the messages already flow (`RaceProgressMessage`, and the opponent's
   portrait moves on your map); what is missing is a real HUD showing their position, HP and
   deck size while you wait at the arena.
2. **`DuelResultScreen`** (DESIGN §6) — replaces the vanilla game-over screen, which currently
   reports run score lines that mean nothing for a duel. Rematch lives here.
3. **M7 entry point** — a dedicated PvP item in the multiplayer menu that sets the same
   modifiers, so only the presentation changes.

Expect the co-located-party pattern to keep recurring as the race covers rest sites, shops and
events — each has its own synchronizer assuming both players are present. Diagnose them the same
way: find where the code assumes every run player is there. And note its content-level twin,
which cost this session too: the engine reads `Players.Count > 1` as "co-op" in card selection,
so a PvP run was being offered ally-targeting co-op cards and Massive Scroll's co-op-only Neow
blessing (`RaceNoCoopCardsPatch`).

Smaller known gaps, none blocking:

- `HellraiserPower`'s infinite-combo cap misfires in a duel (`HittableEnemies.All(...)` on an
  empty list is vacuously true), capping auto-plays at 9 per turn. Arguably desirable.
- Other `AfterSideTurnStart` powers may have the same round-late skew poison had. Audit when
  one shows up; only poison is fixed.
- The duel entry screen's confirm feedback is a colour tint standing in for the intended
  green check + opponent portrait (DESIGN §6, wants an asset pass).
- No `.pck` assets yet beyond the mod image. The duel map node icon (M6) is the first real
  need, and the custom confirm button should be batched with it.

---

## The full loop works (2026-08-05, playtested)

Lobby → race Act 1 → both reach the arena → deck review → duel → victory/defeat screen, on two
clients, with checksums live and no state divergence.

### The lesson that cost this session: `RunManager.EnterRoom` is not how you enter a room

It is the *last step* of entering one. Every vanilla entry point — `EnterMapPointInternal` for
map → room, `EnterRoomDebug` for dev commands — runs a preamble in front of it, and calling
`EnterRoom` alone silently skips all of it. The arena is the first room this mod enters that was
not reached through a map point, so it was the first to need that preamble spelled out.

Four omissions, four unrelated-looking symptoms, none of them loud:

| Missing step | Symptom |
|---|---|
| `ClearScreens()` | **Cards frozen, uninteractable.** `DuelRendezvous` hid the map with `Visible = false`, which leaves `NMapScreen.IsOpen` true — and `ActiveScreenContext.GetCurrentScreen` tests `IsOpen` *before* the combat room. The invisible map stayed the active screen, so `NCombatRoom.OnActiveScreenUpdated` called `Ui.Disable()`: piles off, end-turn off, every card play cancelled as it began. |
| `StartSync`/`WaitForSync` | `RaceCoordinator.EndRace()` was never called at all, so the duel ran with the race's state sync still disabled. |
| `CombatReplayWriter.RecordInitialState` | **Turn loop died mid-start**, hand left half-dealt in the middle of the screen. The replay writer records every checksum and throws without an initial state. Only surfaced once checksums came back on, because `StartTurn`'s first act is `GenerateChecksum("After player turn start")`. |
| the fade | Purely cosmetic, but the cut from map to full-screen card grid read as a glitch. |

`duel start` never hit any of them: it entered from inside a live combat, where the map is
already closed and the previous combat's replay is still open.

`DuelArena.EnterRoom` now reproduces `EnterMapPointInternal`'s preamble step for step, with a
comment listing each one and what it broke. **Keep the two in sync.**

### The other half: what the state sync does *not* cover

Re-enabling `ChecksumTracker` immediately produced a `StateDivergence` and a kicked client. The
two state dumps were identical in every creature, card, pile, HP and RNG seed, and differed only
in `Choice IDs 1,1` vs `0,2` and `Reward IDs 1,0` vs `0,1`.

`CombatStateSynchronizer` reconciles each player's serialized state, the run RNG and the shared
relic grab bag — **and nothing else**. The choice, reward, action and hook counters live on the
*synchronizers*, are bumped locally by every choice / reward set / enqueued action, and so drift
apart by construction during a race. `RaceCoordinator.EndRace` now zeroes all four through the
engine's own public `FastForward*` APIs (they exist for replay playback). Action and hook ids
were not in the observed diff only because nothing had executed yet — they are in the same
checksum and would have diverged on the first card played.

If a divergence ever reappears, the host logs both full state dumps: diff them and the answer is
in the two lines that differ.

## Open issues (2026-08-05, end of session)

**BLOCKING — Neow offers no blessings at all; the room looks skipped.** A regression from this
session, and the first thing to fix.

The log is unambiguous:

```
[EventSynchronizer] Beginning event EVENT.NEOW, shared: False
[EventSynchronizer] Event EVENT.NEOW began for player 1 with options:
[EventSynchronizer] Event EVENT.NEOW began for player 1001 with options:
```

Empty option lists, both players. `Neow.GenerateInitialOptions` branches on
`RunState.Modifiers.Count <= 0`: with no modifiers you get the three blessings, with any modifier
you get only what those modifiers supply — and ours supply none, so it returns `Array.Empty`.
`DuelNeowOptionsPatch` exists precisely to blank `RunState.Modifiers` for the duration of that
call so vanilla takes its normal branch. **On this run it did not.** Start there:

- Did the prefix run at all, and did its guard pass? It returns early unless
  `__instance.Owner?.RunState is RunState`, `_modifiersField != null`, `DuelMatch.IsPvpRun` is
  true, and *every* modifier is a `DuelModifierBase`. Log inside it rather than reasoning about
  it — that guard has four independent ways to opt out silently.
- `_modifiersField` is `AccessTools.Field(typeof(RunState), "<Modifiers>k__BackingField")`. If
  `Modifiers` ever stops being an auto-property, that lookup returns null and the whole patch
  no-ops without a word. Check it is non-null at load.
- The patch gained `DuelMatch.MaskedModifiers` this session (so the mod's own `IsPvpRun` keeps
  answering "yes" while vanilla is being lied to). Suspect the interaction: `IsPvpRun` now reads
  `MaskedModifiers ?? runState.Modifiers`, and a stale non-null `MaskedModifiers` would make the
  guard see a run whose modifiers are not the ones it is about to blank.
- This is a per-player event (`shared: False`), so the option generation runs once per player.
  Confirm the patch covers both passes, not just the local one.

**The `duel over` NullReferenceException, still unpinned.** Thrown once per duel, on both
clients, from `CombatManager.CheckWinCondition` between `duel over` and the game-over screen
loading. Harmless so far — the result screen is already up and the outcome is correct.
`DuelEndCombatPatch` was widened to cover `DuelPhase.Complete`, which removed one route into it
(vanilla `EndCombatInternal` running against the synthetic arena); the remaining candidates are
`ProcessPendingLoss` and `IsCombatEnding`, and inlining has eaten the frames that would say
which. Add logging inside the patch rather than guessing.

**Should the opponent's pet be attackable?** Open *design* question, deliberately not decided.
`DuelLayout` now draws the opponent's pets on the enemy side (`BelongsToOpponent` resolves
`Player ?? PetOwner`), but they are still mechanically on `CombatSide.Player`, so they are
scenery: you cannot hit the opponent's Osty and it cannot be killed. That is a real matchup
question, not a rendering one — it belongs in `DuelOpponentsPatch` / `GetOpponentsOf` and wants a
decision before it is coded.

**Deck review background is the boss background.** Should be plain black or something simple
like the campfire. Lucas is drawing something; until then the fix is whatever `NDeckCardSelectScreen`
uses behind the grid.

**The result screen is vanilla's game-over screen**, so its score lines talk about run progress —
"damage to the Architect" and similar — which is meaningless for a duel. `DuelResultBannerPatch`
rewrites the banner only. The real fix is M6's `DuelResultScreen` (DESIGN §6: winner, per-round
damage, rematch), not more banner patching.

**A flame effect for the deck-review transition** (wanted, not built). The rest site's fire is
`NRestSiteFireVfx`, a scene child of `NRestSiteRoom` with no static `Create`, so it cannot be
reused standalone. The pieces of the rest animation that *are* standalone and parameterless are
`NRestSmokeVfx.Create()` and `NDesaturateTransitionVfx.Create()`. A real flame is scene work,
best batched with the M6 asset pass.

**Run-history icon load failure.** The UI looks for `images/ui/run_history/duel_encounter.png`
and `_outline.png`, vanilla paths the mod cannot write to, so it logs an error once per run.
Cosmetic. Fix by pointing `ImageHelper.GetRoomIconSuffix` at an existing boss icon for our
encounter.
