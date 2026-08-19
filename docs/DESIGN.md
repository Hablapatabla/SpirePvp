# SpirePvp — Technical Design (Real-Time Blitz)

Audience: Lucas + Claude (Opus) implementation agents. Each milestone below is scoped to be
handed off as an independent task. File references like `Core/Combat/CombatManager.cs` point
into the decompiled game source at `D:\modding\sts2\decompiled\MegaCrit\sts2\` (macOS:
`~/Code/sts2-decompiled/MegaCrit/sts2/`) — read the referenced file before implementing
against it. **Game version: v0.111.0** (`41cef1ea`, 2026-08-13) — re-verify facts after game
patches; re-run ilspycmd per README. Most of this document was written against v0.110.1; the
bump was absorbed on 2026-08-14 and `dotnet build` stayed green, so no patch target moved, but
line numbers below may have drifted by a few. Line numbers were originally taken from the
v0.110.1 macOS `data_sts2_macos_arm64/sts2.dll` and matched the Windows figures, so the two
decompiles are interchangeable for navigation.

## 1. The mode

Two players. Same seed → same map, same path options, same rolls. Each plays their own run
through Act 1 (the **race**), then both enter a 1v1 combat (the **duel**).

**Characters are chosen freely — this is not a mirror match.** Decided 2026-08-05. Both
players race the *same map* under the *same rolls*, but may bring different characters, and
character choice is part of the strategy rather than something to equalize.

The consequence, which is expected behaviour and not a bug: **Neow blessings and card rewards
will differ between players who picked different characters.** Both are filtered by character
— Neow through `IsAllowedAtNeow(owner)`, rewards through the character's card pool — so
identical RNG still yields different offers.

This does not make RNG mirroring (I4) pointless; it makes it *more* important. With the seeds
mirrored, neither player can be luckier than the other: every difference traces back to a
choice one of them made, which is the property the race actually needs. Without it, a loss is
always arguably variance.

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

**Resolution order is deferred to M9 — do not let it block M8.** Decided 2026-08-05: get the
lock-in loop working first with the cheapest possible order (submission order — flush both
players' buffered queues in the sequence they were queued, which is what blitz already does,
merely batched). That requires no new concepts and no per-character data.

**But "submission order" is underspecified for two players, and M8 has to pick a merge rule.**
Noticed 2026-08-12 while explaining it. Both players queue simultaneously and there is no shared
clock to order them by, so "the sequence they were queued" does not define a single stream. Three
rules produce three different games:

| Merge rule | What it plays like |
|---|---|
| All of A's, then all of B's | A resolves a whole hand against a defenceless opponent. A priority rule wearing a disguise |
| Interleaved — A1, B1, A2, B2 … | Advantage is one card at a time; neither player is ever more than a play ahead |
| Arrival order at the host | Nearly blitz again: flush timing leaks network latency back in, which is the thing model B exists to remove |

**Decided 2026-08-12: interleaved, starting on fixed slot order (host first).**

Interleaving still needs a tiebreak, and it is worth being clear why, because it looks symmetric
and is not: "mine first, then theirs" reads identically from both seats and both cannot be true.
Someone's first card resolves first. With `[Strike, Block]` against `[Block, Strike]`, starting
with A wastes B's block entirely; starting with B absorbs the strike. Same hands, opposite winner.

What interleaving buys is that the tiebreak stops being *decisive* — one card of advantage rather
than a whole hand — which is exactly what makes shipping an arbitrary one acceptable for M8.

**The tiebreak is the seam where initiative goes later.** ~~Fixed slot order is deterministic and
costs nothing; it is not a design statement.~~ **Replaced 2026-08-12: whoever reached the duel arena
first leads, alternating each turn after** — proposed by Lucas It is the best of the options considered because it is *earned*: it gives the race a
tactical consequence rather than only a material one (HP, deck, relics), and alternating keeps it
from being a first-strike advantage in every round of the duel.

**Built 2026-08-12, with two decisions that were not in the proposal.** *Arrival order is decided by
the host and rides on `DuelStartMessage`*, because it is not a local fact: each client knows only
when its own arrival happened and when the other's message reached it, so on a slow link both can
honestly believe they were first. The host sees both in one order — the same reasoning every other
duel parameter follows, and what the message was left empty waiting for. And *the alternation counts
turns, not batches*: per batch, a player could commit a throwaway one-card batch purely to flip who
leads the next one, which makes initiative something you manipulate by splitting your turn rather
than something you earned in the race.

It is shown as an arrow over the leading duelist for the whole turn — during planning, because that
is when the fact changes what you do, rather than as the batch resolves, when it is too late to use.
Drawn as a `Polygon2D` in code so it needs no art, no scene and no `.pck` change; swapping in a
texture later is one line.

**Rejected for the same reason, and it is a project principle rather than taste: a random
initiative.** Deciding it with the relic-contention rock-paper-scissors animation, or the map-icon
coin flip co-op uses for contested paths, puts luck back into the one place §1 works hardest to
remove it — *"neither player can be luckier than the other; every difference traces back to a
choice one of them made."* Those animations are the right way to **display** priority; they are the
wrong way to decide it. Tuning which order
makes the *best game* is a question worth asking only once the mode is playable, because the
answer depends on how it feels. The options, for when we come back to it:
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

## 5b. Match setup: a PvP run is configured in the lobby, before it starts

*Designed 2026-08-05, replacing the console-driven entry.*

**The problem with `race on`.** Race mode currently activates by typing a console command
partway through an already-running co-op run. That ordering causes every awkward thing in the
current implementation: the run has already been seeded, so `RaceCoordinator.MirrorExistingRun`
has to re-seed both players after the fact; Neow has already been drawn, so it stays
un-mirrored; and `RaceMirrorRngPatch` has to be unconditional because at seeding time nothing
knows this is a PvP match. All of it is compensation for deciding too late.

**The fix is to decide before the run exists.** A PvP match should be a property of the run,
chosen in the lobby, present at creation.

### The vehicle: a custom `ModifierModel`

The engine already has a first-class concept for "this run is played under special rules",
used for daily and custom runs — and it fits our needs exactly:

- `ModifierModel` is an `AbstractModel`, so a mod can add one with **no BaseLib dependency**
  (same auto-registration by mod-assembly scan that `DuelEncounter` already relies on).
- Modifiers are **chosen in the lobby** by the host (`NCustomRunModifiersList`) and **synced
  to clients automatically** via the vanilla `LobbyModifiersChangedMessage`. No custom lobby
  netcode.
- `RunState.CreateForNewRun` takes modifiers and installs them via `CreateShared` **before**
  the `player.InitializeSeed(seed)` loop. So the modifier is queryable at seeding time.
- `ModifierModel.AfterRunCreated(RunState)` is a virtual hook — the natural place to put the
  run into race mode.
- Modifiers are serialized with the run, so a saved PvP run reloads as a PvP run for free.

### What this deletes

| Today | After |
|---|---|
| `race on` typed mid-run | Modifier chosen in lobby; race active from creation |
| `MirrorExistingRun` re-seeds after the fact | Never needed — seeding happens once, already mirrored |
| Neow drawn before mirroring, so divergent | Neow drawn under mirrored seeds |
| `RaceMirrorRngPatch` unconditional | Scoped: mirror only when the run carries the modifier |
| Race state is client-local and hand-synced | Carried by the run itself, on both clients |

The `race` console command stays, demoted to a debug shortcut for exercising the patches
outside a properly configured match.

### Host flow

Multiplayer → host → **Custom** run → tick **1v1 Duel** in the modifier list → friend joins →
start. Custom mode also exposes the seed field, which is a bonus: identical seeds are the
premise of a fair mirror match, and players may want to rematch on a known seed.

Two constraints to know: Custom mode is gated behind `CustomAndSeedsEpoch` being revealed
(`unlock all` clears it on dev profiles), and `--fastmp=host_custom` is the vanilla dev flag
that boots straight into a custom multiplayer host — so the two-client scripts can drive this
without menu clicking.

### Clock duration and turn model ARE the match configuration

*Corrected 2026-08-05 — an earlier draft of this section had these as host-side settings
broadcast at duel time. That was wrong.* These are the two things two players agree on before
a match, exactly like time control and ruleset in chess: **how long is the clock**, and **are
we playing real-time or turn-based**. They belong in the lobby, visible to both players before
anyone commits, and fixed for the run.

So they are expressed as modifiers too, in two mutually exclusive groups:

| Group | Options | Meaning |
|---|---|---|
| **Turn model** (pick one) | `Duel: Real-Time Blitz` · `Duel: Turn-Based` | Also the "this is a PvP match" signal — either one present means duel mode |
| **Clock** (pick one) | `Clock: 3 min` · `5 min` · `10 min` · `No clock` | Per-player time bank (§3.2) |

Six tickboxes, but only two decisions, and both are visible to the joining player in the
lobby before they start. Vanilla supports exclusivity directly:
`ModelDb.MutuallyExclusiveModifiers` is a set-of-sets that `NCustomRunModifiersList` already
uses to make tickboxes behave as radio buttons.

`DuelStartMessage` keeps carrying `clockMs` and the turn model, but now as *derived* values
read off the run's modifiers rather than as an independent host-side setting — one source of
truth, decided in the lobby.

### Two implementation obstacles, both surmountable

1. **`ModelDb.GoodModifiers`, `BadModifiers` and `MutuallyExclusiveModifiers` are hardcoded
   arrays**, not scanned collections — unlike `ModelDb.All`, which is why `DuelEncounter`
   registers itself for free but these will not. Each property constructs a fresh array per
   call, so a Harmony postfix appending our entries is safe and simple. Note
   `NCustomRunModifiersList.GetAllModifiers` reads `GoodModifiers.Concat(BadModifiers)`, so
   patching the `ModelDb` properties covers the UI as well as anything else reading them.
2. **Modifier titles and descriptions come from a localization table** —
   `LocString("modifiers", Id.Entry + ".title")` — so the mod needs loc JSON shipped in its
   `.pck`. This is the first real asset work in the project (the `.pck` currently holds only
   the mod image); Minty Spire 2's `localization/**/*.json` layout is the reference. Budget
   for a `.pck` re-export in the build loop, which the Windows scripts do not currently do.

### Entry point

**Now:** the vanilla custom-run modifier list. Zero custom UI, and it gets the whole flow
working end to end. Slightly buried — the host must know to choose a Custom run.

**M7 — the dedicated Duel host menu.** *Scoped 2026-08-06; the next milestone.*

A third entry beside **host normal** and **host custom**: **host duel**. The mechanism does not
change — it still sets the same modifiers, which is exactly what makes this presentation work
rather than a rewrite — but the route does.

Why it is worth a milestone rather than a shortcut. Today a match is configured by knowing to
pick a Custom run and tick one entry from each of three groups. That is buried, and it is the
wrong *shape*: a flat list of nine tickboxes for what is really two or three coupled decisions,
displayed identically to the run-altering modifiers it sits among. Nothing about the screen says
these three go together, or that picking none of the clock entries silently means "off".

What it should have:

- **Direct controls for the clocks and the ruleset**, rather than radio-button modifiers.
- **Presets on chess conventions.** `10 minute race + 2 minute duel` is the agreed starting
  point for **blitz**. The existing 1-minute entries stay reachable somewhere, since they are
  what makes flagging testable inside a single run.

Art is wanted here and is Lucas's to draw; the inventory is in HANDOFF under "Art still wanted".

Note the constraint that shaped §5b still holds: whatever this screen does, it must end in the
same `ModifierModel`s on the `RunState`, because that is what makes a PvP run reload as a PvP
run and what puts the settings in front of the joining player before they commit.

### Implementation scope, read off the engine 2026-08-11

The route in is smaller than it looks, and needs no scene editing and no BaseLib.

**The menu.** `NMultiplayerHostSubmenu` (`Core/Nodes/Screens/MainMenu/`) holds exactly three
`NSubmenuButton`s, fetched from its scene by node name — `StandardButton`, `DailyButton`,
`CustomRunButton` — each wired to `StartHost(GameMode)`. A mod cannot edit that `.tscn`, but it
does not need to: **duplicate the existing Custom button node in a postfix on `_Ready`**, retitle
it, and connect it to our own handler. That inherits the button's art, sizing and focus
behaviour for free, which is also what makes it look native.

**The label rides in a vanilla table, as ever.** `NSubmenuButton.SetIconAndLocalization(prefix)`
resolves `new LocString("main_menu_ui", prefix + ".title")`, so a `DUEL_MP.title` entry must go
in `main_menu_ui.json`. Same rule that forced `encounters.json` and `modifiers.json`: a table of
our own would never be read.

**`GameMode` is a vanilla enum and we do not add to it.** Host duel starts a
`GameMode.Custom` host — that is what brings the modifier machinery and the seed field along —
and then applies the match configuration itself. Hijacking Custom is the point rather than a
compromise: it is what guarantees the run ends up carrying the same `ModifierModel`s, which is
the constraint above.

**Gating.** `RefreshButtons` disables Custom behind `CustomAndSeedsEpoch`. A duel button built
on Custom's machinery inherits that constraint whether or not it shows the same lock, so decide
deliberately: gate it identically, or unlock it separately and accept that it is a Custom run
wearing another name.

**Suggested order, cheapest first:**

1. The button, opening a Custom lobby with the three duel modifiers **pre-ticked** to the blitz
   preset (`Real-Time` · `Race 10` · `Duel 2`). This alone removes every part of the current
   burial — the host never has to know which three tickboxes to find — while the joining player
   still sees the agreed settings in the vanilla list, so nothing is lost.
2. Preset buttons over the top (blitz, and whatever else play suggests).
3. Direct clock controls, replacing the radio-button modifiers as the *presentation* while still
   ending in the same modifiers underneath.

Art wanted: the button icon, and whatever framing steps 2–3 sit in.

## 6. UI components (Godot side, via BaseLib node factories + our .pck)

| Component | Notes |
|---|---|
| `OpponentDeckPanel` | **Design settled 2026-08-05 — it is the duel's entry flow, not a panel.** Clicking the duel map node opens a full deck screen showing the *opponent's* deck (the campfire-style view), whose confirm button reads **START DUEL** instead of the usual label. Both players enter the arena once both have viewed and confirmed. This folds the information rule and the ready-handshake into one screen: you cannot start without having been shown the decklist, and the confirm doubles as `DuelReadyMessage`. Cheaper than it sounds — `NDeckViewScreen.ShowScreen(Player)` is static and takes any player, so rendering the opponent's deck is a one-liner; the custom work is the button label and the both-confirmed gate. Until the map node exists (M6), `duel start` opens this screen rather than entering the arena directly. |
| `ClockHud` | **Done, and deliberately not a component.** Both clocks share the vanilla run-timer label in the top bar (`NRunTimer`, postfixed), rendered as `YOU 2:31 · OPP 1:47` in a stable `m:ss`. A separate two-element HUD was considered and dropped — one label reads fine and costs no scene work. Local prediction + host `ClockSyncMessage` at 2/sec. "Turns red < 30s" still unimplemented. |
| `RaceProgressHud` | **Cut as a feature, 2026-08-06, after building it and looking at it.** A permanent readout of the opponent's HP and deck is clutter, and it is a competitive change nobody asked for: knowing their exact HP at every moment turns a race run on your own judgement into one run against a status bar, and hands both players information a match should make them infer. It survives as a debug tool (`duel hud on`, off by default). **The tracking stays and is the half that mattered** — `RaceProgress` retains position, HP and deck size for the result screen and post-match analysis. The opponent's *position* is still shown, via their portrait on your map, which is enough to feel like a race without being a dashboard. |
| `DuelResultScreen` | Winner, match stats, rematch button. **Built and confirmed in play 2026-08-12**, except rematch. Vanilla's run-score lines are suppressed (they score a run, not a match, and "+42 for floors climbed" invites the loser to think they were ahead), and six comparison rows stand in their place — damage, cards, HP, gold, elites, deck size, each as `yours · theirs`. **The stats are comparative rather than per-round, and that replaced this row's original ask deliberately:** a duel has an opponent, so "12 cards" says far less than "12 to their 20", and a per-round table costs space the flat list does not have. Rematch is deferred and milestone-sized; see HANDOFF. |
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

  **M6 starts with match setup (§5b), not the map node.** Adding the `PvpDuelModifier` is the
  cheapest item and it deletes the mid-run re-seed, the unconditional mirroring patch and the
  console-driven entry in one go — so everything after it is built on a run that knows what it
  is from the moment it is created. Order: modifier → map node → progress HUD → ready
  handshake.

  ### The arena node (implemented 2026-08-05)

  **The engine already had the shape we needed.** `StandardActMap` builds a *second boss* node
  one row below the first and chains it as the boss's child — the back-to-back layout Act 3
  uses for double bosses — whenever `ActModel.HasSecondBoss` is true. And `HasSecondBoss` is
  just "a second boss encounter has been set". So the whole feature is one public call:
  `act.SetSecondBossEncounter(duelEncounter)` at run creation, before the map is generated.
  No map-generation patching, no custom `MapPointType`, no new node class.

  Consequences that fall out for free: the arena is boss-sized, gets the 2x boss selection
  VFX, and takes its art from `EncounterModel.BossNodePath`. `DuelEncounter.RoomType` had to
  become `Boss` — `SetSecondBossEncounter` rejects anything else — which is also what earns
  the boss presentation.

  Art: `BossNodePath` resolves a Spine skeleton first and falls back to two static PNGs when
  the `.tres` is absent, so pointing at a path with no rig deliberately selects the static
  branch. Ships in the `.pck` at `SpirePvp/map/duel_node.png` and `duel_node_outline.png`.

  ### The arena is the one node where players wait for each other

  Design intent, 2026-08-05: every other node is raced independently, but the arena is a
  rendezvous. Reaching it does not load the duel — it waits for the opponent, then opens the
  opponent's-deck screen (the M4 entry flow), and the duel starts once both confirm.

  This is the natural home for the `DuelReadyMessage` handshake, and it means the race's
  finish line is "arrive and wait" rather than "arrive and fight". It also makes the progress
  HUD matter: while you wait, you want to see where they are. Note `ActChangeSynchronizer`
  already implements a genuine act-boundary rendezvous that the race deliberately left intact
  — worth reading before building a second waiting mechanism.

- **M7 — Dedicated Duel host menu** (scoped in §5b: `host normal / custom / duel`, direct clock
  and ruleset controls, chess-convention presets with blitz = 10 min race + 2 min duel). Then
  balance knobs (§9); Workshop packaging; spectator/obs support (stretch).
  *Accept: a match is configured without the host ever seeing the Custom modifier list, and the
  joining player still sees the agreed settings before starting.*
- **M8 — Simultaneous turn-based duel** (§3.1b model B). **Core built and playtested 2026-08-12**:
  a five-round turn-based duel played end to end on two clients, rounds resolving interleaved,
  turns rolling over, finishing on HP with correct result screens and zero mod errors. Picking
  `1v1 Duel: Turn-Based` in the lobby now plays turn-based.
  `IDuelTurnModel` / `BlitzTurnModel` / `LockInTurnModel`, with the gate on
  `ActionQueueSynchronizer.RequestEnqueue`. **Use submission order and do not tune it** —
  resolution order is M9's problem.

  **Four ordering constraints had to be found one at a time, and they are the whole difficulty of
  this milestone.** Recorded because any change to the round loop has to keep all four:

  1. `EndPlayerTurnAction` is itself a `CombatPlayPhaseOnly` action, so the buffer swallowed it —
     a deadlock, since end turn is what releases the buffer.
  2. The host must hold *both* end turns and enqueue them **after** the plays. Let through early
     the turn rolls over before its cards resolve; dropped, nobody is ever ready.
  3. The lock-in trigger belongs on the `RequestEnqueue` path, not `SetReadyToEndTurn`: it must
     fire on the click so the buffered plays leave *before* the end-turn request.
  4. The local end turn must be recorded **before** locking in, because locking in can flush the
     round immediately — and a flush that beats the assignment appends only the opponent's.

  **Both of the pieces this milestone left open are now built (2026-08-12, unplayed).** Energy is
  reserved while planning (`DuelPlanEnergyPatch`), and a held play is drawn in vanilla's own play
  queue with a ready-icon over the end turn button for who has locked in (`LockInPlanView`).
  Resolution is paced so a round can be read (`DuelPace`), and the duel clock stops while it plays
  out.

  **The draw-card problem is answered by batches, decided 2026-08-12 after playing it.** Planning a
  whole turn from the opening hand makes draw cards near-dead: the cards arrive after planning is
  over and are discarded before the next one. §9 listed four options; the one built is none of them
  exactly, and it contains the leading one. **Locking in commits a *batch* rather than the turn, and
  an empty batch is what ends the turn.** A turn therefore holds as many plan→resolve exchanges as
  the players want, so a card drawn in batch 1 is in hand for batch 2 of the same turn.

  Its virtue is that nothing is special-cased: no card is split between a plan-time effect and a
  resolved one, nothing resolves twice, and no card needs a per-card tag saying whether it may
  resolve early — which is where "resolve draws at plan time" and "tag cards as free vs queued" both
  put their desync risk. "Two planning passes" is what this degenerates to when a turn uses two
  batches, so the fixed count never had to be chosen.

  Consequences worth knowing before changing it: a player who is finished stays ready for the rest
  of the turn (or every later batch would wait on someone with nothing left to commit); the end
  turns are enqueued only on the closing batch; and planning must not reopen until the batch has
  *resolved*, not when it was enqueued, or the paced resolution becomes a free planning window with
  the clocks stopped.

  **The rule that came out of building them, and it generalises past this milestone: `CanPlay` is
  not a UI predicate.** `PlayCardAction.ExecuteAction` re-checks it, `CardSelectCmd` filters a
  choice list with it, `WhisperingEarring` picks which card to auto-play from it — all sim code,
  which must answer identically on both clients. A reservation is local by construction, so
  anything patched in there has to be provably invisible to the sim: the postfix answers only while
  `ActionExecutor.CurrentlyRunningAction` is null, which holds for every sim caller because they all
  run inside an executing action, and *nothing* executes during a planning phase — every play is
  buffered. The same argument is why both clients must release the round on the same condition
  rather than each at its own local event.
  **Explicitly on hold, 2026-08-05: do not start this until real-time blitz is polished end to
  end.** The comparison is only worth making against a finished thing, and there is real work
  left on blitz. Note the consequence that has to be lived with meanwhile: the lobby already
  offers `1v1 Duel: Turn-Based`, and picking it plays blitz — `DuelMatch.IsTurnBased` is read in
  exactly one place, a log line. Either accept that or hide the modifier until M8 lands.
  *Accept: the same duel is playable under both models, chosen at duel start.*
- **M8.5 — Tick-paced blitz ("the OSRS model").** *Proposed by Lucas 2026-08-12, after playing
  both existing models, and it may be the one that ships.*

  **The problem it solves is legibility, not fairness.** In blitz the opponent's effects arrive as
  things that simply happen — damage lands, a power appears — with no readable moment where a card
  was played. You cannot see what hit you, so you cannot react to it, and a duel becomes two people
  clicking rather than two people fencing.

  The shape: **actions are still submitted in real time, but resolve on a fixed cadence**, one card
  at a time, each with an animation long enough to read. Old School RuneScape's 0.6s tick is the
  stated reference — long enough that a play is a visible event you can respond to, short enough
  that the match still feels live. "Pseudo turn-based but playing out really quickly."

  Why it is worth a milestone of its own rather than a tweak: it is a **third turn model**, and the
  seam already exists. `IDuelTurnModel` was built so blitz and lock-in could differ in *when*
  actions execute, and this differs in exactly that dimension — it neither buffers a whole round
  (model B) nor releases instantly (model A), but releases the shared queue on a clock. It also
  fixes model A's host-latency edge as a side effect: if resolution is quantised to a tick, a half
  RTT stops deciding anything unless it crosses a tick boundary.

  **Specified 2026-08-12 by Lucas, who thinks this is what the real-time mode should actually be**
  rather than a third option beside it. Four rules:

  1. **Your first play is instant.** No cooldown on the opener, so opening speed still decides the
     first exchange and blitz keeps its texture.
  2. **A cooldown of ~0.4s before your next play.** Tunable; note this is shorter than the 0.6s
     OSRS tick quoted above, and the number is a playtest question.
  3. **Plays can be queued during the cooldown**, OSRS-style — you commit the next action while the
     current one is still resolving, rather than being locked out of the interface.
  4. **You can see what the opponent has queued**, which is what makes a reaction possible at all,
     together with longer animation and resolution windows so there is time to act on it.

  **Rule 4 is a change to the information rules (§1), not just a HUD.** Every other surface in this
  mod hides intent — `HoverSuppressionPatch` exists to stop even a pointer leaking — and this
  deliberately reveals it. The justification is the same one M8.5 opened with: in blitz the
  opponent's effects arrive as things that simply happen, so there is nothing to fence with. A
  visible queue is the thing being reacted *to*.

  It is also cheaper than it sounds: **vanilla already draws a remote player's queued cards.**
  `NCardPlayQueue.OnActionEnqueued` has a remote branch that flies the other player's card in from
  their intent handler, which is co-op's own answer to "what is my teammate doing". Blitz plays
  already pass through that path, so this may be less a feature to build than one to stop
  suppressing.

  **Three decisions taken 2026-08-12, which settle its shape:**

  1. **It replaces the real-time turn model rather than joining it.** `1v1 Duel: Real-Time` starts
     meaning this, and `BlitzTurnModel` stops being reachable. **Note the name collision before
     touching anything: "Blitz" and "Rapid" in the Duel lobby are *clock presets*** — chess terms
     for how much time each bank gets — and are unaffected by any of this. The turn model that
     happens to be called `DuelBlitz` in code is the one being replaced, and its lobby text already
     reads "Real-Time"; only its *description* is now wrong ("actions resolve in the order they are
     made, so speed decides trades"), which is a `.pck` change.
  2. **The opponent's whole queue is visible, including plays their cooldown has not released yet.**
     That is a deliberate change to the information rules (§1), and it is the point of the mode:
     seeing only what is already resolving shows you their next card at most 0.4s early, which is
     not enough to read or answer. Their unsubmitted queue is not on the wire today, so this needs
     a message sent as each card is queued.
  3. **Resolution is quantised to a tick.**

  **The engine already keeps a queue per player, and then flattens them by submission time.**
  Found 2026-08-12 after the first paced playtest, where the report was that two Defends queued a
  clear second before the opponent's Strike still did not block it: "it seemed as though they were
  semi queued based on time of play as opposed to each player having their own queue".

  `ActionQueueSet` really does hold one `ActionQueue` per player — the log says
  `Enqueueing action … to player queue owned by 1001` — and `GetReadyAction` walks them all and
  picks `gameAction2.Id < gameAction.Id`, the globally lowest action id. Ids are handed out by the
  host in arrival order, so the per-player structure collapses into one stream ordered by *when you
  clicked*, which is the thing this mode was supposed to stop doing.

  **`ActionQueue.isPaused` is the seam, and it must not be used locally.** `GetReadyAction` skips a
  paused queue's play-phase actions and takes the other player's action instead, so the engine can
  genuinely run the two independently. But pausing a queue changes *which action executes next*,
  which is sim-visible: a client pausing on its own wall clock would diverge from the host within a
  card. Per-player cadence therefore has to be decided once, by the host, and expressed in the ids
  it assigns — which is exactly what bucketing is.

  **And the global beat compounds it.** `DuelPace` leaves its gap between *any* two cards, so two
  players sharing one stream each get half their own cadence. The beat belongs between consecutive
  cards of the **same** player; between two different players' cards it should be short or nothing,
  because those are the exchanges the mode exists to make readable.

  **What quantising actually requires, which is easy to get half-right:** bucketing the two players'
  requests by tick removes the *sub-tick* part of the latency edge, and then leaves a real question —
  what orders two plays that land in the same bucket? Arrival order inside the bucket puts the whole
  problem straight back, because the host's own requests never cross the network. **Order within a
  bucket by initiative** — the M9 rule that already exists, whoever reached the arena first,
  alternating each turn — and the half-RTT advantage is gone rather than merely shortened. This also
  gives the initiative rule a second job, which is a point in its favour: the race's reward is the
  same in both modes.

  Build it in three slices, each playable on its own:

  1. **The cooldown and the local queue.** Instant first play, ~0.4s before the next, plays queued
     in between and drawn in the play queue as the lock-in model already draws planned cards. Needs
     no message and no host change.
  2. **Tick bucketing on the host**, with initiative breaking ties inside a bucket.
  3. **The opponent's queue on the wire**, drawn on their side of the screen — the split
     `DuelPlanQueuePatch` already does for the lock-in model.

  Open questions for whoever builds it:
  - **Cooldown is a submit-rate limit; the pacing built for the lock-in model is a resolve-rate
    limit.** They are different mechanisms and this milestone needs both — `DuelPace` slows the
    resolution, and rule 2 slows how often a play may be *requested*. Do not try to make one do
    the other's job.
  - **Where the cooldown is enforced.** Locally is honest and cheap (the client refuses to submit),
    but a client is not trustworthy about its own timing; the host holding a per-player next-allowed
    timestamp is the version that survives someone patching their own copy. Friendly play does not
    need the second, and the seam is `ActionQueueSynchronizer.RequestEnqueue` either way — the same
    gate `IDuelTurnModel` already sits behind.
  - **Whether the card animation is lengthened or the resolution is delayed.** These look the same
    on screen and are not the same thing: one changes presentation, the other changes when damage
    lands. Only the second gives a real reaction window, and it is what `DuelPace` already does.

- **M9 — Turn-model tuning.** Revisit resolution order (§3.1b) now that both modes are
  playable and can be compared by feel rather than argument; per-round planning timer for
  model B; decide whether one model ships or both do as a lobby option.

## 7b. M10 — Draft mode ("Pokemon draft style"). Proposed 2026-08-13, **built 2026-08-14**

**A second game mode, not a variant of the first: no race at all, just a duel.** Both players are
shown a pool of roughly 10-15 random cards and draft from it alternately; then they fight. Mirror
matches to begin with (both duelists on the same character), which removes a whole class of question
rather than answering it.

**The compensation rule is Lucas's and it is the good part: whoever drafts first moves second.**
First pick is a real advantage, so it buys the other player initiative in the duel. Note this is the
same trade M9 already makes in the other direction — there, reaching the arena first *earns* the
first move. Draft mode inverts it because the advantage being paid for is different.

### Why this is a smaller milestone than it looks

Almost everything it needs already exists and is playtested. It reuses `DuelEncounter`, the arena,
both turn models, the duel clock, the result screen, badges, stats and rematch **unchanged**. What it
does not need is the entire race half — M5, the mirrored RNG, `RaceCoordinator`, the race clock, the
rendezvous, the map work, and every "the engine assumes the party is co-located" bug that phase has
produced. A duel without a race is the mod's own core with its riskiest phase deleted.

**Initiative needs a new source.** `IPlanningTurnModel.SetInitiative` is fed from `DuelStartMessage`
with "who reached the arena first", which will not exist. The draft supplies it instead — the field
and the plumbing are already there, so this is one line and a different input.

### The one part that is genuinely new, and where the danger is

**The draft is a shared ordered sequence of decisions, which is the shape this project has desynced
on twice.** Read the stale-`_receivedChoices` and `_nextActionId` notes in HANDOFF before designing
it: a card pick already travels as a `PlayerChoiceResult`, and the race's leftover picks are exactly
what corrupted a duel on 2026-08-12. Rules that follow from that history:

- **The host owns the pool and the turn order**, and clients request rather than decide. Two clients
  deriving a pool from a shared seed independently is the pattern that keeps biting.
- **Announce the pick, do not infer it.** Both players must see the same pool shrink in the same
  order, and a pick the peer did not send is a pick that did not happen.
- **Arm the draft's handlers at run start**, not when the draft screen opens. Five separate bugs in
  this project have been a handler armed lazily and a peer that announced something first.

### DECIDED 2026-08-14 (Lucas). The six questions below are answered; kept for the reasoning.

**Format.** Card pool **15 — 5 common, 5 uncommon, 5 rare**, one shared pool, alternating picks,
**7 each and the 15th discarded**. Then a **relic draft: 8 in the pool, 4 each** (ten was the first
number and played as a few too many — a duel is a handful of turns, and five permanent relics each is
more advantage than that many turns can express).  Then a **potion
draft: 4 in the pool, 2 each**. Both pools drafted to exhaustion, so those two split evenly and only
the card round has a remainder.

*Why the 15th is discarded rather than taken.* Alternating over an odd pool gives the first picker
an eighth card, and the compensation rule already spends first-pick advantage on initiative — the
extra card would be a second payment for the same thing. Discarding keeps the decks symmetric and
keeps the pool at 15, so denial still matters for every pick.

**Loadout.** The character's **normal starting deck plus the drafted cards**, and the character's
**starter relic plus the 4 drafted**. A floor means a bad draft is weak rather than unplayable, and
in a mirror match both sides get the identical floor, so it costs no fairness. The drafted cards are
the whole of the difference between the two decks.

**Mirror stays, and the host picks the character.** The client follows. This is what makes a shared
pool obviously fair and it sidesteps the per-character filtering that made Neow's offers differ.

**Lobby shape: a fourth group**, `MatchFormatModifier` (`MatchFormatRace` / `MatchFormatDraft`),
first in `DuelLobbyPanel.Groups` — above the turn model, because it chooses which game is played
where the turn model only chooses how the duel inside it works. **It must not mark a run as PvP**:
`DuelMatch.HasTurnModel` stays the single test for that, since it is asked from inside seeding and
from inside Neow's option generation, and a second marker is a second thing to keep in sync.

### BUILT 2026-08-14: cards and relics

The card half is in and registered. `DuelDraft` is the whole of it, plus two patches.

**Skipping Neow is vanilla's own branch, not a suppression.** `RunManager` opens act 1 on
`State.ExtraFields.StartedWithNeow`: true enters the Neow map point, false enters a plain `MapRoom`.
`DuelDraftNeowPatch` postfixes `SetStartedWithNeowFlag` and clears it for a draft run, so the run
opens on the map screen and the draft goes up over it. Ordering matters and is why the patch hangs
there: `InitializeNewRun` sets the flag *then* loops the modifiers calling `OnRunCreated`, and map
generation reads the flag afterwards to decide the starting point's type.

**The race half is simply never switched on** — no `ActivateRace`, no `RaceCoordinator`, so every
patch gated on `IsRaceActive` is inert for the whole run. No new `DuelPhase` was needed and none was
added: the phase enum is consulted by exact comparisons all over the mod, and a fourth value would
have to be audited against each. `DuelDraft.IsDrafting` is the state, and it answers a narrower
question. **The arena node is still installed**: nobody walks to it, but `DuelArena` moves both
clients to its coord, so it has to be a real map point.

**Full state, never deltas.** `DraftStateMessage` carries the pool, both pick lists, whose turn it
is and who picked first — every time. That is what makes the draft immune to the family of bugs
that produced the stale `_receivedChoices` and the shared `_nextActionId`: those were increments
applied against a position the two peers disagreed about, and there is no position here to
disagree about. It also makes the retry below free.

**The retry, which was the one genuine surprise.** `NetMessageBus` does *not* buffer for an
unregistered handler — it drops and logs an error, buffering only inside its own loading window. A
draft begins at run launch, so the two peers arm within milliseconds and there is no ordering
guarantee either way; every other announcement in this mod is separated from arming by a whole
race, which is why the margin has never mattered before. So the host repeats the state on the run
timer until a `DraftAckMessage` comes back. Safe only because the state is complete.

**Initiative inverts, and asks the right question.** `DuelRendezvous.FirstToArrive` reads
`DuelDraft.MovesFirstId` for a draft run. Note it keys on `IsDraftRun` (a pool exists) rather than
`IsDrafting` (a draft is on screen) — the read happens at arena entry, which is *after* the last
pick, so the narrower predicate would have silently fallen back to arrival order and undone the
compensation rule.

### The relic round, added the same day

Eight in the pool, four each — **2 boss, 2 rare, 2 uncommon, 2 common** — on the same alternating loop — the rounds are the same code with a
different pool, which is what makes a third round cheap. Relics come from **the character's pool
plus the shared pool**, minus anything already held: a duel has no shop, chest or boss reward, so
the draft is the only source there is and one pool would delete half the relic game.

**The boss tier draws from `EventRelicPool`** (audited 2026-08-17, after Lucas noticed the same two
boss relics every round and asked whether it was really chance — it was not):

| Pool | Ancient (boss) relics |
|---|---|
| `SharedRelicPool` | **2** — Looming Fruit, Very Hot Cocoa |
| every character pool | **0** |
| `EventRelicPool` | **100** |

Two candidates for two slots is not a draw, it is a constant, and no amount of shuffling would have
shown otherwise. The other hundred — Sozu, Pandora's Box, Snecko Eye, Runic Pyramid, Black Star —
live in the pool a duel never opens, because a duel has no boss and no event. The concat is
restricted to `Rarity == Ancient` because that pool also carries 34 Event, 5 Starter and 3 ordinary
relics, and the other three tiers already draw from pools meant for them. **The candidate count per
tier is now logged**, since "is this actually random" is a question this pool has answered wrong once
and a log of the *result* can never settle it.

**The rarity split is fixed rather than shuffled** (2026-08-17). An unweighted draw over the whole
pool is mostly commons, so the picks that actually shape a duel arrive by luck or not at all; Lucas
drew a boss relic in the first played round and it was the most interesting pick in it. Two of each
tier also gives denial something to bite on — there are only two boss relics and your opponent wants
one. Boss relics are `RelicRarity.Ancient` in StS2; there is no `Boss` member. A tier that comes up
short tops up from the rest of the pool rather than shortening it, because the round splits the pool
evenly and drafts it to exhaustion — seven relics is a round where one player gets four picks and the
other three.

**Granted through `RelicCmd.Obtain`**, never `AddRelicInternal`. Obtain records the choice, clears
the grab bags so a relic cannot be offered twice, animates it in and awaits `AfterObtained` — which
is how a relic with an on-pickup effect applies at all.

**Initiative alternates between rounds**, so one coin flip does not hand the same player first pick
in every round.

Two ordering rules the round turned up, both worth keeping:

- **Broadcast the finished round before clearing its picks.** Peers apply their own picks *from the
  broadcast*, so advancing first publishes an empty list and the round's picks reach nobody.
- **Apply picks before testing for completion**, or the last pick of the last round is dropped.

### The draft is seeded (2026-08-18)

**The invariant, stated so it can be tested: same seed, same characters, same run.** Lucas ran the
same seed with the same character twice and got two different card pools and two different relic
pools. He was right to call it — a seed that does not name the run is not a seed, and until this
was fixed the Rematch button replayed only the map.

Every roll in `DuelDraft` was a bare `new Random()`. That was **safe** and it is worth being
precise about why, because the fix is not "it was desyncing": the pool is built on the host and
*broadcast*, so the two peers have never disagreed about it. What a `new Random()` cannot give is
reproducibility, and nothing in the draft was asking for it.

The fix is vanilla's own idiom, `new Rng(seed, name)` — a **side stream off the run seed**, not a
draw from `RunState.Rng`. That distinction is the whole design:

| | `RunState.Rng.UpFront` etc. | `new Rng(RunState.Rng.Seed, name)` |
|---|---|---|
| Derived from the seed | yes | yes |
| Consumed in lockstep by both sims | **yes** | no — nobody else reads it |
| Safe to draw from on the host alone | **no**, this is how a seeded stream diverges | yes |

`SpoilsActMap` takes `"spoils_map"`, `MapSelectionSynchronizer` takes `"map_point_selection"`,
`RunRngSet` builds its entire set this way, and `DuelRematch` already used it for `"act_selection"`.
`DuelDraft.DraftRng` now takes three: `spirepvp_draft_cards`, `spirepvp_draft_relics` and
`spirepvp_draft_first_picker` — **one stream per purpose**, so adding a roll to one does not shift
the others. The potion round must take a fourth rather than borrowing one of these.

**Seeding the stream was half the fix.** A shuffle by random key is a permutation *of the input
order* — the Nth candidate gets the Nth draw — so the same seed over a differently-ordered
candidate list still yields a different pool. `AllCards` is a hardcoded array, but
`ModHelper.ConcatModelsFromMods` appends whatever other mods contribute in their load order, and
the relic list is three pools concatenated. Both candidate lists are now sorted by `Id.Entry`
before the shuffle, which makes a pool a function of **the seed, the character and the unlock
set**, and of nothing else.

**The unlock set is the remaining input, and it is per profile.** Two players with different
unlocks would compute different candidate lists from the same seed. It does not desync anything —
the host builds the pool and sends it — but "same seed, same run" holds across *machines* only if
the unlock states match. Worth knowing before a Steam session; `unlock all` on both dev profiles is
already the standing advice for a different reason.

**What this changes about Rematch — asked and DECIDED 2026-08-18 (Lucas).** A rematch relaunches
on the same seed (§5b), so a redrafted match offers the **identical pool** and gives first pick to
the **same seat**. Both are intended: *"same seed, same characters, just clicking rematch? first
pick should be the same. should just be the default. a rematch should be a rematch."* The pool
repeating was never in doubt — both players have already seen it, exactly the argument §5b made for
replaying the same map — and first pick follows the same rule rather than being carved out as a
special case. **So no rematch counter is mixed into the `first_picker` stream, and none should be
added.** A player who wants a different draft changes the seed, which is what the seed is for.

### What is left

1. **The potion round.** 4 in the pool, 2 each. The loop, the message and the grant
   (`Player.AddPotionInternal`, `CharacterModel.PotionPool`) are all in place — the round is about
   thirty lines. **What is missing is a screen**: vanilla has no potion picker (`NPotionLab` is
   crafting, `NUnlockPotionsScreen` is the timeline) and `NChooseARelicSelection` takes a
   `RelicModel`. Either build a minimal grid from the pieces `DuelLobbyPanel` already uses, or wrap
   potions in a display-only shim for the relic row. A surface decision, not a logic one.
2. ~~**Pool filtering for relics.**~~ **Done 2026-08-14** (`DuelDraft.IsDeadInADuel`), and by hook
   rather than by name: a relic is excluded only when it overrides at least one hook and *every* one
   of them is in the map/shop/rest/event family. Conservative on purpose — a relic overriding
   nothing is kept, since many work through properties, and one combat hook among five map hooks is
   enough to keep it. A dead relic is a bad pick; a missing good relic is a worse pool. The list is
   of hooks, not models, so a relic added by a game update is kept by default rather than silently
   dropped.
3. ~~**The map screen is behind the draft**, reachable if the overlay is ever dismissed.~~
   **Done — `DuelMapLockPatch`, confirmed in play 2026-08-17**, and the confirmation is the
   interesting part. The patch logged nothing at all on the first attempt, which reads identically
   to a guard that is not applying; a button that does nothing and a guard that refuses it are
   indistinguishable from outside. It now traces every player-initiated open, and a later run caught
   `top-bar open reached NMapScreen.Open` immediately followed by `refused to open (duel=True
   draft=True topBar=True)` — the button reaching the guard and being stopped by it. The run's own
   start-up open is refused too (`draft=True topBar=False`), which is what makes a draft load
   straight into the campfire backdrop rather than flashing the map.

### The original open questions, kept for the reasoning

1. **Deck size and pool size** — "10-15 shown" is the pool; how many picks each, and is the pool
   refreshed between picks or drafted to exhaustion?
2. **Shared pool or one each?** A shared pool makes each pick a denial as well as a gain, which is
   most of what makes drafting interesting; separate pools are simpler and fairer to explain.
3. **What you start with**: basic Strikes and Defends as a floor, a starting relic, HP, energy. A
   drafted deck with no floor can be unplayable in a way a random one cannot.
4. **Are relics and potions drafted too**, or cards only?
5. **Does the mirror restriction stay?** Same character both sides makes the pool obviously fair and
   sidesteps the per-character filtering that made Neow's offers differ; different characters would
   need a fairness answer first.
6. **Lobby shape**: a third turn-model-like modifier, or its own entry beside Duel on the host menu?
   M7's `DuelLobbyPanel` re-dresses `NCustomRunScreen` and would have to learn a mode with no race
   clock at all.

~~**Not scheduled.**~~ **Scheduled 2026-08-14 and started.** The scaffolding is in and inert; the
six steps above are the build. The reuse argument still holds and is the reason this is worth doing
next: it is the mod's own core with its riskiest phase deleted.

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
  - **The race bank and the duel bank are two separate settings.** *Decided 2026-08-05;
    **implemented 2026-08-05**.* One shared number was wrong: playing a whole act needs far
    more time than one duel does, so a single bank either rushes the race or makes the duel
    interminable. A match is configured as, say, **a 10-minute race followed by a 2-minute
    duel**.
    - Two modifier groups in the lobby, not one: `Race Clock: 1/10/15/20/Off` and
      `Duel Clock: 1/2/3/5/Off`. Three exclusivity groups in total with the turn model. The
      1-minute entries are there to make flagging reachable in a single test run; they sit in
      the real list rather than behind a dev flag because they are legitimate time controls.
    - Either bank may be 0 independently, so half a match can be untimed. The top bar shows
      *nothing* during an untimed phase rather than a frozen `0:00`, which would read as a
      broken clock.
    - The swap happens in `DuelClockService.GrantDuelBankIfEntered`, off the phase flip both
      clients already share (`DuelRendezvous` → `DuelSession.ActivateDuel`), so it needs no
      message of its own. The clocks are *refilled in place*, not replaced: `DuelFlag`
      subscribes to `DuelClock.Flagged` once at run launch and there is no second arming pass.
    - Mechanically the duel bank is a *fresh* bank granted at duel start, not a remainder — so
      arriving at the arena early no longer buys you duel time. That reverses the old "time
      spent racing is time you will not have in the duel" line above; the race clock now stands
      on its own as a deadline to reach the arena, and the duel is timed independently.
    - **Running the race clock out is a DRAW, not a loss.** *Corrected 2026-08-06 after
      playing it — an earlier draft of this line said "still a loss", which is not a thing the
      race clock can express.* Both race banks start together and never pause (they are a
      global countdown, see below), so they are equal by construction and empty in the same
      tick. Declaring a winner there reported nothing but which clock `DuelClockService`
      happened to tick first — the local one — so the host lost its own race every time. Nobody
      reached the arena, so nobody won: `DuelOutcome.Draw`, a `DRAW` banner, and
      `DuelResultMessage.reason = 2` so both clients agree from the host's one decision.
    - Open knob, deliberately not taken: a race timeout *could* be decided on progress instead
      (further into the act wins, `RaceProgress` already carries what that would need). That is
      a different game — it makes the deadline a race-to-the-front rather than a shared
      deadline — and wants play before code.
  - **Race is a global countdown; the duel is a chess clock** (settled 2026-08-05 after
    playing both). During the race both clocks run continuously and never pause — reach the
    arena before the bank empties. Pausing per-player was tried and is meaningless there: the
    players are in separate combats and never wait on each other. Time spent racing is time
    unavailable in the duel. In the duel they *do* wait on each other, so ending your turn
    stops your clock while theirs runs.
  - Host-authoritative in both phases. Sync carries a paused flag per clock so client-side
    prediction matches the owner rather than rubber-banding on each correction.
  - **Fischer increment: deferred indefinitely, 2026-08-05.** Not merely unbuilt — it is not
    clear it is the right call for this game, and separate race/duel banks may remove the need
    it was meant to answer. Revisit only if play shows a problem it solves.
- **Resigning and agreed draws** — **DECIDED and built 2026-08-06.** A match can end by consent,
  as in chess.
  - **Abandoning a PvP run is a resignation**: a loss for the abandoner, a win for the opponent.
    Previously it tore the run down and told the other player "the host abandoned the game",
    which is not a result and left no record of who won.
  - **Either player may offer a draw** from the pause menu; the opponent accepts or declines.
    Offers that cross on the wire count as agreement rather than as a conflict, and pressing
    Offer Draw while the opponent's offer is outstanding is an acceptance — "we both want a
    draw" should not require anyone to dismiss a prompt and find the button again.
  - **Resigning is legal during the race**, not only the duel. Conceding a race you cannot win
    is a real decision, and the alternative — walking to the arena in order to lose — is worse.
  - A resignation deliberately **does not disconnect**. That is what leaves both players on
    result screens with a live connection, which is what rematch will need.
- **Host advantage**: host resolves ~½ RTT faster. Options: accept it; input-delay
  equalization (delay host's own enqueues by measured RTT/2); alternate hosting across a
  match series. Defer; measure first.
- **First-strike depth** (model A only): pure arrival order, or small windup (0.5s) per card
  so a fast block can answer a seen attack? Playtest question.
- **Which turn model ships** — genuinely open, see §3.1b. Real-time blitz is built and gets
  playtested first; simultaneous turn-based is a supported alternative to be built and tried
  rather than a fallback. Decide from play, not from argument. If both hold up, ship both as
  a lobby option — they are different games and people will want different ones.
- **Co-op-only cards** — **DECIDED 2026-08-12: banned. A PvP run is offered singleplayer
  content.** No longer a knob. The engine's own `CardMultiplayerConstraint.MultiplayerOnly` set
  (Beacon Of Hope, Gang Up, Blade Symphony and some forty more) exists to be offered when there
  is an ally to point it at, and in a race there never is — so an ally-targeting card is a dead
  draft pick occupying a reward slot.
  - Implemented by `RaceNoCoopCardsPatch`, at the three places the engine decides this
    independently: `CardFactory.FilterForPlayerCount` (card rewards and shop stock),
    `CardPoolModel.GetUnlockedCards` (in-combat generation, Scroll Boxes, the character-cards
    modifier) and `MassiveScroll.IsAllowed` (the one relic whose *content* is co-op-only, so it
    has to not be offered rather than be filtered down to nothing).
  - **A generation-level ban is sufficient, confirmed by sweep 2026-08-12.** Nothing outside
    those paths introduces a `MultiplayerOnly` card: no event grants one by name and no starting
    deck holds one, so such a card cannot reach a PvP deck at all. That is why the duel needs no
    rule of its own about playing one — there is nothing to play.
  - The cause is the recurring one: the engine reads `Players.Count > 1` as "playing together".
    Here it fails as *content* rather than as a crash, which is why it survived so long — a run
    offering slightly wrong cards still looks like a run.
  - Deliberately still separate: **relics** whose effect involves an ally (Booming Conch) during
    the duel itself. That is a balance question about something already owned rather than about
    what the race offers; parked in `docs/PLAYTEST_LIST.md`.
- **Potions, powers that reference "monsters"**: audit pass in M2 for mechanics that
  hard-reference `MonsterModel` (e.g. on-kill effects, `ContainsMonster<T>`); most Creature-
  level mechanics are fine.
- **HP carryover** — **DECIDED 2026-08-05: no heal. The duel starts at race-end HP.** Damage you
  took from the Act 1 boss is damage you bring to the duel. This is what the code already does
  (`DuelArena` never heals), so the decision closes the knob rather than opening work. It is also
  what makes the race a real risk/reward: rushing the boss on low HP is a choice you pay for in
  the fight that decides the match. No longer a knob.

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
    **ANSWERED 2026-08-06, and the answer is the second one.** It logs the error, routinely,
    and during the race that is harmless — the held traffic is the opponent's own race actions,
    which `RaceIgnoreRemoteActionsPatch` discards anyway. The real finding is what happens at
    the *end* of the race: the two clients entered the duel arena at their own map coords, so
    the buffer went on holding every host `ActionEnqueuedMessage` through the duel and froze the
    client mid-turn. Fixed by `DuelArena.MoveRunToArenaCoord`, which moves both runs to the
    arena node's coord before the room is built. Full write-up in HANDOFF. The research note
    below — "in a race over a shared map both players visit the same coords, so buffering should
    be transient" — is true of the race and **false of the arena**, which nothing was travelling
    to.
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
