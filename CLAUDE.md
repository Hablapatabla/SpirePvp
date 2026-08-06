# SpirePvp — agent orientation

1v1 real-time blitz PvP mod for Slay the Spire 2 (Godot 4.5.1 .NET, official mod loader,
Harmony ships with the game). Two players race identical-seed runs through Act 1, then duel
in real time with chess clocks.

**Read first:** `docs/HANDOFF.md` — current state, how to run two local clients, console
commands, architecture tour, and the traps that cost real time. Then `README.md` (toolchain,
StS2 modding model) → `docs/DESIGN.md` (full design, milestones, investigations I1–I7).
Platform setup: `docs/MAC_SETUP.md` is macOS-specific; the flags and reasoning in HANDOFF are
OS-neutral.

**Current state (2026-08-06):** **M1–M6 done and playtested** against v0.110.1 on two local
clients over ENet. The whole loop runs: lobby modifiers → race Act 1 on a mirrored seed →
arena rendezvous → deck review → duel → result screen, with split race/duel clocks, checksums
live, and back-to-back matches in one process. Matches also end by **resignation** (abandoning
is a loss and a win for the opponent) and by **agreed draw** (pause menu → Offer Draw).

**Remaining:** rematch (deliberately deferred — it is milestone-sized, not a button; see
HANDOFF), per-round damage stats on the result screen, and **M7's dedicated Duel host menu**,
which is the next real milestone: `host normal / custom / duel` with timer and ruleset
controls and chess-style presets, replacing the current route through the Custom lobby.

**The one idea that explains most of the code:** the duel never breaks card logic — it breaks
every place the engine encodes "enemy" as a *side* rather than a *relationship*. Both duelists
are on `CombatSide.Player` with an empty enemy side. When something behaves oddly, look for a
side comparison before suspecting the mechanic. DESIGN §7 has the symptom → cause table.

**Rules of the road:**
- `dotnet build` must stay green; it auto-installs the mod into the local game.
- **Never use `Harmony.PatchAll`.** It throws on the first bad target and silently abandons the
  rest. `SpirePvpInit` patches per class and logs a count — confirm `N patch classes applied
  cleanly` in the log on every launch, or in-game results are meaningless. **40 as of
  2026-08-06.**
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
- Both players must run identical mod versions (net message ids are positional).
- Anything that decides an outcome is host-authoritative — clients display, the host decides.
  Two clients concluding the same thing independently is how the sim desyncs.
