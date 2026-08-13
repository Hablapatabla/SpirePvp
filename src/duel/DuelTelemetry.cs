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
        _clockReportedPhase = -1;
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
    /// **The residue of the AoE fix, and the only part of it a log can report.**
    ///
    /// This used to be `NoteEmptyAoe`, a pure probe: it reported *every* empty `HittableEnemies`
    /// read during a duel, because at the time every one of them was a bug and the point was to
    /// learn which effects turned up in play. It never reported anything — the probe shipped on
    /// 2026-08-12 and the single duel played on that build used no AoE effect at all, so the fix
    /// that followed was built from the decompile instead.
    ///
    /// Now `DuelAoeTargetingPatch` answers those reads with the asking duelist's opponents, and an
    /// empty result means something narrower: **the mod could not name an actor for this read**, so
    /// it left vanilla's answer alone and the effect hit nothing. That is the gap to bring back,
    /// with the running action as the key — the same key as before, so old and new logs compare.
    ///
    /// Still one line per distinct key per run. A probe that floods the log makes the log useless
    /// for the bug it was added to catch, and this one now sits on a property the UI reads every
    /// frame.
    /// </summary>
    public static void NoteUnresolvedAoe()
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

        Log.Warn($"[SpirePvp] telemetry: an \"all enemies\" read found NO ACTOR during a duel — "
                 + $"running action: {key}. Vanilla's empty list stands, so that effect hit "
                 + "nothing. This names a case DuelAoeActor does not cover.");
    }

    private static int _clockReportedPhase = -1;

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
        // **Once per phase, not once per run.** The first attempt reported once and fired on the
        // title-to-map transition, long before a duel existed — `ourClockEnabled=True` with
        // `visible=False` told us nothing, because the moment sampled was not the moment asked
        // about. Keyed on the phase so the race and the duel each get a line.
        if (_clockReportedPhase == (int)DuelSession.Phase)
        {
            return;
        }

        _clockReportedPhase = (int)DuelSession.Phase;
        Log.Warn($"[SpirePvp] telemetry: run timer [phase {DuelSession.Phase}] — visible={visibleAfter}, pref={prefShowsTimer}, "
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
