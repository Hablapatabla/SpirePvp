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

### R1 regression found and fixed 2026-08-17: Random chosen *before* the format was ticked

**This one broke a match, and it is not the parked cosmetic issue.** Picking Random and *then*
ticking Draft left a live `RANDOM_CHARACTER` in the lobby: the roll-at-click only fires while a
draft lobby is already active, so nothing resolved it, and vanilla resolved it inside
`BeginRunLocally` — after which the client's mirror lands too late and the run seeds mismatched.

    PlayerChanged 1 = RANDOM_CHARACTER          (draft=False — format still Race)
    draft=True at modifiers refresh
    host is on random — waiting for it to resolve
    PlayerChanged 1 = SILENT                    (resolved at run start)
    mirroring the host — taking SILENT          (too late)
    run seeded with 1(me)=SILENT, 1001=IRONCLAD
    draft: refusing to start — SILENT and IRONCLAD

`MirrorNow.ResolvePendingRandom` rolls it when the format arrives. **The general rule, arriving from
a new direction:** this project already knew that *a message which fires only on change cannot carry
initial state, so hook the arrival too.* The mirror image is that a **click cannot carry a later
format change**, so the format change has to re-ask. Whichever of the two facts lands last has to
trigger the work — which is the same sentence, and is now the fifth bug in this family.

**Test (add to R1):** pick Random *first*, then tick Draft, then start. Both peers seed with the same
character and the draft opens.

### PARKED 2026-08-17, after three attempts — do not take a fourth without reading this

**New data point, 2026-08-18, and it narrows the fault rather than reopening it.** Reported as
"clicking random character in draft on host before client joined lobby didn't show an indicator
appear under a character". Read out of the log, the roll landed *before* the client connected:

    [StartRunLobby (1)] ... (client not yet connected)
    draft lobby: rolling Random now — DEFECT (was IRONCLAD)
    lobby telemetry: PlayerChanged 1 = DEFECT
    ...
    [StartRunLobby (1)] Client 1001 connected          ← nine lines later

So the lobby record moved to DEFECT correctly with one player in the lobby. **This is not a remote-
marker problem and it is not new** — it is the same parked fault seen without a second player to
confuse it: `SelectCharacter` updates the record, and the selection *visuals* that `rolled.Select()`
would set (`_isSelected`, which drives the outline and the saturation) never move. Worth knowing
that it reproduces with a single player, because that makes it reproducible without a second client
whenever someone does take the fourth attempt.

Still parked. Lucas 2026-08-18 called it "small visual bug I think", which is the same call as
before.

**Current behaviour: every Random click rolls a new character and both players follow it. What does
not move is the Random-adjacent button's own highlight**, so the control reads as unresponsive even
though the lobby record is correct on both peers every time. Lucas: *"if this is a massive yak shave
let's just abandon it because it at least works currently."* Taken.

**What is actually known, from logs rather than from reading the decompile:**

| Build | Repeat clicks | Highlight |
|---|---|---|
| Roll from a prefix on `NCustomRunScreen.SelectCharacter` | 1 roll, then dead until another character is clicked | does not move |
| Same, plus taking the click at `NCharacterSelectButton.Select` | **6 rolls in 6 presses** | does not move |
| Same, but rolling via `rolled.Select()` instead of `SelectCharacter` | **1 roll from several presses — worse** | still reported as nothing visible |

The third row is the surprising one and it is the reason this is parked rather than continued.
`Select()` is the *more* correct call — it is vanilla's own path and the only place `_isSelected` is
set, which is what the pulsing outline, the saturation and the gold icon all read — and using it
made repeat clicks stop working. So something downstream of a genuine `Select()` prevents the next
click from reaching the patch, and nobody has observed what.

**The narrow unanswered question, for whoever picks this up:** *is `NCharacterSelectButton.Select`
called at all on the second click?* One log line at the top of the click patch, unconditional and
before every guard, answers it and rules out half the search space. Every attempt so far has instead
changed what happens *after* the click, which is why three fixes produced three different failures
and no understanding. Do not add a fourth fix without that line.

The shipped version is row two: the one with the best measurement.

---

**And then a third fix, because the click gate was only half of it.** With the click taken at the
button, the log showed six rolls, six `PlayerChanged`, and the client mirroring every one — while
Lucas reported the button still doing nothing. Both were true. `_isSelected = true` is set in exactly
one place, `NCharacterSelectButton.Select`, and it is what every visual on the button reads: the
pulsing outline in `_Process`, the saturation in `RefreshState`, the gold player icon in
`RefreshPlayerIcons`. The roll was calling `NCustomRunScreen.SelectCharacter` directly, which only
assigns `_selectedButton` and *deselects everyone else* — so the lobby record moved and no button on
the host's own screen ever lit up. It read as a dead button, which is why the report did not change
shape when the gate was fixed. The roll now calls `rolled.Select()`, vanilla's own path.

**Two fixes, one report, and the second was invisible to the log** — the log could only show that the
record was correct, which it was throughout. Worth remembering next to this project's standing rule
about reading logs: a log settles what the *state* is, not what the screen is showing.

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
