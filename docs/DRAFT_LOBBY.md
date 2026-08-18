# Draft lobby — requirements and design

Written 2026-08-14 after a session that fixed the same lobby six times and ended worse than it
started. The fixes were each reasonable and none of them was aimed at a stated requirement, so they
overlapped, contradicted each other, and leaked information in a different place every time.

**Read this before touching `DuelDraftMirrorPatch`, `DuelDraftRandomRollPatch`,
`DuelDraftRandomClickPatch` or the character lock in `DuelLobbyPanel`.**

---

## What went wrong, briefly, because it is the reason this document exists

A draft is a mirror match: both duelists play the same character. That single rule was implemented
six times in one evening —

1. mirror the host's character on `PlayerChanged`,
2. also on `PlayerConnected`, because change cannot carry initial state,
3. also on modifier refresh, because the format arrives separately,
4. block the client's own clicks, at the UI path,
5. block them at the *sync* path instead, because the UI path let the record diverge,
6. defer the mirror out of the message handler, because a send from inside a receive is dropped,

— and each one was correct about the thing it changed. The problem was never the individual fix. It
was that "what should this lobby look like" had never been written down, so every report produced a
new patch rather than a check against a spec.

**The concealment attempts are the clearest example.** Hiding a Random roll was tried three ways in
one hour: keep the selection marker on `?`, blank the character name in the player list, dim the
portrait icon. Each closed one leak and opened another, and the end state — name hidden, face shown,
client's presence marker gone entirely — was worse than showing everything. A lobby that hides the
name and shows the portrait is not secret, it is misleading.

---

## Requirements

### R1 — The host owns the character; the client cannot change it

A shared pool is only fair if both duelists play the same character, so the client's selection is not
a choice. It must be impossible to change, not merely overwritten afterwards.

**Test:** in a draft lobby, click every character on the client. The lobby record on **both** peers
is unchanged, and the run seeds with two identical characters.

### R2 — The client is told why, not just stopped

A control that silently stops responding reads as a broken lobby.

**Test:** the client's character row carries a visible explanation naming the host as the chooser.
Wording must be true whether or not the host used Random.

### R3 — Both players' presence is always visible

Concealing an identity must never conceal that someone is *there*. The lobby has to keep showing two
players, their connection and their ready state.

**Test:** both players appear in the lobby list at all times, with ready state, in every combination
of format and character.

### R4 — Random is a re-roll, and it is not concealed

**WITHDRAWN AND REPLACED 2026-08-17 (Lucas).** R4 used to require that a Random pick be hidden from
both players until the run started. It is now the opposite requirement, and the change is a
*decision*, not a defeat: concealment was attempted three ways in one session, each attempt leaked
somewhere else, and the design that would actually work (below) needs the lobby to genuinely hold
Random on both peers with the real character decided at run creation — research nobody has done.
Lucas's call: *"that seems to have been ultra painful and we never really got there. instead let's
just have it so clicking random will assign both players to a random character."*

So Random is an ordinary control with an obvious meaning: **it picks someone, visibly, for both
players, and pressing it again picks someone else.**

- Clicking Random rolls a real character and both players' selection markers move to it.
- Clicking Random **again** rolls again, every time, with no intervening click on a real character.
- The roll never returns the character already selected, so every press visibly changes something.

**Test:** click Random five times in a row on the host. The character changes on every click, both
players' markers follow it every time, and the log shows five `rolling Random now` lines.

**The old concealment design is kept below** because it is still the only design that would work if
concealment is ever wanted again. Do not attempt it by patching display surfaces.

### R5 — Nothing is concealed, so nothing has to be revealed

Trivially satisfied by R4's replacement, and kept as a numbered requirement so a future concealment
attempt has to argue with it rather than around it.

---

## Why the current implementation cannot satisfy R4, and what would

**The obstacle is that "the host picked Random" is local knowledge.** The host rolls at the click —
it has to, because vanilla's own resolution happens *as the run begins*, which is too late for the
client to mirror (measured: `Player 1001 tried to change character while run was already starting!
Ignoring`). So by the time anything is on the wire, the lobby holds a real character and the fact
that it came from Random exists only on the host.

Every concealment attempt so far has therefore been a **display layer over a resolved value**:
the lobby knows Ironclad, and the UI is asked to lie about it in three places. That is why the leaks
were endless — each new surface that reads the character is a new place to remember to lie.

**The design that works is to stop lying and change what the lobby holds.**

1. **The lobby genuinely holds Random on both peers.** The host picks Random; the client mirrors
   `RANDOM_CHARACTER` like any other character. Both lobbies are honest, both show `?` because that
   is really the selection, and no display patching is required at all — R4 falls out for free.
2. **The real character is decided by the host at run creation and forced onto both players**, in
   `DuelMatch.OnRunCreated` or earlier, *before* players are seeded. This is the part that needs
   research: `RunState.CreateForNewRun` installs modifiers before seeding, so there is a window, but
   whether a player's character can be set there without breaking the starting deck and relics is
   unverified.
3. **If step 2 turns out to be impossible**, the honest fallback is to drop Random from draft lobbies
   with a message saying why — *not* to reintroduce a display layer. Showing a real character while
   pretending otherwise is the state this document exists to prevent.

**Do not attempt R4 by patching more display surfaces.** Three have been tried; the fourth will leak
somewhere too.

---

## Current state, 2026-08-14

Implemented and believed correct:

- **R1** — `DuelDraftCharacterLockPatch` refuses `StartRunLobby.SetLocalCharacter` for a client in a
  draft lobby. It is on the *sync* path deliberately: an earlier version patched
  `NCustomRunScreen.SelectCharacter` and the client's screen obeyed while the host's record did not.
- **R2** — the lock overlay in `DuelLobbyPanel.SyncCharacterLock`, translucent, captioned "The host
  is picking the character for both of you".
- **R3** — restored. The remote-marker suppression added for concealment has been reverted.
- **R5** — trivially true, since nothing is concealed.

**R4 as rewritten (2026-08-17):** Random rolls at the click, the rolled character is shown, and the
roll excludes the incumbent so a repeat press always changes the character. The three reverted
concealment attempts are in the git history around `ef2a14d`..`ccc9f53` if the reasoning is wanted.

**The re-roll needed a second patch, and the log named the level.** A second Random click produced
*no log line at all* — `DuelDraftRandomRollPatch` never ran, because
`NCharacterSelectButton.Select` opens with `if (!_isSelected)` and swallowed the click one level
above `NCustomRunScreen.SelectCharacter`. `DuelDraftRandomClickPatch` takes the click at the button
instead. Note what was *not* done: no attempt to work out which term left the Random button
believing it was still selected. The gate is above us, so the click is taken above the gate — the
same move as `GetOpponentsOf` over `HittableEnemies`. The old prefix is kept as a backstop and logs
when it fires, so the next log says which path a click travelled.

## Also open in this area

- **The remote character marker was reported missing before any of this** and the telemetry showed
  vanilla placing it correctly every time (`vanilla put it on SILENT`). So it is not a placement
  bug; something downstream is not drawing it. Unstarted.
- **Lobby telemetry is still in and noisy**, by Lucas's instruction, until the mode is finished.
  `check-log.ps1 -Compare` reduces it to the one line that matters.
