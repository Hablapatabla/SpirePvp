# Playtest list — first session with a second person (2026-08-11)

Raised by playing with someone who did not build it, which is what that session was for: none of
these came up in a hundred local runs. Grouped by cause rather than by the order they were hit,
because several were one thing wearing different clothes.

**Nine fixed, six open.** The open ones are all reproducible with two local clients — no second
person needed — which is why local testing is the right next mode.

---

## Still open

### Lobby

- **Client cannot see which rules are selected.** They appear only after the host changes
  something; the defaults never show. The *display* half of the bug already fixed in
  `DuelLobbyPanelPatch` — the panel now builds on join, but the ticked state does not sync.
  Same root as that one: **a message that only fires on change cannot carry initial state.** The
  client's opening state arrives in `ClientLobbyJoinResponseMessage`, so the ticked state has to
  be applied from there, not waited for.
- **Duel button hover is unresponsive and illuminates Custom as well as Duel.** Almost certainly
  `custom.Duplicate()` copying signal connections — Godot's default duplicate flags include
  `DUPLICATE_SIGNALS`, so the clone drives Custom's visuals and never its own. Fix: duplicate
  with `Groups | Scripts` only, then wire our own. Second candidate if that is not the whole
  story: `NSubmenuButton` resolves its parts by scene-unique name (`%Lock`, `%Icon`), which can
  resolve back to the original when a subtree is duplicated out of its owner. Fallback: build the
  button from its scene rather than duplicating, as was done for the lobby tickboxes after the
  same "a mod cannot construct this" assumption turned out to be wrong.
- **Random should be a character choice** in the lobby. Missing, not extra.

### Presentation

- **Draw death screen shows both characters spawning and dying.** Should be one.
- **Victory/defeat/draw subtitles want personality** — a set of phrases per outcome, picked from,
  rather than the single flat line each has now. Note the wording must stay reason-aware:
  `DuelResult.EndReason` already distinguishes HP, flag, resignation, race expiry and agreed
  draw, and collapsing them back into one line per outcome is the bug that was just fixed.

### Mouse

- **Mouse disappeared in the chest room** but clicking still worked — cursor visibility, not
  input. Suspect the treasure room's hand handling, which we patched once already
  (`NoHandsInASoloChest` suppresses `NHandImage.AnimateIn`): worth checking whether vanilla hides
  the real cursor in favour of the hand we now never show.

### Race fairness — needs a decision, not a fix

- **Elite rewards differed while chest relics matched.** How much of the run is meant to be
  identical? DESIGN §4 mirrors `PlayerRng`/`PlayerOdds` so *"neither player can be luckier than
  the other"*, and I4 confirms the seeds are mirrored — but rewards are also filtered by
  character, so two different characters legitimately see different cards from identical rolls.
  **Is the intent identical *offers*, or identical *rolls*?** Parked at Lucas's request.

### Disconnection has no handling at all — design decision needed

Leaving the run showed the other player as **disconnected rather than left**, and the host simply
carried on playing. A match can therefore end with no result for either side, which for a
competitive mode is worse than either player losing. `DuelEndReason` has codes for HP, flag, race
expiry, resignation and agreed draw — and none for a disconnect.

Proposal from play, which mirrors how the clocks already work:

- On a disconnect, **freeze both players** and start a reconnect window.
- **Rejoin during the window** resumes the match.
- If rejoining proves infeasible, **a disconnect is a loss** for whoever dropped.
- A **proposed timeout** either player can offer, alongside Resign and Offer Draw — the same
  consent shape as the draw, for when someone needs a minute.

**Answered: the engine supports rejoining a run in progress.** `RunSessionState` is
`None / InLobby / InLoadedLobby / Running`, and `JoinFlow` branches on it — `Running` calls
`AttemptRejoin()` and returns a `ClientRejoinResponseMessage`. So freeze-and-rejoin is buildable
on machinery vanilla already has, and disconnect-as-loss is the fallback rather than the plan.

What remains to work out: whether a rejoining client can be restored into a *race* (its run state
is client-local and divergent by design, so a rejoin has to restore more than co-op needs), and
who arbitrates the window. The clock analogy answers the second — it is a paused chess clock,
host-authoritative like every other decision here, and a player who never returns loses on time.
`RestSiteSynchronizer.OnPeerDisconnected` is vanilla's own pattern for releasing a barrier held
by someone who is not coming back.

---

## Fixed this session

**The campfire break, and it was caused by the fix for the campfire hang.**
`RaceSoloRestSitePatch` resolves the absent opponent's rest site up front — completes their task
source, clears their options — so leaving the room does not wait on them. Correct, and only half
the story, because their messages still arrived: one threw because we had completed their rest
site, the other because we had emptied the list their hover indexed into.

*Why the messages arrived at all*, when the location buffer should hold them: the two runs share
a seed, so they share a map, and the players took essentially the same path throughout. The
buffer gates on **location, not identity** — same coord, so deliver. Same-path play is therefore
both the most natural way to play and the worst case, which is exactly why a hundred local runs
on divergent paths never showed it. **Test same-path from now on.**

It was never only the rest site: rewards showed the same shape in the same log. Fixed for every
per-room synchronizer by `RaceIgnoreRemoteRoomPatch` — rest site, rewards, events, and the
merchant/chest/crystal one-offs — dropping by *sender*, race-only.

**Information leaks**, all four (`RaceNoCoopSurfacesPatch`, plus the above):

- Opponent's cursor in combat — suppressed at the *broadcast*, with controller focus and
  mouse-down alongside, because hiding only the renderer leaves the data on the wire.
- Opponent's event picks — a side effect of their choices being applied to our copy.
- Co-op overlay showing each other's HP and decks — hidden, matching what vanilla draws when
  alone.
- Shared events — no longer roll at all, filtered through `IsAllowed`.

**Mend at the campfire** — added on `Players.Count > 1`, the same content-level twin that offered
co-op cards and Massive Scroll to a racer. Worse than clutter: a campfire choice that cannot do
anything, spending a rest you do not get back.

**The log flood** — several hundred errors, all ours: a `MegaLabel` with no theme font override
throws on every layout pass, which vanilla warns about in as many words. The lobby headings had
none.

---

## Noted, deliberately not acted on

- **Should Booming Conch work during the duel itself?** Balance question, parked. Belongs with
  the other duel-pool questions in DESIGN §9 (co-op-only cards, potions referencing monsters)
  rather than being decided ad hoc.

## Resolved without a change

- Friend's Multiplayer → Host went straight to a normal lobby with no Custom/Daily/Duel choice.
  Their own local issue.
- Client "internal error" popups: `--fastmp=join` firing at startup with no host listening, and
  again after the host leaves the result screen. A dev-harness artefact that cannot happen for
  someone launching through Steam.
