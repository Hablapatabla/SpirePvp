# Handoff — state of the mod as of 2026-08-06 (evening)

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
| **M6** full loop | **working, playtested 2026-08-06.** Lobby modifiers → race → arena node → rendezvous → deck review → duel → result screen, with checksums live, split race/duel clocks and Neow intact. Plus resignation and agreed draws. Remaining: rematch, and duel stats on the result screen |
| M7 polish | **next milestone: a dedicated Duel host menu** (below) |

A duel is fully playable end to end today: enter the arena, fight with real cards and
statuses, win or lose on HP or on the clock, and land on a victory/defeat screen.

**A match can also end by consent.** Added 2026-08-06 and playtested from both sides:

- **Resigning.** Abandoning a PvP run is tipping your king over — a loss for you, a win for
  your opponent. The pause menu's Give Up button is relabelled **Resign** and is *revealed to
  the client*, which vanilla hides because `RunLobby.AbandonRun` throws for non-hosts. A
  resignation skips vanilla's abandon entirely (see below), so the client never calls it.
- **Agreed draws.** An **Offer Draw** button sits under Resign, tinted so it does not read as a
  third way to quit. The opponent gets an accept/decline popup. Offers that cross on the wire
  count as agreement rather than conflict, and pressing Offer Draw while theirs is outstanding
  is an acceptance.

**Why a resignation replaces vanilla's abandon rather than running alongside it.**
`RunManager.Abandon` sends `RunAbandonedMessage` and then *disconnects*. Declaring the result
before that would put a screen up for vanilla to tear down; declaring it after would be a send
into a dead transport — the bug this project had just finished removing. So `DuelResignPatch`
prefixes `RunManager.Abandon`, broadcasts, declares, and returns `false`, leaving the
connection **up**. That is also what a rematch will need.

**But the connection does not survive leaving the result screen** (`QuitGameOver`, observed
twice in logs). So rematch has to be a button *on* that screen — there is no later moment.

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
**40 as of this handoff.**

**Harmony resolves `[HarmonyPatch(typeof(X))]` against methods declared on `X` only.** Naming
an inherited method throws "Undefined target method". This caused the above.

**Verify in game after every patch change.** Several sessions' worth of confusing symptoms
were patches that had never applied.

**A prefix that skips an async method must assign `__result = Task.CompletedTask`.** Otherwise
the caller awaits null and throws — and it throws *in the caller*, so the stack names a vanilla
method with no frame for the one you patched. That reads as inlining and sends you off reading
the callee. It has now cost this project two separate multi-session hunts
(`RaceStarsWithoutCombatPatch`, then `DuelEndCombatPatch`). When a prefix returns `false`,
check the target's return type before anything else; every skipping prefix in `src/` has been
swept for this and the rest target `void` methods.

**A run can end without a duel result, and most teardown routes are not `DuelResult`.**
Abandoning the run, the host quitting, a disconnect — none of them reach
`DuelResult.DeclareWinner`, which was the only thing stopping the clocks. Measured 2026-08-06:
abandoning a race left the host broadcasting `ClockSyncMessage` twice a second into a
disconnected service for 21 seconds — 46 error lines on the host, a matching "no message
handlers are registered" on the client. `DuelClockService.Tick` now stops on any run that is no
longer `IsInProgress`, and the host's broadcast additionally checks `NetService.IsConnected`.
Guard on the *condition*, not on each new route out; there is always another route.

**Ask the condition you mean, not one that happens to correlate.** `DuelClockService` learned
this the hard way: its top bar keyed the one-clock/two-clock choice on the *phase*, and a race
timeout reaches `Complete` without ever passing through `DuelActive` — so it reported two duel
clocks for a duel nobody played. It was fixed there to ask whether the duel bank had been
granted. **The same test survived in `DuelFlag`**, in the branch that decides whether an expiry
is a draw or a loss — the identical trap, one file over, deciding a result rather than a label.
Both now ask `DuelClockService.DuelBankGranted`. When you fix a wrong predicate, grep for it.

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

macOS (tab 1 = host, tab 2 = client). **The binary is `Slay the Spire 2`, with spaces** — not
`SlayTheSpire2`, which is the bundle's name and does not exist inside `MacOS/`:
```
"$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2" --force-steam=off --fastmp=host_standard
"$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2" --force-steam=off --clientId=1001 --fastmp=join
```

**macOS: use `scripts/*.sh`** (there is no pwsh on the MacBook) — same workflow as the
PowerShell set, plus windowed side-by-side tiling, which is not optional there: a fullscreen
window gets its own Space, so you cannot see both clients at once. `./scripts/host.sh --custom`
then `./scripts/client.sh`, and `./scripts/check-log.sh --errors` afterwards. Details and the
points-vs-backing-pixels trap in `docs/MAC_SETUP.md`.

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
because two concurrent builds fight over the same output files. Flags: `-NoBuild`, `-Custom`
(the lobby that exposes the modifier list — needed to configure a match), `-Setup`,
`-Fullscreen`, `-Width <px>`, `-ClientId <n>`. Verify the run with `.\scripts\check-log.ps1`.

Both launchers **rotate the log** rather than truncating it, keeping the last five runs as
`logs/host.<timestamp>.log`. `--log-file` truncates on open, and losing the previous run cost a
real investigation on 2026-08-06 — the host's half of the run being diagnosed was already gone.

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

**Two banks, not one** (built 2026-08-06, DESIGN §9). `Race Clock` and `Duel Clock` are
separate lobby groups, because an act and one duel are not the same length of thing and a
single number could only rush the first or drag out the second. The duel bank is granted
*fresh* at the phase flip, so reaching the arena early buys you nothing in the fight — but
running the race bank out is still a loss. Either may be 0, which makes that half untimed and
hides the top-bar clock for its duration.

**Race — a global countdown.** Both clocks run continuously and never pause: reach the arena
before the bank empties. They start together and never stop, so the two values stay identical.

A chess clock was tried here first and is *wrong* for this phase: the players are in separate
combats and never wait on each other, so stopping your clock while theirs runs measures
nothing.

**Duel — a real chess clock.** Now the players do wait on each other, so ending your turn
stops your clock while your opponent's keeps running. The deck review counts as duel: the
phase flips before it opens, precisely so that reading their deck is charged to the duel bank.

The top-bar display follows the phase: a single countdown during the race (both clocks are
identical there by construction, so two numbers would say the same thing twice), and
`YOU 2:31 · OPP 1:47` once the duel starts and they actually diverge.

Host-authoritative in both phases: nothing pauses during the race, and in the duel the host
sees both players' end-turn state directly. Sync carries each clock's paused flag so a client's
prediction stops when the owner's does, instead of counting a stopped clock down and snapping
it back twice a second.

## Starting a PvP match

Configured in the lobby, before the run exists (DESIGN §5b) — not by a console command.

Host: **Multiplayer → host → Custom run**, then tick one entry from each of the three groups
in the modifier list:

- `1v1 Duel: Real-Time` **or** `1v1 Duel: Turn-Based` — picking either marks the run as PvP
- `Race Clock: 1 / 10 / 15 / 20 min` **or** `Off` — deadline to reach the arena
- `Duel Clock: 1 / 2 / 3 / 5 min` **or** `Off` — a fresh bank granted when the duel begins

Picking no clock at all is the same as `Off`: silently handing someone a timer they never
agreed to would be worse than giving them none. The 1-minute options exist to make flagging
reachable inside one test run.

All three groups are mutually exclusive (radio-button behaviour, via vanilla's
`MutuallyExclusiveModifiers`), and the joining player sees the choices in the lobby before
starting. Custom mode also exposes the seed field, which is useful for rematches on a known
seed. `--fastmp=host_custom` boots straight into a custom multiplayer host.

Custom runs are gated behind `CustomAndSeedsEpoch`; `unlock all` clears it on a dev profile.

**If the modifiers show up as raw keys like `DUEL_BLITZ.title`, the `.pck` is stale.** Names
come from `SpirePvp/localization/eng/modifiers.json`, which ships in the pack, not the DLL.
`host.ps1` re-exports the pack when anything under `SpirePvp/` is newer, but a manual
`dotnet build` alone will not. (The directory is `eng`, not `en` — this document said `en` for
a while and it is the sort of detail that sends someone looking for a missing file.)

**The pack is exported to a temp name and renamed into place, and both halves of that are
load-bearing.** `client.ps1` does not build, so the client's startup read lands seconds after
the host begins exporting — and writing the live pack directly let the client read a *half-
written* one. Measured 2026-08-06: pack written 11:07:15, client launched 11:07:18, client died
on `LocException: Failed to parse language file` with the filename itself truncated inside the
pack's directory. It looked exactly like malformed JSON in the repo, which is where the
investigation started; the JSON was fine. **A fresh clone or a `git pull` makes this likely
rather than rare**, because it refreshes the mtimes that trigger the re-export.

The temp name must end in `.pck` — Godot rejects any other extension outright and exports
*nothing*, which then silently keeps the stale pack (that mistake cost a round trip too) — and
must not be `SpirePvp.pck`, since `ModManager` loads exactly
`Path.Combine(mod.path, modId + ".pck")` and ignores everything else. Hence `SpirePvp.new.pck`.

## Console commands

The dev console opens with **backtick** (also `'`, `*`, `^`, or Shift+8). **Running any mod
unlocks the full vanilla debug command set** (`ModManager.IsRunningModded()` feeds
`shouldAllowDebugCommands`), so you already have everything below without writing tooling.

Mod commands:

| Command | Effect |
|---|---|
| `duel start` | Opens the opponent's decklist as the duel entry screen. Both players confirm, then the arena loads. |
| `duel now` | Skips the entry screen, straight into the arena. Debug shortcut. |
| ~~`duel clock <minutes>`~~ | **Removed.** The clocks are part of the match agreement, picked in the lobby as `Race Clock` and `Duel Clock`. The race bank runs from run creation and the duel gets a fresh one when it begins. A mid-run command could only hand someone a bank they never agreed to or reset one already spent — either silently invalidates the match. Pick the 1-minute options to test flagging. |
| `duel on` / `duel off` | Converts the combat you are already in into a duel, and back. Legacy path from M1; `duel start` is the real flow. |
| `duel hud` / `duel hud off` | **Debug only.** Shows the opponent's floor, HP and deck size on your map during the race. Off by default and deliberately not a feature — see M6 item 1. Useful when diagnosing the race; not something to leave on in a real match. |
| `race on` / `race off` | **Debug shortcut only.** A real match is configured in the lobby (below); this forces race mode onto an already-running co-op run, which is useful for exercising the patches but leaves Neow and pre-existing seeds un-mirrored. |

Useful vanilla ones for testing:

| Command | Notes |
|---|---|
| `unlock all` | **Run this on a fresh dev profile before testing anything reward-related.** A profile with no runs and no epoch unlocks playing Ironclad gets *hardcoded* tutorial rewards with no RNG at all (`RewardsSet.TryGenerateTutorialRewards`), which silently masks real reward generation — it once looked exactly like working RNG mirroring. Unlocking epochs clears the `EpochUnlockCount() == 0` half of that condition. **Not networked: run it on both clients.** |
| `card <ID> [pile]` | Screaming snake case (`BODY_SLAM`). Piles: `Draw Hand Discard Exhaust Play Deck`. **`Deck` is the run-level pile** the entry screen reads. |
| `power <id> <amount> <target-index>` | Index is into `state.Creatures` — `0` is you, `1` is the opponent. Works fine despite the empty enemy side. |
| `damage <amount> <index>` | **Always pass the index.** Bare `damage 10` targets `Enemies`, which is empty in a duel, and silently does nothing. |
| `kill [index\|all]` | **Does not work in a duel, by design.** It indexes `CombatState.Enemies`, which is empty in a duel, so bare `kill` throws `ArgumentOutOfRangeException` on `Enemies[0]` and an index is rejected as out of range. Same root as `damage` below — use `damage <amount> <index>` to finish someone off. Not worth patching: it is a dev command, and the empty enemy side is the design (DESIGN §3.1). |
| `energy`, `draw`, `block`, `heal`, `potion`, `relic` | As labelled. |

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
| `DuelFlag` | Losing on time, and the receive side of every match result. Host-authoritative. |
| `DuelResult` | Ends the match on a victory/defeat/draw screen. |
| `DuelEndReason` | The `reason` codes on `DuelResultMessage`. **A wire format** — the host writes one and every client switches on it. |
| `DuelResign` | Resigning, and offering/answering a draw. |
| `DuelDrawPrompt` | The draw popups, built on vanilla's `NGenericPopup`. |

**`DuelEndReason` exists because the codes had already drifted.** `DuelResultMessage.reason`
was documented as "2 = concede" while `DuelFlag` used 2 for a race-clock expiry. Nothing broke
only because resigning did not exist yet and nothing ever sent a concede — and adding resign is
precisely the change that would have made them collide. Numbers that two files agree on by
coincidence belong in one place.

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

**The next milestone is M7's dedicated Duel host menu** (decided 2026-08-06). Today a match is
configured by knowing to pick a *Custom* run and tick three modifiers, which is both buried and
a poor fit — the modifier list is a flat set of tickboxes for something that is really two or
three coupled choices. The wanted shape is a third entry beside **host normal** and **host
custom**: **host duel**, with

- clean controls for the race and duel clocks and for the ruleset, rather than radio-button
  modifiers, and
- **presets on chess conventions.** `10 minute race + 2 minute duel` is the agreed starting
  point for a "blitz" preset.

The mechanism does not change — it still sets the same modifiers, which is what makes this
presentation work rather than a rewrite (DESIGN §5b). Art is wanted here and Lucas intends to
draw it; see "Art still wanted" below.

**Then: finish the duel result screen.** The meaningless run-score lines are gone; what should
replace them is the match's own story. See the stats note under Open Issues.

*Everything the previous handoff listed as unverified has now been playtested — the four fixes
below, plus resignation from both sides and all three draw paths.*

| Fix | Verified 2026-08-06 |
|---|---|
| `duel over` NRE — `DuelEndCombatPatch` skipped an `async Task` without `__result` | **Zero** NullReferenceExceptions on an HP win, both logs |
| Race clock expiry is a **draw**, not a coin-flip loss | `race clock expired for both players — draw` → `duel over — DRAW` on both |
| Result screen after a race timeout showed `YOU 0:00 · OPP 0:00` | Correct: no `duel begins: fresh bank` line, so the HUD took the single-race-clock branch |
| Abandoning left the host broadcasting `ClockSyncMessage` for 21s | **0** `not connected` / `no message handlers`, down from 46 |

**~~One gap left from the clock split: an untimed duel.~~ CLOSED, playtested 2026-08-06.**
`Race Clock: 10` + `Duel Clock: Off` behaves correctly end to end: the race counts down from
10:00, and at the deck review the top bar swaps to the vanilla run timer counting up — the same
presentation the untimed *race* already had, so both untimed halves look alike. Nobody can lose
on time in the duel (`flag fell`: 0), the duel plays out to an HP finish, and both clients log
`duel begins: fresh bank of 0 min each (untimed)`.

Worth knowing why it cannot flag, since granting a zero bank sets `HasFlagged` true
immediately: two independent guards stop it. `DuelClock.Tick` returns early when the clock is
not running (`Refill` leaves it paused), and `DuelClockService.Tick` bails on
`CurrentBankMs <= 0` before reaching it.

Then M6 is feature-complete except for the three items below. Content and polish, none of it
risky:

0. ~~**Split the clock into a race bank and a duel bank** (DESIGN §9).~~ **Done, playtested
   2026-08-06** on a 1-min/1-min match: fresh duel bank granted at the phase flip on both
   clients, host-authoritative flag, correct win/loss, zero errors in either log. Three lobby
   groups now (turn model · `Race Clock` · `Duel Clock`). Either bank may be 0 independently,
   so half a match can be untimed and the top bar shows nothing at all during that half; an
   untimed race is confirmed, an untimed duel is not (see above).

   Found while building it, and fixed in the same change: **`DuelFlag.Arm()` ran before
   `DuelClockService.Start()` in `DuelMatch.OnRunLaunched`**, so it subscribed to two null
   clocks, set `_armed` anyway, and nobody has been able to lose on time in a
   modifier-configured match since the clock became run-scoped (`fb2b657`). M3's flag was
   playtested before that commit, which is why it was believed to work. Same shape as the
   arm-too-late trap the message handlers keep hitting — the ordering is now commented at both
   ends and `Arm` logs an error if it is ever called first again.

1. ~~**`RaceProgressHud`**~~ — **built, then deliberately cut to a debug tool** (`duel hud on`,
   off by default). A permanent readout of the opponent's HP and deck is clutter, and it is a
   competitive change nobody asked for: knowing their exact HP at every moment turns a race run
   on your own judgement into one run against a status bar. **The tracking survives and is the
   useful half** — `RaceProgress` retains their position, HP and deck size for the result screen
   and post-match analysis. DESIGN §6 asked for a live HUD; play said the display belongs after
   the match.
2. **`DuelResultScreen`** (DESIGN §6) — half done. The vanilla run-score lines (floors climbed,
   gold, elites, bosses, ascension) are suppressed, because after a duel they are meaningless at
   best and misleading at worst: "+42 for floors climbed" invites the loser to think they were
   ahead. What should stand in their place — the winner, and the match's own numbers — needs
   damage tracked through the duel, which nothing does yet. **Rematch lives here** and is
   deferred; see below.
3. **M7 entry point** — now the next milestone and scoped above.

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
### Art still wanted

The `.pck` currently holds the mod image, the duel node texture and its outline, and two loc
tables. Everything else in the mod is a borrowed vanilla node. In rough order of how much each
would improve the thing:

| Piece | Why, and what exists now |
|---|---|
| **Duel host menu** (M7) | The next milestone. Wants a menu entry and whatever framing the preset/clock controls sit in. Nothing exists. |
| **Result screen** | The banner reads VICTORY / DEFEATED / DRAW in vanilla's frame with the score lines cut, so there is now visible empty space where a duel's own summary belongs. |
| **Deck review background** | Currently the *boss* background, which is wrong and was flagged as wrong on sight. Anything plain — black, or the campfire — beats it; until then the fallback is whatever `NDeckCardSelectScreen` uses behind its grid. |
| **Duel map node** | Exists (`SpirePvp/map/duel_node.png` + `_outline`). Now doubles as the top-bar boss icon via `DuelRoomIconPatch`, so it is being drawn at two sizes and may want a small variant. |
| **Entry-screen confirm feedback** | Still a colour tint standing in for the intended green check plus opponent portrait (DESIGN §6). |
| **Flame effect for the deck-review transition** | Wanted, not built. `NRestSiteFireVfx` is a scene child with no static `Create` so it cannot be reused standalone; `NRestSmokeVfx.Create()` and `NDesaturateTransitionVfx.Create()` are standalone and parameterless. A real flame is scene work. |

**Loc tables are assets too, and their filenames are load-bearing.** `LocManager` merges a mod's
tables only into tables vanilla already has, *by filename* — so a new table called
`spirepvp.json` would never be read at all. Modifier names ride in `modifiers.json`; the
resign/draw strings ride in `gameplay_ui.json`. Anything new must pick an existing vanilla table
to live in.

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

**~~BLOCKING — Neow offers no blessings at all.~~ FIXED, playtested 2026-08-06.** Both clients
now log `Neow: hiding 3 duel modifier(s) so vanilla rolls its blessings` **twice** — once per
player, which is the per-player pass this was failing on — and the blessings are back.

What changed: the prefix's own guard asked `DuelMatch.IsPvpRun`, which since `MaskedModifiers`
was added answers from `MaskedModifiers ?? runState.Modifiers` — the very mask this patch
installs. That is circular, and it has a failure mode that matches the symptom exactly: with a
mask already in place, the list the patch blanks and parks is `Array.Empty`, so from then on
every `IsPvpRun` answers "not a PvP run" and the *next* player's Neow falls into vanilla's
modifier branch and returns nothing. The guard now reads `DuelMatch.IsPvpRunUnmasked`, and the
patch refuses to mask over an existing mask. Every bail-out logs which one it was, because an
empty option list is indistinguishable in game from Neow being skipped — and that logging is
worth keeping: it is what would name the cause next time instead of leaving four silent
opt-outs to be reasoned about.

The four things it was written against, still worth knowing if Neow ever goes quiet again:

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

**~~The `duel over` NullReferenceException.~~ ROOT-CAUSED AND FIXED 2026-08-06** (unplaytested
at time of writing). It was **`DuelEndCombatPatch.Prefix` returning `false` without assigning
`__result`** — the async-skip rule, in the one patch that had not applied it.
`EndCombatInternal` is `async Task`, so skipping it left the caller holding `null` and awaiting
it.

Two things made this take three sessions, both worth remembering:

- **The stack frame lies about where the bug is.** `await null` throws in the *caller*, so the
  trace read `CombatManager.CheckWinCondition` with no `EndCombatInternal` frame beneath it —
  which looked exactly like inlining having eaten the frames, and sent two investigations into
  reading `ProcessPendingLoss` and `IsCombatEnding` line by line. Nothing was ever wrong with
  either. **A missing frame under an `await` is a signal to check the patch, not the callee.**
- **It only reproduced on HP wins.** A duel decided on the clock ends through `DuelFlag` →
  `DuelResult.DeclareWinner` without `IsCombatEnding` ever going true, so `EndCombatInternal`
  is never called and there is nothing to skip. The flag-win playtest came back with zero
  errors on both clients and briefly looked like the bug had gone away on its own.

Harmless throughout — everything in the prefix had already run, so the result screen was up
and the winner correct — but it threw once per duel on both clients, which meant every log
read began by discounting a real exception.

**Should the opponent's pet be attackable?** Open *design* question, deliberately not decided.
`DuelLayout` now draws the opponent's pets on the enemy side (`BelongsToOpponent` resolves
`Player ?? PetOwner`), but they are still mechanically on `CombatSide.Player`, so they are
scenery: you cannot hit the opponent's Osty and it cannot be killed. That is a real matchup
question, not a rendering one — it belongs in `DuelOpponentsPatch` / `GetOpponentsOf` and wants a
decision before it is coded.

**Deck review background is the boss background.** Should be plain black or something simple
like the campfire. Lucas is drawing something; until then the fix is whatever `NDeckCardSelectScreen`
uses behind the grid.

**The result screen is still vanilla's game-over screen**, with the banner rewritten
(`DuelResultBannerPatch`) and the run-score lines suppressed (`DuelResultScoreLinesPatch`).
What is missing is what should stand in their place. `RaceProgress` already retains the
opponent's final HP and deck size; **per-round damage needs a tracker that does not exist** —
nothing accumulates damage across the duel. That tracker is the prerequisite for DESIGN §6's
stats, and it is also what would make post-match analysis possible, which is the stated reason
the race HUD's data was kept.

**Rematch: deliberately deferred, 2026-08-06, and it is milestone-sized rather than a button.**
The run is already over by the time the result screen is up: `RunManager.CleanUp` has fired,
`DuelRunCleanupPatch` has released every handler, the clocks are reset. Starting a fresh match
means getting both clients into a `StartRunLobby` carrying the same modifiers, seed and player
set and launching, *without* passing through the main menu — which is where the connection
drops. So it needs a rematch handshake, a route back into a lobby that skips the menu, and
teardown ordering that keeps the transport alive across a run boundary the mod has never
crossed. Every one of those is the shape of bug that has cost this project multi-session hunts.

One design question is open and should be settled before building it: **does a rematch replay
the same seed or roll a new one?** Same-seed is the truer rematch — both players have seen the
map, so the second run is pure decision-making — and it is *strictly easier*, because the seed
is already in the run being ended.

**A flame effect for the deck-review transition** (wanted, not built). The rest site's fire is
`NRestSiteFireVfx`, a scene child of `NRestSiteRoom` with no static `Create`, so it cannot be
reused standalone. The pieces of the rest animation that *are* standalone and parameterless are
`NRestSmokeVfx.Create()` and `NDesaturateTransitionVfx.Create()`. A real flame is scene work,
best batched with the M6 asset pass.

**~~Run-history icon load failure.~~ FIXED 2026-08-06, and it was not cosmetic.** Recorded here
as "logs an error once per run"; measured, it was **19 failures per client per session**, and
the mechanism is why: `AssetCache` logs a cache *miss*, attempts the load, fails, and **never
caches the failure** — so every repaint of the top-bar boss icon re-attempted a resource lookup
that threw, synchronously, on the UI path. `NTopBarBossIcon.RefreshBossIcon` does it twice per
call and again for the second boss slot, which is us. This is the best available explanation for
in-game hitching that was initially put down to failing hardware.

`DuelRoomIconPatch` redirects to the duel node art already in the `.pck`. Note it patches the
public `GetRoomIconPath` / `GetRoomIconOutlinePath` rather than the shared private
`GetRoomIconSuffix` the old note suggested: a suffix is concatenated into vanilla's
`ui/run_history/` directory, so changing it could only ever name a different missing file in a
directory the mod still cannot write to.

**The lesson generalises:** "it only logs an error" is a claim worth measuring. Count the lines
before believing it, and check whether the failure is cached.
