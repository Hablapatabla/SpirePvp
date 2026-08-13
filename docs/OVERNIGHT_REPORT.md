# Overnight report — 2026-08-13

Branch: **`overnight/2026-08-13`**, unmerged, unpushed. Nothing on `master`.

**Everything here is UNPLAYED.** The only verification available was `dotnet build --nologo -v
minimal`, which is green after every commit, plus reading the decompile. Nothing below has been in a
game, and no claim in it should be read as "it works".

---

## 1. Priority 1 — the AoE family (`HittableEnemies`). BUILT, unplayed.

**Commit:** *An actor for "all enemies", and the getter becomes patchable*

### The evidence question first, because the queue asked for it

The queue said to grep Lucas's logs for `telemetry: HittableEnemies came back EMPTY` and build for
what actually appears. **Nothing appears — not one line, anywhere.**

- `D:\modding\sts2\SpirePvp\logs\*.log` (12 files, 2026-08-12 21:06–22:44): all from builds that
  predate the probe. Zero telemetry lines of any kind.
- `%APPDATA%\SlayTheSpire2\logs\` (5 files): only `godot.log` (2026-08-12 22:59) runs the build that
  carries the probe — it logs `69 patch classes applied cleanly (107 methods)`, a Steam duel against
  a second player (two `765611980…` ids, both Necrobinder), `duel begins: fresh bank of 3 min each`,
  `duel over — WON`. The cards played in that duel are Squeeze, Neurosurge, Putrefy and Photon Cut.
  **No AoE effect was used, so the probe correctly said nothing.**
- One older log (`godot2026-08-12T21.59.11.log`, 63 patch classes, i.e. pre-probe) shows a player
  picking up `RELIC.BAG_OF_MARBLES` — which is where the original report came from.

So the fix was built from the decompile instead. **A silent probe is evidence about the sample, not
about the bug**, and that is worth saying out loud because the queue's plan was to let it choose the
scope.

### What was built

Two new patch classes and one new non-patch class; one patch class retired.

- `src/duel/DuelAoeActor.cs` (new) — resolves *who is asking* for "all enemies", from state the
  simulation itself defines so both clients agree.
- `src/duel/patches/DuelAoeTargetingPatch.cs` (new) — two classes:
  - `DuelAoeTargetingPatch`, a postfix on the `CombatState.HittableEnemies` getter that answers with
    `GetOpponentsOf(actor)` filtered by `IsHittable`;
  - `DuelHookListenerScopePatch`, a postfix on `CombatState.IterateHookListeners` that wraps the
    returned list so the model currently being dispatched a hook is ambient for exactly that stretch.
- `src/duel/patches/DuelAoeProbePatch.cs` — **deleted**, superseded.
- `DuelTelemetry.NoteEmptyAoe` → `NoteUnresolvedAoe`: same rate-limited shape, but it now reports
  only reads the mod could **not** attribute to a duelist, which is the residue worth seeing.

**Patch count 69/107 → 70/108**, updated in `CLAUDE.md` and `HANDOFF.md` in the same commit. That
number is arithmetic; it has not been confirmed against a log.

### The two findings that changed the shape of the fix

1. **At hook time the running action is not merely absent — it is often the *wrong* duelist's.** The
   queue's plan was "resolve the actor from `ActionExecutor.CurrentlyRunningAction`", with the known
   hole that it is null in `BeforeSideTurnStart` (Bag of Marbles). Reading the decompile, a dozen
   hooks that read `HittableEnemies` fire *inside the other player's action*:
   `CorrosiveWavePower.AfterCardDrawn`, `PanachePower.AfterCardPlayed`, `LostWisp.AfterCardPlayed`,
   `GravityPower.AfterCardPlayed`, `SerpentFormPower.AfterCardPlayed`, `Kusarigama`,
   `TingSha.AfterCardDiscarded`, `SurroundedPower.AfterDeath`, … A fix on that tier alone would have
   applied your own poison and Vulnerable **to your own side**, silently, and only when the opponent
   moved — invisible in every solo test. Hence tier A (the hook's model) taking precedence.

2. **Most of the "70 models" were never broken in the way the note implied.** Cards that damage all
   enemies (Dagger Spray, Cleave, Sweeping Beam) already work: their damage goes through
   `AttackCommand.TargetingAllOpponents` → `CombatState.GetOpponentsOf` → `DuelOpponentsPatch`,
   playtested since M2. What was missing on those was only the *rider* — Dagger Spray's impact VFX,
   Thunderclap's Vulnerable. Fully broken were the direct readers: Bag of Marbles, Noxious Fumes,
   The Bomb, Inferno, Letter Opener, Charon's Ashes, Stomp, Outbreak, Misery, Piercing Wail, and the
   random-target picks (`Rng.CombatTargets.NextItem`) behind Beat Down, Bouncing Flask, Tingsha,
   Parrying Shield and Whispering Earring.

### Two approaches evaluated and rejected, with the reason

- **Patch the call sites that hold `Owner`** (what `DuelTargetingPatch`'s own note proposed).
  `PowerCmd.Apply` and `CreatureCmd.Damage` are handed both the target list and the source, so they
  cover Bag of Marbles, Thunderclap, Noxious Fumes and Letter Opener — but roughly a third of the
  read sites iterate the list themselves (`Stomp`, `Outbreak`, `Misery`, `Shockwave`, `PiercingWail`,
  `TwistedFunnel`) or take one element from it (`Shiv`, `WhisperingEarring`, every random pick).
  A command-layer fix would have looked complete and left those dead — the exact failure mode the
  queue's rule 8 warns about.
- **Give each duelist their own `CombatState` view.** Tempting, because `Creature.CombatState` and
  `AbstractModel.CombatState` *are* per-owner getters, so a per-owner proxy answers all 70 sites with
  no ambient at all. Rejected on three concrete grounds: `CardModel.CardScope` does
  `((ICardScope)CombatState)`, so the proxy would have to implement every interface `CombatState`
  does or throw `InvalidCastException` inside the card layer; `CreaturesChanged` subscriptions taken
  on a per-call proxy would never fire; and `Creature.CombatState` is read by hundreds of engine call
  sites a duel has no business touching.

### Known imprecision, not hidden

A hook body that `await`s leaves tier A's ambient set across the await, so anything running in that
window — a card asking whether it should glow gold, a targeting visual — sees the hook's actor rather
than its own. Those readers are presentation and change no state; a *sim* read cannot land there,
because the executor runs one action at a time with hooks awaited inline. Cosmetic if it ever shows.

### What to playtest first

Any duel with an "all enemies" effect. **Bag of Marbles is the cleanest single test** — it is the
original report and it fires at turn start with no card involved (tier A). Then a Thunderclap or a
Haze for tier B. The log line to watch for is the *absence* of `telemetry: an "all enemies" read
found NO ACTOR`; one of those names a case this does not cover, and it prints the running action as
the lead.
