# SpirePvp — agent orientation

1v1 real-time blitz PvP mod for Slay the Spire 2 (Godot 4.5.1 .NET, official mod loader,
Harmony ships with the game). Two players race identical-seed runs through Act 1, then duel
in real time with chess clocks.

**Read first:** `README.md` (toolchain, StS2 modding model, engine research) →
`docs/DESIGN.md` (full technical design, milestones M1–M7, open investigations I1–I7).
On macOS: `docs/MAC_SETUP.md` has the complete setup + takeover checklist.

**Current state:** M0 done (toolchain verified on Windows, skeleton mod loads in-game).
Next: **M1 — duel spike** (DESIGN §7). Skeletons in `src/` compile against the real game
APIs; patches are guarded no-ops until implemented.

**Rules of the road:**
- `dotnet build` must stay green; it auto-installs the mod into the local game.
- DESIGN.md file references (`Core/...`) point into a locally decompiled `sts2.dll`
  (not committed — regenerate per README/MAC_SETUP). Verify APIs against the current game
  version before patching; the design was written against v0.110.1.
- When an I# investigation resolves, write the answer into DESIGN.md — it's the shared
  source of truth across machines (Windows desktop + MacBook) and agents.
- Godot is only needed headless for `.pck` export, and only when Godot-side assets change.
  Keep Godot at exactly 4.5.1 mono.
- Both players must run identical mod versions (net message ids are positional).
