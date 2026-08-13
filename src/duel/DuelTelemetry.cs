using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// Evidence-gathering for three open reports, added 2026-08-12 at Lucas's request so the next
/// session answers them instead of describing them.
///
/// **Everything here logs and changes nothing.** A probe that alters behaviour cannot be trusted to
/// describe it, and two of these three questions are about *whether* a code path runs at all — the
/// worst possible place to also start making it run. Each is rate-limited, because this project has
/// already measured a "one line per run" that was 19 per client per session (`AssetCache`), and a
/// probe that floods the log makes the log useless for the bug it was added to catch.
/// </summary>
public static class DuelTelemetry
{
    /// <summary>Reported once each, then quiet: these are conditions, not events.</summary>
    private static bool _deathReported;

    private static readonly HashSet<string> _emptyAoeSeen = new HashSet<string>();

    /// <summary>Static state, run-scoped like everything else here — released in `DuelMatch.OnRunEnded`.</summary>
    public static void Reset()
    {
        _deathReported = false;
        _emptyAoeSeen.Clear();
    }

    /// <summary>
    /// **"I died to the boss and it did not end the run."** Reported 2026-08-12, during the race.
    ///
    /// `DuelResult.Arm()` is called from `DuelArena`, i.e. on *arena entry*, so for the whole of the
    /// race nothing is watching a duelist die at all — which matches the report exactly. This probe
    /// does not fix that; it establishes the shape before the fix, because "nothing was armed" and
    /// "something was armed and declined" produce the same silence, and this project has been caught
    /// by that difference before.
    ///
    /// Ridden on the run timer's tick, the one hook already guaranteed to run for the whole match —
    /// the same reason `DuelDisconnect.Tick` lives there.
    /// </summary>
    public static void TickDeathProbe()
    {
        if (_deathReported)
        {
            return;
        }

        RunManager? run = RunManager.Instance;
        IRunState? state = run?.State;
        if (run == null || state == null || !DuelMatch.IsPvpRun(state))
        {
            return;
        }

        Player? me = LocalContext.GetMe(state);
        if (me?.Creature.IsDead != true)
        {
            return;
        }

        _deathReported = true;
        Log.Warn($"[SpirePvp] telemetry: local duelist is DEAD — phase={DuelSession.Phase}, "
                 + $"duelActive={DuelSession.IsDuelActive}, runInProgress={run.State?.IsGameOver == false}, "
                 + $"resultArmed={DuelResult.IsArmed}, abandoned={run.IsAbandoned}. "
                 + "If no duel result follows this line, the death had no arbiter.");
    }

    /// <summary>
    /// **The Bag of Marbles family.** `CombatState.HittableEnemies` is `Enemies.Where(IsHittable)`,
    /// and a duel has an empty enemy side — so every "all enemies" effect in the game resolves
    /// against nothing. 70 models read that property; `DuelTargetingPatch` has carried "STILL OPEN —
    /// AOE and random targeting" since M2.
    ///
    /// Rather than guess which of the 70 actually turn up in play, this names them as they happen:
    /// one line per distinct culprit per run, keyed by the action the sim is running. The key is
    /// also the evidence — a report with no running action (Bag of Marbles is applied from
    /// `BeforeSideTurnStart`, outside any action) is exactly the case that makes the obvious fix,
    /// resolving the actor from `CurrentlyRunningAction`, insufficient on its own.
    /// </summary>
    public static void NoteEmptyAoe()
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        GameAction? running = RunManager.Instance?.ActionExecutor.CurrentlyRunningAction;
        string key = running?.ToString() ?? "<no running action>";
        if (!_emptyAoeSeen.Add(key))
        {
            return;
        }

        Log.Warn($"[SpirePvp] telemetry: HittableEnemies came back EMPTY during a duel — "
                 + $"running action: {key}. That effect hit nothing.");
    }

    private static bool _clockReported;

    /// <summary>
    /// **"Client not showing the clock, host showing it."** Both logs said the match was untimed
    /// (`raceClock=0 min, duelClock=0 min`), so the widget in question is vanilla's `NRunTimer`
    /// rather than ours — and `show_run_timer` is `false` in *both* dev profiles, which rules out
    /// the obvious explanation.
    ///
    /// So this prints the whole decision, once per run per side, and the answer is the diff between
    /// the two logs. Every input vanilla's `RefreshVisibility` uses is here, plus ours: it shows the
    /// timer when the preference is on, otherwise only while the map or a capstone is up, and
    /// `DuelClockHudPatch` then forces it visible whenever a duel clock is running.
    /// </summary>
    public static void NoteClockHud(bool visibleAfter, bool prefShowsTimer, bool mapVisible)
    {
        if (_clockReported)
        {
            return;
        }

        _clockReported = true;
        Log.Warn($"[SpirePvp] telemetry: run timer — visible={visibleAfter}, pref={prefShowsTimer}, "
                 + $"mapVisible={mapVisible}, ourClockEnabled={DuelClockService.Enabled}, "
                 + $"ourClockLocal={DuelClockService.Local != null}. "
                 + "Compare this line against the other client's.");
    }

    /// <summary>
    /// **"We are both Necro and got different Neow bonuses."** Non-urgent, and the open design
    /// question it belongs to is in `docs/PLAYTEST_LIST.md`: whether the two runs should offer
    /// identical *rolls* or identical *offers*, which differ because Neow is filtered per character
    /// (DESIGN §1). Two players on the same character should not have been separated by that
    /// filter, which is what makes this worth a line rather than a shrug.
    ///
    /// Names rather than indices, deliberately: the indices already match by construction and mean
    /// different things on the two clients, which is the exact trap that killed the opponent-vote
    /// icon at Neow.
    /// </summary>
    public static void NoteNeowOptions(ulong netId, string character, IEnumerable<string> options)
    {
        Log.Warn($"[SpirePvp] telemetry: Neow offered {netId} ({character}): {string.Join(" / ", options)}");
    }
}
