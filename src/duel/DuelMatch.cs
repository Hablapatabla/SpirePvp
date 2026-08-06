using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
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
        RaceCoordinator.BeginRace();

        // Bank size only — the clocks cannot start yet, see OnRunLaunched.
        DuelClockService.Configure(ClockMinutes(runState));

        InstallArenaNode(runState);
    }

    /// <summary>
    /// Makes the duel arena a real map node, sitting immediately after the act boss.
    ///
    /// This needs no map-generation code of its own. `StandardActMap` already builds a second
    /// boss node one row below the first and chains it as the boss's child — that is the
    /// back-to-back layout Act 3 uses for double bosses — and it does so whenever
    /// `ActModel.HasSecondBoss` is true, which is simply "a second boss encounter has been
    /// set". So handing the act our DuelEncounter through the public
    /// `SetSecondBossEncounter` is the entire feature: boss → arena, exactly as intended.
    ///
    /// Called at run creation, before the act's map is generated, because generation reads
    /// `HasSecondBoss` at that moment. Doing it later would build a map with no arena on it.
    ///
    /// Any act with a boss gets one; for v1 that is Act 1, which is where the race ends.
    /// </summary>
    private static void InstallArenaNode(RunState runState)
    {
        DuelEncounter arena = ModelDb.Encounter<DuelEncounter>();
        foreach (ActModel act in runState.Acts)
        {
            act.SetSecondBossEncounter(arena);
        }

        Log.Warn($"[SpirePvp] arena node installed after the boss in {runState.Acts.Count} act(s)");
    }

    /// <summary>
    /// The second half of match setup: everything that needs to know which player is *us*.
    ///
    /// `LocalContext.NetId` is assigned in `RunManager.Launch`, which runs *after* modifiers'
    /// `AfterRunCreated`. Doing local-player work in `OnRunCreated` therefore silently
    /// misfires: `IsMe` is false for everyone, so the race deactivated hooks for *both*
    /// players rather than just the opponent, and the clocks never started because
    /// `GetMe` returned null. The log said it plainly — "hooks deactivated for remote player 1"
    /// and "1001" on the same client.
    ///
    /// So anything identity-dependent belongs here instead.
    /// </summary>
    public static void OnRunLaunched(RunState runState)
    {
        if (!IsPvpRun(runState))
        {
            return;
        }

        RaceCoordinator.DeactivateRemotePlayerHooks(runState);

        // Arm the duel handshake now rather than when the local player first acts. Both sides
        // must be listening before *either* can announce anything, or the first announcement
        // is simply lost.
        DuelRendezvous.Reset();
        DuelRendezvous.Arm();
        DuelEntry.Arm();

        // The bank covers the whole run, not just the duel (DESIGN §9). During the race both
        // clocks simply run down — the players act continuously and simultaneously — and only
        // in the duel does it become a true chess clock that pauses on end turn. Ticking rides
        // the vanilla run timer, which is alive for the whole run.
        Player? me = LocalContext.GetMe(runState.Players);
        Player? opponent = null;
        foreach (Player player in runState.Players)
        {
            if (!LocalContext.IsMe(player))
            {
                opponent = player;
                break;
            }
        }

        if (DuelClockService.Enabled && me != null && opponent != null)
        {
            DuelClockService.Start(me.NetId, opponent.NetId);
        }
        else if (DuelClockService.Enabled)
        {
            Log.Error($"[SpirePvp] clock configured but not started: me={me?.NetId}, opponent={opponent?.NetId}");
        }
    }
}
