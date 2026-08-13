# Overnight work queue — written 2026-08-12 23:05 by the session that built the items above it

**Read `docs/HANDOFF.md` first, then `CLAUDE.md`, then this.** This file is the *ordered* list for an
unattended session. HANDOFF is still the source of truth for how the code works and which traps cost
this project real time; nothing here overrides it.

---

## The rules for an unattended session, and why each exists

1. **Work on a branch, never `master`.** A second player is pulling from `master` now, and unplayed
   overnight commits landing where they pull is the single way this bites Lucas. Branch name:
   `overnight/<date>`.
2. **Never `git push`.** Pushing is explicitly Lucas's call in this project.
3. **Never claim anything works.** Nothing here can be playtested — Lucas playtests every change, and
   every genuine bug this project found came from him noticing something a log alone did not say.
   Mark every change **unplayed** in its commit message and in HANDOFF.
4. **`dotnet build` must stay green after every change**, and the build is the only verification
   available. If a change cannot be verified by compiling, say so rather than implying it was tested.
5. **Do not start the game.** The launch scripts stop running instances, and Lucas may have one open.
   The rule "never kill the user's running game processes" is absolute here.
6. **Confirm the patch count expectation.** 69 classes / 107 methods as of this file. If a change adds
   a Harmony patch class, update the count in `CLAUDE.md` and `HANDOFF.md` in the same commit.
7. **Leave a report at `docs/OVERNIGHT_REPORT.md`**: what was built, what compiles, what is unplayed,
   what was attempted and abandoned and why. Write it as you go, not at the end.
8. **Prefer stopping to guessing.** An item you cannot do correctly is better left with a written
   scope than half-built. This project's costliest bugs were all plausible-looking half-work.

---

## Priority 1 — the AoE family (`HittableEnemies`), the biggest real gap

**Symptom:** Bag of Marbles applies no Vulnerable in a duel. **Scope:** `CombatState.HittableEnemies`
is `Enemies.Where(IsHittable)`, and a duel has an **empty enemy side** — so *every* "all enemies"
effect in the game resolves against nothing. **70 models read it** (27+ cards: Dagger Spray,
Thunderclap, Shockwave, Cleave…, plus orbs, enchantments, relics). `DuelTargetingPatch` has carried
"STILL OPEN — AOE and random targeting" since M2.

**Do not patch the `HittableEnemies` getter to return something.** It is handed no attacker, so any
answer it invents is a local guess inside sim code — i.e. a desync. HANDOFF has called it unpatchable
since M1 and that reasoning stands. `CombatState.GetOpponentsOf` is the patchable chokepoint because
it is *told* who is asking (`DuelOpponentsPatch` already does this).

**The approach to evaluate first:** resolve the actor from `ActionExecutor.CurrentlyRunningAction`,
which is identical on both sims by construction (both execute the same ordered stream), and is
already used this way by `DuelPlanEnergyPatch` and `DuelTurnModel`. That makes the answer
deterministic rather than machine-local.

**The known hole in that approach, and it is why this is P1 rather than trivial:** Bag of Marbles
applies from `BeforeSideTurnStart`, which runs **outside any action**, so `CurrentlyRunningAction` is
null exactly there. `DuelAoeProbePatch` logs `<no running action>` for precisely this case. So the
fix likely needs two halves: the running-action resolution for card/orb effects, and something
owner-scoped for hook-time effects (a relic knows its `Owner`; patching the handful of relic methods
individually may be correct where a general answer is not).

**Evidence to gather first:** grep the probe's output in any log Lucas has left
(`%APPDATA%\SlayTheSpire2\logs\godot.log` when he plays via Steam, `logs/*.log` via the scripts) for
`telemetry: HittableEnemies came back EMPTY`. Each line names its culprit. Build for what actually
appears before generalising to 70 models.

## Priority 2 — the `RestSiteRoom` version of the arena rest

`DuelArenaRest` (built 2026-08-12, unplayed) heals 30% of max HP on the way into the arena, which is
what Lucas asked for. The *room* version — with the Smith option and a real choice — is scoped in
HANDOFF under "A rest site before the duel". Build it **on the branch, unmerged**, because it needs
the `DuelArena` split described there and that method has produced six quiet omissions already.

Ordering constraints, all three of which must hold: rest before the arrival announcement (that
message carries the deck the review reads); coord move before the rest (`RestSiteSynchronizer` waits
on a co-located party); room construction after everything (`AddVisitedMapCoord` resets `NextRoomId`).

`RestSiteRoom.Exit` awaits `AfterAllRestSitesCompleted()`, which is already a both-players gate.

**Undecided and Lucas's call, so do not decide it:** whether resting spends race-clock time.

## Priority 3 — backing out of a lock-in

Open since 2026-08-12 and it wants a *decision* first, so **scope it, do not build it**: whether
backing out is allowed at all is a competitive rule and DESIGN §3.1b does not settle it. The
mechanism, if it is ever wanted: a client's plays are already at the host by then (`LockIn` forwards
before announcing), so backing out means recalling them — a message, the host dropping that player's
`_remote` and clearing `_remoteLockedIn`, and the queue view handing the cards back to the hand.
`NEndTurnButton.CallReleaseLogic` disables the button on click and only offers Undo while
`IsPlayerReadyToEndTurn` is true, which under the batch model is false until the flush.

## Priority 4 — the opponent's relics in the deck review

Wanted, and small. Note the rule it falls under: the race decouples the runs, so your copy of their
relics is **stale** — this has to be *sent*, not looked up. `DuelArrivedMessage` already carries their
deck for exactly this reason and is the natural place to add them, which keeps the ordering free (the
review opens once both arrivals are in hand).

## Priority 5 — the both-die corner in `DuelRaceDeath`

Built 2026-08-12, unplayed. If both players die within the same moment, each broadcasts a win for the
other and both would see defeat. Rare (separate combats, same second) and the same crossing case
`DuelDrawPrompt` already handles for draw offers — copy that shape.

## Priority 6 — the per-turn index question in `DuelPlayScheduler`

Ordering is now by **timeline** (`PlayAt`: when a play was made, pushed later only by its own
player's 0.4s cooldown), with ties inside 60ms going to alternating initiative. The per-player index
survives in the log only. **Do not change the ordering rule without evidence from a log**; three
attempts have now been made and two changed nothing observable. The evidence that would reopen it is
a booking whose index is not `#0` together with `pending (N waiting)` where N ≥ 2.

## Priority 7 — documentation consolidation

HANDOFF is ~1800 lines and has accumulated several superseded passages today (the per-player index
description, the `CurrentlyRunningAction` guard rationale, the "ordering cannot be playtested solo"
note which is true but was nearly used to explain away a real bug). Tighten *without deleting the
reasoning* — the traps are the value. Any passage you correct, say what it used to claim and why that
was wrong; this project's docs are written that way on purpose.

---

## Explicitly NOT for an unattended session

- **Anything needing a playtest to validate.** The turn models, the scheduler ordering, the arena
  heal, the race-death fix, the initiative arrow, the 0.55s dwell — all unplayed and all Lucas's to
  judge.
- **`git push`.**
- **Deciding competitive rules**: whether resting costs clock, whether backing out is allowed,
  identical rolls vs identical offers at Neow (in `docs/PLAYTEST_LIST.md`).
- **Starting the game or killing game processes.**

## Open questions that need Lucas, listed so they are not silently answered

1. Does resting before the duel spend race-clock time?
2. Is backing out of a lock-in allowed?
3. Identical Neow *rolls* or identical *offers*? (Both players were Necrobinder on 2026-08-12 and got
   different blessings — telemetry now logs `TextKey` per player, so the next log answers what they
   were, but not what they *should* be.)
4. Should discarding a queued card cancel its play loudly rather than silently?
