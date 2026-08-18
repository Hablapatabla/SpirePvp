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

1. **Which potions are instant vs must be queued, in turn-based.** Recorded as HANDOFF item "Not
   every potion should be queued — needs Lucas's classification." A turn-based round is planned
   privately and resolved together, so a potion that reads the board at *use* time behaves
   differently from one that just adds a card or block. The classification is a content call, not a
   code one — the queue mechanism works; what is missing is the per-potion decision of which side of
   it each potion sits on. Applies to draft mode too, where potions are drafted deliberately rather
   than found.

2. **Choice-on-obtain relics (BIIIG_HUG and its kind).** A relic whose `AfterObtained` gathers a
   player choice (BIIIG_HUG removes cards; bottle / transform / remove relics are the same shape)
   currently does nothing in a draft — the choice is fired fire-and-forget under the draft screen,
   never presents, and the effect no-ops. The counter-reset shipped 2026-08-18 makes this *safe*
   (no divergence), so they are dead picks rather than broken matches. The balance decision:
   **make them work** (defer the effect to draft completion and resolve it locally — moderate, with
   real UX/timing questions) **or cut them** (filter from the draft pool — clean for a competitive
   draft, since a "remove 4 cards" relic mid-draft is odd anyway). Left as a safe dead pick until
   this pass. See the 2026-08-18 divergence write-up in HANDOFF for the full mechanism.

3. **What to cut from 1v1.** Vanilla content assumes a co-op or solo run against monsters; a duel
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
