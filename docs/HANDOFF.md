# Handoff — state of the mod as of 2026-08-05

Written for someone (human or agent) picking this up cold, on any OS. Everything below was
built and playtested against **Slay the Spire 2 v0.110.1**, on two local clients connected
over ENet.

Read order: this file → `CLAUDE.md` → `README.md` → `docs/DESIGN.md`.
Platform setup: `docs/MAC_SETUP.md` is macOS-specific but its *reasoning* is portable — the
flags, console commands and gotchas below are OS-neutral unless marked.

---

## Where the project is

| Milestone | State |
|---|---|
| **M0** toolchain | done |
| **M1** duel spike | **done**, playtested |
| **M2** round loop | **done**, playtested |
| **M3** chess clock | **done**, playtested |
| **M4** information rules | **done**, playtested |
| **M5** race phase | **researched only** — see DESIGN I3/I4 |
| M6 full loop, M7 polish | not started |

A duel is fully playable end to end today: enter the arena, fight with real cards and
statuses, win or lose on HP or on the clock, and land on a victory/defeat screen.

---

## The one idea that explains most of the code

**The duel never breaks card logic. It breaks every place the engine encodes "enemy" as a
*side* rather than a *relationship*.**

Both duelists sit on `CombatSide.Player` with an empty enemy side (DESIGN §3.1). Damage,
block, powers and damage-over-time all operate on `Creature` and needed no changes at all.
Every single bug in M1–M4 was a side comparison somewhere. DESIGN §7 has the full
symptom → cause table; consult it before suspecting a mechanic.

Two rules that follow from this, and that cost real time to learn:

- **`CombatState.HittableEnemies` is not patchable.** It is a bare property with no idea who
  is attacking, so it cannot know whose opponent to return. `CombatState.GetOpponentsOf` is
  the correct chokepoint — it is handed the attacker.
- **Targeting is validated more than once, independently.** `CardModel.IsValidTarget`,
  `PotionModel.IsValidTarget` and `NTargetManager.AllowedToTargetCreature` all check sides
  separately. Fixing one and testing is how you conclude, wrongly, that nothing happened.

---

## Things that will bite you

**Patches fail silently if you use `Harmony.PatchAll`.** It throws on the first bad target and
abandons the rest, so one typo disables an arbitrary subset while the mod still loads and
still logs "loaded". `SpirePvpInit` therefore applies each patch class independently and logs
a count. **On every launch, confirm the log says `N patch classes applied cleanly`** — if it
says `PATCH FAILED`, some of the mod is not running and in-game results mean nothing.

**Harmony resolves `[HarmonyPatch(typeof(X))]` against methods declared on `X` only.** Naming
an inherited method throws "Undefined target method". This caused the above.

**Verify in game after every patch change.** Several sessions' worth of confusing symptoms
were patches that had never applied.

**Steam updates the game silently.** A pending update landed mid-session and moved the codebase
from v0.109.0 to v0.110.1 underneath a decompile, producing an investigation that was entirely
wrong (a method that "did not exist" was added in the update). After any launch through Steam,
check `release_info.json` and re-run the decompile if the version moved.

---

## Running two local clients (any OS)

No second machine, no Steam lobby, no second account. Vanilla command-line flags do it — this
is I7, and it needed no mod code.

```
<game binary> --force-steam=off --fastmp=host_standard
<game binary> --force-steam=off --clientId=1001 --fastmp=join
```

Concrete per-OS (tab 1 = host, tab 2 = client):

macOS:
```
"$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/SlayTheSpire2" --force-steam=off --fastmp=host_standard
"$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/SlayTheSpire2" --force-steam=off --clientId=1001 --fastmp=join
```

Windows (PowerShell; game on the D: Steam library — adjust if elsewhere):
```
& "D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" --force-steam=off --fastmp=host_standard
& "D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" --force-steam=off --clientId=1001 --fastmp=join
```

- `--force-steam=off` skips Steamworks entirely (`NGame.InitializePlatform`). Required: a
  direct launch otherwise fails `SteamAPI_Init` with "No appID found" and the game quits. It
  also sidesteps Steam's one-instance-per-account limit, which is what makes two local clients
  possible at all.
- `--fastmp=<host_standard|join>` is a vanilla dev flag that auto-clicks through the menus
  **and** forces `PlatformType.None`, i.e. the ENet transport on `127.0.0.1:33771` instead of
  Steam lobbies.
- `--clientId=N` sets the net id *and* selects the save profile, so each instance needs its
  own.

**First launch per instance:** start it with no `--fastmp`, accept the mod-loading warning on
the Mods screen, quit. The consent is stored per profile and mods will not load without it.

The client window retitles itself to "Slay The Spire 2 (Client)", which is how you tell them
apart.

---

## Console commands

The dev console opens with **backtick** (also `'`, `*`, `^`, or Shift+8). **Running any mod
unlocks the full vanilla debug command set** (`ModManager.IsRunningModded()` feeds
`shouldAllowDebugCommands`), so you already have everything below without writing tooling.

Mod commands:

| Command | Effect |
|---|---|
| `duel start` | Opens the opponent's decklist as the duel entry screen. Both players confirm, then the arena loads. |
| `duel now` | Skips the entry screen, straight into the arena. Debug shortcut. |
| `duel clock <minutes>` | Sets the time bank. `0` disables the clock entirely (the default). |
| `duel on` / `duel off` | Converts the combat you are already in into a duel, and back. Legacy path from M1; `duel start` is the real flow. |

Useful vanilla ones for testing:

| Command | Notes |
|---|---|
| `card <ID> [pile]` | Screaming snake case (`BODY_SLAM`). Piles: `Draw Hand Discard Exhaust Play Deck`. **`Deck` is the run-level pile** the entry screen reads. |
| `power <id> <amount> <target-index>` | Index is into `state.Creatures` — `0` is you, `1` is the opponent. Works fine despite the empty enemy side. |
| `damage <amount> <index>` | **Always pass the index.** Bare `damage 10` targets `Enemies`, which is empty in a duel, and silently does nothing. |
| `energy`, `draw`, `block`, `heal`, `potion`, `relic`, `kill` | As labelled. |

Known vanilla quirk, not a bug in this mod: the top-bar deck counter caches its value and only
refreshes on the pile's `CardAddFinished`/`CardRemoveFinished`, which the console's add into
the run-level Deck pile does not raise. Cards added by console are really there; the label is
just stale.

---

## Architecture tour

`src/duel/`

| File | Role |
|---|---|
| `DuelSession` | Client-local phase state. Every patch is inert unless a phase is active, so the mod does nothing in normal play. |
| `DuelEntry` | The entry flow: opponent's decklist, revocable confirm, both-ready gate. |
| `DuelArena` | Enters the arena room. **Ordering is load-bearing** — `DuelSession` must be active *before* `EnterRoom`, or the empty enemy side ends combat instantly. |
| `DuelEncounter` | A combat encounter with no monsters. Registered automatically: `ModelDb.AllAbstractModelSubtypes` scans mod assemblies, so custom models need **no BaseLib**. |
| `DuelLayout` | Draws the opponent on the enemy side and mirrors their art. Presentation only — `CombatSide` is untouched. |
| `DuelClockService` / `DuelClock` | Chess clocks. Wall-clock based, run-scoped by design. |
| `DuelFlag` | Losing on time. Host-authoritative. |
| `DuelResult` | Ends the duel on a victory/defeat screen. |

`src/duel/patches/` — one class per concern, each documenting *why* the patch exists and what
the engine does that requires it. Those comments are the real documentation; read them before
changing behaviour.

`src/net/DuelMessages.cs` — `INetMessage` types. **Auto-registered** by `MessageTypes.Initialize`
scanning mod assemblies; no registration call needed. **Message ids are positional**, so both
clients must run the same build. `ForcedEndTurnMessage` is dead (sudden death replaced it) but
retained deliberately, because removing it renumbers the rest.

### Determinism, and why the netcode looks the way it does

The engine is a host-authoritative deterministic simulation: clients request actions, the host
orders them, everyone executes the same stream. Anything that decides an outcome must have
exactly one decision-maker, or the two sims disagree.

So: **the host alone decides losing on time** (clients' clocks are display-only), and **the host
alone decides when the duel starts** (`DuelStartMessage`; two clients independently entering a
room is a race). Clock display is synced separately and cosmetically via `ClockSyncMessage`.
Keep that split if you extend this.

---

## Immediate next step

**M5's spike, and only the spike.** DESIGN I3 has the full research; the summary is that the
state synchronizers are trivially disablable public bools, the RNG mirroring is a one-line
patch, and *all* the risk is in map traversal, where position is global and
`MoveToMapCoordAction` travels every client.

Prove two clients can occupy different map coords without the engine falling over **before**
building any race HUD. If it resists, DESIGN §4's v1.5 fallback — co-op through Act 1 together,
then duel — is a complete playable product, and M1–M4 are unaffected either way.

Smaller known gaps, none blocking:

- `HellraiserPower`'s infinite-combo cap misfires in a duel (`HittableEnemies.All(...)` on an
  empty list is vacuously true), capping auto-plays at 9 per turn. Arguably desirable.
- Other `AfterSideTurnStart` powers may have the same round-late skew poison had. Audit when
  one shows up; only poison is fixed.
- The duel entry screen's confirm feedback is a colour tint standing in for the intended
  green check + opponent portrait (DESIGN §6, wants an asset pass).
- No `.pck` assets yet beyond the mod image. The duel map node icon (M6) is the first real
  need, and the custom confirm button should be batched with it.
