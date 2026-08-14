# Handoff — state of the mod as of 2026-08-12

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
| **M6** full loop | **done, playtested through 2026-08-11.** Lobby modifiers → race → arena node → rendezvous → deck review → duel → result screen, with checksums live, split race/duel clocks and Neow intact. Plus resignation and agreed draws. Result-screen stats and badges reach the screen and compare correctly. The 2026-08-11 sweep closed the race phase's remaining rough edges — rest site, treasure chest, shop, map portraits, the loser's result screen and opponent summons. Remaining: rematch |
| **M7** | **done, playtested 2026-08-11.** A **Duel** entry beside Standard/Daily/Custom opens a lobby retitled "Duel": Blitz/Rapid/No-clock presets, then the three real decisions as headed rows of chips, then the other custom-run modifiers behind a collapsed caret. Re-dresses `NCustomRunScreen` rather than replacing it |
| **Disconnects** | **done, playtested 2026-08-12.** A dropped opponent no longer evaporates a match: whoever remains is shown "Opponent disconnected — the match is yours in 5…" and wins. Four routes, all tested — an announced quit, heartbeat silence, our own link dying, and a deliberate leave. Rejoining is a separate milestone and deliberately not built |
| **Shipping** | **done.** `git clone && dotnet build` is a complete install — the `.pck` is committed, so no Godot is needed. README has a step-by-step for a non-technical Windows player. Debug builds stamp the git commit into the mod version, so the engine's mod-match gate enforces "same build" rather than us asking. Coexistence verified with a Workshop mod (RegentFX): patches clean on both clients and VFX rendering in a duel. **The `.pck` is committed, so no Godot is needed to build — only to re-export after changing something under `SpirePvp/`, and the exported pack must then be committed too** |

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
**80 as of this handoff** (118 methods). 69/107 was confirmed against a live log on 2026-08-12.
Since then `DuelModifierMinimumPatch` added one of each, and the AoE fix retired `DuelAoeProbePatch`
and added `DuelAoeTargetingPatch` and `DuelHookListenerScopePatch` — so **71/109 is arithmetic and
has not been seen in a log yet**. The count is per *class*, not per patch: a class holding
several patch methods still counts once, so grouping patches by concern does not move it.

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

**One line of that survives on purpose, and it is not the same bug.** Measured on an agreed draw
2026-08-12: **exactly one** `Received message of type SpirePvp.Net.ClockSyncMessage, but no
message handlers are registered` on the client, none on the host — down from 46. It is a packet
already *in flight* when the result was declared: `DuelResult.Declare` stops the clocks before
anything else, so no sync is sent after the match ends, but a sync sent a fraction of a second
earlier still lands after the receiver has torn its run down. No sender-side guard can close that
window, because at send time the message was correct.

The receive-side fix — keeping the `ClockSyncMessage` handler armed past run teardown, since the
connection deliberately outlives the run — was considered and **rejected**: handler arming and
release is the single thing that has bitten this project most (five times), and trading a
documented one-line residue for a change in that lifecycle is a bad bet. If this ever grows past
one line per match, that is a real regression and worth reopening; one line is the expected cost.

**ENet does not report a dropped peer, so "the opponent left" is not an event you can wait
for.** `ENetHost.Update` answers the transport's own `Disconnect` event with a bare `continue`.
Only an application-level `Disconnection` packet — a polite quit through the menus — is reported;
a killed process or a dead link is silent forever. The Steam transport does report drops, so this
bites exactly where all the testing happens. Anything that must notice an absent peer has to
measure it: `ConnectionStats.LastReceivedTime` is the signal vanilla itself uses, and
`DuelDisconnect` acts on 30 seconds of silence.

**Resetting a counter does not clear what is stored under the old ids.** FOUND AND FIXED
2026-08-12, unplaytested at time of writing, and it is the first *desync* this project has shipped
into a real match. `RaceCoordinator.ResetSynchronizerCounters` zeroes the choice / reward / action
/ hook counters at the phase flip because the race pulls them apart. But `PlayerChoiceSynchronizer`
also keeps `_receivedChoices` — peer choices that arrived with nobody waiting for them — and
matches them later by `(choiceId, senderId)` **alone**. `FastForwardChoiceIds` touches only the
counter, so those entries survive with ids the duel is about to hand out again from 0.

The race fills that list by construction: **a card reward pick travels as a player choice**, so
every reward either player takes is broadcast, stored by the peer, and never consumed. Measured:
the client took two rewards during the race (host stored choices `0 = indexes 1`, `1 = indexes 2`);
in the duel the client played **Photon Cut**, whose `OnPlay` ends in `CardSelectCmd.FromHand`; the
host reserved choice id 0 for the client and found the race's choice 0 already there —

```
Was going to wait for remote choice 0 for player 1001 but we've already received it
Finished waiting for remote choice 0 for player 1001: PlayerChoiceResult indexes 1
System.InvalidOperationException: Tried to get combat cards from player choice result of type Index!
```

— so the host's put-back never happened, the client's did, and the very next checksum diverged
(`Local: 1514961121. Remote: 2932920909`). `RaceCoordinator.ClearStaleReceivedChoices` now empties
the list alongside the counter reset.

**What made it look random:** it needs a duel card that *gathers a player choice*, and only the
peer's choices are stored this way — you consume your own locally. So it fires on the first
choosing card the client plays and never on the host's. Sweep the siblings when touching this:
`RewardsSetSynchronizer` has the identical shape (`bufferedMessages`, `completedRewards`, both
keyed by an id `FastForwardRewardIds` resets without clearing) and is latent only because a race's
reward picks travel as *choices* rather than reward messages — measured zero
`RewardsSetSynchronizer` traffic across a whole race.

**Zeroing a counter that keys a *shared ordered stream* is not the same as zeroing a per-player
one, and only one of the four is shared.** FOUND 2026-08-12, in the very next playtest after the
fix above — the stale-choice fix worked (`dropped 1 stale peer choice(s)`, both sides) and a
*different* divergence followed. Choice, reward and hook counters are bumped locally by their
owner; `ActionQueueSet._nextActionId` numbers the queue both peers execute in common. Resetting it
is only safe if both peers reset at the **same position in that stream**, and
`ResetSynchronizerCounters` runs wherever each client happens to reach the phase flip.

Reproduced by both players typing `duel now`, which is networked and therefore travels *as a
`ConsoleCmdGameAction` in that queue*. The host consumed its own copy as action id 0 and then
reset, so its next action was id 0 again; the client had already entered the arena and reset by the
time the host's copy arrived, so that copy *became* the client's id 0 and every later id was off by
one. The dumps said it outright — `Last executed action ID: 1` against `3`, with the client three
cards ahead — and the client's own log named the culprit: an `ActionEnqueuedMessage … duel now`
with `Source Location: act 0 coord (3, 0) room 0`, the *pre-arena* coord, arriving after the reset.

**Do not try to fix this inside the command.** Making `duel now` host-only was tried and reverted
the same session, and the reason generalises to every networked console command: **the action is
enqueued and consumes its id before `Process` runs**, so refusing inside the command removes
nothing from the stream — it leaves an action that still takes an id and now also does nothing.
Measured: the host's `duel now` reached the client, the guard refused it there, and the client
never entered the arena at all (`entering duel arena` on the host, absent on the client) while the
divergence it was meant to prevent was untouched. Worse on both counts.

The client executing the host's `duel now` **is** the mechanism by which the client enters the
arena, so anything that blocks it breaks the shortcut entirely. The mitigation is operational:
**exactly one player types `duel now`.** Two people typing it puts two actions in the queue, and
that is what desyncs.

**It bit again on 2026-08-12**, during the first turn-based playtest, in exactly the same shape:
client `id 0 duel now (1001)`, then `id 0 duel now (1)` arriving after its reset, so its first play
took id 1 while the host's took id 0 — `Last executed action ID: 0` against `1`, and *nothing else
in either dump differed*. Two hits in one day is enough evidence that "exactly one player types it"
does not survive contact with actual testing, because `duel now` is precisely what you reach for
when you want to be in the arena quickly.

**So the operational mitigation is not enough, and the real fix should move up.** Do not reset
`ActionQueueSet._nextActionId` on both sides independently; have the **host broadcast the value and
the client adopt it**, which is immune to where in the stream each peer happens to be. That is one
message and it retires this whole family.

**The underlying hazard is not the command.** Any traffic in flight across the phase flip is
accounted for differently on the two peers. The real rendezvous starts the duel from a
`DuelStartMessage`, a mod message that bypasses the action queue entirely, so nothing is being
ordered against it — which is why the flow has survived many playtests. If a divergence ever
appears at the flip *without* a console command in the log, this is the first thing to suspect, and
the fix would be to reconcile the action id host-authoritatively rather than by each side zeroing
independently.

**CLOSED 2026-08-12, and it took a third divergence to see the shape of it.** The hazard was not
the counters and not the command: **the shortcut flipped the phase locally, so each client flipped
at its own point in the message stream.** The host typed `duel now` while the client was still
taking its Act 1 card rewards; the client's reward traffic reached the host *after* the host had
entered the arena, reset its counters and turned race mode off. The host then replayed that race
work under duel rules — consuming choice ids 0 and 1 and reward id 0, and generating a reward set
from a different RNG, so it handed player 1001 Predator and Capacitor where the client held Shrug
It Off and Acrobatics. The dumps differed in **exactly three lines**:

```
Choice IDs: 0,2          vs   0,0
Reward IDs: 0,1          vs   0,0
RNG counter Niche: 7     vs   1
```

Everything else — every card, pile, HP, creature — matched, because the pre-combat state sync
covers those and covers none of these.

**Why the rendezvous is immune, which is the part worth keeping:** both players announce arrival
and the flip happens once both announcements are in hand. The transport is reliable and ordered per
direction, so anything a player sent during the race necessarily arrives *before* their arrival
message — waiting for that message is therefore waiting for their whole race to be applied. It is
not a timing margin; it is an ordering guarantee, which is why the real flow has never produced
this and the shortcut around it now has twice.

So `duel now` and `duel start` both go through `DuelRendezvous.ArriveLocal` now. `duel now` still
skips the *reading* — the review opens and confirms itself via `DuelEntry.AutoConfirm`, which is
local and not on the wire, so one player skipping does not drag the other past a screen they were
reading. **The shortcut is no longer a second path into the duel**, which is the actual fix; it was
never really about the counters.

**A desync is not a disconnect, and treating it as one put VICTORY on both screens.** Same match,
2026-08-12. The divergence made the host eject the client (`Disconnecting client 1001, reason:
StateDivergence`), and both sides then independently declared a win by disconnect — the host
reading its *own* kick as the client walking away, the client reading its ejection as the host
vanishing. Both were shown *"Their connection gave out."* over a victory banner.

`DuelEndReason.Desync` now voids the match as a **draw**. A desync means the two games disagree
about the board, so neither client's state is evidence of who was ahead; there is no winner to
name. Note this is the one disconnect route where both sides can agree *without talking*, because
the reason code is symmetric and each is told it — which is what makes deciding locally safe here
and not in the general partition case `DuelDisconnect.Declare` documents. The host needs a patch to
see it at all: `RunLobby.OnDisconnectedFromClientAsHost` has the `NetErrorInfo`, logs it, and then
raises `RemotePlayerDisconnected` carrying only the player id.

**The paced model hid its own backlog from the scheduler, so slice 2 could never fire. FOUND AND
FIXED 2026-08-12, unplayed.** Reported three sessions running as "the client is waiting behind the
host", and the third report is the one that landed it: *"the host still queued cards up, client had
to sit there as 2 strikes went thru before its first defend play went through"* — which is the
identical "got stiffed" case slice 2 was written for and had been recorded as fixed.

`TickTurnModel` held a burst in its own list and released one card per 400ms cooldown, so
`DuelPlayScheduler` only ever saw the trickle. Measured over a whole duel: **22 bookings, every one
`pending (1 waiting)`, every one `#0`.** Three cards fired in a second were booked as three separate
`#0`s, seconds apart, each released into an idle executor before the opponent's play arrived — so
the opponent's first card queued behind all three. **The per-player index rule was structurally
unable to fire**, because the backlog it exists to compare against was in the other class.

Plays now go to the pool as they are clicked, so a burst is `#0, #1, #2` and the opponent's first
card is a `#0` that beats `#1` and `#2`. The cooldown is **deleted, not moved**: the mode's pacing is
the one-card-in-flight rule plus `BeatSeconds`, and all the gate really did was order plays by wall
clock, which is the single thing this slice exists to stop. `_queued` stays as the *in-flight* list,
because energy reservation and the queued-card highlight need to know what you have committed and
not yet seen resolve.

Two traps handled while doing it, both worth keeping:

- **`OnActionResolved` cannot match on identity.** A client's play is submitted as a request and
  comes back as the host's ordered copy, so the object that executes is not the object that was
  listed — the same mismatch `DuelPlanQueuePatch` handles for the queue view. Matching the card
  model survives the round trip; identity alone would leak every client-side reservation until the
  turn rolled, i.e. would re-introduce the dead-hand bug on the client only.
- **A cancelled play never reports back at all**, since the executor skips it before firing either
  event. Those stay reserved until `OnTurnStarted` clears the list, which bounds the cost to one
  turn and errs toward reserving energy you have already spent rather than letting you spend it
  twice.

**And a methodology note that is true but was very nearly used to explain this away.** One person
driving two windows plays the host's cards, alt-tabs, then plays the client's — both logs show it
(`MuteInBackground: FocusOut`/`FocusIn` alternating all duel, plays in per-seat bursts: host `:57
:57 :58`, client `:59 :00`). So a **simultaneous** contest genuinely cannot be produced solo, and
the tie-break has still never executed once. But *"I queue three, then play one on the other seat"*
needs no simultaneity, is reproducible solo, and was a real bug — the rig explained the empty pool
and explained nothing about the symptom. **When the test rig accounts for your evidence but not for
what the player is describing, believe the player.**

**The scheduler's own lines are the check**: `pending (N waiting)` with N ≥ 2 is a real contest, and
a release reading `[tie …]` is the tie-break actually running. Before this fix, neither had ever
appeared in any log.

**Test on the same path, not divergent ones.** The two runs share a seed and therefore a map,
and `RunLocationTargetedMessageBuffer` gates on **location, not identity** — so two players
standing on the same coord deliver every message to each other. Divergent-path testing hides an
entire family of bugs: a hundred local runs missed what the first real two-player session found
in an hour, because two people given the same map naturally walk the same obvious route. The
campfire break, the reward errors and the event leak were all this. When something works locally
and breaks in a real match, ask whether your test ever put both players on one coord.

**A message that only fires on *change* cannot carry initial state.** The peer that arrives
late gets its state some other way, so hook the arrival too. Four instances now: the duel
handshake, race progress, the decklist reveal, and most recently the joining client showing the
plain Custom lobby — the host applied the preset before anyone was connected, so
`LobbyModifiersChangedMessage` had nothing left to announce and the client's opening state came
in its `ClientLobbyJoinResponseMessage` instead. Same family as arming handlers at run start
rather than on first local use. The diagnostic is an *absent* log line, so check that the
message you are relying on actually arrived before assuming the handler is wrong.

**If any patch class fails, duelling refuses to start.** `SpirePvpInit.PatchesHealthy` gates
both the Duel menu entry (which locks, showing `DUEL_MP.LOCKED.description`) and
`DuelMatch.OnRunCreated` (which bails, leaving the run as ordinary co-op). Every other kind of
mod degrades gracefully with a patch missing; this one arbitrates a two-player game, so a hole
in it is a hang or a desync that reads as a gameplay bug. The run is deliberately not torn down
— refusing to arbitrate is the safe failure.

**Patch targets are `nameof`, not strings.** A game update that moves one is then a build error
naming the method, on the machine that pulled it, rather than a runtime `PATCH FAILED` and a
mod running with a hole. Publicizer makes even private members work. The one exception is
`Neow.GenerateInitialOptions`, which is virtual and so not publicized — the only target that can
still fail at runtime.

**Ask the condition you mean, not one that happens to correlate.** `DuelClockService` learned
this the hard way: its top bar keyed the one-clock/two-clock choice on the *phase*, and a race
timeout reaches `Complete` without ever passing through `DuelActive` — so it reported two duel
clocks for a duel nobody played. It was fixed there to ask whether the duel bank had been
granted. **The same test survived in `DuelFlag`**, in the branch that decides whether an expiry
is a draw or a loss — the identical trap, one file over, deciding a result rather than a label.
Both now ask `DuelClockService.DuelBankGranted`. When you fix a wrong predicate, grep for it.

**The duel needs both clients at the same `RunLocation`, and nothing was establishing that.**
FOUND AND FIXED 2026-08-06, unplaytested at time of writing. Symptom: **the host plays the duel
normally while the client is frozen — cards hang in mid-air, the end-turn button clicks but the
turn never ends.**

Every `ActionEnqueuedMessage` is an `IRunLocationTargetedMessage` tagged with the sender's
location, and `RunLocationTargetedMessageBuffer` holds any message for a location the receiver has
never visited. The duel is host-arbitrated: the client *requests* a play and the host broadcasts
the ordering. Two different locations therefore buffer every arbitration message bound for the
client, forever — while the host, which arbitrates for itself, notices nothing. Measured: 28 stuck
messages, host at `coord (1, 12) room 1`, client at `coord (3, 0) room 1`.

The arena is a real map node, but nothing ever travelled to it: `DuelRendezvous` deliberately does
not enter the node on click — it announces arrival and waits, which is what makes it a rendezvous.
So each client entered the arena room while still standing wherever it happened to be. **It
survived every previous playtest because both players had walked to the Act 1 boss, and all paths
converge there** — so the coords matched by accident. `travel` breaks the accident instantly,
which is why a dev shortcut exposed what months of real play did not.

The trap, again: *"both players walked to the boss" is a correlate; the condition is "both clients
are at the same RunLocation."* `DuelArena.MoveRunToArenaCoord` now moves both to the arena's own
coord — the one coord they agree on by definition. Note it runs **before the `CombatRoom` is
constructed**: `RunLocation` is (coord, room id), `AbstractRoom` takes its Id from `NextRoomId` in
its constructor, and `AddVisitedMapCoord` is what resets that counter — do it afterwards and you
fix half of `RunLocation` and leave the other half divergent.

**Read that error rather than scrolling past it.** `RunLocationTargetedMessageBuffer` logs `there
are still N messages for other locations` on every transition, and the per-message `enqueueing it
because we are currently at location ...` lines name both locations outright. It is noisy and
harmless during the race — that traffic is the opponent's own race actions, which
`RaceIgnoreRemoteActionsPatch` discards — so it reads as background. Once the phase flips to the
duel it is a hard failure. DESIGN §I3 predicted exactly this signal and called it "useful signal
during the spike".

**The result screen reads a *run*, and a duel is not one.** Three separate places had to be told
so, all found in one playtest on 2026-08-06 and all fixed:

- **The death line named the wrong killer.** A duel loss read *"The Silent was absorbed by a
  Skulking Colony"* — an elite from the race, already beaten. `DuelResultBannerPatch` was setting
  `_deathQuote.Text`, but `InitializeBannerAndQuote` also stashes `_encounterQuote`, and
  `AnimateInQuote` fades our text out a second later and writes that one in its place. Set both.
- **A win still reported damage to the Architect** (`_victoryDamageLabel`, from `StatsManager` and
  the run score) — a boss the run never fought. Blanked.
- **Elites defeated read 0 after killing an elite**, and the run-history breakdown named that
  elite as the cause of death. Both because `DuelArena` never called `AppendToMapPointHistory`, so
  the last room the run recorded was the last room of the *race* — and
  `ScoreUtility.GetElitesKilledCount` subtracts the final room when it is an elite, on the
  assumption that is what killed you. Now recorded, like every other room.

**The winner's line and the loser's line survive the Continue button differently, and vanilla is
right to do it.** Reported 2026-08-12: on the *summary* screen the defeat line showed and the
victory line did not. `NGameOverScreen.OpenSummaryScreen` opens with
`_victoryDamageLabel.Visible = false` and then runs `AnimateInQuote`, which on a win tweens that
same label's `modulate:a` and `visible_ratio` — so the animation plays on a hidden node. The loss
branch tweens `_deathQuote`, which nobody hid.

For vanilla that is correct: `_victoryDamageLabel` is a full-screen block of Architect prose that
would lie across the summary. For a duel it holds our one-line epitaph, reparented into the banner,
so it should keep the same place the loser's line keeps. `DuelResultBannerPatch.AfterOpenSummaryScreen`
re-shows it. **Note this is the third distinct thing that had to be told the winner's line lives in
a different label** — set it, place it, and now keep it visible — which is the cost of borrowing
the only label that animates in on a win.

**The opponent's mouse cursor on the result screen is a FEATURE. Do not suppress it.** Decided by
Lucas 2026-08-12, after seeing it in play: with both players sitting on a result screen and no chat,
watching the other cursor drift toward Rematch is the only way to read their intent, and it arrived
for free. It is the one place in the mod where a co-op presence surface is *wanted*.

This is worth writing down because the standing rule points the other way. DESIGN §I6 says to
suppress hover and pointer leaks **at the broadcast, not at the display**, precisely so that a
surface added later cannot quietly reveal something — and `HoverSuppressionPatch` exists to do it.
A future pass that tightens that rule would kill this without anyone noticing it had been a
decision. If pointer broadcasting is ever gated, gate it on the *duel* rather than on the run, and
leave `DuelPhase.Complete` alone.

The rematch vote marker (`DuelRematchPatch`) makes the same intent explicit rather than replacing
it — the opponent's character icon appears over the Rematch button once they have offered.

**No BBCode in score lines.** `[gold]…[/gold]` was drawn literally, tags and all, across every
result line: `NScoreLine` puts its text in a `MegaLabel`, which is a plain Godot `Label`. The
other labels on that same screen — `_deathQuote`, `_victoryDamageLabel` — are `MegaRichTextLabel`
and *do* take markup, which is exactly what makes it easy to get wrong.

**The opponent's decklist on the entry screen was stale, and stale is worse than absent.** It
showed their deck as of the *start* of the race, missing every card they had picked up — cards
they then played in the duel, in front of you, having never appeared in the reveal. The race
decouples the two runs, so your copy of their `Player` stops updating; the pre-combat state sync
does fix it, but that runs on arena entry, **after** the deck review. The decklist reveal is a
core information rule (DESIGN §1), so a quietly wrong one undermines the thing it exists for.
`DuelArrivedMessage` now carries the sender's deck as `List<SerializableCard>`, and
`DuelRendezvous` rebuilds it with `CardModel.FromSerializable`. Carried on *arrival* rather than
in a message of its own precisely so the ordering is free — the review opens once both arrivals
are in hand, so the deck is always there, with no second handler to arm and no race to lose.

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
window gets its own Space, so you cannot see both clients at once. `./scripts/host.sh` then
`./scripts/client.sh`, and `./scripts/check-log.sh --errors` afterwards. Details and the
points-vs-backing-pixels trap in `docs/MAC_SETUP.md`.

**Both launchers open the title screen and stop there, deliberately (changed 2026-08-12).**
They used to pass `--fastmp`, and that flag does two things where only one was wanted: it
auto-clicks into a lobby, and it *keeps doing so*. Returning to the main menu after a run
rebuilds it, which re-runs the auto-navigation — so the host is shoved back into a lobby the
instant a match ends, and the client re-attempts a join against a host that has gone, times out,
and raises vanilla's own malformed popup (`Invalid net error passed to NErrorPopup:
ConnectionFailureReason None`). Both were reported as the mod mishandling the end of a match;
both were the flag, and the args line on line 66 of each log is what settled it.

M7 also removed the reason to shortcut in: a match is configured through the **Duel** entry on
the multiplayer host menu, so the route is title screen → Multiplayer → Host → Duel. Opt back in
with `--custom`/`-Custom` (plain Custom lobby), `--fast`/`-Fast` (standard host) or
`--join`/`-Join` on the client. `--setup`/`-Setup` still parse and now do nothing, since what
they asked for is the default.

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
(the plain Custom lobby), `-Fast` (standard host), `-Setup` (now a no-op),
`-Fullscreen`, `-Width <px>`, `-ClientId <n>`. Verify the run with `.\scripts\check-log.ps1`.

Both launchers **rotate the log** rather than truncating it, keeping the last five runs as
`logs/host.<timestamp>.log`. `--log-file` truncates on open, and losing the previous run cost a
real investigation on 2026-08-06 — the host's half of the run being diagnosed was already gone.

### Testing while playing a normal Steam game (fixed 2026-08-13)

**You can run the two-client rig and a Steam game at the same time.** Only one thing ever
prevented it, and it was the launcher rather than the game: `host.ps1` ran
`Get-Process SlayTheSpire2` and killed everything it found, because a running instance holds an
open handle on the installed mod DLL and the post-build copy then fails with a dozen lines of
MSBuild retry noise that reads like a compile error. That kill also took down a real match Lucas
was in the middle of with a friend.

`Get-Sts2Process` now splits the instances by command line: **`--force-steam=off` is the
discriminator**, and it is reliable in both directions — a dev client cannot run without it (a
direct launch otherwise fails `SteamAPI_Init` with "No appID found" and quits), and a Steam launch
never passes it. `host.ps1` stops only its own; `stop.ps1` does the same and takes `-All` for the
old behaviour. A CIM query that fails, or a process whose command line cannot be read, counts as
**foreign and is left alone** — the safe direction, since the cost of not killing is a build error
naming the locked DLL, against the cost of ending someone's match.

**Nothing else collides, and this was checked rather than assumed:**

- **The profiles are separate directory trees.** A Steam launch uses
  `%APPDATA%\SlayTheSpire2\steam\<steamid64>\`; the dev clients use
  `%APPDATA%\SlayTheSpire2\default\1\` and `…\1001\`. `Set-Sts2DevProfile` writes only under
  `default\`, so the windowing and mod-consent edits cannot reach the Steam profile.
- **The DLL lock is already moot on this machine.** Mod enablement is per profile, and SpirePvp is
  `"is_enabled": false` on the Steam profile (four Workshop mods are on there). `RemoveDisabledMods`
  marks it `ModLoadState.Disabled` and the loader returns **before** `LoadFromAssemblyPath` and
  before `LoadResourcePack` — so a Steam instance opens neither the DLL nor the `.pck`, and a build
  during one succeeds. Keep it disabled there: it is required anyway to play with an unmodded
  friend, since `JoinFlow` refuses a mod mismatch outright.
- **Transports do not meet.** The dev clients are ENet on `127.0.0.1:33771`; a Steam game uses the
  Steam transport, and `--force-steam=off` skips Steamworks entirely, which is also what sidesteps
  the one-instance-per-account limit.
- **Logs do not interleave.** The dev clients write `logs/host.log` and `logs/client.log` via
  `--log-file`; a Steam instance writes `%APPDATA%\SlayTheSpire2\logs\godot.log`. `Move-Sts2Log`
  rotates only the path it is handed, and `check-log.ps1` reads the shared log without writing it.

**The one case that still cannot work:** playing SpirePvp *itself* with someone over Steam while
rebuilding. Then the mod is enabled on that profile, the DLL and pack are open, and the post-build
copy fails on the lock — `host.ps1` now says so before MSBuild does. Use `-NoBuild`, or finish the
Steam match first.

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

**`dotnet build` copies the committed pack over a freshly exported one.** The csproj copies
`SpirePvp.pck` from the repo into the mods folder on every build — which is what makes
`git clone && dotnet build` a complete install, and which also means a build run *after* an
export silently reverts the mods folder to the committed pack. Hit 2026-08-12, and the fresh
export was gone before it was noticed. Two things keep it survivable, both worth knowing:

- `host.ps1`/`host.sh` build *first* and re-export *after*, and MSBuild's `Copy` preserves the
  source's timestamp — so the copied pack keeps the committed one's old mtime, the "is anything
  under `SpirePvp/` newer" test still fires, and the launchers self-correct.
- Nothing else does. A bare `dotnet build` followed by a manual launch will run the committed
  pack whatever you exported a moment ago.

**So after changing anything under `SpirePvp/`, copy the exported pack back into the repo and
commit it** — otherwise the working tree is right, the game is right until the next build, and
the thing anyone else clones is neither.

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
| `duel now` | Skips the entry *screen*, not the rendezvous — it announces arrival like the map node does and the review confirms itself, so both players still have to be at the arena before anything flips. Entering locally is what desynced a match on 2026-08-12. **Both players type it**, and the mod commands are now **local-only** (`IsNetworked = false`) so that is safe: a networked console command is enqueued into the shared stream, where each side assigns ids from its own counter, and the asymmetry desynced two sessions running. Do not make them networked again without reading `DuelConsoleCmd`'s comment. |
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
| `DuelStats` | Counts the duel, reconstructs the race half, and exchanges both with the peer. |
| `DuelBadges` | Which badges a match earned, decided by comparing the two players. |
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

### FIXED: the energy display, both halves (2026-08-13, UNPLAYED)

*"The most clunky part of the duel"* — costs not turning red until you tried a card, and the orb
reading full while your hand was already spoken for. Both are the same fact (a planned play has
committed energy it has not spent), and **the shared cause was neither of the things that looked
guilty.**

**The red costs were never broken.** `DuelPlanEnergyPatch` has raised `EnergyCostTooHigh` against
`Energy - ReservedEnergy` since 2026-08-12, and the route it depends on is real, verified against the
decompile: `NCard` asks `CardCostHelper.GetEnergyCostColor`, which asks `CardModel.CanPlay`, which is
what the patch postfixes. **Nothing ever asked it again.** A card re-evaluates its cost colour only
when something repaints it, and that something is `NPlayerHand.OnCombatStateChanged` — a *combat
state* event. Planning a play changes no combat state, because the energy is not spent until the play
executes. So the answer was computed once, when the card was dealt, and never revisited.

The orb had the identical gap: `NEnergyCounter.RefreshLabel` runs on the same event.

So `LockInPlanView.RefreshPlannedCosts` asks both to redraw whenever a play is planned and at each
turn boundary, and `DuelPlannedEnergyDisplayPatch` recomputes the orb from
`Energy - ReservedEnergy`.

**The orb is repainted, never re-energised.** The tempting fix is to lower `PlayerCombatState.Energy`
while planning and put it back — that is sim state, it raises `EnergyChanged`, every affordability
check in the engine reads it, and it is exactly the local mutation this project spent the day
removing from the arena heal. Presentation does not move the number the simulation runs on.

**All five reads, deliberately.** `RefreshLabel` keys the label text, font colour, outline colour,
orb material and `_layers` modulate on `Energy == 0` independently. Rewriting only the text would
render `0/3` in cream on a lit orb — a rendering fault rather than a rule, and worse than the honest
display it replaced.

**The general lesson, which is new and worth keeping:** a patch on a *pure question* — `CanPlay`,
`GetEnergyCostColor` — only shows up when something asks the question. Adding an answer is half the
work; the other half is finding what triggers the ask, and in a planning model the vanilla trigger is
usually an event that planning deliberately does not raise.

### Playtesting over Steam, with a real second player — what differs from the dev rig

Everything above was found on two local clients over ENet. A Steam session changes four things, and
the first is silent.

**1. Mod consent and enablement are per profile, and the Steam profile is not a dev profile.**
Checked 2026-08-13: `%APPDATA%\SlayTheSpire2\steam\<steamid64>\settings.save` had
`SpirePvp: is_enabled = false` — switched off so an unmodded friend could be played with, which is the
right thing to do and the wrong state to start a duel in. A disabled mod logs
`Skipping loading mod SpirePvp` and **loads nothing at all while looking entirely normal**. Turn it
back on from the Mods screen first, and confirm `80 patch classes applied cleanly (118 methods)` in
the log before trusting anything in the session.

**2. Both players' mod lists must match, or the join is refused outright.** `JoinFlow` compares
`PeerVersionInfo.gameplayAffectingMods` and answers a mismatch with `ConnectionFailureReason.
ModMismatch` — the "Mod mismatch!" popup. That profile also has **RitsuLib, BaseLib, MintySpire2 and
RegentFX** enabled from the Workshop; whatever is on has to be on for both. The simplest starting
position is SpirePvp alone on both machines. (Coexistence with RegentFX specifically was verified
earlier — patches clean and VFX rendering in a duel — so it is a known-good extra if wanted.)

**3. Both must build from the same commit.** Message ids are positional and debug builds stamp the git
commit into the mod version, so the engine's own gate enforces it rather than anyone having to
remember. `git pull && dotnet build` on both, from `origin/master`.

**4. The log is somewhere else, and there is only one of it.** The dev scripts write
`logs/host.log` and `logs/client.log` and rotate five runs; a Steam launch does neither. It writes
`%APPDATA%\SlayTheSpire2\logs\godot.log` — one shared file per machine, so **each player has half the
story and both halves are needed** for anything involving a divergence. Collect both right after the
session; `scripts/check-log.ps1` already reads that path as `SHARED`.

**One thing genuinely gets better, and it has never been exercised.** ENet does not report a dropped
peer — `ENetHost.Update` answers the transport's own `Disconnect` with a bare `continue` — so every
disconnect test so far has gone through `DuelDisconnect`'s 30-second silence measurement. **The Steam
transport does report drops.** So a Steam session is the first time the announced-disconnect path runs
for real, and it is worth deliberately having someone quit mid-duel to see it.

### START HERE — 2026-08-13 evening, pushed for playtesting

**Playtested and confirmed today**, in order: the arena heal on the wire; real-time paced (position
ordering, cross-player beat, contest window — *"felt great"*); turn-based end to end (*"working
great"*, which closes the lock-in gate inversion); the AoE actor (*"worked great"*, zero divergences,
zero unresolved reads, Bag of Marbles the relic actually tested); the opponent's relics in the deck
review; and the combat teardown (*"looks clean on death now"*).

**Unplayed, and this is what a playtest should cover:**

| What | How you know |
|---|---|
| **Opponent's incoming plays** (M8.5 slice 3) | Burst two or three cards on one seat: their titles appear over your opponent on the other seat *before* they resolve, and drop off one at a time. Partly seen working already — but the client half was dead, see below |
| **The loc-exception guard** | `grep -c LocException logs/client.log` → **0**. Nine of them killed the message on the client last run |
| **A planned potion** | Click a potion in turn-based: its belt slot greys and refuses a second click, instead of looking untouched |
| **The `.pck`** | If anything shows as a raw key (`SPIREPVP_INCOMING`), the pack did not load — it was re-exported and committed on 2026-08-13, so a fresh pull should be clean |

**The slice 3 failure is worth reading before anything else**, because it is a shape this project will
hit again: a missing loc key threw `LocException` **inside a net message handler**, so `NetMessageBus`
logged it and dropped the whole message. The feature was not merely uncaptioned on the client, it was
dead there — while the host, which had a freshly exported pack, was perfect. **Zero errors on one
side and nine on the other is the signature of pack staleness**, because `client.ps1` never
re-exports. The guard is the fix rather than the re-export, since a loc key and the code that reads
it ship in different files and only one of them is rebuilt.

**Patch count is 80 classes / 118 methods.**

**Deliberately not taken, so nobody assumes it was missed:** row 6 of the teardown audit
(`Hook.AfterCombatEnd`, the largest remaining omission — it is `async`, so including it changes what
the caller awaits, and it wants its own pass), and the both-die corner in `DuelRaceDeath`, which
needs a decision rather than code. Both are scoped below.

### The earlier state, kept for the reasoning

**Playtested and good today:** the arena heal on the wire, real-time paced (pacing, position
ordering, cross-player beat, contest window — *"felt great"*), and turn-based end to end (*"working
great"*), which closes the lock-in gate inversion. Details are in the sections below.

**THE AoE FIX IS PLAYTESTED AND GOOD** (2026-08-13, *"wow looks like it worked great, did a more
complex test and everything worked well"*). Confirmed from the log rather than the report:

- `71 patch classes applied cleanly (109 methods)` — that count seen in a live log for the first
  time.
- **Zero divergences on either client**, which was the failure mode to watch for: the actor is
  resolved inside sim code, so a disagreement would have shown as a checksum split rather than a
  wrong number on screen.
- **Zero unresolved AoE reads.** `DuelTelemetry.NoteUnresolvedAoe` fired not once, so every "all
  enemies" read the match produced found an actor — no silent fallbacks to vanilla's empty list.
- **Bag of Marbles was the relic actually tested** (`cmd relic BAG_OF_MARBLES` in the log), which is
  the exact report the whole thing came from.
- **Timeout death works**, tested in the same session.

**The opponent's relics reached the review too**, wire and screen both:
`DuelArrivedMessage { … hp = 80, maxHp = 80, deck = …, relics = … }`, then
`opponent 1001 arrived with 11 cards and 1 relic(s)`, then `duel entry — 1 opponent relic(s) drawn`.

**So `CombatState.HittableEnemies` is patchable after all**, under a condition this document should
now state precisely rather than leaving as the old blanket "unpatchable": the getter cannot invent an
answer, but it *can* be given one the simulation already defines identically on both peers. What made
it safe was never the getter — it was `DuelAoeActor` having a deterministic actor to name, and
falling back to vanilla's empty list when it has none.

### FOUND AND FIXED: a client's console command was held in the round buffer (2026-08-13)

Reported in two halves that turned out to be one bug: *"unable to grant myself potion on client"*,
then *"the fire potion appeared in the client AFTER both sides clicked end turn. Also the turn didn't
end even after both sides clicked."*

**`ConsoleCmdGameAction.ActionType` is `CombatPlayPhaseOnly` whenever the command is issued in
combat.** So a dev command is indistinguishable from a card play by type alone, and the host held it
in the round buffer exactly like one. That is both symptoms at once: the potion landed only when the
batch flushed, and the turn would not end because **a buffer holding a console command is never the
empty batch that ends a turn**.

**The guard already existed and was in one of the two places it was needed.**
`DuelTurnModel.IsPlayerInitiated` is an allow-list — `PlayCardAction`, `UsePotionAction`,
`DiscardPotionGameAction`, `EndPlayerTurnAction`, `UndoEndPlayerTurnAction` — and its comment already
named `ConsoleCmdGameAction` as deliberately excluded, having been caught in the play queue once
before. But it is applied in `DuelTurnModel.ShouldDefer`, which is the **local** path only. A client's
play travels as a `RequestEnqueueActionMessage` and arrives at `DuelLockInPatch`, which asked only
about `ActionType`.

**That asymmetry is exactly why the report looked like a client-only problem.** Issuing the command
on the host worked — the host's own commands take the local path and are filtered there. Only a
client's crossed the wire into the unguarded one. `DuelLockInPatch` now asks the same predicate.

**Fourth instance of one predicate living in two files and being fixed in one** (`DuelClockService`
and its duplicate in `DuelFlag`, then the phase test, now this). The rule stands and is worth
re-reading: *when you fix a predicate, grep for it.* The sweep this time was `CombatPlayPhaseOnly`
across `src/` — the two model implementations are reachable only through the guarded local entry, so
there were exactly two call sites and both are now covered.

**It was never specific to potions.** Any console command issued from the client during a duel was
being deferred — `damage`, `energy`, `card`, `power`. A previous note here concluded the potion was
refused for want of a free belt slot; that was wrong, and it was wrong because it reasoned from the
absence of a log line rather than from what the action *was*.

### CLOSED: the badge teardown guard is reachable and stays (2026-08-13)

Listed as "may be unreachable — find the route or drop it as unreachable", on the reasoning that
the Main Menu button does not appear until the badges have finished animating, so nothing can be
clicked during the window the guard protects.

**That enumerated the wrong exit.** `NGameOverScreen._Ready` wires `%ContinueButton` straight to
`OpenSummaryScreen`, and Continue is on the screen from the moment the result is shown — it is
visible in the 2026-08-13 screenshots, next to the banner, long before any badge has animated. So a
player who clicks Continue while the badges are still coming in leaves the screen mid-animation,
which is exactly the window.

It was never hypothetical either, which the note missed: the guard's own comment records the failure
as **measured** — `ObjectDisposedException: 'Godot.HBoxContainer'` out of `GetChildren`, reported as
"duel badges failed" and reading like broken badge logic rather than a screen that had simply gone.
Vanilla's own `AnimateScoreBar` throws the same exception on the same click.

**So the guard stays**, and this is the project's own rule arriving in a new costume: *guard on the
condition, not on each route out — there is always another route.* The note reasoned about the one
exit it happened to think of.

### Three items scoped 2026-08-13, each larger than its one-line description

Worked down the open list in order and stopped at the first line of each, because the queue file's
summaries understate all three. **Nothing below is built.** The findings are the value — each one is
the thing that would have been discovered two hours into building it.

**M8.5 slice 3 (opponent's unsubmitted queue) and the planned potion share one blocker.**
`NCardPlayQueue` has no by-model entry point: every public method keys on a `PlayCardAction`
(`OnLocalCardPlayed`, `RemoveCardFromQueueForCancellation`, `UpdateCardBeforeExecution`), and
`OnLocalCardPlayed` additionally gates on `model.Pile?.Type == PileType.Hand`. So drawing a play the
local client does not have an action for means **fabricating a `PlayCardAction` purely for
presentation** — and then suppressing the real one when the host's copy arrives, or the card is filed
twice. The suppression already exists (`DuelPlanQueuePatch` does exactly this for the local plan) and
should be reused rather than rebuilt. The potion is the same wall in miniature: `UsePotionAction` is
buffered like a card and the queue is a *card strip*, so it has nowhere to go.

The honest shape of slice 3 is therefore four parts, not one: a wire format for the pending pool
(host-authoritative, appended last — ids are positional), host broadcast on every pool change,
fabricated presentation entries on receipt, and suppression of the double-file. Plus the standing
rules: arm on run start, release in `OnRunEnded`, and a fabricated action must never reach the sim.

**The both-die corner in `DuelRaceDeath` cannot be fixed the way the queue file suggests.** "Copy
`DuelDrawPrompt`'s crossing shape" does not transfer, and the reason is ordering. Crossed *draw
offers* are reconciled before any result exists; crossed *deaths* are not. Each client's sequence is:
declare `_declared`, broadcast "the opponent wins", then `DeclareWinner(false)` — which sets
`DuelPhase.Complete`, runs `RunManager.OnEnded` and puts a DEFEAT screen up. The peer's mirror-image
message then arrives at a client whose `Declare` **returns early on `Complete`**. So both players
correctly see DEFEAT and there is no live path to change it.

Fixing it means one of two things, and both are decisions rather than code: **delay** the local
declaration by a short window so a crossing death can be reconciled first — which taxes every race
death for a case measured as rare — or allow a declared result to be **upgraded to a draw** after the
screen is up, which means re-running work `RunManager.OnEnded` has already done. Note the desync case
is *not* precedent: it is symmetric because the reason code is delivered to both sides before either
declares.

### OPEN: the corner brackets around the initiative holder (2026-08-13, marked not fixed)

**Deferred by Lucas — "it's fine, just mark it and let's come back to it."** Recorded now while the
evidence is fresh, because the next person will otherwise start from the wrong suspect.

**What it is:** four faint L-shaped corner marks framing a duelist, visible in a screenshot on the
*result* screen around the surviving character. **It accompanies the initiative arrow and moves with
it when turns end**, so it is part of the initiative indicator as a player experiences it.

**What it is not, and this is the part worth keeping.** It is not the mod's. SpirePvp constructs
exactly **two** nodes in the whole codebase — `LockInPlanView`'s `Polygon2D` arrow and
`DuelRematchPatch`'s vote-marker `TextureRect` — and the arrow is freed in `DuelResult.Declare` along
with its caption, which is a child of it. That the caption ("You move first") *is* gone while the
brackets remain is the proof: the two are one node tree, so whatever survives is not that tree.

**Also not a bug**, in the same screenshot: the gold pointer beside the fallen duelist is the
*opponent's mouse cursor*, which is a deliberate feature on the result screen — see the note above
about it being the only co-op presence surface that is wanted. Do not "fix" it.

**Best hypothesis:** vanilla combat framing left up by the same unraised `CombatEnded` (row 24 of the
teardown audit), i.e. the same root cause as the dim play area, of which only the `PlayContainer`
fade half has been taken. Something is presumably highlighting whichever creature the arrow is
parented to. The cheap next step is to find what draws a four-corner frame on an `NCreature` and
whether it keys on a child being added, on hover, or on a targeting state that never clears.

### FOUR FIXES TO VALIDATE TOGETHER (2026-08-13, all UNPLAYED)

Batched deliberately, and each has its **own** signal so a failure is still attributable to one of
them. Three are log-countable and one is visual.

| # | Fix | How you know it worked |
|---|---|---|
| 1 | **Initiative arrow off the result screen** (`DuelResult.Declare`) | Visual: "You move first" is gone once the banner is up. Reported after a timeout death |
| 2 | **The `NCard` double-frees** (`KillPendingQueueTweens`) | `grep -c "already been freed" logs/host.log` → **3 becomes 0** |
| 3 | **Rematch hotkey icon** (`DuelRematchPatch`) | `grep -c "Node not found" logs/host.log` → **4 becomes 0**, 2 per result screen per client |
| 4 | **Combat teardown audit** (`DuelEndCombatPatch`) | Visual: a hover tip no longer hangs over the result screen. The other added row (`PlayersTakingExtraTurn`) only shows across a **rematch** |

**The double-free was root-caused from the log, and the recorded suspicion was impossible.** It had
been blamed on `NCardPlayQueue.AnimOut` — which is reached only from `NCombatUi.OnCombatEnded`, a
`CombatManager.CombatEnded` subscriber, and `DuelEndCombatPatch` never raises that event. **`AnimOut`
cannot run in a duel at all.** The suspicion had named the method whose *description* matched the
symptom, in a file the patch had already cut off at the root. What the log actually shows is two
cancelled plays, two errors, and a `Callable.From` trampoline in the trace — a tween callback:
`TweenCardForCancellation` is a 0.5s fade ending in `TweenCallback(card.QueueFreeSafely)`, the result
screen goes up inside that half second, and the callback then returns an already-freed node.

**Row 24 of the teardown audit is still skipped, and the double-free fix does not unblock it.**
Raising `CombatEnded` would make `AnimOut` run for the **first** time rather than a second, so that
risk is untested rather than removed. It remains the prime suspect for *"the killing blow hangs in
mid-air"*, and it is the next thing to try — on its own, not in a batch.

**A pooling rule worth keeping, learned twice in one afternoon:** `QueueFreeSafely` hands an
`IPoolable` node back to `NodePool`. That is right for a node the pool issued and wrong for one this
mod built with `Duplicate()` — returning it puts a node in the pool that nothing ever took out. Use
plain `QueueFree` for anything the mod constructed. It is the same family as the double-free above.

**"You move first" survived onto the result screen** after a timeout death — reported in the same
session and **fixed, unplayed**. See `DuelResult.Declare`: the arrow is raised and cleared at *turn
start*, and a decided match has no next turn, so nothing took it down until `DuelMatch.OnRunEnded` —
a screen too late, because the run is still in progress while the result is up. Cleared in `Declare`
rather than in the death path, because "the duel is over" is the condition and death is one route to
it; a clock expiry, a resignation, either draw, a race death and a desync all reach a result with
nobody dying. That method is the single point they share, which is why the clocks and the stats
broadcast already live on it.

**Still on the branch, not taken:** `32e59a5` (audit of the combat teardown the duel skips, which is
the "killing blow hangs in mid-air" fix) and `3c43656` (the `NCard` double-free root cause). Both
unplayed, both deliberately left until the two above have been in a game — stacking four unplayed
changes makes a failure impossible to attribute.

### The morning's state, kept for the reasoning — 2026-08-13 midday

**Confirmed working in play today** (so do not re-test these): the initiative arrow no longer leaks
onto the map; the race timer shows on both clients with clocks on (which also retires the old "client
doesn't show the clock" report — it was vanilla's `NRunTimer`, and the mod's clock had simply never
been switched on in any run); **dying in the race now ends the match**; the arena heal fires **on
arrival, before the deck review**, at the right time; and the campfire cue plays.

**THE ONE OPEN THREAD IS CLOSED — cause found, fixed, and CONFIRMED IN PLAY 2026-08-13 afternoon.**
Host `arrived at 80/80`, client `arrived at 70/70`, each matching what the other had healed itself
to, and **zero divergences on either side**. The safety net still logged a reconcile, so it is doing
no harm but is no longer the thing holding the match together.

**The `hp`/`maxHp` fields were never on the wire.** `DuelMessages.cs` hand-writes every
`Serialize`/`Deserialize` pair, and the commit that added the two fields to `DuelArrivedMessage` —
declaring them, documenting them at length, and populating them at the send site — never added them
to either method. So the sender filled in a real number, the packet carried `modVersion` and `deck`
and nothing else, and the receiver read the struct's default. Both sides announced `hp=0/0` and
`ApplyOpponentHp`'s zero guard correctly refused it.

**The diagnostic worked exactly as intended and named the answer on the first run.** The three
candidates were (1) the fields not crossing, (2) a null `RunManager.Instance.State`, (3) the sender
missing from `State.Players`; the 11:40 logs said (1) on both machines, in one line each:

```
arena: opponent 1001 sent hp=0/0, which is unusable — their heal is NOT applied locally.
```

Worth noting *why* the earlier reasoning had ruled the send out. `ArriveLocal` calls
`DuelArenaRest.HealLocalDuelist(state)` and then reads `LocalContext.GetMe(state)?.Creature` two
lines later — the heal logged `healed 1 56 -> 70 / 70 on arrival`, so the same lookup had just
succeeded and `mine` could not have been null. That was correct, and it is why the failure had to be
below the send site rather than at it. The wire format was the one layer nobody had looked at,
because a field that compiles and a field that transmits look identical in the caller.

**The general rule, and it is new: a field on the struct is not a field on the wire.** These
serializers are hand-written, so adding a field is a two-place change and the compiler checks
neither half against the other. Nothing throws, nothing fails to build, and the only symptom is a
first-checksum divergence in a match that has already started. `DuelMessages.cs` now says so at the
site. **All 11 mod messages were swept for the same omission — `DuelArrivedMessage` was the only
one**, and the sweep is worth repeating whenever a message gains a field.

**The rule this cost two runs to learn, and it is not written anywhere else:** the pre-combat state
sync does **not** carry your own state to the peer — it fixes *your* copy of *them*. So any local
self-mutation before it is invisible on the opponent's machine forever, and the duel's first checksum
is what catches it. This is why the heal has to be *sent*, exactly as the decklist is.

**The safety net stays.** `DuelArenaRest.ReconcileAfterSync` runs after `WaitForSync`, over both
duelists, on both machines — the placement that provably agrees. It is **idempotent** (it assigns the
target a rest reaches rather than adding 30% again, and skips a duelist this machine already healed
on arrival), so with the send fixed it should now be a **no-op**, which is the point of having built
it that way. It kept the 11:40 match playable — `reconciled 1001 to 80 / 80 after the sync` on the
host, `reconciled 1 to 70 / 70` on the client — while the send was broken.

**The playtest, and it is short.** One match to the arena, either turn model, `duel now` from
**exactly one** player. In each log, at arena arrival:

- `arena: opponent N arrived at H/M after their rest` — the success line, which has **never once
  appeared**. Its absence is the bug; `sent hp=0/0` means the fix did not take.
- The host's `healed`/`arrived at` pair should agree with the client's, crossed over: whatever the
  host healed itself to is what the client reports receiving, and vice versa.
- `arena rest: reconciled …` should now be **absent** — the safety net finding nothing to do is the
  confirmation that the two machines already agreed.
- Then play one card and check for `State divergence detected!`. Checksum ID 0 is the one that
  caught this; getting past it is the result.

**Still unplayed and queued behind that:** the alternating tie-break — and the whole
`overnight/2026-08-13` branch, which is **unmerged** and carries the AoE/`HittableEnemies` work. Read
`docs/OVERNIGHT_REPORT.md` before merging it: that change patches a getter this document had called
unpatchable since M1, with an argument, and it has never run.

### The real-time wait is the dwell, not the ordering — measured 2026-08-13

Reported after the same session: *"queued 2 defends and 1 attack on Silent, then 1 defend on
Ironclad, and the Ironclad's defend waited for Silent's strike instead of just going through because
it's the first card."* This is the fourth report of this shape and the first with a log that answers
it. **The ordering rule is not what this hits, and changing it would fix nothing** — which is exactly
what `docs/OVERNIGHT_QUEUE.md` P6 predicted, and the evidence it named as the thing that would
reopen the question (`pending (N waiting)` with N ≥ 2, and an index that is not `#0`) **still does not
exist in any log.**

The exchange, from the host log, in order:

```
paced: submitting PlayCardAction CARD.STRIKE_SILENT
queue: 1's play #0 pending at +0ms (1 waiting)
queue: releasing 1's play #0 [earliest]
[ActionExecutor] Executing action: PlayCardAction CARD.STRIKE_SILENT
queue: 1001's play #0 pending at +0ms (1 waiting)     <- the defend arrives HERE
[SpirePvp] paced: resolved CARD.STRIKE_SILENT
queue: releasing 1001's play #0 [earliest]
```

**The defend was booked after the strike was already in the executor.** There was no contest for the
scheduler to arbitrate — and there never has been: across the whole match, **every one of the 20
bookings read `(1 waiting)` and every release read `[earliest]`.** `[tie …]` has still never fired
once, in any session.

**Why the pool can never accumulate on an idle board, which is the structural fact under all four
reports.** `Submit` calls `Pump`, and `PumpAsync`'s wait loop does not await at all when the executor
is idle and the play is due — so `Release` runs *synchronously inside the click*, and
`EnqueueAction` in turn runs the action to completion before returning. A play made on a still board
is therefore committed to the executor before control leaves the click handler. Two plays can only
meet in the pool when the second arrives while the executor is busy with a *third*.

**The same match contains the control case, which is what settles it.** In the first exchange the
identical four cards were played in the identical order and it felt fine — because the Ironclad
defend arrived a fraction later, *after* `paced: resolved CARD.STRIKE_SILENT`, and went straight
through. Whether a card "goes instantly" or "waits" is decided entirely by whether the click lands
inside the opponent's card's resolution-plus-dwell window. Nothing about firstness, and nothing a
scheduler can preempt: once a card is in the executor it cannot be pulled back.

**And the design does handle the case it was written for** — a genuine burst. Silent clicking three
cards at 0.0/0.1/0.2 gets `PlayAt` 0.0/0.4/0.8; an Ironclad click at 0.5 enters a pool that still
holds `S#1@0.4` and `S#2@0.8`, and beats the third. That requires clicks fast enough to outrun the
executor, which **one person alt-tabbing between two windows cannot produce** — the plays in this log
are 330ms+ apart with each one fully resolved before the next. Note this is the rig limitation
HANDOFF already documents, but the conclusion is the opposite of last time: here the rig accounts for
the evidence *and* for what the player described, which is the case where it is not an excuse.

### THE REAL FAULT UNDER IT: the beat was per-stream where it had to be per-player

FIXED 2026-08-13, **UNPLAYED**. Lucas, pushing back on the analysis above and right to:
*"the dwell is supposed to be individual… Ironclad's card play should come out instantly for the
first card play. I saw Silent's strike still in the air when I played Ironclad's defend and it still
didn't resolve."*

**He is describing a real fault, and the analysis above stopped one step short of it.** `DuelPace`
takes its gap by calling `ActionExecutor.Pause()`, and there is exactly **one** `ActionExecutor` per
run. So the beat was never "Silent's dwell" — it is a pause on the single shared action stream, and
whatever card came next paid it regardless of whose it was. The opponent's reply was waiting out the
reading gap that existed **so that they could read the card they had already read and answered.**

It leaked further than that. `DuelPlayScheduler.PumpAsync` gates on `ActionExecutor.IsRunning`, and a
*paused* executor still reads as running — `IsRunning` tracks the queue-drain task, which does not
complete while `WaitForUnpause()` blocks at the top of the loop. So the **host's own** beat, taken
from the host's own preference, gated the release of the client's card. That is precisely the failure
`DuelPace`'s own comment says it avoided by pausing locally instead of having the host release per
tick: *"would have made the host's personal preference decide the pace on both screens."* It did
anyway, through the scheduler.

**What cannot be fixed, and it bounds every option here:** the engine executes actions strictly one
at a time, and that serial stream is the deterministic sim the checksums are taken over. The
opponent's card can never overlap yours. "Instant" can only mean *next, with no added gap*.

**The fix: the beat is now owner-aware.** `IPlanningTurnModel.CrossPlayerBeatSeconds` is the gap owed
before a play belonging to the *other* duelist. `TickTurnModel` returns **0.2s** against its 0.55s
own-beat (Lucas's choice over removing the cross gap entirely — two players trading with no gap at
all puts the round back where `DuelPace` found it, unreadable). **`LockInTurnModel` returns its full
beat and changes nothing**, which is the important half: a resolving round is interleaved by design,
so almost every gap in it is a cross-player gap, and shortening those would restore the exact "six
plays resolved and neither player could say what happened" report `DuelPace` was built to answer.

**The beat waits in 0.05s slices and re-asks each one**, rather than deciding once at the top. That
is not tidiness — the opponent's card is usually *not* queued when the beat begins, because the host
releases it only once the executor's drain completes, which is a moment after the pause is taken. A
decision made only at the start would take the full beat every time and the whole change would be
invisible.

Knowing whose card is waiting comes from `ActionQueueSet.ActionEnqueued` plus `GameAction.OwnerId`,
which is on the base type. `GetReadyAction` is the executor's own consumer and is not something to
call from a mod for a look. Armed in `DuelPace.Arm` with every other handler and released in
`Reset` — same rule as everything else. A **cancelled** play never executes and leaks one entry; the
cost is bounded and one-directional, since a stale foreign entry can only ever *shorten* a beat.

**Initiative stays** (Lucas, 2026-08-13), though it has still never fired: the tie-break needs two
plays inside 60ms and no log has ever contained a pair.

**What to watch in the log:** `pace: cut <id>'s beat at 0.20s of 0.55s — the opponent has answered`.
That line is the whole feature; if it never appears, the cut is not firing and the beat is still
per-stream. The scheduler lines now also say whether the board was `BUSY` or `idle` at booking and
report `after Nms — beat 1001#0` or `uncontested` on release, because the old lines could not
distinguish "waited" from "went straight through" — the `+Nms` on a booking is the *cooldown* offset,
not a wait, and the game's log lines carry no timestamps at all.

#### The second half, and the first half could not work without it (2026-08-13, UNPLAYED)

The owner-aware beat above was played immediately and **the report came back unchanged**: *"happened
again with certainty on every turn… again with client waiting for host card to resolve."* The log
says why in one line, and it is the leak named two paragraphs up — written down, and then not fixed:

```
queue: releasing 1001's play #0 [earliest] after 579ms — uncontested
```

**579ms with nothing executing.** The `Pausing queue` / `Un-pausing queue` pair straddles it: the
scheduler was waiting out the host's beat before it would even *release* the client's card.

**And that deadlocked the new cut-off against the scheduler.** The beat shortens itself once the
opponent's play is **enqueued**; the play could not be enqueued until the beat **ended**. Each waited
on the other. That is why `pace: cut` appears only 3 times on the host across a whole match instead
of on every exchange — it fired only where the card happened to be queued *before* the beat began,
which is the case that never needed it.

**The predicate was wrong, and it is the same trap this document keeps recording.**
`ActionExecutor.IsRunning` tracks the queue-drain *task*, which stays alive across `WaitForUnpause` —
so an executor merely paused for presentation reads as busy. It correlates with "the sim is working"
everywhere except the one state this mode invented. `DuelPlayScheduler` now asks
`!ActionQueueSet.IsEmpty || ActionExecutor.CurrentlyRunningAction != null` — a card queued and not
yet executed, or one genuinely mid-execution. Neither is true during a beat.

Two properties worth not re-deriving: a release enqueues immediately, so `IsEmpty` goes false at once
and **exactly one card still leaves per drain** — the pool cannot empty itself into a single beat.
And a **cancelled** action is dropped from the queue by the executor's own skip, so it cannot wedge
this loop the way a hand-kept in-flight set would have. That was the first design tried here and it
would have turned a cancelled play into a hung duel.

#### And the third half: it really was the ordering rule, once a contest could exist to show it

Reported again immediately — *"same deal turn 2"* — and this time the log holds something no previous
one did. The gate fix worked: the pool held two plays at once, which is the contest four sessions of
reasoning had been done without.

```
queue: 1's play #0   pending at +261ms (1 waiting, board BUSY)   <- host's THIRD card of the burst
queue: 1001's play #0 pending at +0ms  (2 waiting, board BUSY)   <- client's FIRST, due now
queue: releasing 1's play #0 [earliest] after 830ms — beat 1001#0
```

**Chronology looks fair and is not, because a burst buys its own priority.** The host's third card was
pushed to `+261ms` by its own cooldown and the client's first was due immediately — and the third card
still won, because it had been *clicked* earlier and cooldown time is still time. The player already
monopolising the stream is exactly the player whose next card is nearest on the clock, so ordering by
clock compounds a lead instead of arbitrating it.

**So ordering is by position again** — your Nth card of the turn races their Nth — which is the rule
this class was written around and which its own summary never stopped describing. Ties at the same
position go to the clock, and near-simultaneous ties inside 60ms to alternating initiative, which is
where initiative belongs and where it now might actually fire.

**The argument for the change is that position satisfies both of Lucas's reports and chronology
satisfies one.** The case chronology was adopted for — 2026-08-12, *"player 2 plays a card at .3
seconds, that should beat player 1's second card at .5"* — is player 2's **first** card against player
1's **second**, so position gives player 2 as well. What position gives up, stated plainly: your `#0`
beats their `#1` even when yours was clicked slightly later, bounded by the cooldown. That is what
"your first card cannot be buried by their burst" is made of, and it is what was asked for four times.

**The rule could not have worked before this even if it had been left in place**, which is worth
knowing before anyone reads the earlier attempts as failures of the idea. `_nextIndex` was cleared
whenever the pool drained, and the pool drained between almost every pair of plays — so **every card
in every duel was booked as `#0`** and there was no position to compare. It resets at the turn
boundary now. Note this is the third distinct mechanism that had quietly flattened the same
information: the model's own submission cooldown, then the scheduler's busy-gate, now the index reset.

**Only *due* plays are candidates**, which pure chronology got for free and position does not: the
earliest-clock pick was always due by construction, whereas a lowest-position pick has to be told, or
a player's not-yet-due second card could be released ahead of its own cooldown.

#### Turn-based confirmed in play 2026-08-13 — and the resolution was too slow

**"Turn-based seems to be working great"** — so the lock-in gate inversion fixed on 2026-08-12 is
confirmed, and the mode is no longer "believed working on no evidence". Both models are now
playtested.

**Then: "one card every 3 seconds, kinda painfully slow — I turned Fast Mode on mid-combat and it
stayed slow."** Two separate causes, and the second half is this mod working as designed:

1. **A duel pins Fast Mode for both clients** (`DuelFastModePatch`), so a mid-duel toggle does
   nothing by construction. That pin is right and stays — vanilla sizes almost every wait through
   Fast Mode, so an unpinned duel hands reaction time to whoever has the faster setting. But it was
   pinning to **`Normal`**, and the reason recorded for choosing `Normal` over `Fast` was "the report
   asked for a *feelable* delay". That conflated two mechanisms introduced the same week: **the
   feelable delay is `DuelPace`'s beat**, which is a `Cmd.Wait` and *does not shorten at `Fast`* —
   `Cmd.Wait` only skips outright at `Instant`. So `Normal` was buying nothing but slower animations
   inside an unchanged gap. **Pinned to `Fast` now**; the readable gap is untouched.
2. **The lock-in beat was 1.2s**, set before real-time had a beat at all. Real-time reads at 0.55s
   and was reported as feeling right, and a resolving round is if anything *easier* to follow — it is
   a replay of plays already committed, with nothing to decide while you watch. **0.8s now.**

**Never pin to `Instant`.** `Cmd.Wait` skips entirely there, so it would silently delete the beat —
the exact unreadable round the beat exists to prevent, reached through a setting rather than a code
change. The two knobs are separable and worth keeping straight: **the pin decides the card's own
animation time, `BeatSeconds` decides the gap after it**, and the second means the same thing at any
setting.

#### CONFIRMED IN PLAY 2026-08-13 — "felt great this time"

The four fixes below are **playtested together and good**. From the confirming log: contests now form
(`2 waiting`), `releasing 1001's play #0 [position #0] after 16ms — beat 1#2` and again `after
117ms`, `pace: cut` firing five times on each side, and **zero errors and zero divergences on either
client**. The release waits that were 763–901ms are now 16–167ms.

**Real-time paced (`TickTurnModel`) is therefore playtested end to end** — pacing, ordering, the
cross-player beat and the contest window. Treat it as working; the four items below are the record of
why, not open threads.

**Turn-based is untouched by all of it**, which was checked rather than assumed and matters because
the two modes share `DuelPace` and the scheduler: `DuelLockInPatch` routes to `DuelPlayScheduler`
only when the model is `TickTurnModel`, and `LockInTurnModel.CrossPlayerBeatSeconds` returns its full
beat, so the sliced beat resolves identically for it. Turn-based remains unplayed for its **own**
reasons (the lock-in gate inversion), not because of this work.

#### The contest window, which turned out to be the rest of it after all

Position ordering was played immediately and the report was *"third turn actually felt good this
time, but I still think turns 1 and 2 should've let the client get its defend out — am I
hallucinating?"* **No.** One match, three turns, and the log separates them cleanly:

```
turn 3:  1's play #2    pending at +174ms (1 waiting, board BUSY)
         1001's play #0 pending at +0ms   (2 waiting, board BUSY)
         releasing 1001's play #0 [position #0] after 50ms — beat 1#2

turns 1 and 2:
         1001's play #0 pending at +0ms   (1 waiting, board BUSY)
         releasing 1001's play #0 [position #0] after 901ms — uncontested
```

**Same play, same position, opposite outcome — decided by which side of one release instant the
click landed on.** In turn 3 the host's `#2` was still pending, so a contest existed and position
won it. In turns 1 and 2 the host's `#2` had been released a moment earlier, and **an enqueued card
cannot be preempted**, so there was nothing to arbitrate: `(1 waiting)`, alone in the pool, 901ms and
763ms spent behind a card that was already committed.

Position ordering is powerless there by construction, which is worth stating plainly because it
looks like the same bug and is not: ordering can only choose between things that are pending
together. Making them pending together is a different mechanism, and it is the **contest window**
this document twice recorded as "wanted, but not what this report is about". It was what the report
was about; the two earlier dismissals were wrong, and each was argued from a log in which no contest
could form at all.

**A card that would extend a burst now waits up to 150ms at the release point** before being
committed, so the opponent's answer can arrive inside that window and take the slot on position.
**A player's own `#0` is never held**, which is Lucas's rule nearly verbatim: *"their card play
should come out instantly for the first card play and after that there is essentially a global
cooldown."* The player answering pays nothing; only the player already several cards deep waits, and
only while they hold the stream alone. The hold is dropped the instant the opponent has anything
pending, because then the contest exists and there is nothing left to wait for.

150ms is sized against the margin the log missed by, and sits under the 400ms cooldown so it never
becomes the thing pacing a burst.

**Four mechanisms have now been found flattening this same signal**, which is the thing to remember
if it ever regresses: the model's own submission cooldown, the scheduler's `IsRunning` busy-gate, the
per-drain index reset, and finally releasing the moment the board freed. Each hid the next, and every
one of them individually made the ordering rule untestable while looking like a working duel.



### Read this first (written 2026-08-12, for whoever picks this up cold)

**A duel now has two modes and both were rebuilt on 2026-08-12. Almost everything below this
heading is built and *not yet played*, so treat "it works" as a claim, not a fact.**

- **Real-time is paced** (`TickTurnModel`). Your first play is instant, the next leaves on a 0.4s
  cooldown, and what you click in between is queued rather than dropped. The host orders the two
  players' plays with `DuelPlayScheduler`.
- **Turn-based is batched** (`LockInTurnModel`). Locking in commits a *batch*; an empty batch ends
  the turn; a turn holds as many plan→resolve exchanges as the players want, which is what makes
  draw cards work.
- Both defer plays, so both implement `IPlanningTurnModel` and share the energy reservation, the
  play-queue presentation and the queued-card highlight.
- Initiative (M9) is live in both: whoever reached the arena first leads, alternating each turn,
  shown as an arrow over that duelist with "You move first" / "They move first" above it.

**Patch count: 80 classes / 118 methods.** 69/107 was verified against a live log before the last
two 2026-08-12 commits, which add no patches (`DuelTurnModel.ShouldDefer`'s guard, and the scheduler
rewrite/rename). `DuelModifierMinimumPatch` and the AoE fix take it to 71/109 on paper. **None of
those have been run in game at all.**

### A rest site before the duel — approved 2026-08-12, designed, NOT built

Lucas's request, and the shape is settled: **not a new map node — a rest at the arena, before the
duel starts.** The arena is not a node we generate (it reuses vanilla's `SecondBossMapPoint`), and
inserting a real node into generated map data means both clients agreeing on a map they did not
generate together, on top of the six quiet omissions `DuelArena` has already produced mirroring
`EnterMapPointInternal`. The rest-at-arena version needs no map change at all.

**The find that makes it cheap: `RestSiteRoom` is already a rendezvous.** Its `Exit` awaits
`RestSiteSynchronizer.AfterAllRestSitesCompleted()`, which blocks until *every* player has finished
— exactly the both-players gate the duel needs, written by the engine and already synchronised.

**The ordering is the whole problem, and it is not free.** Three constraints that fight:

1. **The rest must happen before arrival is announced.** `DuelArrivedMessage` carries your deck, and
   the review opens once both arrivals are in — so upgrading after that point re-creates the stale
   decklist bug fixed on 2026-08-12, and re-creates it *invisibly*.
2. **But the rest synchroniser waits for a co-located party**, and the players are only guaranteed to
   share a coord after `DuelArena.MoveRunToArenaCoord`. A rest before that runs the same hazard that
   already produced "a client that could not leave a rest site" during the race — and here it would
   hang the match rather than one room.
3. **So the coord move has to come first**, which means splitting `DuelArena`: coord move → rest →
   arrival + deck review → combat room. Note `MoveRunToArenaCoord` must still run *before* the
   `CombatRoom` is constructed (`AddVisitedMapCoord` resets `NextRoomId`), so the split has to keep
   the room construction on the far side of everything.

**Also undecided, and it is a rules question rather than an implementation one:** whether resting
costs race-clock time. It should, or the race bank stops meaning anything at the finish line — but
that is Lucas's call, and it only bites in a timed match, which no run has used yet.

#### RE-SCOPED 2026-08-13 (still NOT built): two of the three constraints above are wrong

The overnight session took this as its second priority, read the decompile for it, and **stopped
rather than build it** — the plan above cannot be executed as written, and the corrected plan turns
on a rules decision that is Lucas's. What each constraint got wrong:

**Constraint 2 is already solved, for the whole race.** "The rest synchroniser waits for a
co-located party" is true of vanilla and false of this mod: `RaceSoloRestSitePatch` pre-completes
every absent player's `PlayerRestSite` at `BeginRestSite` whenever `DuelSession.IsRaceActive`, which
is exactly why race rest sites work today (it closed "a client that could not leave a rest site" on
2026-08-11). **A rest entered while the race is still active therefore needs no shared coord at
all** — it is solo, like every other race room, and `RaceIgnoreRemoteRoomPatch` drops the
opponent's rest traffic anyway. Constraint 3 (split `DuelArena` so the coord move comes first) falls
with it.

**Constraint 1 and the "find that makes it cheap" are in direct contradiction, and the thing that
settles it was missed: `RestSiteRoom.Exit` generates a checksum.** Its last line is
`ChecksumTracker.GenerateChecksum("Exiting rest site room")`. `RaceCoordinator.EndRace` is what
re-enables `ChecksumTracker` (it is off for the whole race, `BeginRace` line 60), and the two runs
are divergent by construction until `CombatStateSynchronizer.WaitForSync` — which runs *inside*
`DuelArena.EnterRoom`, i.e. after the deck review. So a rest room placed where the design wants it,
**after `EndRace` and before the sync, produces a `StateDivergence` on every single match** — and
since 2026-08-12 a desync voids the match as a draw, so this would not even fail loudly as a bug.
The engine's both-players gate cannot be used here: it only exists after the phase flip, and after
the phase flip the checksum is live.

**So there is exactly one viable placement, and it is the opposite of the one designed:** the rest
belongs **inside the race phase**, at the moment the player clicks the arena node, *before*
`DuelRendezvous.ArriveLocal`. Checksums are off, the solo patch handles the absent opponent, the
deck the arrival message carries is post-upgrade so the reveal stays honest, and the both-players
gate is the rendezvous itself — which is the gate this flow already trusts, and the one whose
ordering guarantee is written up above ("Why the rendezvous is immune").

**What is left to build, and why it was not built tonight:**

1. **Entering and leaving the room.** `DuelRendezvous` would fade out, run `EnterMapPointInternal`'s
   preamble for a `RestSiteRoom` (a *different* subset from `DuelArena.EnterRoom`'s — no replay
   state, no sync, but `ClearScreens` and the fade still), await the local rest completing
   (`AfterAllRestSitesCompleted` returns as soon as the local player's options are exhausted, since
   the opponent's source is pre-completed), then `ExitCurrentRooms()` and announce arrival. This is
   the `RunManager.EnterRoom` trap again, at a third door; it wants the same step-by-step
   commented mirror `DuelArena` has, and it cannot be verified by compiling.
2. **Which options a pre-duel rest offers is a competitive rule, not an implementation detail.**
   Vanilla's set includes Smith (upgrade), Dig (a *relic*), Kindle, Lift, Cook, Clone and Hatch.
   "Rest before a duel" reads as heal-or-upgrade; handing someone a relic at the finish line is a
   different game. `RestSiteOption.Generate` is the filter point (`RaceNoCoopSurfacesPatch` already
   strips Mend there), but **which to strip is Lucas's call.**
3. **The clock question is unchanged and is now load-bearing rather than theoretical.** Inside the
   race phase the race bank keeps running while a player reads a rest screen, so building it this
   way *answers* "resting costs race-clock time" by default. That is the answer the note above leans
   toward, but it would be answered by omission rather than decided.

`DuelArenaRest` (the plain 30% heal, built 2026-08-12, unplayed) is unaffected by any of this and
remains what a match gets today.

### Dying in the race now loses the match (2026-08-12, unplayed)

Reported: *"I died to the boss and it didn't end the run, giving the opponent the victory."* The
cause was a **route**, not a mechanic — `DuelResult.Arm()` is called from `DuelArena`, i.e. on arena
entry, so for the whole of the race nothing was watching a duelist die, and the match carried on
with a corpse in it.

**Vanilla is right not to end it.** `CreatureCmd.Kill` gates the game-over screen on *every* player
being dead; in a race the opponent is alive in their own combat, so the party has not wiped. The
co-located-party assumption again, this time producing a bug in the *ending* rather than in a room.

`DuelRaceDeath` hooks `CombatManager.CombatEnded` **from run start**, and declares locally then
broadcasts, mirroring `DuelResign`. Two things about it worth not re-deriving:

- **It declares locally on purpose.** Your death in a race is a fact only you can see — the runs are
  decoupled and no state sync covers a room the opponent is not standing in — so it must be *sent*.
  That is not a breach of host authority: that rule exists so two sims cannot reach different
  conclusions from *shared* state, and here there is none to disagree about.
- **`CombatEnded`, never a poll on `IsDead`.** Revival effects run inside combat, so a probe on the
  flag would declare a loss for a player about to stand back up.

`DuelEndReason.RaceDeath` is its own reason with its own result lines, because the loser never
fought their opponent and should not be told they lost a duel they never had. Adds no patch class —
it is an event subscriber, so the count stays 69.

**Untested corner:** both players dying at nearly the same moment would have each broadcast a win
for the other. Rare (separate combats, and it needs the same second), and the same crossing case
`DuelDrawPrompt` already handles for offers — but it is not handled here.

**Read statically 2026-08-13 and deliberately NOT built. Two corrections to that paragraph:**

- **What it actually produces is two DEFEAT screens, not two victories.** `DuelResult.Declare`
  returns immediately when `DuelSession.Phase == DuelPhase.Complete`, so each client declares its
  own loss and then *ignores* the peer's message naming it the winner. Worth knowing before
  reaching for a fix: the failure is a double loss, which is wrong but not incoherent.
- **The near-simultaneous case is already right, and only the truly crossing one is not.** If B
  dies 200ms before A, B's message reaches A first, A declares a win, `RunManager.OnEnded` sets
  `IsGameOver`, and A's own death then hits `DuelRaceDeath`'s `state.IsGameOver` guard and stops
  there. The broken window is exactly "both died before either message landed".

**And the crossing-offer shape does not transfer, which is why this is a scope rather than a
commit.** A draw offer is *outstanding* by nature — it sits on the wire waiting for an answer, so a
crossing pair can be resolved on arrival. A death declaration is immediate and irreversible: the
result screen is up and `RunManager.OnEnded` has run before the peer's message could arrive, and
`Declare` is idempotent by design. There is nothing outstanding to reinterpret.

So the two candidate fixes, neither free:

1. **A grace window.** Broadcast the death, wait (a second or so, on the run timer's tick like
   `DuelDisconnect`), then declare — converting to a draw if the peer's `RaceDeath` lands inside
   the window. Correct, and it is the only shape that closes the true crossing. **Its cost falls on
   the common case**: every ordinary race death would sit a second before its result screen, and
   race death is itself unplayed, so this trades a rare wrong result for a change nobody has seen
   in the path that actually happens.
2. **Host arbitration.** The dying client reports rather than declares, and the host — which sees
   its own death directly and the client's by message — decides. It narrows the crossing window
   from a round trip to one leg but does not close it, and it gives up the argument
   `DuelRaceDeath`'s comment makes for declaring locally.

**It also wants a rules answer first:** whether both dying is a draw or a double loss. Every other
no-winner ending in this mod is a draw (`RaceExpired`, `Desync`), which is the obvious reading, but
it is a competitive rule and this file does not get to settle it. If it becomes a draw it needs a
new `DuelEndReason` (appended, 8) and its own result lines — the existing draw branch would
otherwise word it as the race clock expiring, which is a specific false claim.

### The AoE family is fixed (2026-08-13, UNPLAYED) — and the getter is patchable after all

**Symptom, open since M2:** Bag of Marbles applies no Vulnerable in a duel, and the same for every
other "all enemies" effect. **Cause:** `CombatState.HittableEnemies` is `Enemies.Where(IsHittable)`
and a duel has an empty enemy side, so all of them resolve against nothing.

**This document has said since M1 that the getter "is not patchable", and that claim was right
about the getter and wrong as a conclusion.** What it actually established is that the property has
no attacker to reason from, so *an answer invented at the getter is a local guess inside sim code,
i.e. a desync*. That argument constrains where the actor comes from; it does not forbid answering.
`DuelAoeActor` supplies an actor that the **simulation itself defines**, so both clients resolve the
same one, and `DuelAoeTargetingPatch` answers the getter with `GetOpponentsOf(actor)`.

Two tiers, and **the second one is the finding worth keeping**:

- **Tier A — the model being handed a hook.** `DuelHookListenerScopePatch` wraps
  `CombatState.IterateHookListeners` so that whichever relic/power/card is currently being
  dispatched is ambient for exactly that stretch. `IterateHookListeners` builds a `List` and returns
  it, so this is an ordinary iterator wrapper and not a patch on a compiler-generated state machine
  — and it is the **single funnel**: all 74 dispatch loops in `Hook.cs` enumerate it, directly or
  through `Hook.IterateCombatHookListeners`. One patch, every hook, including hooks a game update
  adds.
- **Tier B — the running action's owner**, for card/potion/orb effects, which run inside their own
  action (`Thunderclap.OnPlay` inside its `PlayCardAction`, whose `OwnerId` is the player who played
  it).
- **Neither resolves → vanilla's empty list stands**, reported once per distinct case. An
  unresolved read behaves exactly as the mod does today, which is the whole reason this is
  defensible without a playtest.

**The queue expected tier B to be merely *insufficient* at hook time (`CurrentlyRunningAction` is
null in `BeforeSideTurnStart`). Reading the decompile makes the hole bigger, and that is the part to
remember: at hook time the running action is frequently *wrong* rather than absent.**
`CorrosiveWavePower.AfterCardDrawn`, `PanachePower.AfterCardPlayed`, `LostWisp.AfterCardPlayed` and
a dozen more fire while the **other** duelist's action is executing. A fix built on tier B alone
would have applied your poison to your own side — silently, only when the opponent moved, and never
in a solo test. Tier A therefore takes precedence whenever a hook is running, which is also correct
for a card a relic *auto-plays*: Whispering Earring picks and plays a card during its owner's hook,
and the owner is the actor either way.

**Three things it deliberately does not do**, each of which was the tempting version:

- **It does not build its own target set.** It answers through `GetOpponentsOf`, which
  `DuelOpponentsPatch` already retargets and which every attack already travels through. Thunderclap
  is the worked example — it damages through `TargetingAllOpponents` and applies Vulnerable through
  `HittableEnemies` — so a second, slightly different set here would have it damage the duelist and
  Vulnerable the duelist *and their pets*. It also means the open question "should the opponent's
  pet be attackable?" stays open in one place instead of being answered twice by accident.
- **It does not patch the call sites.** The note in `DuelTargetingPatch` proposed exactly that
  ("retarget at the call sites that do know the actor"), and it does not survive contact with the
  decompile: there are **70 read sites**, and while `PowerCmd.Apply` and `CreatureCmd.Damage` are
  handed both the list and the source — which covers Bag of Marbles, Thunderclap, Noxious Fumes,
  Letter Opener — a third of the sites iterate the list themselves (`Stomp`, `Outbreak`, `Misery`,
  `Shockwave`, `Piercing Wail`, `TwistedFunnel`) or take one element from it (`Shiv`,
  `WhisperingEarring`, and every `Rng.CombatTargets.NextItem` random pick). A command-layer fix
  would have looked complete and left those dead.
- **It does not give each duelist their own `CombatState`.** This is the idea that looks best on
  paper — `Creature.CombatState` and `AbstractModel.CombatState` *are* per-owner getters, so a
  per-owner proxy would answer all 70 sites with no ambient at all — and it is a trap.
  `CardModel.CardScope` does `((ICardScope)CombatState)`, so the proxy must implement every
  interface `CombatState` does or throw an `InvalidCastException` in the card layer; `CreaturesChanged`
  subscriptions taken on a fresh proxy would never fire; and `Creature.CombatState` is read by
  hundreds of engine call sites that a duel has no business touching. Rejected on those three, not
  on taste.

**What was already working, and had been mis-scoped as broken.** The "70 models" number counts read
sites, not broken effects. Cards that *damage* all enemies — Dagger Spray, Cleave, Sweeping Beam —
deal that damage through `AttackCommand.TargetingAllOpponents` → `GetOpponentsOf`, which
`DuelOpponentsPatch` has covered since M2. On those cards only the **rider** was missing: Dagger
Spray's impact VFX, Thunderclap's Vulnerable. Fully broken were the effects that read the property
directly — Bag of Marbles, Noxious Fumes, The Bomb, Inferno, Letter Opener, Charon's Ashes, and the
random-target picks behind Beat Down, Bouncing Flask, Tingsha and Parrying Shield.

**The evidence base is the decompile, because there is no other.** The queue said to grep Lucas's
logs for `telemetry: HittableEnemies came back EMPTY` and build for what appears. **Nothing appears
— not one line, in any log on this machine.** `logs/*.log` are all from builds that predate the
probe, and of the five `%APPDATA%\SlayTheSpire2\logs\godot.log` files exactly one carries it
(`69 patch classes applied cleanly`, a Steam duel against a second player, `duel over — WON`); that
duel's whole card list is Squeeze / Neurosurge / Putrefy / Photon Cut and contains no AoE effect at
all. So the probe worked and had nothing to say. Worth knowing for next time: **a silent probe is
evidence about the sample, not about the bug.**

**Known imprecision, written down rather than smoothed over.** A hook body that `await`s leaves tier
A's ambient set across the await, so anything that runs in that window — a card asking whether it
should glow gold, a targeting visual — sees the hook's actor instead of its own. Those readers are
presentation and change no state, and a *sim* read cannot land in that window because the executor
runs one action at a time with hooks awaited inline. If a duel ever shows an AoE card highlighting
the wrong creature for a moment, this is why, and it is cosmetic.

**What to play first.** Any duel with an "all enemies" effect on either side. Bag of Marbles is the
cleanest single test because it is the original report and it fires at turn start with no card
involved. Then a Thunderclap or a Haze, which exercise tier B. The line to watch for is the *absence*
of `telemetry: an "all enemies" read found NO ACTOR` — one of those names a case this does not
cover, and the running action it prints is the lead.

### Telemetry added 2026-08-12, and what each line answers

`DuelTelemetry` **logs and changes nothing** — added at Lucas's request so the
next session answers four open reports instead of describing them. Rate-limited on purpose; a probe
that floods the log makes the log useless for the bug it was added to catch. All four lines are
`[SpirePvp] telemetry:`.

| Line | The report it settles |
|---|---|
| `local duelist is DEAD — phase=… resultArmed=…` | *"I died to the boss and it didn't end the run."* `DuelResult.Arm()` runs from `DuelArena`, i.e. **on arena entry**, so nothing watches a death for the whole race — `resultArmed=False` next to a dead duelist is that, confirmed. |
| ~~`HittableEnemies came back EMPTY`~~ → `an "all enemies" read found NO ACTOR` | Was the Bag of Marbles probe (`DuelAoeProbePatch`, now retired). **It never printed a single line**, in any log on any machine — see the AoE section below. It now reports the *residue* of the fix instead: a read the mod could not attribute to a duelist, where vanilla's empty list still stands. |
| `run timer — visible=… pref=… mapVisible=…` | *"Client doesn't show the clock, host does."* Both sides logged `raceClock=0 min`, so the widget is vanilla's `NRunTimer`, not ours, and `show_run_timer` is `false` in **both** dev profiles. The answer is the diff between the two clients' lines. |
| `Neow offered <id> (<character>): A / B / C` | *"We were both Necro and got different Neow bonuses."* By **name**, never index — the indices match by construction and mean different things on the two clients, which is the trap that killed the opponent-vote icon. |

**Race clock tiers are 8 / 10 / 12 / 15 / Off** as of 2026-08-12 (Lucas's call; the duel clock is
unchanged). Note what went with the 1-minute option: it was the only way to reach a race-clock expiry
inside one test run, so **flagging on time is now an 8-minute test**. If that becomes annoying,
bring back a dev-only tier rather than re-tuning the real ones. The rename touches
`SpirePvp/localization/eng/modifiers.json`, so **the `.pck` was re-exported and committed** — a
puller who builds without it sees `RACE_CLOCK_EIGHT.title` as a raw key.

**Playtest order, because the pieces stack:**

1. **Real-time.** Played once on 2026-08-12 and it found a real bug — the scheduler works when it is
   asked, and the host's clicks were skipping it (see *"never defer an action the sim raised"*
   below). Fixed and **not yet replayed.** One player plays three cards; the other then plays their
   *first*. That first card should take the next slot rather than waiting behind the other three —
   the whole point of `DuelPlayScheduler`. Watch for `queue: <id>'s play #N pending` and `queue:
   releasing <id>'s play #N` in the log; the numbers are per-player positions, so `1001`'s `#0`
   beating `1`'s `#1` is the thing working. **The check that would have caught the bug: every
   `Enqueueing action PlayCardAction … from owner <id>` must have a `queue: releasing <id>'s play
   #N` next to it.** One without the other is a play that skipped the scheduler.
2. **Turn-based**, which has not been played since the batch model, the auto-close, the arrow and
   the purple highlight all landed together.

**Three things nobody has confirmed, and one of them is a trap:**

- **Powers "not ticking down" is not supported by the log** — the per-turn dump shows
  `VULNERABLE_POWER:2` → `1` → gone, and amounts matching their sources (Bash applies 2). If it
  still looks wrong on screen, suspect the **display**: `NPowerContainer` refreshes off
  `PowerRemoved`, and 2 → 1 removes nothing. Do not "fix" the model without new evidence.
- **Whether the queue side-split shows in real-time.** It is the same patch that works in
  turn-based and it keys on any deferring model, so it should. Unconfirmed by eye.
- **Three `NCard` double-frees at duel teardown**, measured at 3 per match and 0 before the play
  queue started holding planned cards. Root cause not proven — see the note further down, and find
  the *first* free rather than assuming.

**BUILT 2026-08-13 and confirmed in play — M8.5 slice 3** — the opponent's *unsubmitted* queue on the wire, drawn on their
side. Without it you can only see what has already been released, which is at most 0.4s of warning:
not enough to read or answer, which is the point of the mode. It is a deliberate change to the
information rules (DESIGN §1) and was decided as such.

### Start here: playtest the planning phase

**M8's two remaining pieces are built (2026-08-12), and the playtest of them found something
bigger.** Energy is reserved while you plan, planned cards are drawn in vanilla's play queue, and an
icon over the end turn button says who has locked in. What to try is at the end of this section,
under *"The playtest this needs"*.

**BLOCKER FOUND AND FIXED, UNPLAYED: the lock-in gate was inverted, so the interleaved merge had
never actually run.** Reported from the seat that saw it — on the client, cards resolved the instant
end turn was clicked rather than when both players were in, while the host's correctly waited.
`DuelLockInPatch` ended in `return action is not UndoEndPlayerTurnAction`, which passes every *play*
through to vanilla and swallows only the undo: the exact inverse of what its own comment describes.
The host therefore held each of the client's plays in the round buffer **and** enqueued it on
arrival. Three consecutive lines in the host log say it:

```
lock-in: holding opponent's PlayCardAction CARD.DEFEND_SILENT (1 held)
[ActionQueueSynchronizer] Enqueueing action PlayCardAction CARD.DEFEND_SILENT
[ActionExecutor] Executing action: PlayCardAction CARD.DEFEND_SILENT
```

The flush then enqueued the same plays a second time, where they no-opped because the cards had
already left the hand. **So `resolving round — 3 then 3` was three real plays and three
already-spent ones, in both sessions this model has been played in, and one side of every round has
been playing blitz.** It never desynced because both sims took their ordering from the same host
stream — which is exactly why nothing in the logs looked wrong and why the five-round playtest
passed. Treat the turn-based mode as **unplayed** until this is confirmed in a match.

The general lesson is the one this project keeps paying for in a new costume: **a predicate that
merely correlates**. Every observable was consistent with a working lock-in — plays were held, the
round was flushed, the merge logged sensible counts, both clients agreed — because the wrong half of
the work was still being done by the right code.

**Two more findings came out of building the pieces, and the second is the one to remember.**

**The energy reservation was recorded here as "built, unverified". It was half built, and the half
that existed could not have worked.** `0b57348` added `LockInTurnModel.ReservedEnergy` and nothing
that reads it — no `CanPlay` patch was ever written — so the reported "cards still playable past 3
energy spent" was a live bug, not a stale observation. And the property summed
`PlayCardAction._card`, which `ExecuteAction` assigns: it is null for every action the model holds,
because a *buffered* play by definition has not executed. It returned 0 for the whole of its first
day. `NetCombatCard.ToCardModelOrNull` is the accessor that works on an action that has not run, and
it is what vanilla's own play queue uses for exactly that reason.

**The client never let go of a round, and the working duel hid it.** `BeginNextRound` was reached
only through the host-only branch of `TryFlush`, so after its first lock-in a client stayed
`_localLockedIn` forever and its buffer kept round 1's cards for the rest of the match. The
five-round playtest passed anyway, because the *host* holds a client's plays regardless
(`DuelLockInPatch`) — so from round 2 the client was silently submitting blitz-style into a host-side
buffer and the round still resolved. The client's own log says it plainly: `holding … (1 planned)`
appears in round 1 and never again, while the host's restarts at 1 every round.

It stops being survivable the moment anything *reads* the buffer, which is what the reservation
does: a client would have been charged round 1's cards for the whole duel — the "hand refuses to
play anything, with no way to tell why" failure the code comment had already named. Both sides now
release the round on the same condition (both locked in), the client learning it from the host's
`DuelLockInMessage`, which is sent before the flush on a reliable ordered transport and therefore
cannot arrive after the actions it precedes.

**That symmetry is not tidiness, it is the desync argument**, and it is why `CanPlay` is patchable
at all here. `CanPlay` is not a UI predicate — `PlayCardAction.ExecuteAction` re-checks it,
`CardSelectCmd` filters a choice list with it, `WhisperingEarring` picks a card to auto-play from it
— and a reservation is local by construction, so an unguarded postfix would answer differently on
the two sims. Two conditions close it: the patch answers only while
`ActionExecutor.CurrentlyRunningAction` is null (every sim caller above runs inside an executing
action, and *nothing* executes during planning because every play is buffered), and both clients
hold an empty buffer before the round's first action executes on either.

**The lock-in turn model itself is playtested (2026-08-12).** A five-round turn-based duel end
to end on two clients: rounds resolving interleaved, turns rolling over, an HP finish with correct
paired result screens, and zero mod errors on either side. Picking `1v1 Duel: Turn-Based` now plays
turn-based. The four ordering constraints that took four attempts to find are written up in
DESIGN §7 — read them before touching the round loop.

**BLOCKING DESIGN QUESTION, found by playing 2026-08-12: draw cards are close to dead in
turn-based.** You plan the whole round from your opening hand, so a draw card does not resolve
until lock-in — the cards it draws arrive *after* planning is over, and then the round ends and the
hand is discarded. You pay energy for cards you can never plan with. This is inherent to model B,
not an implementation bug, and it is exactly the kind of thing DESIGN said could only be found by
playing.

Four options, none free:

| Option | Cost |
|---|---|
| Accept it | Draw is dead weight in one mode only, and the card pool is shared with blitz |
| **Resolve draws at plan time** | Drawing is private, so nothing leaks — but the plan-time effect and the resolved action must not both draw, and that split is where it desyncs |
| **Two planning passes** — plan, resolve draws only, plan again, then resolve | Coherent and closest to how the cards were designed; doubles the round's ceremony |
| Ban draw cards from turn-based runs | Cheap (`RaceNoCoopCardsPatch` already filters at generation) but a real content cut |

**ANSWERED AND BUILT 2026-08-12 (unplayed): a turn holds as many batches as you want.** Chosen by
Lucas from a pitch, and it is none of the four exactly — it contains the one he was leaning toward.
**Locking in commits a *batch* rather than the turn, and an empty batch is what ends the turn.**
Plan two cards, commit, watch them resolve, and you are still in the same turn with the energy and
the hand you have left, including what you just drew. Press with nothing planned and you are
finished; the turn rolls when *both* players are. The button's label carries the rule — `Lock In`
while you hold cards, `End Turn` while you hold none — because there is nowhere else in that UI to
explain it.

Its virtue over the other three is that **nothing is special-cased**: no card is split between a
plan-time effect and a resolved one, nothing resolves twice, and no card needs a tag saying whether
it may resolve early, which is where the desync risk sat in both of the "make draws work" options.
"Two planning passes" is what this degenerates to when a turn uses two batches, so the fixed count
never had to be picked.

Three things hold it together, and any change to the loop has to keep all three:

1. **Being finished is sticky for the turn.** Otherwise a player out of energy would be waited on
   again for every batch the other one takes, and the turn would hang. `done` counts as ready.
2. **The end turns are enqueued only on the closing batch.** On every batch, the turn would end
   after the first one — which is the model this replaced.
3. **Planning reopens when the batch has *resolved*, not when it was enqueued.** `DuelPace.WatchBatch`
   waits for the action queue to drain. Reopening at flush time would hand both players a planning
   window during the resolution they are meant to be reading — and, since the clocks now stop while
   a batch resolves, a free *thinking* window, which is the kind of hole a competitive mode gets
   played through.

That watcher has one trap already handled and worth not re-discovering: the executor skips a
**cancelled** action before firing `BeforeActionExecuted`, so a batch whose every play was cancelled
executes nothing at all. Hanging on that would leave the hand live, the button dark and no way to
commit — a soft lock with no error — so the wait for the batch to *start* is bounded and giving up
reopens planning.

### Playtest notes from the two-player session, 2026-08-12

- **Tick-paced blitz is now the most interesting open idea** — scoped as **M8.5** in DESIGN §7.
  Short version: in blitz you cannot see *what* the opponent played, only that something happened,
  so there is nothing to react to. Resolving one card at a time on a fixed cadence (OSRS's 0.6s
  tick is the reference) makes each play a readable event. It is a third turn model and slots into
  the `IDuelTurnModel` seam that already exists.
- ~~**The opponent's relics are not shown in the deck review.**~~ **Built 2026-08-13, unplayed**
  (`DuelEntryRelics`). They ride on `DuelArrivedMessage` beside the deck, for the reason the note
  gave: the race decouples the two runs, so your copy of their relics is **stale** and this has to
  be *sent*, not looked up. Three things worth knowing about it:
  - **`NRelicHistory.LoadRelics` is the worked example and it is followed step for step** —
    `RelicModel.FromSerializable`, `DeprecatedRelic` as the fallback for an id this build does not
    know, **assign `Owner`**, then `NRelicBasicHolder.Create`. The owner assignment is the
    non-obvious one: the holder's hover tip reads through the model, and vanilla sets an owner in
    the one place it draws relics that are not attached to a live player. Nothing is added to the
    opponent's real relic list.
  - **`NRelicBasicHolder`, not a bare `NRelic`** — the holder brings the hover tip. A row of
    nameless icons satisfies "show their relics" and answers nothing.
  - **The row is positioned from `_infoLabel`'s resolved rect, not from constants**, and the rect
    is logged. Nobody has seen where it lands; if it is wrong, the log line says what it was told
    to sit above. (This project has already reverted one placement "corrected" from a screenshot.)
  - The new field goes **last** in `DuelArrivedMessage`'s hand-written `Serialize`/`Deserialize`,
    for the same positional reason message ids go last.
- **"Does a client pull in the host's mods automatically?" — no, and not in this game.**
  Answered 2026-08-12 against the decompile so it does not have to be wondered about again.
  `Core/Modding` has a `workshopId` field for identifying a Workshop-sourced mod but no subscribe
  or download path, and `JoinFlow` *refuses* a mismatch outright with
  `ConnectionFailureReason.ModMismatch` — which is the "Mod mismatch!" popup. SpirePvp is not
  distributed through the Workshop at all, so even if the engine had that feature it could not
  apply. Both players build from the same commit; there is no way around it.

### Two bugs found 2026-08-12 — the lobby row one is FIXED (2026-08-13), the other is below

- ~~**The lobby's radio rows can be emptied.**~~ **FIXED 2026-08-13 (`DuelModifierMinimumPatch`),
  unplayed.** Unticking the last option in a group left *no* selection, and an empty turn-model row
  means the run carries no `DuelBlitz`/`DuelTurnBased` modifier — so `DuelMatch.IsPvpRun` is false,
  `OnRunCreated` bails, and two people start an ordinary co-op run having just configured a match,
  with no sign until none of the duel exists. Vanilla is not wrong, it is answering a different
  question: `UntickMutuallyExclusiveModifiersForTickbox` opens with `if (!tickbox.IsTicked) return;`,
  so its mechanism is "ticking unticks the siblings" — *at most* one, which is all vanilla needs
  because its own exclusive modifiers are optional. Ours are decisions, so the minimum is ours to
  add. Postfixed on that method rather than on `AfterModifiersChanged` so the correction lands
  **before** `EmitSignal(ModifiersChanged)`, which is what `DuelLobbyPanel` broadcasts to the client
  — the peer is told the corrected row rather than a momentarily empty one. Vanilla's own group is
  left strictly alone. Re-ticking cannot recurse: `NTickbox.IsTicked`'s setter assigns the field and
  flips two images, and does not raise `Toggled`.
- ~~**The energy counter by the end-turn button is not visible at all** in a duel.~~ **Not
  reproducing — Lucas can see the orb in a duel (2026-08-12).** Left here because the note sent one
  investigation down a blind alley: nothing in the mod touches that widget, and its absence would
  have had no log signature either way.
- ~~**Cards reported still playable past 3 energy spent.**~~ **Real, and fixed 2026-08-12** — the
  reservation had no consumer at all (see the top of this section). `DuelPlanEnergyPatch` now
  spends it.

What is left, in order:

1. ~~**Energy reservation.**~~ **Built 2026-08-12** — `DuelPlanEnergyPatch`, mirroring
   `HasEnoughResourcesFor` against the buffer, raising vanilla's own `EnergyCostTooHigh` so the cost
   turns red through `CardCostHelper` rather than inventing a second way to say "no". Unplayed.
2. ~~**Presentation for held cards.**~~ **Built 2026-08-12** — `LockInPlanView`, and both surfaces
   are vanilla's own, because a held play and a co-op play awaiting the host's ordering are the same
   thing: submitted, not yet resolved. `NCardPlayQueue.OnLocalCardPlayed` files the card in the play
   queue as it is planned (which also means the card leaves the hand, so it cannot be planned
   twice), and `NEndTurnButton`'s ready-icon says who has locked in — the model answers
   `ShouldDisplayPlayerIcon` because `IsPlayerReadyToEndTurn` cannot: the end turn does not execute
   until the flush, so the one stretch of the round where the question is live is the one stretch
   vanilla has no answer for. Unplayed. **A planned *potion* still looks like nothing happened**
   (`UsePotionAction` is `CombatPlayPhaseOnly`, so it is buffered too, and the queue is a card
   strip) — the same gap, smaller.

**The queue keys a play on action identity, and a planned play crosses the wire**, so on a client
the object that executes is not the object that was planned. `DuelPlanQueuePatch` handles both ends
of that: vanilla must not file a card the plan already filed, and a *cancelled* play (a card whose
target died, a hand discarded by an earlier card in the round) has to be removed by card model,
because its by-identity lookup misses on a client. `RemoveCardFromQueueForExecution` keys on the
model already and needs no help — worth knowing which of the three does which before touching it.

### The playtest this needs

One turn-based match, and the whole checklist is in the first two rounds. `Duel: Turn-Based` +
`Race Clock: 1` gets you to the arena fast; `duel now` from **exactly one** player is quicker still.

- **First, the thing that has never worked: the client's cards must not resolve when the client
  locks in.** Both players plan, and whoever locks in *first* should see nothing happen until the
  other does. Then the round resolves interleaved — one of yours, one of theirs, alternating, host
  first — which is a thing no playtest has seen yet. In the host log, every
  `holding opponent's PlayCardAction` must **not** be followed by an
  `[ActionQueueSynchronizer] Enqueueing action` for that same card until `resolving round`.
- **Keep planning past your energy.** Cards you cannot afford should go red-cost and refuse to be
  played — *not* the "forbidden" icon, which is for a different kind of no. Nothing should fizzle at
  resolution any more.
- **Then end your turn and wait.** Your icon appears over the end turn button, and your hand should
  go dead — nothing playable until the round resolves. When the opponent locks in, their icon
  appears too.
- **Both sides, and check the second round specifically.** The client's round reset is the fix that
  had no symptom, so `lock-in: round closed — N play(s) handed over` must appear in the client log
  *every* round, and `holding … (N planned, M energy reserved)` must restart from 1 each round on
  both.
- **Then let a planned card be cancelled**, if it happens naturally — kill a summon that a planned
  card was aimed at, or plan a card behind one that discards your hand. The card should return to
  the hand rather than hanging over the play area for the rest of the duel. This is the client's
  by-identity miss, so it only shows on the client.

### Also from that playtest: the round is now paced, and the clock stops for it

**A resolved round was unreadable.** The cards queued and resolved correctly and neither player
could say afterwards what had been played at them — six plays drained as fast as the queue allowed.
`DuelPace` now leaves a gap after each play: 1.2s at Normal, 0.6s at Fast, nothing at Instant, taken
from the game's own Fast Mode so the duel speeds up exactly as everything else does. It is
**M8.5's thesis arriving early**, in the model that needed it first — and it is the
delay-the-resolution half of the choice §7 names, not the lengthen-the-animation half.

Paced **on each client from that client's own preference**, which is why it is a local
`ActionExecutor.Pause` rather than the host releasing one action per tick as M8.5 sketched: host
release would have made the host's personal setting decide the pace on both screens and handed the
client's reading window to network jitter. Pacing locally cannot desync — it changes *when* a
client executes, never what or in what order. Leaving the executor paused would be a hard lock, so
the unpause is in a `finally` and teardown clears it unconditionally.

**And the duel clock was measuring the wrong thing in this mode.** It paused a player's clock on
`IsPlayerReadyToEndTurn`, which only becomes true when `EndPlayerTurnAction` *executes* — at the
flush. So in turn-based you locked in, watched your opponent think, and were charged for all of it.
**Third instance of the same trap** (the phase test in `DuelClockService`, its duplicate in
`DuelFlag`, now this): a condition that correlates with the one you mean until a new mode separates
them. It asks the turn model now, and counts resolution as committed for *both* players — which
matters far more with a paced round than an instant one. Missed until now because both turn-based
sessions ran on `Duel Clock: Off`; **test this one with a duel clock on**.

**Confirmed working 2026-08-12:** the lock-in icons show over the end turn button on both sides.

### Two more from that playtest, both unfixed and both about the same property

**You cannot back out of a lock-in.** `NEndTurnButton.CallReleaseLogic` sets the button
`Disabled` on the click and only offers *Undo End Turn* while
`CombatManager.IsPlayerReadyToEndTurn(me)` is true — which under this model is false until the
flush, a whole round later. So the button is dead from the click until the round resolves, and the
undo path in `LockInTurnModel.HoldRemote` ("opponent backed out of their end turn") has never been
reachable. It matters more now that locking in also greys the hand: a mis-click commits the round.

**It is not just a button.** A client's plays are already at the host by then (`LockIn` forwards
them before announcing), so backing out means recalling them: a message, the host dropping that
player's `_remote` and clearing `_remoteLockedIn`, and the queue view handing the cards back to the
hand. It also wants a **decision** first — whether backing out is allowed at all is a competitive
rule, not an implementation detail, and DESIGN §3.1b does not settle it.

**A hand-selection effect offers your planned cards, and takes their nodes.** Playing Survivor
(discard a card) put the still-queued plays back in the hand as eligible picks. This is not a bug
in the queue view: a planned card is **still in the Hand pile** until it resolves — that is the same
fact `PlayCardAction.ExecuteAction` relies on when it cancels a play whose pile has changed — so the
selection screen is right to offer it, and `NCard.GetNodeForCard` finds our queued node
(`hand.GetCard(card) ?? playQueue.GetCardNode(card) ?? …`) and pulls it into the grid.

**Do not fix it by filtering the list.** A card selection travels as a player choice keyed by
*index*, so a list the two clients build differently is a desync — and the client cannot know the
host's plan anyway. The selection is *right* to offer the card; the player just could not see which
one it was.

**Marked instead, 2026-08-12 (`DuelQueuedCardHighlightPatch`, unplayed).** Anything the grid shows
that still has a node waiting in the play queue gets vanilla's own `HighlightCard` ring, so
discarding a Defend you have already queued is at least a visible choice rather than an invisible
one. Note the predicate: **the queue, not the turn model's buffer.** By the time a selection opens
mid-resolution the batch has been flushed and `_local` is empty — the model has already forgotten
what was planned, while the queue still holds the node.

Still open: whether discarding a queued card should cancel its play *loudly* rather than silently.

### M8.5 slice 1 is in (2026-08-12, unplayed): real-time is paced now

**`1v1 Duel: Real-Time` no longer means unpaced blitz.** `TickTurnModel` replaces
`BlitzTurnModel` in that slot: your first play goes instantly, everything after it leaves on a
**0.4s cooldown**, and what you click in between is *queued* rather than lost — drawn in the play
queue exactly as a planned card is in turn-based. `BlitzTurnModel` stays in the tree as the seam's
trivial case ("never defer") but nothing selects it.

**Watch the naming.** *Blitz* and *Rapid* in the Duel lobby are **clock presets** — chess terms for
bank size — and are untouched by any of this. The turn model called `DuelBlitz` in code is the one
that changed, and its lobby entry already read "Real-Time"; its *description* is now wrong
("actions resolve in the order they are made, so speed decides trades") and wants a `.pck` change
when slice 2 lands.

**Speed is no longer a personal preference inside a duel** (`DuelFastModePatch`, 2026-08-12).
Reported after playing slice 1: plays still felt instantaneous, "it should be a feelable delay even
in fast mode", and — the better half of the observation — "maybe fast mode should be fixed across
host and client? otherwise there's an advantage?". It is an advantage: vanilla sizes nearly every
wait through Fast Mode, so a player on `Instant` sees the board settle while their opponent is still
watching a card fly, which in a real-time duel is reaction time bought from a settings screen.

Both clients now read `Normal` for the length of the duel. **The getter is patched, not the stored
value** — writing the preference would mean writing it back through every route out of a duel, and
a mod that leaves someone's settings changed is a bad mod. Our own beat stopped scaling with Fast
Mode at the same time and takes its length from the turn model instead: 1.2s for a lock-in batch,
which is a recap, and 0.45s for the paced mode, where it is a reaction window that has to sit near
the cooldown or plays stack up behind their own animations.

**Left open deliberately: the race.** The same argument applies to a *timed* race, and forcing a
speed there changes how a whole act feels. It wants its own decision rather than being smuggled in
with this one.

**Slice 2, second attempt (2026-08-12, unplayed): fair ordering, not wall-clock buckets.** The
first attempt bucketed plays by 0.4s ticks and changed nothing observable, and the log said why —
`booked … for 1 into tick 0/4/5` against `booked … for 1001 into tick 10/11`. **Two players rarely
act inside the same 0.4s, so bucketing by time is ordering by time.**

The case that settled it: one player had played three cards, the other then played their *first*, a
Defend, and it "got stiffed" — the Strike "hovering in the air" while the Defend waited. Nothing was
out of order by time. It was out of order by **fairness**: with one executor and a readable dwell
after each play, three cards own the stream for over a second, so a first play arriving during them
is not instant. It is fifth.

`DuelPlayScheduler` now keeps **one card in flight at a time** — nothing is enqueued while the
executor is working, so the choice of who goes next is made fresh instead of frozen into ids
seconds earlier — and picks by **each player's own position in their own queue**. Your first beats
their second; your second beats their third; ties go to initiative. Indices reset once the pool
drains and the board is still, so each exchange starts even.

**The old note, kept because the reasoning still holds:** Reported from the
first paced playtest — two Defends queued a clear second before the opponent's Strike still did not
land first — and the engine explains it exactly. `ActionQueueSet` does keep one queue per player,
and then `GetReadyAction` flattens them by taking the globally lowest action id, which the host
hands out **in arrival order**. The per-player structure collapses into "whoever clicked first".

`DuelTickScheduler` now books every play a tick taken from *its own player's* next free slot, so a
player firing three cards in half a second occupies three consecutive ticks of their own and cannot
push the other player's cards later. **Ties inside a tick go to initiative** (M9), which is the part
that matters: bucketing alone removes only the sub-tick slice of the host's advantage, since the
host's own requests never cross the network, and ordering a shared bucket by arrival hands the whole
problem back.

**The seam that looks right and is a desync: `ActionQueue.isPaused`.** `GetReadyAction` skips a
paused queue and takes the other player's action instead, so the engine really can run two queues
independently — but pausing changes *which action executes next*, which is sim-visible. A client
pacing its own queue on its own wall clock would diverge from the host within a card. Per-player
cadence has to be decided once, by the host, and expressed in the ids it assigns.

Two consequences worth knowing before touching it: **the host's own plays go through the scheduler
too**, including the instant first one — reaching `RequestEnqueue` directly would enqueue it in
arrival order, for the play most likely to decide an exchange. And **the executor beat is off for
this model** (`BeatSeconds => 0`): the tick *is* the rhythm, and a global beat on top paces the
stream twice while halving each player's cadence, which is the thing slice 2 exists to stop.

**A turn model must never defer an action the sim raised.** The rule is right. **The predicate
written for it on 2026-08-12 was wrong, the evidence for it was a log-ordering artifact, and it cost
the client a card of tempo per exchange until it was replaced on 2026-08-12 (unplayed).**

The guard was `ActionExecutor.CurrentlyRunningAction != null` — *is something executing* — which is
true when the sim raises an action **and** when a player clicks a card while another card resolves.
In a paced real-time duel the second is not an edge case: with a beat after every play, someone
else's card is resolving most of the time. So the local player's clicks skipped `DuelPlayScheduler`
entirely and went into the queue in arrival order. Measured on the host, with the client's plays #1
and #2 sitting in the pool throughout:

```
Executing action: PlayCardAction CARD.DEFEND_SILENT (47148665)   ← client's card resolving
Enqueueing action PlayCardAction CARD.NEUTRALIZE from owner 1    ← host click, never booked
Enqueueing action PlayCardAction CARD.STRIKE_SILENT from owner 1 ← host click, never booked
queue: releasing 1001's play #1 …                                ← client, two host cards later
```

**A client's plays cannot take that route** — they arrive over the wire at
`DuelLockInPatch.BeforeHandleRequestEnqueue`, which books every one — so the guard was a standing
advantage for whoever hosted, and it defeated the 0.4s cooldown at the same time, since a bypassed
play resolves back-to-back with the one before it. Six host plays that session, two of them
bypassed; eight client plays, none. Reported as "first turn felt like client was waiting behind
host", and that is exactly what it was.

**The evidence that motivated the original guard never happened.** The deferred-log line prints
*after* `ShouldDefer` returns, and the paced model's instant first play releases synchronously
inside that call — so a card that was held, released and executed prints "holding …" *below* its own
`Executing action`. The FALLING_STAR pair read as a card rescheduling itself:

```
Executing action: PlayCardAction CARD.FALLING_STAR (9423456)
Player 1 playing card FALLING_STAR (targeting PlayerId 1001)
turn model: holding PlayCardAction card: CARD.FALLING_STAR (9423456) … until lock-in
```

— **same card id on all three lines**, and the sibling log from the same session has that id held 17
lines *before* it executed, through the queued path where the ordering comes out right. One play,
logged out of order. **Check the id before concluding anything from that line's position**; a real
re-enqueue would carry a different one. The line now reads `turn model: deferred …` and carries the
warning in `DuelTurnModelPatch`.

**Ask provenance, not timing**, and the decompile closes the question rather than leaving it to
judgement: every `CombatPlayPhaseOnly` action is built in exactly two kinds of place — a
`Net*Action.ToGameAction` (the peer's, which never reaches this patch) and an input node — except
`GenericHookGameAction`, built only by `ActionQueueSynchronizer` itself, and `ConsoleCmdGameAction`.
`CardModel.EnqueueManualPlay` is the engine's own name for the click path and is reached from
`NCardPlay` alone. So `DuelTurnModel.IsPlayerInitiated` is an **allow-list** of the five click-raised
types, which also keeps a dev command out of the play queue (`holding ConsoleCmdGameAction … potion
POISON_POTION` was real) and defaults anything a future game version adds to vanilla behaviour.

**`DuelPlanEnergyPatch` still asks `CurrentlyRunningAction` and is right to** — it needs to know
whether *the caller* is sim code, which is a question about the stack, not about the action. Same
expression, different question; the "grep for the wrong predicate" rule points here, and this is the
one place it should not be applied.

**Powers do tick down; the report that they do not is not supported by the log.** Asked twice, and
the per-turn power dump settles it: `VULNERABLE_POWER:2` at one turn start, `1` at the next, absent
by the third. The amounts also match their sources exactly — Bash applies Vulnerable **2**, which is
where a "Falling Star applies 1" mismatch came from, and the client's `WEAK_POWER:1,
VULNERABLE_POWER:1` is Falling Star's own pair. If it still reads wrong on screen, suspect the
*display* rather than the model: `NPowerContainer` refreshes off `PowerRemoved`, and a duration
ticking from 2 to 1 removes nothing.

**One slice remains:**

3. Ordering is still arrival order today, so the host keeps its
   inherent half-RTT edge. Bucketing alone does not fix that — it removes the *sub-tick* part and
   leaves the question of what orders two plays inside one bucket. **Order within a bucket by
   initiative** (the M9 rule that now exists) and the edge is gone rather than shortened. Slice 2
   also wants in-flight plays tracked through to execution, which closes the reservation window
   noted on `TickTurnModel.ReservedEnergy`.
**The opponent's queue on the wire**, drawn on their side. Their unsubmitted plays are not
   broadcast today, so you can only see what has already been released — at most 0.4s of warning,
   which is not enough to read or answer. This is a deliberate change to the information rules
   (DESIGN §1) and was decided as such.

The three patches that serve a deferring model now ask for `IPlanningTurnModel` rather than for
`LockInTurnModel` — energy reservation, the play queue, and the queued-card highlight are owed by
any model that holds plays, and a second such model is exactly when that stops being an
implementation detail.

### M9's initiative is in (2026-08-12, unplayed)

**Whoever reached the arena first leads, and it alternates every turn after** — Lucas's rule,
replacing fixed slot order, and shown as a gold arrow bobbing over the leading duelist for the whole
turn. During planning, deliberately: that is when knowing it changes what you do, and a banner as
the batch resolves would arrive too late to use.

**The opening turn was inverted in the paced model, found by playing 2026-08-12 and fixed
(unplayed).** Reported as "still felt like it was waiting for the other player first on turn 1", and
the log says it in two lines: `paced: opening initiative to 1 (reached the arena first)` and then
`initiative: 1001 strikes first this turn` on turn 1, with player 1 leading turn 2. So the arrow
alternated correctly and started on the wrong side — which is why it reads as a feel rather than a
fault.

**Two counters with different meanings and the same parity test copied between them.**
`LockInTurnModel` counts turns **closed** (`_turnsClosed`), which is 0 for the whole opening turn, so
`% 2 == 0` means "the first turn" and is correct there. `TickTurnModel` counts turns **started**, and
`OnTurnStarted` increments *before* anything reads `CurrentLeader` — so turn 1 was odd and the
opening initiative went to whoever reached the arena **second**. It now takes the parity of the turn
number, clamped, because the scheduler reads it before the first `TurnStarted` too.

**It is not cosmetic, for a reason worth knowing:** `DuelPlayScheduler` breaks ties on
`CurrentLeader`, and ties are the common case (below), so an inverted opening turn hands a turn of
tempo to the wrong player.

**Open, and a design call rather than a bug: the per-player index rarely decides anything.** The
scheduler resets `_nextIndex` whenever the pool drains, and the pool drains after most releases —
each player's own 0.4s cooldown spaces their plays out, so they arrive one at a time rather than as
a burst. Counted over two duels: **13 of 14 bookings were `#0`** in the clean run, and 8 of 12 in the
run before it, where the host's bypassed plays kept the pool backed up long enough for the client to
reach `#1` and `#2`. So the tie-break — initiative — is what orders most contested plays, and "your
first beats their second" is the exception rather than the rule.

**REPLACED 2026-08-12 (unplayed): ordering is a timeline, not a fairness rule.** Lucas, after
watching a burst beat a single card: *"it is not like a fairness rule… it is a matter of laying the
events out on a timeline. If you play 3 cards in a burst, card 1 happens at 0 seconds, then 2 at .5,
then 3 at 1. Player 2 plays a card at .3 seconds, that should beat"* the second one.

He is right, and the per-player index was a **proxy for that which could invert it**: play at 0 and
0.5 against a single play at 0.6, and the index made the *later* card win for being its owner's
first. A quota, not a chronology, and the inversion is the kind a player notices.

So each play now carries a `PlayAt` — the moment it was made, pushed later only by its own player's
0.4s cooldown — and the earliest one resolves next. **The cooldown moved from `TickTurnModel` into
the scheduler**, which is the whole fix: held in the model it could only delay your *own*
submissions, so a burst reached the pool as three separate "now"s and the opponent's click was
ordered behind all of it. Applied by the host to both players, a burst occupies 0 / 0.4 / 0.8 of its
owner's line and a click at 0.3 falls between the first and the second.

**A play also waits for its own moment even when the board is idle**, and that is not an
optimisation to remove: releasing the burst's second card as soon as the executor was free would
consume the slot the 0.3s card should get, deciding the order before the other player's card
existed.

The index survives in the log only, so `#N` still reads as "that player's Nth", and the release line
now carries `at +Nms` — how long the cooldown held it — which is what tells you the spacing is real.

**Ties are now genuinely ties: 60ms or less apart**, below which "who was first" is jitter and a
frame of network rather than a fact. Those go to initiative, alternating within the turn: Because ties are
the common case, a tie-break that always went to the initiative holder did not mean "you strike
first this turn" — it meant **"you win every trade this turn"**, compounding across the turn's
exchanges (your Strike before their Block, repeatedly) and then inverting wholesale on the next
turn. Far more than reaching the arena first was meant to buy, and nobody decided it; it fell out of
the tie-break.

So the leader takes the turn's **first** tie, the other player its second, and so on, reset at each
turn start. Initiative now means exactly what the arrow over the duelist claims. Deterministic and
host-side, so it cannot desync. A seeded coin flip would have been equally safe and was rejected for
a different reason: *"why did my card lose"* has to have an answer a player can plan around.

The release line now names why it won — `[lowest index]` or `[tie N this turn → initiative
/ alternated]` — so the next playtest can be read rather than felt.

**What to watch in the log now**, since the index no longer decides anything: a booking reading
`pending at +400ms` (or +800) is the cooldown spacing a burst, and a release reading `[earliest]`
after it is the timeline ordering working. A release reading `[tie …]` means two plays landed inside
60ms of each other, which should be rare and is the only place initiative touches ordering at all.

Three things about it worth not re-deriving:

- **Arrival order is decided by the host and rides on `DuelStartMessage`.** It is not a local fact:
  each client knows when its own arrival happened and when the other's message reached it, so on a
  slow link both can honestly believe they were first. This is the field that message was left
  empty waiting for — "a field nobody reads is worse than no field".
- **The alternation counts turns, not batches.** Per batch, a player could commit a throwaway
  one-card batch purely to flip who leads the next one, turning initiative into something you
  manipulate by splitting your turn rather than something you earned in the race.
- **The arrow is a `Polygon2D` built in code**, so it needs no art, no scene and no `.pck` change.
  It hangs off the creature's node and is placed by `GetTopOfHitbox`, vanilla's documented anchor
  for aligning UI to a creature — *not* the `IntentContainer`, which `NCreature` hides. Swapping in
  a texture when there is art is one line.

**Also fixed: you no longer press End Turn with a dead hand.** Reported as "having to click end turn
when nothing's playable and couldn't play in the first place" — the batch model was charging the
common case for the rare one, since energy is a *turn* resource but planning is a *batch* activity.
A player with no playable card and no potion has their turn closed for them, through the button's
own `CallReleaseLogic` so it is the same event a press is. A potion stops it: drinking is exactly
what you do once the energy is gone.

### Three fixes from the first batched playtest (2026-08-12, unplayed)

- **Cards showed unplayable when they were playable.** The dead-hand rule asked
  `DuelPace.IsResolving` — "is the action queue busy" — which is a *correlate*. The queue is also
  busy during a planning window: a card that pauses for a player choice resumes after the drain
  that carried it has finished, so the log reads `batch resolved, planning reopens` and then, two
  lines later, `Executing action: PlayCardAction CARD.SNAP`. That straggler greyed the player's
  whole hand while they were planning the next batch. The condition meant is "I have committed and
  the batch has not been handed back yet", which is ours to know rather than the engine's to be
  asked: `LockInTurnModel.ResolvingBatch`, set at the flush and cleared when the batch comes back.
  **The same flag now drives the clock**, so the hand and the clock cannot disagree about whether
  you are on the move. *(Same trap as the clock's phase test and `DuelFlag` — that is four.)*
- **The two play queues sit on opposite sides now**, yours left and theirs right. Vanilla stacks
  every queued card into one strip, which is right for four co-op teammates and wrong for two
  duelists. It mirrors vanilla's own `Vector2.Left * 300 * num` offset rather than inventing a
  layout, so the fan keeps its spacing and curve.
- **The initiative arrow drifted off the top of the screen.** Two looped tweens with `AsRelative()`,
  one up and one down on a delay, do not cancel — they loop on their own schedules and leave a net
  10px climb per cycle. "Way too high" and "disappeared randomly" were one bug: it climbed away and
  came back only when the next turn rebuilt it. One tween, two absolute steps.

**Still open, deliberately parked by Lucas:** turn-based "feels weird" in a way neither of us has
pinned down yet — his words, "the interleaving of energy and planning is wonky in some way". Two
things it is *not*: the energy orb is visible in a duel (so the older "energy counter missing" note
is stale), and it is not supposed to tick down as you plan. Revisit after more play rather than
guessing at it.

### The old M8 note, kept for its scoping

**Everything from the 2026-08-12 session is built and playtested.** The loop, the desync fixes, the
result screen, rematch — all confirmed in play on both clients, with the only errors in either log
being vanilla's `Error deleting path …current_run_mp.save.backup`, which is noise and predates this
work. Patch count was **69 classes / 107 methods** at the time of that note (80/118 now); confirm
that line on every launch.

Closed and confirmed this session, so nothing below needs re-testing:

| Fix | Evidence |
|---|---|
| Stale peer choices desynced the duel | `dropped 1 stale peer choice(s) held by the race` on both, then a full duel with no divergence |
| A desync gave *both* players the win | `DuelEndReason.Desync` voids it as a draw (unplayed — needs a divergence to provoke, and those are now rare) |
| Only your own portrait at Neow | Both now sit on the map's starting node |
| Winner's line missing on the summary screen | `victory line re-shown on the summary screen` |
| Score lines shoved up when badges arrived | A sized spacer above the grid; measured settled at grid `(0, 40)`, badges `(0, 294)` |
| **Rematch** | Offer/accept, same seed both sides (`seed 'MAPF5LWGZS9E'` twice), transport held open through teardown |
| Rematch vote marker, and greying out when the opponent leaves | Confirmed from both seats |

**M8 slice 1 is in and is deliberately a no-op:** `IDuelTurnModel`, `BlitzTurnModel` and
`DuelTurnModelPatch` — the gate on `ActionQueueSynchronizer.RequestEnqueue` — are live, and blitz
answers "never defer", so the seam is exercised by every existing match before its alternative
exists. `turn model: blitz` appears once per duel.

**Slice 2 is the lock-in model itself**, and the design is settled in DESIGN §3.1b: interleaved
submission (A1, B1, A2, B2 …) starting on fixed slot order. The shape:

1. `LockInTurnModel.ShouldDefer` returns true for the local player's play-phase actions, buffering
   them instead of submitting.
2. A lock-in control — the end-turn button is the obvious host for it — and a message saying so.
3. On both locked in, **the host** interleaves the two buffers and enqueues each play with
   `ActionQueueSynchronizer.EnqueueAction(action, actionOwnerId)`. That is I5's finding, proven in
   M3 research and unused since: the public `RequestEnqueue` hardcodes the host's own id, but the
   private `EnqueueAction` takes an owner, and a **client cannot** spoof one (the host derives it
   from `senderId`), so the flush must originate host-side — which is what determinism wants anyway.
4. The client's buffered plays have to reach the host as data: `GameAction.ToNetAction()` is what
   `RequestEnqueueActionMessage` already uses, so a list of those is the wire format.

**Do not tune the order** (DESIGN §3.1b). Fixed slot order is arbitrary on purpose; the seam where
initiative belongs is named in §3.1b and the candidate is Lucas's first-to-arena, alternating each
round, in M9.

### Two known gaps, neither blocking

- **The killing blow hangs in mid-air behind the result screen.** Root-caused, **audited
  2026-08-13, still not fixed.** `DuelEndCombatPatch` skips `CombatManager.EndCombatInternal`
  wholesale — the `RunManager.EnterRoom` trap at the other end of the combat — and it now carries
  the step-by-step table that treatment asks for: all 24 of vanilla's steps, each marked done,
  skipped or added, with the reason. **Read it in the patch rather than here.**

  Two things came out of the audit worth surfacing:
  - **The prime suspect is `CombatEnded?.Invoke(room)`**, vanilla's last line and the signal
    `NPlayerHand`, `NCombatUi`, `NCreature`, `NTargetManager` and `NCombatRoom` all subscribe to in
    order to wind the presentation down. **It was deliberately not added**: `NCardPlayQueue.AnimOut`
    hands queued cards back to the hand, three `NCard` double-frees per match are already on the
    books from that path, and raising an event that plausibly runs it a second time — unplayably —
    risks making a known bug worse to fix a cosmetic one. **Find the first free before adding that
    line**; the two items are the same investigation.
  - Two inert steps *were* added (`PlayersTakingExtraTurn.Clear()`, `NHoverTipSet.Clear()`), and two
    more are named as real omissions rather than choices: `Hook.AfterCombatEnd` (how relics reset
    themselves) and `WriteReplay(stopRecording: true)`. Both are latent only because the run ends
    here — **and rematch keeps the process alive across runs**, which is what turns "harmless at
    teardown" into a bug in the next match.
- **The badge teardown guard is still unexercised**, and for a reason worth knowing: the Main Menu
  button does not appear until the badges have finished animating, so the window the guard covers
  cannot be reached by clicking it. Find the route (Continue? Escape?) before assuming the guard
  works, or drop it as unreachable.

### Also unplaytested: both portraits on the map from the start

Raised 2026-08-12 — at Neow you saw only your own icon. **Neither player has a map coord yet**:
`CurrentMapCoord` is the last entry in `_visitedMapCoords`, and that list is empty until a room is
entered, so `RaceMapPositionPatch` answered "nowhere" for both. An unmoved run is standing on
`ActMap.StartingMapPoint` by definition, so each side now defaults the other there until the first
real report arrives.

**No message was added, and one was tried and backed out.** Broadcasting the starting position
from `OnRunLaunched` announces a fact the receiver can derive, and it cannot fire any earlier than
the default does anyway. Worth remembering as the counter-case to the rule below: *hook the
arrival too* applies to state the peer cannot work out for itself, and this is not that.

**Deliberately not built: the opponent's icon on the Neow blessing options.** Decided 2026-08-12.
The logs rule it out twice over, and both reasons are worth keeping:

1. **The data is not on the wire.** Neow is `shared: False`, so no `SharedEventOptionChosenMessage`
   is sent; each client logs only `Local player chose event option index 0` and its own
   `Option index 0 chosen for player <self>`. Neither client knows what the other picked.
2. **Even with the data, the index means different things.** The vote UI is keyed by option
   *index*, and Neow is filtered per character (DESIGN §1). Measured on the host in one run: player
   1 was offered Nutritious Oyster / Scroll Boxes / Hefty Tablet, player 1001 Kaleidoscope /
   Neow's Torment / Neow's Bones — **no overlap at all**. A portrait on your index 2 would name a
   blessing that was never on their list.

`RaceNoOpponentVoteIconsPatch.NoOpponentEventVote` predicted exactly this ("two different events
whose indices happen to coincide would put their portrait on your option") and suppressed it as a
precaution; the logs have now confirmed it live. Reviving it would need a message carrying the
blessing **by name** and a presentation that is not the index-keyed portrait — plus an
information-rule decision (DESIGN §1), since a live Neow readout tells you their opening plan
before the race has begun.

### Then: three things built but never played

Everything the 2026-08-11 two-player session raised is closed, and disconnect handling is done
and playtested on every route. What is left from 2026-08-12 is small and needs a screen rather
than a fix:

1. **The result-screen wording**, shortened so it fits on one line. The box under the banner is
   459px at font 24 — about 38 characters — and several lines ran to 45–58, so they wrapped and
   looked wrong. Every phrase is now ≤38 characters. Any ending shows it; a resignation is
   quickest. (The font was *measured* and was never the problem: `font=24 … was font=24`.)
2. **The badge-teardown guard.** Click off the result screen while the badges are still
   animating — the log must not say `duel badges failed`.
3. **The console idempotence guards**, deliberately unplayed at Lucas's request: `duel now`
   twice should answer "Already in the duel arena", `duel start` twice should not reopen the
   entry screen, `race on` twice should decline the second.

### Then pick a milestone

| Work | Size | Notes |
|---|---|---|
| ~~**Rematch**~~ | — | **Done and playtested 2026-08-12.** Offer/accept on the result screen, same seed, transport held open through teardown. |
| **Return to lobby** | medium | **Parked 2026-08-12 at Lucas's request, with the research done.** A second result-screen button returning both players to the Duel lobby to change rules or characters. The mechanism is confirmed viable: `StartRunLobby`'s constructor iterates `ConnectedPeers` and adopts already-connected peers, and `HandleClientLobbyJoinRequestMessage` answers with a `ClientLobbyJoinResponseMessage` exactly as it would for a fresh join — so **vanilla's join handshake works over a connection that is already up**. What is left is the ordering: host tears down (disconnect suppressed, as `DuelRematch` does) and opens `NCustomRunScreen` via `InitializeMultiplayerAsHost`, then the client tears down, re-sends the join request, and builds its own screen from the response via `InitializeMultiplayerAsClient`. Unresearched: opening `NCustomRunScreen` programmatically (screen-stack mechanics). Bigger than Rematch, because Rematch could skip the lobby entirely and this cannot. |
| ~~**Rematch (old scoping)**~~ | — | The biggest remaining hole in the loop. Scoped under Open Issues below: the run is torn down by result-screen time, so it needs a handshake, a route into a lobby that skips the menu, and teardown ordering that keeps the transport alive across a run boundary |
| ~~**Per-round damage stats**~~ | — | **Done, and confirmed in play 2026-08-12.** `DuelStats` tracks cards and damage through the duel, broadcasts them as the match is decided, and `DuelResultLinesPatch` draws six `yours · theirs` comparison rows. A per-*round* breakdown is deliberately not built — see below |
| **True rejoin** | milestone | Scoped in `docs/PLAYTEST_LIST.md`. Vanilla's rejoin is half-built and the missing half is the UI; the run-state rule is already decided |
| **Random as a character choice** | small feature | Deferred with a full scope in `docs/PLAYTEST_LIST.md` |
| **M8 turn-based** | milestone | On hold until blitz is polished (DESIGN §7) |

**One decision is still Lucas's**, in `docs/PLAYTEST_LIST.md`: whether the two runs should offer
identical *rolls* or identical *offers*, given that character filtering makes those differ.

### M7 — the dedicated Duel host menu (done; kept for the reasoning)

**Everything that was pending here has been playtested and closed.** The 2026-08-11 session ran
the loop end to end repeatedly and fixed what it found; the result screen, the arena, the
rendezvous, the deck review, resignation and the duel itself are all confirmed working from
both sides. What follows is history worth keeping, not a to-do list.

M7 is scoped below under "Then: M7's dedicated Duel host menu".

### What 2026-08-11 fixed, and the one idea behind most of it

Nine of the eleven bugs that session were the same thing wearing different clothes: **the
engine assumes the party is standing together, and in a race it is not.** DESIGN I3 predicted
exactly this recurrence for "rest sites, shops and events" and all three duly arrived.

`src/race/RaceSolo.cs` is now where that is written down — the two shapes it takes, the places
each has bitten, and the rule to reach for first. Read it before diagnosing any new race-phase
room bug. The short version:

- **A barrier that waits for everyone.** `RestSiteRoom.Exit` awaits every player's completion
  source; `TreasureRoomRelicSynchronizer.PickRelic` returns early until every vote is in. The
  opponent never satisfies theirs, so the room never releases. Vanilla's own
  `OnPeerDisconnected` is the blessed pattern: satisfy the absent player's slot up front.
- **Presentation indexed by player slot.** A slot-1 client gets the second seat, the second
  hand, the second holder. **In a race the local player must present as slot 0.** Where vanilla
  has a real singleplayer path, prefer it to correcting the multiplayer one — checking what the
  engine draws when alone replaced two hand patches with none.

Two more findings from that session that generalise:

- **A missing loc key can wreck a whole screen, not just a label.** `DUEL_ENCOUNTER.title` was
  absent from the `encounters` table, so the *loser's* result screen threw inside
  `InitializeBannerAndQuote` — four lines from the end of `NGameOverScreen._Ready`, skipping
  `_leaderboard.Visible = false` and leaving the daily-run leaderboard drawn over everything.
  The winner took another branch and looked perfect. `EncounterModel` asks for exactly two keys,
  `.title` and `.loss`.
- **Sorting anything once, at duel activation, misses everything summoned later.**
  `DuelLayout.MoveOpponentToEnemySide` ran once and could only sort creatures that existed then,
  so the opponent's Osty — spawned by Bound Phylactery at combat start *and again every turn
  from `AfterEnergyResetLate`* — stayed on the player side, un-mirrored. It presented as a
  facing bug that struck one client and not the other; it was a layout bug, and the logs said so
  outright (`moved 1` against `moved 2`). `DuelLateSummonLayoutPatch` re-sorts on
  `CombatManager.AfterCreatureAdded`, which is documented to run once the node exists.

**And a process note that cost real time in that session:** two placement bugs were "corrected"
from screenshots, and one of those corrections was wrong and had to be reverted. What settled it
was logging both sides' positions and diffing them — the same method the project already uses
for state divergence. Pixels are not exempt from "read the logs yourself".


**Then: M7's dedicated Duel host menu** (decided 2026-08-06). Today a match is
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

- ~~**Three `NCard` double-frees at the end of a turn-based duel**, not root-caused.~~
  **ROOT-CAUSED AND FIXED 2026-08-13 from the log, unplayed** (`DuelEndCombatPatch.KillPendingQueueTweens`).

  **The recorded suspicion was `NCardPlayQueue.AnimOut`, and it was impossible.** `AnimOut` is
  reached only from `NCombatUi.AnimOut` ← `NCombatUi.OnCombatEnded` ← the `CombatManager.CombatEnded`
  event — the one line `DuelEndCombatPatch` never raises. **It cannot run in a duel at all.** Keep
  the lesson: the suspicion named the method whose *description* matched the symptom, inside a file
  the patch had already cut off at the root. "Find the first free rather than assuming" was the right
  instruction and it is what settled this.

  What the log says, in the 2026-08-12 Steam duel: two `Cancelling action PlayCardAction` lines
  (Neurosurge, Putrefy — the opponent's queued plays, cancelled when the killing Squeeze landed) and
  then **exactly two** double-free errors, whose stack is a `Godot.Callable.<From>` trampoline into
  `NodePool.Free` — a tween callback. That is
  `NCardPlayQueue.TweenCardForCancellation`: a 0.5s fade ending in
  `TweenCallback(Callable.From(card.QueueFreeSafely))`. The result screen goes up inside that half
  second, the node is freed with everything else, and the callback then hands an already-freed node
  back to the pool.

  **What rules out the engine's other two tween-callback frees** (`CardPileCmd`'s exhaust fade and
  its card-removal preview, both also `Callable.From(cardNode.QueueFreeSafely)`): they call
  `cardNode.CreateTween()`, so the tween is bound to the node it frees and dies with it.
  `TweenCardForCancellation` calls `CreateTween()` on **the queue**, so its tween outlives the card.
  That asymmetry is the whole bug.

  The fix is the first line of vanilla's own `AnimOut` — kill each item's pending tween — and
  deliberately only that line; AnimOut's remaining work re-tweens and re-parents cards, which is
  the part that could run twice.

  **One correction to what this note feared:** the pool *refused* the second free, so no node was
  ever handed out twice and rematch was never at risk. It is log noise — but noise that made every
  read of a duel log begin by discounting two real-looking errors.
- ~~`HellraiserPower`'s infinite-combo cap misfires in a duel (`HittableEnemies.All(...)` on an
  empty list is vacuously true), capping auto-plays at 9 per turn.~~ **Should be gone as a
  side-effect of the 2026-08-13 AoE fix, unplayed:** the list is no longer empty, so
  `All(c => c.HpDisplay.IsInfinite())` is false against a real duelist and the cap stops firing.
  Noted rather than celebrated — it was called "arguably desirable", so if Hellraiser now runs long
  in a duel, this is the change that did it.
- Other `AfterSideTurnStart` powers may have the same round-late skew poison had. Audit when
  one shows up; only poison is fixed.
- The duel entry screen's confirm feedback is a colour tint standing in for the intended
  green check + opponent portrait (DESIGN §6, wants an asset pass).
- **The killing blow is left hanging in mid-air behind the result screen.** Reported 2026-08-12.
  Cause is known and is the familiar shape: `DuelEndCombatPatch` **skips
  `CombatManager.EndCombatInternal` wholesale** and calls `DuelResult.ShowFor` in its place, so
  every wind-down step vanilla does on the way out of a combat — including whatever retires the
  card currently mid-play — simply never happens. The patch exists because `EndCombatInternal`
  assumes a real map room and NREs in the arena, so it cannot just be let through.

  **Not a quick fix, and it is the same trap as `RunManager.EnterRoom`**: a vanilla teardown
  skipped in one line, inheriting every omission silently. `DuelArena` had to mirror
  `EnterMapPointInternal` step for step and six omissions were found one at a time; this is that
  problem at the other end of the combat. Doing it properly means reading `EndCombatInternal` and
  deciding which of its steps are safe in an arena, with a comment listing each — not adding a
  card-cleanup call and hoping. Worth its own pass.
- **The arena's top-bar icon still hovers as a boss** — *"Boss — the deadliest foe in the
  area…"*. Reported 2026-08-12 and **deliberately deferred, with the seam found so it does not
  have to be found twice.** `NTopBarBossIcon.OnFocus` builds the tip from
  `static_hover_tips` — `BOSS.title`/`BOSS.description`, or `DOUBLE_BOSS.*` while both bosses are
  ahead — and interpolates `EncounterModel.Title`. The *title* is already ours, since the
  encounter is `DuelEncounter`; only the description is vanilla's.

  Neither obvious fix is cheap. `OnFocus` is `protected override`, so it cannot be named with
  `nameof` (the publicizer runs with `IncludeVirtualMembers="false"`) and patching it means a
  string target, giving up the build-error-on-rename property. Rewriting `BOSS.description` in
  the loc table would change the tooltip for *every real boss in the game*, since that table is
  shared. The remaining seam is a patch on `NHoverTipSet.CreateAndShow` swapping the tip when the
  caller is the boss icon and a duel is live — workable, but it is a global UI entry point being
  special-cased for one icon, which is more risk than a tooltip is worth today.

  Note the arena takes the `DOUBLE_BOSS` branch until the act boss is dead and the `BOSS` branch
  after (`ShouldOnlyShowSecondBossIcon`), so a fix has to cover both.
### Art still wanted

The `.pck` currently holds the mod image, the duel node texture and its outline, and two loc
tables. Everything else in the mod is a borrowed vanilla node. In rough order of how much each
would improve the thing:

| Piece | Why, and what exists now |
|---|---|
| **Duel host menu** (M7) | The next milestone. Wants a menu entry and whatever framing the preset/clock controls sit in. Nothing exists. |
| **Modifier icons** | Three would cover it — one per lobby group (turn model · race clock · duel clock), reused across each group's variants. Currently all three borrow vanilla's Draft icon. `DuelModifierBase.IconPath` is the seam: override it on `RaceClockModifier` and `DuelClockModifier` to split them. **Note the `.png` is load-bearing** — `ImageHelper.GetImagePath` only prefixes `res://images/`, and an extensionless path silently falls back to `powers/missing_power`, which is the placeholder that drew three "NOPE"s across the top bar for most of this project's life. |
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

**The result screen is vanilla's game-over screen, and it is now finished.** The banner is
rewritten (`DuelResultBannerPatch`), the run-score lines are suppressed, and **what stands in their
place is built and confirmed in play (2026-08-12)**: `DuelStats` counts cards played and damage
dealt through the duel, `DuelStatsTrackingPatch` feeds it, `Broadcast` sends the pair before
teardown takes the transport, and `DuelResultLinesPatch` draws six rows as `yours · theirs` —
damage, cards, HP, gold, elites, deck size. Measured across a match: `stats sent: 17 cards, 56 dmg`
against `stats received: 1 cards, 6 dmg`.

Two things worth knowing about those numbers, both already handled and both non-obvious:

- **Damage is duel-only and opponent-only.** Gated on `IsDuelActive`, so the race's elites do not
  inflate it, and on `DuelLayout.BelongsToOpponent` rather than `target.Player` — a pet's `Player`
  is null and its owner lives in `PetOwner`, so the plain test counted damage to *your own* summon
  as offence.
- **A summon's damage is credited to nobody.** Symmetric on both clients for both duelists, so the
  comparison stays honest, and "damage you dealt" meaning damage *you* dealt is the simpler thing
  to explain.

**A per-*round* breakdown is deliberately not built.** DESIGN §6 asked for one before the
comparative design existed, and the comparison answers the question a player actually has — *did I
out-damage them* — in one row. A per-round table needs space the flat score list does not have, and
it is the same shape of addition that got the race HUD cut: more numbers, not more insight. Revisit
only if play asks for it.

**Rematch — rescoped 2026-08-12, and the old scoping was wrong on its central premise.**

This section used to open "the run is already over by the time the result screen is up:
`RunManager.CleanUp` has fired, `DuelRunCleanupPatch` has released every handler, the clocks are
reset", and concluded that rematch needed *teardown ordering that keeps the transport alive across
a run boundary*. **`CleanUp` has not fired at result-screen time.** It is called from `NGame` and
`NMainMenu` on the way back to the menu, not when a run ends — a run *ending* is `OnEnded`, which
sets `IsGameOver` and nothing else. The log settles it: the entire result screen, summary screen
included, renders before

```
[ENetHost] Disconnecting client 1001, reason: QuitGameOver
[RunLobby] Disconnected. Reason: QuitGameOver
[Startup] Time to main menu
```

and that disconnect is issued explicitly by `NGameOverScreen.OnMainMenuButtonPressed`, host-side
only. Everything else follows from that: `RunManager.State` is still non-null (only `CleanUp`'s
`finally` nulls it), every mod handler is still armed (`DuelRunCleanupPatch` hooks `CleanUp`), and
the seed and modifiers are still readable off the run being left.

**So the hard part is not keeping the transport alive — it is already alive. The hard part is the
launch path.** A run is started by `StartRunLobby`: the host sends `LobbyBeginRunMessage`
(players, seed, modifiers, act1) and both sides run `BeginRunLocally` → `LobbyListener.BeginRun`.
Two things to resolve there, both known rather than open:

- **Is `RunManager.RunLobby` still alive on the result screen?** It should be — `CleanUp` is what
  disposes it — which would make a rematch closer to "call begin-run again" than to "rebuild a
  lobby". **Verify this first; the whole shape of the work depends on it.**
- **`_isBeginningRun` is latched and never cleared** — `BeginRunLocally` sets it and the guard
  logs "Tried to begin run twice, ignoring second one!". Reusing a lobby instance means clearing
  it. `SetHostIsClosed(true)` and `SetBufferMessages(true)` are set on the same path and want the
  same look.

What is genuinely still needed: a **rematch handshake** (both must agree — `DuelResign` and
`DuelDrawPrompt` already implement exactly this offer/accept shape over the connection that is
still up), and a **button on the result screen**, which is still the only moment that works
because leaving it is what disconnects.

**The seed question is settled: same seed.** Both players have seen the map, so the second run is
pure decision-making, and it is strictly less work — the seed is already in the run being ended.

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
