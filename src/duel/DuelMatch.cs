using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Modifiers;
using SpirePvp.Race;

namespace SpirePvp.Duel;

/// <summary>
/// Reads the match configuration off the run's modifiers (DESIGN §5b).
///
/// One source of truth: the lobby decides, the run carries it, and everything downstream —
/// RNG mirroring, race activation, the clock, `DuelStartMessage` — asks here rather than
/// keeping its own copy. Because modifiers are installed before players are seeded and are
/// serialized with the run, this is answerable at every point that used to need a hand-synced
/// flag.
/// </summary>
public static class DuelMatch
{
    /// <summary>
    /// True when this run was configured as a PvP match. Safe to call at any time, including
    /// during seeding, before RunManager has a State.
    /// </summary>
    public static bool IsPvpRun(IRunState? runState)
    {
        if (runState?.Modifiers == null)
        {
            return false;
        }

        foreach (ModifierModel modifier in runState.Modifiers)
        {
            if (modifier is DuelBlitz or DuelTurnBased)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The agreed turn model; blitz unless the turn-based modifier is present.</summary>
    public static bool IsTurnBased(IRunState? runState)
    {
        if (runState?.Modifiers == null)
        {
            return false;
        }

        foreach (ModifierModel modifier in runState.Modifiers)
        {
            if (modifier is DuelTurnBased)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The agreed per-player time bank, in minutes. Zero means no clock, which is also the
    /// answer when no clock modifier was picked — silently giving someone a timer they did not
    /// agree to would be worse than giving them none.
    /// </summary>
    public static double ClockMinutes(IRunState? runState)
    {
        if (runState?.Modifiers == null)
        {
            return 0;
        }

        foreach (ModifierModel modifier in runState.Modifiers)
        {
            if (modifier is DuelClockModifier clock)
            {
                return clock.Minutes;
            }
        }
        return 0;
    }

    /// <summary>
    /// Called by whichever turn-model modifier is present, once the run exists.
    ///
    /// This replaces the `race on` console command: the race is live from run creation, so
    /// Neow is drawn under mirrored seeds and nothing needs re-seeding afterwards.
    /// </summary>
    public static void OnRunCreated(RunState runState)
    {
        Log.Warn($"[SpirePvp] PvP match: turnModel={(IsTurnBased(runState) ? "turn-based" : "blitz")}, " +
                 $"clock={ClockMinutes(runState)} min, seed '{runState.Rng.StringSeed}'");

        DuelSession.ActivateRace();
        DuelClockService.Configure(ClockMinutes(runState));
        RaceCoordinator.BeginRace();
    }
}
