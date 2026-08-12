# Playtest list — first session with a second person (2026-08-11)

Raised by playing, not by testing. Grouped by what they actually are rather than by the order
they were hit, because several are one cause wearing different clothes.

Status: **triage written, none fixed yet.** Being batched deliberately so the playtest is one
pass rather than five.

---

## 1. Hard failures

**Entering the campfire on the client broke.** Highest priority — a hard stop, and the rest site
has already produced two separate co-located-party bugs (`RaceSoloRestSitePatch` for the exit
barrier, `RaceSoloRestSiteArtPatch` for the seating). **Client-only: the host walked on past it
without trouble**, which is the same asymmetry every rest-site bug here has had. Needs the
client's `godot.log`; a Steam launch does not write to `logs/`.

**Mouse disappeared in the chest room** but clicking still worked. Cursor visibility, not input.
Possibly the treasure room's hand/cursor handling, which we already patched once
(`NoHandsInASoloChest` suppresses `NHandImage.AnimateIn`) — worth checking whether the real
cursor is being hidden in favour of the hand we now never show.

---

## 2. Information leaking between racers

These are all DESIGN §1's information rules and investigation I6, which predicted exactly this:
*"What is missing is only a renderer — the consumers are co-op surfaces that the arena does not
currently surface. Anything that later shows a player panel turns the leak visible. Suppress at
the broadcast, not at the display."* The race surfaces them.

- **Opponent's cursor visible in combat.**
- **Can see what the opponent is picking in events.**
- **Co-op overlay showing each other's health and decks** should not be there.
- **Collaborative multiplayer events should not appear at all** during a race.

Treat as one piece of work: find every co-op peer-input/peer-state surface and gate it on the
race, at the broadcast where possible.

## 3. Co-op UI that a solo racer should not see

The co-located-party pattern again (`src/race/RaceSolo.cs`), now in rooms we have not swept.

- **Mend should not be a campfire option** — co-op-only rest site option.
- **Random should be a character choice** in the lobby (missing, not extra).

## 4. Lobby

- **Client cannot see which rules are selected.** They appear only after the host changes
  something; the defaults (No clock) never show. This is the *display* half of the bug fixed in
  `DuelLobbyPanelPatch` — the panel now builds on join, but the ticked state does not sync.
  Same root: a message that only fires on change cannot carry initial state.
- **Duel button hover is unresponsive and illuminates Custom as well as Duel.** Almost certainly
  `custom.Duplicate()` copying signal connections (Godot's default flags include
  `DUPLICATE_SIGNALS`), so the clone drives Custom's visuals. Fallback if that is not the whole
  story: build the button from its scene rather than duplicating, as was done for the lobby
  tickboxes.

## 5. Race fairness

- **Elite rewards differed while chest relics matched.** Open question rather than a known bug:
  how much of the run is supposed to be identical? DESIGN §4 mirrors `PlayerRng`/`PlayerOdds`
  so *"neither player can be luckier than the other"*, and I4 confirms the seeds are mirrored —
  but rewards are also filtered by character, so two different characters legitimately see
  different cards from identical rolls. Needs deciding: is the intent identical *offers*, or
  identical *rolls*?

## 6. Presentation

- **Draw death screen shows both characters spawning and dying.** Should be one.
- **Victory/defeat/draw subtitles want personality** — a set of phrases per outcome rather than
  the single flat line each currently has.

---

## 7. Disconnection has no handling at all — design decision needed

Leaving the run showed the other player as **disconnected rather than left**, and the host simply
carried on playing. So a match can currently end with no result for either side, which for a
competitive mode is worse than either player losing.

This is a real gap rather than a bug: `DuelEndReason` has codes for HP, flag, race expiry,
resignation and agreed draw — and none for a disconnect. HANDOFF already notes that most
teardown routes never reach `DuelResult.DeclareWinner`, and this is one of them.

Proposal from play, worth taking seriously because it mirrors how clocks already work here:

- On a disconnect, **freeze both players** and start a reconnect window.
- **Rejoin during the window** resumes the match.
- If rejoining is not feasible, **a disconnect is a loss** for the player who dropped.
- A **proposed timeout** either player can offer, alongside Resign and Offer Draw — same
  consent-based shape as the draw, for when someone needs a minute.

**Answered 2026-08-11: the engine supports rejoining a run in progress.** `RunSessionState` has
`None / InLobby / InLoadedLobby / Running`, and `JoinFlow` branches on it — `Running` calls
`AttemptRejoin()` and returns a `ClientRejoinResponseMessage`. So freeze-and-rejoin is buildable
on machinery vanilla already has, and disconnect-as-loss is the fallback rather than the default.

What that leaves to work out: whether a rejoining client can be put back into a *race* (its run
state is client-local and divergent by design, so a rejoin has to restore more than vanilla
co-op needs), and who arbitrates the reconnect window. The clock analogy points at the answer
for the window — it is a paused chess clock, host-authoritative like every other decision here,
and a player who never comes back loses on time.

Note the engine already distinguishes disconnect from leave, and `RestSiteSynchronizer
.OnPeerDisconnected` shows vanilla's own pattern for releasing a barrier held by someone who is
never coming back.

---

## Noted, deliberately not acted on

- **Should Booming Conch work during the duel itself?** Balance question, parked at Lucas's
  request. Belongs with the other duel-pool questions in DESIGN §9 (co-op-only cards, potions
  referencing monsters) rather than being decided ad hoc.

---

## Resolved during the session

- Friend's Multiplayer → Host went straight to a normal lobby with no Custom/Daily/Duel choice.
  Their own local issue, not the mod.
