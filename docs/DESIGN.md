# SpirePvp — Technical Design (Real-Time Blitz)

Audience: Lucas + Claude (Opus) implementation agents. Each milestone below is scoped to be
handed off as an independent task. File references like `Core/Combat/CombatManager.cs` point
into the decompiled game source at `D:\modding\sts2\decompiled\MegaCrit\sts2\` (macOS:
`~/Code/sts2-decompiled/MegaCrit/sts2/`) — read the referenced file before implementing
against it. Game version: v0.110.1 (re-verify facts after game patches; re-run ilspycmd per
README). Line numbers here are from the v0.110.1 macOS `data_sts2_macos_arm64/sts2.dll` and
match the Windows figures, so the two decompiles are interchangeable for navigation.

## 1. The mode

Two players. Same seed → same map, same path options, same Neow bonus, same reward streams.
Each plays their own run through Act 1 (the **race**), then both enter a 1v1 combat (the
**duel**).

The duel is **real-time blitz**: both players act simultaneously within a round. You're
racing to get your Strike out while your opening hand is still animating in. Actions resolve
in the order the host receives them — if your attack lands before their block resolves, it
hits unblocked HP. When both players have ended turn (or a clock forces it), the round rolls
over: statuses tick, hands redraw, energy refills, next round begins.

Chess clock: each player has a time bank. Your clock runs while the round is live and you
haven't ended turn; it pauses when you click end turn. Flag (clock = 0) behavior is a design
knob (§9): default = your turn auto-ends instantly every round (you draw and pass); optional
sudden-death = flag means loss.

Information rules:
- **Opponent's decklist**: revealed at duel start (read-only panel). No new netcode needed —
  the engine already syncs each player's full serialized state (including piles) to every
  client before combat (`Core/Multiplayer/CombatStateSynchronizer.cs`).
- **Opponent's hover/selection**: hidden. Co-op currently broadcasts hovered card/relic/potion
  (`Core/Multiplayer/Game/PeerInput/HoveredModelTracker.cs` + `PeerInputSynchronizer`); we
  suppress display (or broadcast) in duel mode.
- **Opponent's plays**: fully visible with animations — free, because every action executes
  on every client (deterministic sim).
- Hand contents, draw order, energy: hidden (opponent's hand is not rendered for remote
  players anyway; verify nothing leaks via UI — I6).

## 2. Verified engine facts (the foundation)

| Fact | Where |
|---|---|
| Official mod loader; `[ModInitializer]` entry; Harmony ships with the game | `Core/Modding/`, `data_sts2_windows_x86_64/0Harmony.dll` |
| Host-authoritative deterministic sim. Clients request actions; host orders; everyone executes the same stream | `Core/GameActions/Multiplayer/ActionQueueSynchronizer.cs` |
| Before every combat, all players' full serialized state + host RNG broadcast to all peers; `IsDisabled` flag exists | `Core/Multiplayer/CombatStateSynchronizer.cs` |
| Desync detection via checksums | `Core/Multiplayer/Game/ChecksumTracker.cs`, `StateDivergenceException.cs` |
| Player phase is already simultaneous/real-time in co-op: turn ends only when **all** players are in `PlayersReadyToEndTurn` | `Core/Combat/CombatTurnState.cs`, `CombatManager.SetReadyToEndTurn` (~line 907) |
| Combat is side-based: `_allies` / `_enemies` are both `List<Creature>`; `Player` has-a `Creature` (`IsPlayer`, `.Player` back-ref); HP/block/powers live on `Creature` | `Core/Combat/CombatState.cs`, `Core/Entities/Creatures/Creature.cs`, `Core/Entities/Players/Player.cs` |
| Targeting is an enum incl. co-op types (`AnyPlayer`, `AnyAlly`, `AllAllies`) alongside `AnyEnemy`/`AllEnemies`/`RandomEnemy` | `Core/Entities/Cards/TargetType.cs` |
| Custom net messages are first-class: `MessageTypes.Initialize` scans mod assemblies for `INetMessage` subtypes | `Core/Multiplayer/Serialization/MessageTypes.cs` |
| Two transports: Steam and ENet (LAN/direct). LAN mod on Nexus proves ENet is usable for 2-instance dev | `Core/Multiplayer/Connection/`, `Transport/ENet/` |
| Per-player RNG/odds seeded via RunState; host RNG set syncs pre-combat | `Core/Entities/Players/Player.cs` (`PlayerRng`, `PlayerOdds`) |
| End-turn flows through an action (`EndPlayerTurnAction`) → `SetReadyToEndTurn(player, canBackOut)`; there is even `UndoEndPlayerTurnAction` | `Core/GameActions/` |

**Why blitz "just works":** the engine never pauses for turn alternation between players —
co-op players already race their plays into a shared host-ordered action queue during one
common player phase. The duel reuses that machinery unchanged; we only change *who the
targets are* and *when combat ends*.

## 3. Architecture: two phases, two coupling models

```
 Lobby (2 players, co-op lobby, same seed)
   │
   ├─ RACE PHASE — loosely coupled
   │    Each client simulates its own run (like singleplayer),
   │    shared-room synchronizers disabled. Lightweight
   │    RaceProgressMessage broadcasts for the progress HUD.
   │    Same seed ⇒ same map/Neow/encounters/rewards for both.
   │
   ├─ CONVERGE — both players beat Act 1 boss & pick rewards
   │    DuelReadyMessage from each; host schedules duel.
   │
   └─ DUEL PHASE — fully coupled (vanilla co-op machinery)
        CombatStateSynchronizer re-syncs both players' full
        state + RNG (this is exactly what it was built for),
        then a shared CombatRoom runs with duel patches active.
```

The key insight making the race phase cheap: **the engine re-establishes full determinism
from scratch at every combat entry**. We don't need lockstep during the race; divergent
local sims are fine because the duel entry does an authoritative state sync anyway. During
the race we must disable the shared-state machinery (`CombatStateSynchronizer.IsDisabled`,
checksum tracking, map vote / shared room entry — see I3) so the two clients don't fight
over state they no longer share.

### 3.1 Duel combat model (the core trick)

Both duelists enter ONE shared `CombatRoom` **on the same side** (`CombatSide.Player`),
exactly like co-op. The enemy side is empty — no monsters, no monster AI, no intents, no
enemy turn to worry about. Three patch groups make it a duel:

1. **Retargeting** (`DuelTargetingPatch`): when duel mode is active, cards/potions with
   `AnyEnemy` / `AllEnemies` / `RandomEnemy` resolve their candidate set to
   `{opponent's player creature}` instead of the enemy side. Damage, block, thorns, Weak,
   Vulnerable, powers, etc. all operate on `Creature` and don't care about sides — the
   entire card mechanics layer comes along for free. (Ally-targeting co-op cards keep
   working as-is; they just target you or the opponent per their own rules — design knob
   whether co-op multiplayer cards are even in the pool.)
2. **Win condition** (`DuelWinConditionPatch`): patch `CombatManager.IsCombatEnding` /
   loss handling: combat ends when either player's creature dies; the survivor wins.
   Suppress vanilla "no hittable enemies ⇒ victory" (always true with an empty enemy side —
   this is the first thing M1 must defeat) and vanilla "all players dead ⇒ run loss".
3. **Round rollover**: with no enemies, the enemy phase should be a fast no-op (verify —
   I2). Statuses tick, discard/redraw, energy refill via the vanilla turn loop.

Resolution order *within* a round is host-arrival order via `ActionQueueSynchronizer` —
that's the "first strike" mechanic, no code needed. Note the latency asymmetry: the host's
own requests don't cross the network, so the host has an inherent edge (~½ RTT). Acceptable
for friendly play; mitigation ideas in §9 (design knobs).

### 3.1b Duel turn models — build for two, ship whichever plays better

**Decision 2026-08-05: the duel's turn model is a first-class, swappable option, not a
constant.** Real-time blitz is what M1–M4 built and what we playtest first, but it is an
open question whether reflex-driven play or deliberate simultaneous planning makes the better
game. That question is answerable only by playing both, so the code should not assume either.

| | **A. Real-time blitz** (built) | **B. Simultaneous turn-based** ("competitive Pokémon") |
|---|---|---|
| Within a round | Both act freely and concurrently | Both plan privately, then lock in |
| What decides outcomes | Order the host receives actions — reflex and speed | A deterministic resolution rule — prediction and sequencing |
| Skill tested | Execution speed under pressure | Reading the opponent, ordering your own plays |
| Clock role | Core mechanic: the clock *is* the pressure | Safety net: stops stalling, does not create tension |
| Engine support | Free — this is vanilla co-op's player phase | Needs a lock-in gate plus a resolution order |

**Why both are cheap to support:** the difference is *when* actions execute, never *what*
they do. Retargeting, the win condition, powers and DoT are identical in both. Model B is
therefore not a rewrite — it is a gate in front of the same action stream.

**The seam.** Model A lets `RequestEnqueue` flow straight through, so host-arrival order
decides everything. Model B buffers each player's actions locally until both have locked in,
then submits them in a deterministic order. Vanilla already does exactly this kind of
deferral — `ActionQueueSynchronizer._requestedActionsWaitingForPlayerTurn` holds
play-phase-only actions queued during the enemy turn and flushes them at player-turn start —
so the mechanism is proven; Model B just changes the release condition. Keep the choice
behind a single policy object (`IDuelTurnModel` with `ShouldDeferAction` / `OnLockIn` /
`ResolutionOrder`) rather than scattering `if (blitz)` through the patches.

**Model B's real design question is resolution order**, and it is a genuine game-design
choice, not a technical one:
- *Cost order* — cheaper cards resolve first. Readable, makes energy a tempo currency.
- *Alternating priority* — like a card game's initiative, swapping each round. Symmetric
  and easy to reason about.
- *Speed stat* — a new per-character attribute. Most Pokémon-like, most new content.
- *Submission order* — resolves in the order each player queued their own plays, interleaved.
  Preserves some of blitz's "sequence your combo correctly" texture without the reflex.

**Interaction with the chess clock.** Under A the clock is load-bearing and per-player running
time is the whole tension. Under B the natural analogue is a per-round planning timer (with
the bank as a reserve, like real chess increments). `DuelClock` is already pure logic with
start/pause/tick, so it serves both; only the *policy* for when it runs changes.

**Both players must run the same model.** It rides on `DuelStartMessage` alongside `clockMs`
and `suddenDeath`, so the host's choice is authoritative for the duel — same reasoning as
every other duel parameter.

### 3.2 Chess clock

- `DuelClock` is pure logic (per-player time bank, running/paused, flag event) — unit-testable
  without the game.
- Authoritative timekeeping on the host: host ticks both clocks, broadcasts `ClockSyncMessage`
  (unreliable, ~2/sec) for HUD smoothing; clients render predicted time locally between syncs.
- Clock starts when the round's player phase opens (hook: wherever
  `CombatTurnState.EndTurnSignalSource` is recreated at player-turn start — find the turn
  loop call site in `CombatManager`, I5). Player's clock pauses on their `SetReadyToEndTurn`
  (hook `CombatManager.PlayerEndedTurn` event — public, no patch needed), resumes if they
  back out (`UndoEndPlayerTurnAction`).
- Flag: host sends `ForcedEndTurnMessage(playerId)`; every client (and host) executes the
  forced end-turn as a queued action so the sim stays deterministic. Cleanest path: reuse
  the existing `EndPlayerTurnAction` request flow on the flagged player's behalf (I5:
  confirm an action can be enqueued server-side *for* another player; fallback: the flagged
  player's own client auto-submits, host enforces with a timeout).

### 3.3 Custom net messages (all `record struct : INetMessage` in mod assembly — auto-registered)

| Message | Direction | Mode | Purpose |
|---|---|---|---|
| `RaceProgressMessage` | both → all | Reliable | node reached, HP, deck size, boss-killed flag → progress HUD |
| `DuelReadyMessage` | both → host | Reliable | player finished Act 1 + rewards |
| `DuelStartMessage` | host → all | Reliable | enter duel room (with agreed parameters) |
| `ClockSyncMessage` | host → all | Unreliable | authoritative clock values |
| `ForcedEndTurnMessage` | host → all | Reliable | flag fell — force end turn |
| `DuelResultMessage` | host → all | Reliable | winner, stats, rematch offer |

CAUTION: the vanilla bus tolerates unknown message ids from mods but assumes they "do not
affect gameplay" (`NetMessageBus.TryDeserializeMessage`). Both players must run the same
mod version; gate the duel on a version handshake inside `DuelReadyMessage` (carry mod
version string).

## 4. Race phase details

- **Seed mirroring**: both players share the run seed (co-op lobby already does this — the
  map is common). Additionally mirror per-player streams (`PlayerRng` / `PlayerOdds` seeds)
  so card rewards / shop stock / events are identical for both players → mirror-match
  fairness. (I4: find where `PlayerRng` gets seeded "when added to a RunState" and patch
  both players to the same seed.)
- **Decoupling**: during the race, players traverse the map independently — vanilla co-op
  moves the party together (`MapSelectionSynchronizer`, `MapVote`) and enters rooms
  together. Patch plan (I3): with race active, short-circuit map voting to "local choice
  wins locally", set `CombatStateSynchronizer.IsDisabled = true`, disable checksum
  tracking, and make room entry/exit not wait on peers. The other player's run still
  *exists* in local state (their `Player` object sits idle) — the engine tolerates this
  because nothing references them while synchronizers are off. This is the riskiest area
  of the design; M5's first task is a spike proving two clients can occupy different rooms
  without exceptions before building the HUD.
- **v1 fallback** (de-risk): if decoupling fights back hard, ship v1.5 as "co-op through
  Act 1 together, then duel" — zero decoupling work, still a complete playable loop, and
  M1–M4 (the whole duel) are unaffected. Decide after the M5 spike.

## 5. Duel entry & flow

State machine in `DuelSession` (static, client-local, mirrored by messages):

`Inactive → RaceActive → LocalReady (sent DuelReadyMessage) → DuelPending (both ready,
host sent DuelStartMessage) → DuelActive → Complete(winner)`

Duel room entry: after both `DuelReadyMessage`s, host triggers a synthetic combat room
entry on all clients (I1: find the API that enters a `CombatRoom` with a chosen encounter —
look at `Core/Rooms/CombatRoom.cs`, `RoomSet`, and how `ActChangeSynchronizer` moves
everyone; an "empty encounter" or a 0-monster `MonsterGroup` is the goal). Standard
`CombatStateSynchronizer.StartSync/WaitForSync` runs on entry and reconciles the two
divergent race states authoritatively. Duel patches key off `DuelSession.IsDuelActive`.

For M1 the entry is just a dev-console command / hotkey both players press.

## 6. UI components (Godot side, via BaseLib node factories + our .pck)

| Component | Notes |
|---|---|
| `OpponentDeckPanel` | **Design settled 2026-08-05 — it is the duel's entry flow, not a panel.** Clicking the duel map node opens a full deck screen showing the *opponent's* deck (the campfire-style view), whose confirm button reads **START DUEL** instead of the usual label. Both players enter the arena once both have viewed and confirmed. This folds the information rule and the ready-handshake into one screen: you cannot start without having been shown the decklist, and the confirm doubles as `DuelReadyMessage`. Cheaper than it sounds — `NDeckViewScreen.ShowScreen(Player)` is static and takes any player, so rendering the opponent's deck is a one-liner; the custom work is the button label and the both-confirmed gate. Until the map node exists (M6), `duel start` opens this screen rather than entering the arena directly. |
| `ClockHud` | **Done, and deliberately not a component.** Both clocks share the vanilla run-timer label in the top bar (`NRunTimer`, postfixed), rendered as `YOU 2:31 · OPP 1:47` in a stable `m:ss`. A separate two-element HUD was considered and dropped — one label reads fine and costs no scene work. Local prediction + host `ClockSyncMessage` at 2/sec. "Turns red < 30s" still unimplemented. |
| `RaceProgressHud` | Opponent's map position, HP, deck count. Driven by `RaceProgressMessage`. |
| `DuelResultScreen` | Winner, per-round damage stats, rematch button. |
| Entry-screen confirm feedback | *Wanted, not built.* A large green check on screen once **you** have confirmed, and a small portrait of the opponent on the confirm button once **they** have — so you can see their state without asking. Model it on the per-player icons the group-choice random events already show. |

BaseLib (Workshop id 3737335127, NuGet `Alchyr.Sts2.BaseLib`) provides node factories and
config UI — study how Minty Spire 2 builds UI (`scratchpad clone or github: erasels/Minty-Spire-2`)
before rolling custom scenes.

## 7. Milestones (each is a handoff unit)

Every milestone ends with: builds green, loads in-game without errors in
`%APPDATA%\SlayTheSpire2\logs\godot.log`, and its acceptance test done in 2-client ENet
setup (see §8).

- **M0 — done.** Toolchain, skeleton mod loads, decompiled source.
- **M1 — done** (2026-08-05, playtested on two local macOS clients over ENet). Retargeting,
  win condition, duel arena entry via `duel start`, opponent drawn and facing correctly on the
  enemy side, HP bar always visible.
- **M2 — done** (2026-08-05). AOE/random/auto-play retargeting, round loop verified over
  multiple rounds, poison timing corrected, duel ends on a victory/defeat screen.

### The M1/M2 lesson, which predicts most future bugs

**The duel never breaks card logic — it breaks every place the engine encodes "enemy" as a
*side* rather than a *relationship*.** Damage, block, powers and DoT all operate on `Creature`
and worked untouched, exactly as §3.1 bet. Every single bug was a side test:

| Symptom | Cause |
|---|---|
| Strike would not drop on the opponent | `CardModel.IsValidTarget`: `target.Side != Owner.Creature.Side` |
| ...and still would not, after fixing that | `NTargetManager.AllowedToTargetCreature` requires `Side == Enemy` — targeting is validated twice, independently |
| Fire Potion would not throw | `PotionModel.IsValidTarget`, a separate method with the same check |
| AOE cards "played" and hit nothing | `CombatState.GetOpponentsOf` returns the opposite side |
| Hellraiser's auto-Strikes whiffed | `CardCmd.AutoPlay` picks from `HittableEnemies` directly |
| Opponent HP bar hover-only | `_isRemotePlayerOrPet` — co-op teammate presentation |
| Poison ticked a round late | `AfterSideTurnStart` fires per side; a poisoned *player* resolves at player-turn start, not enemy-turn start |
| Duel froze after a kill | `EndCombatInternal` assumes a real map room; NRE killed the turn loop |

When something behaves oddly in a duel, look for a side comparison before suspecting the
mechanic. Note `HittableEnemies` is **not** patchable — it has no acting-player context.
`GetOpponentsOf` is the correct chokepoint because it is handed the attacker.
- **M1 — Duel spike (hardest risk first).** Two clients in a co-op run; hotkey enters a
  shared combat with an empty enemy side; retargeting patch makes Strike hit the opponent;
  win-condition patch prevents instant "victory" and ends combat on a death.
  *Accept: P1 kills P2 with Strikes; both clients agree on the result; no desync.*
- **M2 — Round loop.** Multi-round duel: end-turn by both → statuses tick, redraw, energy
  refill, next round. Powers (Weak/Vulnerable/Strength) verified across rounds.
  *Accept: a 5+ round duel with status cards completes cleanly.*
- **M3 — Chess clock.** `DuelClock` + host sync + forced end turn + `ClockHud`.
  *Accept: letting your clock run out force-ends your turns; clocks agree on both screens
  within 200ms.*
- **M4 — Information rules.** Hover suppression in duel; `OpponentDeckPanel` at duel start.
  *Accept: hovering cards produces no visible signal on the opponent's screen; deck panel
  matches the opponent's actual decklist.*
- **M5 — Race decoupling spike, then race phase.** Spike: two clients in different rooms
  simultaneously without errors. Then: independent map traversal, per-player mirrored RNG,
  `RaceProgressMessage` + HUD, `DuelReadyMessage` handshake. Fallback to v1.5 (§4) if the
  spike says no.
  *Accept: both players independently clear a 3-room slice on identical seeds and see each
  other's progress.*
  **Researched, not started — read I3 and I4 below before writing any code.** Short version:
  the state synchronizers are trivially disablable (public bools), the RNG mirroring is a
  one-line patch, and the whole risk sits in map traversal, where position is global and the
  move action travels every client. Spike that one thing first; if it resists, v1.5 is a
  complete playable product and M1–M4 are unaffected.
- **M6 — Full loop.** Lobby → Neow → race Act 1 → boss + rare card → duel → result screen →
  rematch. Duel entry automatic once both ready.

  ### M6's first task: the duel as a real map node (researched 2026-08-05)

  Motivation is not cosmetics. `DuelEndCombatPatch` currently *replaces* `EndCombatInternal`
  wholesale — the most brittle patch in the mod, and the one most likely to break on a game
  update — purely because the synthetic arena has no map point behind it, so vanilla's
  progression path NREs on `CurrentMapPointHistoryEntry.Rooms.Last()`. A real node removes
  that whole class of "no map point" hack.

  **Feasible, and the chokepoints are known:**
  - `RunManager.CreateRoom(RoomType, MapPointType, AbstractModel?)` is a single switch and the
    natural interception point: return a duel `CombatRoom` (carrying `DuelEncounter`) instead
    of rolling a normal encounter.
  - `RollRoomTypeFor` maps point type → room type, also a single switch.

  **The constraint that shapes the design: `MapPointType` and `RoomType` are plain enums**,
  not extensible models like encounters were. A mod cannot add a value, so the duel node must
  masquerade as an existing type. `Boss` is the natural host — it is where the duel sits in
  the run anyway, `NBossMapPoint` already supplies large node art, and `TravelToMapCoord`
  even scales its selection VFX 2x. That also means **a custom icon is optional for v1**;
  piggybacking boss art gets a working node with no `.pck` asset work at all.

  **What it does *not* remove:** the duel still must suppress combat rewards, skip
  `UpdateProgressAfterCombatWon` and the "defeated all enemies" achievement, and end the run
  on a result screen rather than continuing. So the patch shrinks from "replace the method"
  to "suppress progression and route to the result screen" — a real improvement in
  robustness, but not a deletion.

  **Sequenced after M5 deliberately.** The node's design depends on which world M5 lands in:
  with a decoupled race, the duel node is a convergence point reached from two *different*
  map positions (who triggers entry? what if one player arrives first? does the node exist at
  the same coord on both clients?); with the v1.5 fallback the party is already together and
  the node is a plain shared room. Building it before that is known risks building the wrong
  one.
- **M7 — Polish/knobs.** Config UI (BaseLib) for clock settings & flag rule; balance knobs
  (§9); Workshop packaging; spectator/obs support (stretch).
- **M8 — Simultaneous turn-based duel** (§3.1b model B). Introduce `IDuelTurnModel`, move the
  existing behaviour behind a `BlitzTurnModel` unchanged, then add the lock-in model beside
  it. Carry the choice on `DuelStartMessage`. Best done after a real blitz duel has been
  played end to end, so the comparison is against something known rather than imagined.
  *Accept: the same duel is playable under both models, chosen at duel start.*

## 8. Dev workflow

- **IDE**: Rider, open `D:\modding\sts2\SpirePvp\SpirePvp.csproj`. Attach the decompiled
  tree (`D:\modding\sts2\decompiled`) as a second project/folder for search, or just
  ctrl-click into `sts2.dll` — Rider decompiles inline.
- **Build**: `dotnet build` → auto-copies dll+json+pdb into the game's `mods\SpirePvp\`.
  `.pck` re-export only when Godot assets change (see README).
- **Two clients on one PC**: the game supports ENet direct connect
  (`Core/Multiplayer/Connection/ENetClientConnectionInitializer.cs`); the Nexus
  "sts2-lan-multiplayer" mod and the couch co-op launcher prove two local instances + ENet
  works — study them (I7) and add a `--spirepvp-connect=<host:port>` style dev path so the
  second instance skips Steam lobbies. Watch RAM: two instances + Rider is heavy; close the
  Godot editor (only needed headless for exports).
- **Logs**: `%APPDATA%\SlayTheSpire2\logs\godot.log`. `Log.Warn`/`Log.Info` from mod code
  land there. Network debug: `Core/Multiplayer/MultiplayerDebugUtil.cs`, `LogType.GameSync`,
  `LogType.Network`.
- **Debugger**: Godot debug server — game launch option `--remote-debug tcp://127.0.0.1:6007`
  (see STS2FirstMod README); or printf-debug via logs, which is usually enough.
- **Console**: the game has a dev console (`Core/DevConsole/`) — check how to enable it;
  register duel commands there for M1.

## 9. Design knobs & open design questions

- **Flag rule** — **DECIDED 2026-08-05: sudden death.** Running out of time is an instant
  loss, as in chess. Zugzwang (auto-pass every round) is dropped.
  This removes the forced-end-turn machinery from M3 entirely: the host detects the flag and
  ends the duel, routing the flagged player through the same loss path a death takes. I5's
  host-side enqueue-for-another-player finding is therefore *not* on M3's critical path,
  though it stays true and would be needed for any per-turn timer variant.
- **Clock size / increment** — **DECIDED 2026-08-05.**
  - **Duration is configurable, and 0 means no clock at all** — nobody can ever lose on time.
    0 is the default so the mod stays inert for anyone not opting in.
  - **The bank covers the whole run, not just the duel.** The clock is therefore
    *run-scoped*, started at run start and surviving room transitions — not created when the
    duel begins. Built that way from the outset so M5's race phase needs no retrofit.
  - Race vs duel tick semantics differ: during the **race** both players act continuously and
    simultaneously, so both clocks simply run down. During the **duel** it is a true chess
    clock — yours runs while you have not ended turn, and pauses when you do.
  - Fischer increment still unimplemented (M7).
- **Host advantage**: host resolves ~½ RTT faster. Options: accept it; input-delay
  equalization (delay host's own enqueues by measured RTT/2); alternate hosting across a
  match series. Defer; measure first.
- **First-strike depth** (model A only): pure arrival order, or small windup (0.5s) per card
  so a fast block can answer a seen attack? Playtest question.
- **Which turn model ships** — genuinely open, see §3.1b. Real-time blitz is built and gets
  playtested first; simultaneous turn-based is a supported alternative to be built and tried
  rather than a fallback. Decide from play, not from argument. If both hold up, ship both as
  a lobby option — they are different games and people will want different ones.
- **Co-op-only cards** (ally-targeting) in the duel pool: probably ban at draft/reward
  level during the race; harmless mechanically.
- **Potions, powers that reference "monsters"**: audit pass in M2 for mechanics that
  hard-reference `MonsterModel` (e.g. on-kill effects, `ContainsMonster<T>`); most Creature-
  level mechanics are fine.
- **HP carryover**: duel starts at race-end HP (racing risky = arrive hurt) or full heal?
  Start with full heal for clean testing; knob later.

## 10. Open investigations (I#) — do these inside their milestone

- **I1 (M1)** — *partly resolved 2026-08-05 against v0.110.1 (macOS build).*
  - **Win condition: SOLVED, and it needs no patch on the win check.** `IsEnding` delegates
    to private `IsCombatEnding(CombatTurnState)` (`Core/Combat/CombatManager.cs:395`), whose
    last step before concluding "no primary enemies alive ⇒ over" is
    `Hook.ShouldStopCombatFromEnding(turnState.State)`. That hook (`Core/Hooks/Hook.cs:2451`)
    polls `AbstractModel.ShouldStopCombatFromEnding()` across
    `CombatState.IterateHookListeners()` (powers, relics, monsters) and any single `true`
    vetoes the ending. `DuelWinConditionPatch` postfixes the hook and votes "keep going"
    while ≥2 player creatures are alive. Ending is then vanilla and free: a duelist dies →
    veto drops → `CheckWinCondition` closes combat. The native alternative is an invisible
    `PowerModel` per duelist, which costs a BaseLib dependency for no behavioural gain.
  - **Single-target retargeting: SOLVED.** `CardModel.IsValidTarget` (~line 1772) reduces
    `TargetType.AnyEnemy` to `target.Side != Owner.Creature.Side`; both duelists are on
    `CombatSide.Player`, hence the rejection. `DuelTargetingPatch` postfixes it, which also
    covers `PlayCardAction`'s authoritative re-check (~line 85) so UI and synced action
    agree. `PotionModel.IsValidTarget` (~line 260) is a separate method — same treatment
    when potions matter.
  - **AOE/random retargeting: still open (defer to M2).** They read
    `CombatState.HittableEnemies`, which has no acting-player context, so the getter cannot
    know whose opponent to return. Retarget at call sites holding `Owner` instead.
  - **Still open for M1: duel room entry.** How to enter a `CombatRoom` with an
    empty/custom encounter on demand — `Core/Rooms/CombatRoom.cs`, encounter construction
    (search `MonsterGroup`/`EncounterModel` usages), and how `ActChangeSynchronizer` moves
    everyone at once.
- **I2 (M2)** — **RESOLVED, no work needed.** The enemy phase no-ops correctly with an empty
  enemy side. Playtested: round rollover, fresh hand, energy refill and power decrement all
  behave normally over multiple rounds. The empty enemy phase still *runs* each round
  (`After enemy turn start action` appears in the combat log), which turned out to matter —
  it is the correct moment for duelists' DoT to resolve (see the poison fix).
  - Related, and not obvious: a player who dies **mid-turn** stalls the round forever.
    `AllPlayersReadyToEndTurn` compares readiness count to player count, and the dead never
    signal. Vanilla auto-readies the dead only at *turn start*, which in a duel is never when
    anyone dies. `DuelDeadPlayerReadyPatch` applies that rule continuously.
- **I3 (M5)** — **SPIKE PASSED, playtested 2026-08-05 on two Windows clients, v0.110.1.**
  Two clients traversed the shared map independently, fought their own combats, and one
  finished a combat and moved to the next room while the other was still fighting. Zero errors
  in the log — including no `StateDivergence` and none of the buffered-message errors that
  the earlier attempts produced.

  **Decoupling is viable. The v1.5 fallback (§4) is not needed.**

  It took **four** blockers, not the one the research predicted, and they are worth reading as
  a set because they share a root cause: *the engine assumes the party is co-located in many
  unrelated places, and each assumption fails in a different way.* A hang, a silent freeze, and
  a crash all traced back to the same wrong premise.

  | # | Patch | Symptom | Assumption broken |
  |---|---|---|---|
  | 1 | `RaceMapTravelPatch` | party moves as one | map vote needs every player, then moves everyone |
  | 2 | `RaceSoloCombatPatch` | first turn never ends | `CombatRoom.EnterInternal` enrols every run player |
  | 3 | `RaceLocalActionPatch` + `RaceIgnoreRemoteActionsPatch` | client frozen turn 1, host fine | client actions need host arbitration, which is location-gated |
  | 4 | `RaceAbsentPlayerPanelPatch` | black screen entering combat | co-op teammate panel binds to a `PlayerCombatState` that only enrolled players have |

  Blocker 3's second half was never observed in play and was fixed pre-emptively: peer action
  traffic would have flushed out of the location buffer on reaching a visited coord and
  replayed the opponent's card plays inside your own run, minutes later and in another room.

  Expect more of this family as the race covers more of the game — rest sites, shops, events
  and rewards each have their own synchronizer. The pattern for diagnosing them is established:
  find where the code assumes every run player is present.

  *Original research, and the two blockers predicted before the playtest, follow.*

  1. **Map traversal — `RaceMapTravelPatch`.** Less dangerous than feared, because the
     "global position" framing is misleading. `RunState.CurrentMapCoord` is global *within a
     client*, but each client owns its own `RunState`; the two only agree because one
     `MoveToMapCoordAction` executes on everyone. Prefix
     `NMapScreen.OnMapPointSelectedLocally`, call `TravelToMapCoord` locally instead of
     enqueuing the vote, and the positions diverge with no shared authority to fight. Same
     call the vanilla action makes, so animation/fade/`EnterMapCoord`/visited-coords
     bookkeeping are unchanged.
  2. **Party enrolment into combat — `RaceSoloCombatPatch`. This is the one the research
     missed, and it fails as a silent hang.** `CombatRoom.EnterInternal` adds *every*
     `runState.Players` entry to whatever combat you enter. In a race your solo combat would
     therefore contain the absent opponent's creature, and the player phase ends only when
     every player in `CombatState.Players` has readied — so your first turn never ends. The
     same class of bug as M2's dead-duelist stall, arrived at from a different direction.
     The fix rides vanilla's own guard: it only enrols the party `if
     (CombatState.Players.Count == 0)`, so a prefix that adds just the local player makes
     that condition false and the loop is skipped.

  Everything else the research predicted held: `CombatStateSynchronizer.IsDisabled` and
  `ChecksumTracker.IsEnabled` are public settable bools (`RaceCoordinator` saves and restores
  them rather than assuming vanilla's values, because the duel needs both back on).

  Open questions the playtest must answer, in order:
  - Does `RunLocationTargetedMessageBuffer` stay transient as predicted, or does
    `OnLocationChanged` start logging its "still buffered" error?
  - Does anything else read the remote player's state during the race and get stale data?
  - Does the duel still start correctly *after* a divergent race — i.e. does
    `CombatStateSynchronizer` genuinely reconcile the two runs on duel entry, as §4 bets?

  *Original research follows.*

  **The verdict: harder than the design assumed, but not obviously impossible. The blocker is
  map traversal, not state sync.**

  What is *easy* (public API, no patching):
  - `CombatStateSynchronizer.IsDisabled` — a public settable bool. `RunManager.Instance
    .CombatStateSynchronizer` is public, so the race can turn pre-combat state sync off with
    an assignment. Vanilla's own `NMultiplayerTest` debug screen does exactly this.
  - `ChecksumTracker.IsEnabled` — likewise a public settable bool. Divergence detection can
    be switched off for the race and back on for the duel.

  What is *hard*: **map position is global, not per-player.**
  - `MapSelectionSynchronizer.PlayerVotedForMapCoord` only proceeds when
    `_votes.All(...)` — every player must vote — and then the host calls `MoveToMapCoord`,
    which picks **one** destination (randomly among tied votes) and enqueues a single
    `MoveToMapCoordAction`.
  - That action's `_player` is only the queue owner. `ExecuteAction` runs
    `NMapScreen.Instance.TravelToMapCoord(destination)` on *every* client, and `RunState`
    exposes a single `CurrentMapCoord`. There is no per-player location in the run state.
  - So decoupling is not "disable a synchronizer" — it means giving each client its own map
    position while the engine believes there is one. That is the real M5 risk, and it is
    where the spike should start.

  What is *more permissive than feared*: `RunLocationTargetedMessageBuffer` (which gates
  every `IRunLocationTargetedMessage`) blocks on `_visitedLocations`, a set of everywhere the
  receiver has *ever been* — not on current location. A message about a room you already
  passed is delivered immediately; only messages from a peer who is *ahead* of you are held.
  In a race over a shared map both players visit the same coords, so buffering should be
  transient rather than permanent. Note `OnLocationChanged` logs an **error** if anything is
  still buffered after a transition, so this will be noisy if it goes wrong — useful signal
  during the spike.

  Note the mod's own `DuelMessages` do **not** implement `IRunLocationTargetedMessage`, so
  they bypass this buffer entirely.

  Other waits to expect: `ActChangeSynchronizer` gates act transitions behind
  `SetLocalPlayerReady` / `IsWaitingForOtherPlayers` before calling `EnterNextAct` —
  a genuine rendezvous, and arguably one the race *wants* to keep at the Act 1 boundary.
  `RestSiteSynchronizer` and `RewardSynchronizer` are per-choice message relays keyed on the
  location buffer rather than hard barriers, so they are likely to tolerate divergence better
  than map traversal does.

- **I4 (M5)** — **CONFIRMED STILL NEEDED, measured 2026-08-05.** Playtesting appeared to show
  the two players already receiving identical card rewards, which would have made this
  unnecessary. It was a false signal, and the reason matters for all future testing.

  Measured on two clients (`race on` prints this; see `RaceCoordinator.LogSeedDiagnostics`):

  ```
  run seed 'MXQEJSBZUFNH'          (identical on both — random, not fixed)
  netId=1    slot=0  playerRngSeed=6094536868692103799
  netId=1001 slot=1  playerRngSeed=6094536868692103800
  ```

  Slot ordering is consistent across clients, and the per-player seeds differ by exactly 1,
  precisely as `InitializeSeed` implies. **The RNGs are not mirrored.**

  The identical rewards came from `RewardsSet.TryGenerateTutorialRewards`, which **bypasses
  RNG entirely** when `UnlockState.NumberOfRuns == 0 && EpochUnlockCount() == 0 && Character
  is Ironclad`, and hands out hardcoded cards, potions and relics (literally `Bludgeon, Pyre,
  EvilEye`, a `Vajra`, and so on) for roughly the first seven monster rooms and the first two
  elites.

  **Testing caveat, and it is a trap:** both dev profiles are permanently first-run unless a
  run is actually completed on them, so *every race test so far has observed scripted tutorial
  content rather than real reward generation*. To exercise the real path, either finish a run
  on each profile, or simply **pick a non-Ironclad character** — the tutorial branch requires
  Ironclad, so any other character bypasses it immediately. Do this before concluding anything
  about reward parity.

  The fix itself is still the one-line change described below.
  `Player.InitializeSeed(string seed)` (`Core/Entities/Players/Player.cs:328`) does:
  ```
  PlayerRng = new PlayerRngSet(GetDeterministicHashCode(seed) + (ulong)_runState.GetPlayerSlotIndex(this));
  PlayerOdds = new PlayerOddsSet(PlayerRng);
  ```
  The per-player slot index is the *only* thing making two players' card rewards, shop stock
  and event rolls differ on a shared seed. Dropping that offset — patching `InitializeSeed` so
  both duelists seed from the run seed alone — gives the mirror-match fairness §4 asks for.
  Beware the two other construction sites: line 269 seeds `0uL` before a run seed exists, and
  line 321 restores from a save via `PlayerRngSet.FromSerializable`, which a mirrored run must
  also keep consistent.
- **I4 (M5)**: Where `Player.PlayerRng`/`PlayerOdds` get their real seeds; mirror across
  players. Files: `Core/Entities/Players/Player.cs`, `Core/Runs/RunState` seeding.
- **I5 (M3)** — *host-authority half resolved; the fallback is not needed.*
  **The host CAN enqueue an action on behalf of another player.**
  `ActionQueueSynchronizer.EnqueueAction(action, actionOwnerId)` broadcasts
  `ActionEnqueuedMessage { playerId = actionOwnerId }`, and clients enqueue against
  `message.playerId` verbatim. Private, but reachable via Publicizer. So the flag can force
  an end turn host-side and stay deterministic — no need for the flagged player's own client
  to auto-submit.
  Two constraints found alongside it:
  - The public `RequestEnqueue(action)` cannot do this: on host it hardcodes
    `EnqueueAction(action, _netService.NetId)`, always attributing to the host. Call
    `EnqueueAction` directly with the target player's id.
  - A **client** cannot spoof another player: `HandleRequestEnqueueActionMessage` derives the
    owner from `senderId` and ignores any claim in the message. So forced end turn must
    originate on the host, which is what §3.2 wants anyway.
  Still open: the exact round-start hook for resuming the clock.
- **I6 (M4)**: Audit what per-player info the UI already renders for remote players
  (hand count? drawn cards? relic triggers?) so nothing leaks that we intend hidden — and
  confirm `HoveredModelTracker` suppression covers all surfaces (map pings too:
  `NetMapDrawingEvent`).
  - **Do not skip this because hovers look invisible in the duel arena.** Playtesting 2026-08-05
    showed no visible hover leak, but the data is on the wire regardless:
    `HoveredModelTracker.SynchronizeLocalHoveredModel` → `PeerInputSynchronizer.SyncLocalHoveredModel`
    sends a `PeerInputMessage` on every hover change, ungated. What is missing is only a
    *renderer* — the consumers (`NMultiplayerPlayerIntentHandler`,
    `NRemoteMouseCursorContainer`, `NMapDrawings`) are co-op surfaces that the arena does not
    currently surface. Anything that later shows a player panel in the duel turns the leak
    visible. Suppress at the broadcast, not at the display.
- **I7 (M1)**: Two-local-instance recipe: how sts2-lan-multiplayer forces the ENet
  transport (Nexus mod 579), Steam single-account constraints, `--goldberg`-free options.

## 11. Non-goals (v1)

Spectating, >2 players, ranked/matchmaking, Act 2+ races, mobile/console, Workshop-published
balance patches to vanilla cards. The duel uses vanilla card mechanics untouched.
