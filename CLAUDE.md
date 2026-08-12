# SpirePvp — agent orientation

1v1 real-time blitz PvP mod for Slay the Spire 2 (Godot 4.5.1 .NET, official mod loader,
Harmony ships with the game). Two players race identical-seed runs through Act 1, then duel
in real time with chess clocks.

**Read first:** `docs/HANDOFF.md` — current state, how to run two local clients, console
commands, architecture tour, and the traps that cost real time. Then `README.md` (toolchain,
StS2 modding model) → `docs/DESIGN.md` (full design, milestones, investigations I1–I7).
Platform setup: `docs/MAC_SETUP.md` is macOS-specific; the flags and reasoning in HANDOFF are
OS-neutral.

**Current state (2026-08-11):** **M1–M6 done and playtested** against v0.110.1 on two local
clients over ENet. The whole loop runs: lobby modifiers → race Act 1 on a mirrored seed →
arena rendezvous → deck review → duel → result screen, with split race/duel clocks, checksums
live, and back-to-back matches in one process. Matches also end by **resignation** (abandoning
is a loss and a win for the opponent) and by **agreed draw** (pause menu → Offer Draw).

The 2026-08-11 session was a long playtest sweep that closed the race phase's remaining
rough edges — a client that could not leave a rest site, a chest that offered two relics and
then would not let the client take one, opponent portraits stuck to your own map node, the
loser's result screen wrecked by a missing loc key, and the opponent's summons drawn on the
wrong side. All fixed and played through.

**M7 is done:** a **Duel** entry sits beside Standard/Daily/Custom on the multiplayer host
menu, opening a lobby retitled "Duel" whose three real decisions (turn model · race clock ·
duel clock) are promoted into headed rows above a collapsed list of the other custom-run
modifiers, with Blitz/Rapid/No-clock presets. It re-dresses `NCustomRunScreen` rather than
replacing it, because that screen owns the whole lobby lifecycle — see `DuelLobbyPanel`.

**Distribution is live.** `git clone && dotnet build` is a complete install (the `.pck` is
committed, so no Godot needed), README has a step-by-step for a non-technical Windows player,
and the mod version carries the git commit so the engine's own mod-match gate enforces
"both on the same build". Verified coexisting with a Workshop mod (RegentFX).

**Disconnects are handled (2026-08-12):** a dropped opponent no longer leaves a match with no
result — whoever remains gets a five-second notice and the win. Note the finding underneath it,
because it bites anything that waits on a peer: **ENet never reports a hard drop**
(`ENetHost.Update` answers the transport's own `Disconnect` event with a bare `continue`), so
absence has to be *measured* via `ConnectionStats.LastReceivedTime` rather than waited for.

**Rematch and M8 are done and playtested (2026-08-12).** A Rematch button on the result screen
replays the same seed without passing through the main menu — the transport is held open through
run teardown, which works because `CleanUp` has *not* fired while that screen is up. And
`1v1 Duel: Turn-Based` now plays turn-based: each side plans a round privately, ending your turn is
the lock-in, and the host resolves the two buffers interleaved.

**The planning phase now shows itself (2026-08-12, unplayed):** energy is reserved as you plan, held
cards sit in vanilla's play queue, and an icon over the end turn button says who has locked in. Both
surfaces are the engine's own — a held play and a co-op play awaiting the host's ordering are the
same thing. Note what this cost: `CanPlay` is read by sim code (`PlayCardAction`, `CardSelectCmd`,
`WhisperingEarring`), so a *local* rule like a reservation may only answer while nothing is
executing, or the two sims disagree about which cards exist.

**Remaining:** turn-based has an open *design* problem — draw cards are near-dead, because the
round is planned from the opening hand (options and the leaning are in HANDOFF). Then M8.5,
tick-paced blitz, which is the most promising idea the playtests produced. See HANDOFF for the
current list and two open bugs.

**The one idea that explains most of the code:** the duel never breaks card logic — it breaks
every place the engine encodes "enemy" as a *side* rather than a *relationship*. Both duelists
are on `CombatSide.Player` with an empty enemy side. When something behaves oddly, look for a
side comparison before suspecting the mechanic. DESIGN §7 has the symptom → cause table.

**Rules of the road:**
- `dotnet build` must stay green; it auto-installs the mod into the local game.
- **Never use `Harmony.PatchAll`.** It throws on the first bad target and silently abandons the
  rest. `SpirePvpInit` patches per class and logs a count — confirm `N patch classes applied
  cleanly` in the log on every launch, or in-game results are meaningless. **66 as of 2026-08-12** (102 methods). Note the count is per *class*, not per patch: a class holding several patch
  methods still counts once, so grouping patches by concern does not move it.
- **The engine assumes the party is standing together, and in a race it is not.** This is the
  single most productive thing to suspect when a race-phase room misbehaves — it has now
  produced bugs in combat, map travel, rest sites, the treasure chest and the shop. It comes in
  two shapes and `src/race/RaceSolo.cs` documents both, along with the rule that falls out: **in
  a race the local player must present as slot 0.** Where vanilla has a real singleplayer path,
  prefer it to correcting the multiplayer one.
- **Read the logs yourself** (`logs/host.log`, `logs/client.log`) rather than asking for
  symptoms, and check the log's timestamp against the installed DLL — a stale log is
  indistinguishable from a patch that stopped applying. The launchers rotate rather than
  truncate, so the previous five runs are still there as `host.<timestamp>.log`.
- **Verify in game after every patch change.** Several rounds of confusing symptoms turned out
  to be patches that had never applied.
- DESIGN.md file references (`Core/...`) point into a locally decompiled `sts2.dll` (not
  committed — regenerate per README/MAC_SETUP). **Steam can update the game silently**; check
  `release_info.json` and re-decompile if the version moved, or you will research the wrong
  codebase.
- When an I# investigation resolves, write the answer into DESIGN.md — it's the shared source
  of truth across machines (Windows desktop + MacBook) and agents.
- Godot is only needed headless for `.pck` export, and only when Godot-side assets change.
  Keep Godot at exactly 4.5.1 mono.
- **A message that only fires on *change* cannot carry initial state.** The peer that arrives
  late gets its state some other way, so hook the arrival too. This has now bitten four times —
  most recently the joining client seeing the plain Custom lobby, because the host applied the
  preset before anyone was connected and `LobbyModifiersChangedMessage` had nothing left to
  announce. Same family as arming handlers at run start rather than on first local use.
- **Patch targets are `nameof`, not strings**, so a game update that moves one is a build error
  naming the method rather than a runtime `PATCH FAILED`. Keep it that way. The single exception
  is `Neow.GenerateInitialOptions`, which is virtual and so not publicized.
- **If any patch class fails, duelling refuses to start** (`SpirePvpInit.PatchesHealthy`): the
  Duel menu entry locks and `DuelMatch.OnRunCreated` bails, leaving the run as ordinary co-op.
  A half-applied patch set arbitrating a two-player match is a hang or a desync that reads as a
  gameplay bug.
- Both players must run identical mod versions (net message ids are positional). Debug builds
  stamp the git commit into the mod version so the engine's own mod-match gate enforces this;
  `-c Release` keeps the clean semver for Workshop publishing.
- Anything that decides an outcome is host-authoritative — clients display, the host decides.
  Two clients concluding the same thing independently is how the sim desyncs.
