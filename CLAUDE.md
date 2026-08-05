# SpirePvp — agent orientation

1v1 real-time blitz PvP mod for Slay the Spire 2 (Godot 4.5.1 .NET, official mod loader,
Harmony ships with the game). Two players race identical-seed runs through Act 1, then duel
in real time with chess clocks.

**Read first:** `docs/HANDOFF.md` — current state, how to run two local clients, console
commands, architecture tour, and the traps that cost real time. Then `README.md` (toolchain,
StS2 modding model) → `docs/DESIGN.md` (full design, milestones, investigations I1–I7).
Platform setup: `docs/MAC_SETUP.md` is macOS-specific; the flags and reasoning in HANDOFF are
OS-neutral.

**Current state (2026-08-05):** **M1–M4 done and playtested** against v0.110.1 on two local
clients over ENet. A duel is playable end to end — entry screen, real cards and statuses,
chess clock, win/lose on HP or time, result screen. **M5 (race phase) is researched but not
started**; read DESIGN I3/I4 first, and spike map traversal before anything else.

**The one idea that explains most of the code:** the duel never breaks card logic — it breaks
every place the engine encodes "enemy" as a *side* rather than a *relationship*. Both duelists
are on `CombatSide.Player` with an empty enemy side. When something behaves oddly, look for a
side comparison before suspecting the mechanic. DESIGN §7 has the symptom → cause table.

**Rules of the road:**
- `dotnet build` must stay green; it auto-installs the mod into the local game.
- **Never use `Harmony.PatchAll`.** It throws on the first bad target and silently abandons the
  rest. `SpirePvpInit` patches per class and logs a count — confirm `N patch classes applied
  cleanly` in the log on every launch, or in-game results are meaningless.
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
