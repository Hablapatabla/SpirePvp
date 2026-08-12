# Playtest list — first session with a second person (2026-08-11)

Raised by playing with someone who did not build it, which is what that session was for: none of
these came up in a hundred local runs. Grouped by cause rather than by the order they were hit,
because several were one thing wearing different clothes.

**Fourteen fixed, one open.** The one left — Random as a character choice — is deferred with a
scope rather than blocked; everything else raised by that session is closed.

---

## Still open

### Lobby

- **Random should be a character choice** in the lobby. Missing, not extra. **Deferred
  2026-08-12** after scoping it — it is a small feature rather than a small fix, and it lands in
  the one area where two clients must agree. Scoped below so it can be picked up cold.

  **Vanilla refuses Random in a Custom lobby outright**, and a duel lobby *is* a Custom lobby:

  ```
  public void PlayerChanged(StartRunLobbyPlayer player, bool isRandomCharacterResolution)
  {
      if (isRandomCharacterResolution)
          throw new InvalidOperationException("Random character is not currently allowed in custom!");
  ```

  The good news is that the *mechanism* is not Custom-specific and needs nothing built:
  `StartRunLobby.BeginRunLocally` already resolves a `RandomCharacter` into a real one with an
  `Rng` seeded from the run seed, so both clients resolve identically without a message — which
  is exactly the property a PvP lobby needs. What is missing is only the screen's half:

  1. **There is no Random button to press.** `ModelDb.AllCharacters` holds the five real
     characters and not `RandomCharacter`, so `NCustomRunScreen.InitCharacterButtons` never makes
     one. `NCharacterSelectScreen` has a dedicated `_randomCharacterButton` node in its own
     scene instead. So a duel lobby has to build the button itself — `char_select_button.tscn`
     plus `Init(ModelDb.Character<RandomCharacter>(), this)` — and then repair the focus-neighbour
     chain, which `InitCharacterButtons` wires by child index and would otherwise skip it for
     controller navigation.
  2. **The resolution callback has to stop throwing**, via a prefix on that guard.
  3. **Then the things that only two clients can tell you**: whether
     `RefreshButtonSelectionForPlayer` draws a remote player's Random pick sensibly, and whether
     the resolution reaches our own run-creation path in the right order — `DuelMatch.OnRunCreated`
     and the RNG mirroring both read characters, and resolution happens inside `BeginRunLocally`.

  Gate it on the lobby being a duel rather than on Custom generally: vanilla disabled this
  deliberately and the mod's standing rule is to be inert outside a PvP match.

### Mouse

- ~~**Mouse disappeared in the chest room.**~~ **FIXED 2026-08-12, awaiting playtest** — and the
  old note's suspicion was right. `NHandImageCollection.UpdateHandVisibility` ends with

  ```
  CursorManager.SetCursorShown(screenType != NetScreenType.SharedRelicPicking);
  ```

  which together with its restore in `_ExitTree` is the **only** code in the entire game that
  touches cursor visibility. The hand and the cursor are a matched pair — in co-op the hand *is*
  your pointer — and `NoHandsInASoloChest` removed one half of it: no hands appear, the cursor
  is still switched off for them, and the room is left with no pointer at all. Clicking kept
  working throughout, which is what made it read as a rendering glitch rather than as a cursor
  being deliberately hidden. `KeepTheCursorInASoloChest` puts it back, race-only.

### Race fairness — needs a decision, not a fix

- **Elite rewards differed while chest relics matched.** How much of the run is meant to be
  identical? DESIGN §4 mirrors `PlayerRng`/`PlayerOdds` so *"neither player can be luckier than
  the other"*, and I4 confirms the seeds are mirrored — but rewards are also filtered by
  character, so two different characters legitimately see different cards from identical rolls.
  **Is the intent identical *offers*, or identical *rolls*?** Parked at Lucas's request.

### Disconnection — first slice **built and playtested 2026-08-12**

Leaving the run showed the other player as **disconnected rather than left**, and the host simply
carried on playing. A match could therefore end with no result for either side, which for a
competitive mode is worse than either player losing.

**`DuelDisconnectPatch` closes that: a drop now decides the match** (`DuelEndReason.Disconnect`,
appended rather than inserted, since the codes are positional on the wire). This is deliberately
the *endgame* half of the proposal below rather than the whole of it — a reconnect window has to
end somehow, so disconnect-as-loss is needed either way, and it is the half that stops a match
evaporating.

Two routes in, and they are **not symmetric**, because a disconnect is the one event that can
remove the arbiter:

- **The peer dropped** (`RunManager.RemotePlayerDisconnected`) — the host sees a client go, or a
  client is told a peer left. Whoever is still here wins.
- **We lost the connection** (`RunManager.LocalPlayerDisconnected`) — how a *client* learns the
  host is gone. There is nobody left to arbitrate, so the client decides locally. That breaks
  host authority knowingly: with one player left there is exactly one possible answer, and
  refusing to answer it is the original bug.

**The guards are vanilla's own, and are the correctness argument.** `LocalPlayerDisconnected`
already separates a genuine drop from the ordinary ways a connection ends —
`info.GetReason() != NetError.QuitGameOver && !IsAbandoned && !State.IsGameOver` — and those are
exactly the exclusions needed here, for the same reasons: leaving the result screen disconnects,
and that is a finished match rather than a forfeit. `DuelResult.Declare` being idempotent once
the phase is `Complete` is the backstop, and is why a resignation still reports as a resignation:
it declares first, and the connection closes afterwards to find the match already decided.

**ENet never reports a hard drop, and that is why the announced route alone was not enough.**
`ENetHost.Update` handles the transport's own `Disconnect` event with a bare `continue`, so a
killed process, a closed window or a pulled cable produce no event whatsoever; a disconnect is
only reported when the leaving client sends an application-level `Disconnection` packet, i.e. a
polite quit. (`SteamHost` does report real drops, so this is an ENet-transport gap — and ENet is
the entire local dev harness.) Measured: the client was closed mid-race and the host played on
for another eight hundred log lines, logging `Peer not connected` on every send while
`NetQualityTracker` reported `Packet Loss: 0.99999934` for a peer it still considered present.
Everything needed to notice was already being measured; nothing was asking.

So `DuelDisconnect.Tick` asks, reading `ConnectionStats.LastReceivedTime` — the same signal
vanilla's own `NMultiplayerTimeoutOverlay` watches to decide a peer has stopped responding.
Heartbeats carry the peer's loading flag, so a peer on a loading screen is still talking and
never looks silent. **30 seconds**, deliberately past vanilla's 3-second "unresponsive" curtain
and ENet's own 20-second peer timeout: showing an overlay is cheap, ending a match is not.

Verified end to end on two local clients — `opponent 1001 silent for 30s … declaring a win by
disconnect` → `duel over — WON` → a `wonDisconnect` result screen, solo, victor standing.

**Known limitation, documented rather than discovered later: a true network partition has both
sides claim the win.** With the link cut there is no shared authority left, and neither client
can tell "they crashed" from "my own link died". A killed process does not hit this, since the
dead side sees nothing. The freeze-and-arbitrate design below is what resolves it.

**The window's visible half is built and playtested 2026-08-12.** After **5 seconds** of silence
the match puts up vanilla's own `NMultiplayerTimeoutOverlay` — the curtain players already know
from a stalled host — carrying our text and a live countdown to the forfeit. Verified: `opponent
silent for 5s — showing the timeout curtain, forfeit in 24s`, then the declaration at 30s.

Two things fall out of it that are worth knowing:

- **Vanilla only ever drives that overlay on a client** (`Initialize` returns unless the service
  is a `NetClientGameService`), watching the host. That is why a host whose client vanishes has
  always seen nothing at all. Both sides show it now, about whichever peer went quiet.
- **Recovering from a hitch already works**, without a reconnect handshake: the curtain comes
  down as soon as the peer talks again, because a returning peer refreshes `LastReceivedTime` and
  the measured silence collapses to zero. Nothing latches a "disconnected" state, so nothing has
  to be un-latched.

5 seconds is deliberately above vanilla's own 3-second threshold — announcing a disconnection
that then resolves itself would be worse than saying nothing.

**Open, and a rules question rather than a UI one: should the waiting player be able to end the
wait early?** A "keep waiting" dialogue implies a "do not wait" — i.e. claiming the win before
the window expires, which is a competitive rule, not an affordance. It also needs an answer for
both players staring at one. The popup machinery exists (`DuelDrawPrompt` builds on
`NGenericPopup`), so the cost is the decision, not the code.

**Rejoin is a milestone of its own, and deliberately not built. Decided 2026-08-12: the player
who remains simply wins.**

The original proposal was freeze-both-players plus a reconnect window, with disconnect-as-loss
only for someone who never returned. Research killed the middle of it:

**Vanilla's rejoin is half-built, and the missing half is the UI.** The transport side genuinely
works — `JoinFlow.AttemptRejoin`, `RunManager.GetRejoinMessage` (which ships the survivor's entire
run *and* combat state), `PlayerRejoinedMessage`, and a host-side gate that stays open because it
checks `RunState.Players`, which is not pruned on disconnect. But `NJoinFriendScreen` throws the
result away:

```
else if (joinResult.sessionState == RunSessionState.Running)
{
    NErrorPopup.Create(new NetErrorInfo(NetError.RunInProgress, selfInitiated: false));
    _currentJoinFlow.NetService.Disconnect(NetError.RunInProgress);
}
```

The handshake completes, the full run arrives, and it is discarded with an error popup. Mega Crit
say so in their own enum docs: *"RunInProgress: The run is already in progress, and rejoining is
not implemented."*

**So a wait window was offering something the code cannot deliver**, which is why the buttons and
the thirty seconds are gone. What remains is a five-second notice — long enough to read what
happened, too short to hope on — and then the match is awarded to whoever is still there.

**If it is ever built**, the shape is known and the hard part is not the netcode:

1. Replace that `Running` branch so the response is consumed rather than rejected: build the run
   from `serializableRun`, apply `combatState`, enter it. `RunManager.SetUpSavedMultiplayer` is
   the nearest pattern, but it is built around a `LoadRunLobby` rather than a rejoin, so it is not
   a reuse.
2. **Re-arm the mod for a restored run.** `AfterRunCreated` fires only from
   `RunState.CreateForNewRun`; a restored run keeps its modifiers — so `IsPvpRun` stays true — but
   never runs `DuelMatch.OnRunCreated`, so a rejoined client would come back with no race patches,
   no armed handlers and no clocks.
3. **Only a dropped *client* can rejoin.** If the host is the one who went, there is no session
   left to rejoin; that is inherent rather than a design choice.

**The run-state question is already answered**, and by vanilla rather than by us: `GetRejoinMessage`
sends the survivor's run wholesale, which is exactly the rule chosen here — *defer to whoever was
still connected*. Worth knowing what it implies: the survivor's copy of the rejoiner's player
stopped updating when the race decoupled, so a rejoiner would return to roughly their Neow state.

---

## Result screen, 2026-08-12 — **playtested and confirmed**

**A PvP result screen presents as an ordinary solo death: your character, alone.** Decided
2026-08-12, and it is a wider decision than the bug that prompted it. The report was only that a
*draw* drew both duelists spawning and dying, and the first pass fixed only that, reasoning that
the arena is the one screen where two figures is the truth. **Overruled by play:** the result
screen is the run's epitaph, not the duel's group photo, and the opponent standing in it reads as
a co-op wipe whichever way the match went. So the opponent is out of it on every ending,
including a duel you just lost to them in the arena.

`NGameOverScreen.MoveCreaturesToDifferentLayerAndDisableUi` needs telling twice, because it
assembles that tableau from two different sources:

- **The player list** — the rest-site branch, and the `else` branch (no room instance, i.e. the
  map screen) which creates a visual per player, plays `die` on each and spreads them across the
  screen. This is where the draw bug lived, since a race-clock expiry reaches the result screen
  with no combat room at all. Fixed by shortening the run's player list for the duration of the
  call, so vanilla takes its own singleplayer path — which re-centres the survivor for free,
  because the branch computes its spacing from the list length.
- **The combat room's creature nodes** — the remaining branch, which the player list never
  reaches. The opponent's visuals are hidden directly instead, and their *pets* go with them:
  `DuelLayout.BelongsToOpponent` resolves `Player ?? PetOwner`, the same test that decides who is
  drawn on the enemy side during the duel, so a summon cannot be left standing on a screen its
  owner has been removed from.

Restored with a Harmony **finalizer**, not a postfix, because a postfix does not run when the
original throws and a run left permanently one player short would be far worse than the cosmetic
bug being fixed.

**Three more found by playing it, all from the same root: vanilla only reaches this screen when
the run is over, and "the run is over" has always meant "you died".** A PvP match can arrive here
having *won*, and nothing on the screen was written for that.

- **The victor had no subtitle.** Not our text failing to appear — our text appearing in a label
  vanilla hides on a win. `AnimateInQuote` fades `_deathQuote` to alpha 0 and then fades it back
  in *only on the loss branch*; on a win it animates `_victoryDamageLabel` instead and never
  touches `_deathQuote` again. We were writing to the first and blanking the second. Now keyed on
  `_history.Win` — the exact field `AnimateInQuote` branches on, so the label written cannot
  disagree with the label shown.
- **The victor played the death animation.** The non-combat branches call `SetAnimation("die")` on
  every creature they spawn, which is right when the party is dead and wrong when you have just
  won by resignation or by their race clock. The spawned visuals are found by diffing the
  container's children and set to `idle_loop` (the name confirmed against `NUnlockCharacterScreen`,
  which drives the same standalone `CreateVisuals()` node). Only the *spawned* ones — a duel win
  already shows you standing over a body, and forcing an idle there would restart the animation
  of someone who is simply still alive.
- **The duel survivor stood off to the left.** The combat branch does no layout at all; it
  reparents the existing creature nodes, which keep their arena positions — and the player side
  is the left half. It only looked centred while an opponent stood opposite. Now the local
  *player* is moved to the same spot the `else` branch computes for a one-creature list, with the
  rest of your side moved by the same delta so pets keep their formation.

**Two of those were fixed twice, and the logs are why.** Both first attempts looked reasonable and
neither could have worked:

- **The victory line was still drawn over the character.** The diagnostic said outright why:
  `quote pos=(97.5, 150) size=(459, 40) parent=Banner` against
  `victory pos=(0, 0) size=(1920, 1080) parent=Ui`. The two labels are not siblings and are not
  the same kind of thing — the death quote is a small box inside the banner, while
  `_victoryDamageLabel` is a **full-screen** label under `Ui`, so its text centres in the middle
  of the screen, which is exactly where the character stands. Copying coordinates between
  different parents was meaningless, and the `sameParent` guard is the only reason the first
  attempt did nothing rather than something worse. The label is now *moved into the banner* and
  given the quote's own box, with anchors reset to top-left first — a full-rect anchored control
  recomputes its offsets from its parent on every layout pass and would have silently undone the
  position.
- **The victor's idle was being forced onto live combat creatures.** `victor stands rather than
  dies` logged **twice** on a duel win, a screen where nothing is spawned at all: the combat
  branch *reparents* its creatures into the same container the diff was watching, so they arrived
  looking freshly spawned. Nobody could have seen it — the winner was standing either way — and
  it only ever showed up as a count in a log line. The room's creatures are known from the
  prefix, so they are excluded by identity now rather than inferred from where they ended up.

**Leaving the result screen mid-animation is its own hazard.** Clicking through while the badges
are animating frees the container underneath an `await`, and the continuation then walks a
disposed node — `ObjectDisposedException: 'Godot.HBoxContainer'`, caught and reported as "duel
badges failed", which reads like broken badge logic rather than a screen that is simply gone.
Guarded at each await. Vanilla's own `AnimateScoreBar` throws the identical exception on the same
click, so this is the shape of the screen rather than a mistake of ours; ours is just the half we
can stop logging.

**Subtitles have several phrasings per ending** (`DuelResultQuotes`), 3–4 each, in
`game_over_screen.json`. Numbered from 1 and probed until one is missing, so a set grows by
editing JSON. The mechanism is playtested — two clients drew different lines from the same set on
the same draw — and the **wording was then rewritten with more bite**, which is the half still
awaiting a look.

## Fixed 2026-08-12 — **playtested and confirmed**

The reason-awareness is kept and is the constraint that shaped it: sets are keyed on **outcome
and reason together**, never outcome alone, so an agreed draw can never claim time ran out. Every
ending also keeps a hardcoded fallback line — the exact sentence it used before — because a stale
`.pck` makes every loc lookup return the raw key, and the climax of a match reading
`SPIREPVP_QUOTE.wonHp.1` is a worse failure than having no variety. The chosen line is logged, so
the screen's text is recoverable afterwards and a stale pack announces itself.

Both are lobby-presentation bugs, so the log proved the mechanism and a person confirmed the
screen: hovering Duel no longer touches Custom, and a client entering the lobby sees the rows
already ticked without the host touching anything.

**The client's ticked rules never arrived, and vanilla is why.** `StartRunLobby
.InitializeFromMessage` unpacks the join response into `Lobby.Modifiers` and then calls back only
`PlayerConnected` — never `ModifiersChanged`, which is the sole path to
`NCustomRunModifiersList.SyncModifierList` and therefore the only thing that ever ticks a
client's boxes. So a client saw the host's modifiers *after the host next touched one*, and never
before. `DuelLobbyPanelPatch.AfterClientJoined` now calls `ModifiersChanged()` itself — the same
path every later change takes, rather than a second notion of "apply the host's modifiers".

Two things fell out of it:

- **The client's preset chips were live and would have thrown.** They are real tickboxes we
  build, so vanilla's `Initialize(Client)` pass — which disables the tickboxes that exist at that
  moment — never reached them, and clicking one calls `SetTickedModifiers`, which *throws* in any
  mode but host and singleplayer. Now disabled and greyed for anyone who cannot edit, matching
  what vanilla does to its own. Asked as "may I set modifiers", not "am I a client": a lobby
  loaded from a multiplayer save is neither, and can edit nothing either.
- The client now logs what it believes it agreed to (`duel lobby: joined with …`), because an
  unticked row and a row nobody told the client about look identical on screen.

**The Duel button shared Custom's material, measured rather than guessed.** Both hypotheses in
the old note were wrong, and one log line settled it:

```
customScene='res://scenes/ui/submenu_button.tscn', bgMaterialWasShared=True,
titleResolved=True, iconResolved=True, hoverConnections=1, releaseConnections=1
```

Scene-unique names resolve fine (`titleResolved`), and there were never duplicated signal
connections — Godot only duplicates connections flagged `CONNECT_PERSIST`, i.e. made in the
editor, and every connection here is a runtime `Callable.From`. What *was* wrong:
`NSubmenuButton.ConnectSignals` caches `BgPanel.Material` as `_hsv` and the hover tween writes
its `v` parameter directly, and `Duplicate()` handed the clone **the same material object**. Two
buttons on one material are one button as far as illumination goes — hence Custom lighting up
with Duel, and hence the "unresponsive" feel, which is two instances tweening one parameter
against each other. The clone now takes its own copy, **before** entering the tree, since `_hsv`
is cached during `_Ready`.

`DUPLICATE_SIGNALS` is dropped anyway, and the diagnostics line is kept: the clone is the one
widget in this mod built by copying vanilla rather than constructing it, and everything copying
can get wrong here fails as presentation — which this project has already learned not to
diagnose from screenshots.

## Fixed in the 2026-08-11 session

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
  the other duel-pool questions in DESIGN §9 (potions referencing monsters) rather than being
  decided ad hoc.

  Note this is **not** answered by the co-op card ban (DESIGN §9, decided 2026-08-12). That ban
  is generation-level — it stops the race *offering* ally-targeting cards — whereas Booming Conch
  is a relic already in hand, and the question is what it should do once the duel starts. The two
  look alike and are different decisions.

## Resolved without a change

- Friend's Multiplayer → Host went straight to a normal lobby with no Custom/Daily/Duel choice.
  Their own local issue.
- Client "internal error" popups: `--fastmp=join` firing at startup with no host listening, and
  again after the host leaves the result screen. A dev-harness artefact that cannot happen for
  someone launching through Steam.
