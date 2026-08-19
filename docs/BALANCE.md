# Balance pass — deferred by design

A parking lot for **tuning and content decisions**, and a rule about when to open it: **not yet.**
The milestone starts only once the loop is fully playable — every known bug closed and the
UX / playability / netcode list drained. Until then this file collects the questions so they are
not lost, and keeps them out of the bug work so neither distorts the other.

## Why this is its own pass, and later

**Balance made against a moving loop is balance made twice.** A decision about whether a potion
should be usable instantly only means something once the turn model, the clock and the reclaim
path are all final; a decision about which cards to cut only means something once the duel plays
the way it is meant to. Tune against a loop that is still changing and every answer gets re-derived
when the ground under it moves — the same trap the netcode notes describe one level down.

**So the gate is playability, not a date.** "Make it playable first" is the whole instruction.
When a balance idea surfaces during bug or UX work, it lands *here* rather than being acted on, and
the loop work continues.

## What belongs here (and what does not)

**Here:** decisions where the code already works and the question is whether it plays *well* — is
this fun, fair, and legible in a 1v1 that vanilla never designed for. Cutting content, retiming an
action, retuning a number, making a working-but-pointless thing worthwhile.

**Not here:** anything that hangs, desyncs, freezes, or reads wrong. Those are bugs or UX and get
fixed now, in their own milestones — a balance milestone that quietly absorbs a correctness bug is
how a "make it good" pass becomes a "make it work" pass wearing a nicer name. If in doubt, it is
not balance.

## Running task list

Seeded from questions already raised; add to it as more surface. Each carries enough to be picked
up cold, and a pointer to where the reasoning already lives.

1. ~~**Which potions are instant vs must be queued, in turn-based.**~~ **BUILT 2026-08-18, and
   the classification turned out not to be a content call after all.** The rule that decides it is
   structural: a potion that touches the *board* is a play and has to be ordered against the
   opponent's; a potion that only changes your own hand has nothing to order against. That question
   is answerable off the models — every potion's `OnUse` was read, and the nineteen that reach only
   `CardCmd` / `CardSelectCmd` / `CardPileCmd` / `CardFactory` resolve on click while everything
   else queues. See `DuelTurnModel.FreeUsePotions`.

   **What is left for this pass is the exceptions, not the rule.** Two are already known:
   `DistilledChaos` is hand-only by call family and excluded by hand because `AutoPlayFromDrawPile`
   plays your cards at the opponent, and anything with a similar gap wants catching by eye. If a
   potion feels wrong on the side the rule put it on, that is the balance judgement — the mechanism
   is a one-line list edit.

2. ~~**Choice-on-obtain relics (BIIIG_HUG and its kind).**~~ **DECIDED and BUILT 2026-08-18
   (Lucas: "ideally let's make it so cards with choices like kaleidoscope or biiig hug work").**
   Of the two options this item offered — make them work, or cut them from the pool — the first
   was taken. `DuelDraft.ObtainAndResolve` awaits `RelicCmd.Obtain` instead of firing it and
   forgetting it, and holds the draft's own screen down for the duration, keyed on the model's own
   `HasUponPickupEffect` rather than a list of relic names.

   Two things that stay decided rather than open:

   - **The choice comes out of the picker's own draft clock.** Asked for explicitly. It is also
     what happens if nothing touches the clock, so the implementation is the absence of a pause.
   - **It resolves at pick time, not deferred to the end of the round.** Lucas offered end-of-round
     as an easier fallback; awaiting in place turned out to be the smaller change, since deferring
     would have meant re-showing a reward set that vanilla had already parked.

   **TRI_BOOMERANG, added 2026-08-19, is the same family and found the gate's false negative.**
   Reported as "tri-boomering relic got skipped". Its `AfterObtained` runs
   `CardSelectCmd.FromDeckForEnchantment` and waits for you to pick a card — and it declares
   `HasUponPickupEffect` **nowhere**, so the gate that holds the draft screen for KALEIDOSCOPE let
   this one straight through. The relic was taken, the choice was reserved, no screen came up
   because the draft was drawing over it, and the await never returned: no error, no `obtained`
   line, and to the player the relic simply did nothing. The gate now asks whether the relic
   overrides `AfterObtained` at all, which is a question reflection answers exactly, so
   Tri-Boomerang works the way Kaleidoscope does. **The mechanism is fixed; whether a
   mid-draft "enchant a card in your deck" is a good offer is this pass's call.**

   **What this pass still owns is the content question**, which is untouched: whether a
   "remove 4 cards" or "pick 1 of 3 off-class cards" relic is a *good* thing to offer in a
   competitive draft at all. It works now; whether it belongs is still a judgement call, and it
   overlaps with item 3.

   Worth knowing for that judgement: KALEIDOSCOPE's `AfterObtained` was also what produced the
   2026-08-18 black screen. It offers two `CardReward`s, nobody could take them under the draft
   screen, and the pending set NRE'd at arena entry because a draft has no map-point history entry.
   Both halves are fixed and neither is a reason to keep or cut the relic — see HANDOFF.

3. **Should a disconnect simply be a loss?** Raised 2026-08-18 on seeing the HP rule work:
   *"realistically maybe the right choice in the future is that a disconnect is a loss rather than
   deferring to hp/score."*

   What ships today is the evidence rule: an accidental drop is decided on HP if it happened in the
   duel (both machines provably agree there, so both reach the same answer with nothing on the
   wire), and drawn anywhere else. It exists because **a partition is symmetric** — the first real
   Steam session had both ends independently declare the link dead — so "whoever remains wins"
   handed the match to *both* players.

   "The dropper loses" is a cleaner competitive rule and is the one most games use. It is a
   *balance* decision rather than a correctness one, and it is not free: neither side can tell "they
   crashed" from "my own link died", so on a true partition both would conclude they had lost, which
   is the mirror of the bug just fixed rather than an improvement on it. Making it work needs
   something the current design does not have — a third party, a rejoin window that expires, or a
   post-hoc reconcile when the two clients next meet. **Decide it against native reconnect**, which
   is the milestone that changes the answer.

4. **The three relics that need `SetupForPlayer`, and currently no-op in a draft.** Deferred here
   2026-08-18. Reported as "dusty tome didn't do anything", diagnosed, and parked rather than fixed
   because the fix has a content question inside it.

   `DustyTome`, `ArchaicTooth` and `TouchOfOrobas` each carry a `[SavedProperty]` chosen *per
   player* — the Ancient card Dusty Tome will give you, for instance — and each exposes a
   `SetupForPlayer(Player)` that picks it. **Vanilla calls that method from exactly two places, both
   events** (`Darv` and `Orobas`), and a duel visits no events. So the draft hands out a relic whose
   property is still null:

       Property AncientCard on RELIC.DUSTY_TOME is null, which is not a valid SavedProperty
       [SpirePvp] draft: DUSTY_TOME threw while being obtained:
           System.ArgumentNullException: Value cannot be null. (Parameter 'key')

   `AfterObtained` does `ModelDb.GetById<CardModel>(AncientCard)` on that null. It is **safe** —
   `DuelDraft.ObtainAndResolve` catches it, logs it and the draft carries on — so these are dead
   picks rather than broken matches, which is why this can wait.

   Same family as "draft cards were never built through the factory": a vanilla creation path
   reimplemented in one line, inheriting an omission silently. The mechanical fix is to call
   `SetupForPlayer` on the three types, and the only real decision is *when*: at pool-build time the
   grid can show which card Dusty Tome is offering, which is information you want while choosing —
   but the pool is built once on the host and broadcast, while `SetupForPlayer` draws from
   `player.PlayerRng.Rewards`, so "which player is it set up for" has to be answered before the
   code can be written. Setting it up at grant time instead is trivially correct and makes the pick
   blind.

   Note `ArchaicTooth` and `TouchOfOrobas` return `bool` from theirs — setup can fail — so a relic
   that cannot be set up needs a filter, which is a pool question and belongs in this pass anyway.

5. **Fur Coat.** Deferred here 2026-08-18 on sight; recorded now because *why* it is suspicious is
   the part worth keeping.

   It is a map relic in a mode with no map: `ModifyGeneratedMapLate` marks eight Monster/Elite
   coords when the act is generated, and the payoff only fires in a combat at one of those coords.
   A draft generates no map to mark and the arena is a Boss-type point, so in practice it should do
   nothing — a dead Ancient pick taking a slot in a tier that only has two.

   **The reason to look rather than assume**: its payoff is
   `CreatureCmd.SetCurrentHp(item, 1m)` over `hittableEnemies`, and in a duel `HittableEnemies` is
   the getter `DuelAoeTargetingPatch` resolves to *the opponent*. So the failure mode if the
   condition ever does hold is not "does nothing" — it is setting the other player to 1 HP. Worth
   confirming which of the two it actually is before deciding whether to cut it.

6. **What to cut from 1v1.** Vanilla content assumes a co-op or solo run against monsters; a duel
   is neither. Some relics, potions and cards are pointless, degenerate, or unfun across the table.
   The dead-in-a-duel *reflex* is already handled by hook (`DuelDraft.IsDeadInADuel`); this is the
   *judgement* layer on top — the things that technically function but should not be offered because
   of how they play against a person. Needs a pass with the deck and pools in front of you.

   - **The draft's boss/Ancient tier specifically.** It is sourced from `EventRelicPool`'s ~100
     Ancient relics (see `BuildRelicPool`) because the character pools hold none. That pool mixes
     genuine boss relics (Ectoplasm) with event- and Neow-flavoured Ancient relics that read oddly
     as a duel "boss" pick (Lucas flagged Silver Crucible 2026-08-18, wondering if it was a Neow
     artifact — it is not; the draft skips Neow, and Silver Crucible is just an Ancient relic the
     tier offered). The question for this pass is which of those hundred actually belong in a duel's
     top tier, and it is a curated judgement, not a hook.

<!-- Add balance items here as they surface during bug/UX work. Keep the "not here" test in mind. -->
