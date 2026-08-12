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
    /// The run's modifiers as *we* should see them.
    ///
    /// `DuelNeowOptionsPatch` temporarily empties `RunState.Modifiers` so vanilla takes its
    /// normal Neow branch, and everything here reads that same list — so for the duration of
    /// that call the run stopped looking like a PvP match to its own mod. That is not
    /// theoretical: it is why Massive Scroll, the co-op-only Neow blessing, kept being offered.
    /// `RaceNoCoopCardsPatch` asks `IsPvpRun` from inside `MassiveScroll.IsAllowed`, which Neow
    /// calls from inside exactly that window, so the answer came back "not a PvP run" and the
    /// blessing survived the filter. Taking it then threw, because the *card* filters ran later
    /// with the modifiers restored and left its pool empty.
    ///
    /// The patch is lying to vanilla on purpose. It should not be able to lie to us, so it
    /// parks the real list here and this is the only place that knows.
    /// </summary>
    internal static IReadOnlyList<ModifierModel>? MaskedModifiers { get; set; }

    private static IEnumerable<ModifierModel> EffectiveModifiers(IRunState? runState) =>
        MaskedModifiers ?? runState?.Modifiers ?? (IEnumerable<ModifierModel>)Array.Empty<ModifierModel>();

    /// <summary>Public so the lobby can ask whether a set of modifiers describes a duel.</summary>
    public static bool HasTurnModel(IEnumerable<ModifierModel> modifiers)
    {
        foreach (ModifierModel modifier in modifiers)
        {
            if (modifier is DuelBlitz or DuelTurnBased)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when this run was configured as a PvP match. Safe to call at any time, including
    /// during seeding, before RunManager has a State.
    /// </summary>
    public static bool IsPvpRun(IRunState? runState) => HasTurnModel(EffectiveModifiers(runState));

    /// <summary>
    /// The same question asked of what the run itself declares, ignoring <see cref="MaskedModifiers"/>.
    ///
    /// Only `DuelNeowOptionsPatch` should need this, and it needs it badly: it is the thing that
    /// installs the mask, so it must decide from the list it is about to hide rather than from
    /// one it — or a call still in flight — has already been told to pretend. Asking the masked
    /// question there is circular, and answers about the wrong list.
    /// </summary>
    internal static bool IsPvpRunUnmasked(IRunState? runState) =>
        HasTurnModel(runState?.Modifiers ?? (IEnumerable<ModifierModel>)Array.Empty<ModifierModel>());

    /// <summary>The agreed turn model; blitz unless the turn-based modifier is present.</summary>
    public static bool IsTurnBased(IRunState? runState)
    {
        foreach (ModifierModel modifier in EffectiveModifiers(runState))
        {
            if (modifier is DuelTurnBased)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The agreed per-player bank for reaching the arena, in minutes.
    ///
    /// Zero means no clock, which is also the answer when no clock modifier was picked —
    /// silently giving someone a timer they did not agree to would be worse than giving them
    /// none.
    /// </summary>
    public static double RaceClockMinutes(IRunState? runState) => MinutesOf<RaceClockModifier>(runState);

    /// <summary>
    /// The agreed per-player bank for the duel, in minutes. Granted fresh when the duel begins
    /// rather than carried over from the race (DESIGN §9), so the two are wholly independent:
    /// a match is configured as, say, a 10-minute race followed by a 2-minute duel.
    /// </summary>
    public static double DuelClockMinutes(IRunState? runState) => MinutesOf<DuelClockModifier>(runState);

    private static double MinutesOf<T>(IRunState? runState) where T : ClockModifierBase
    {
        foreach (ModifierModel modifier in EffectiveModifiers(runState))
        {
            if (modifier is T clock)
            {
                return clock.Minutes;
            }
        }
        return 0;
    }

    /// <summary>
    /// Drops everything that belonged to the run that just finished.
    ///
    /// All of this mod's state is static, so it outlives a run — and the net service it is bound
    /// to does not. Playing a second match in the same process therefore breaks in two ways,
    /// both silent:
    ///
    /// - Handlers armed against the old service are gone, but the `_armed` flags still say
    ///   armed, so `Arm()` no-ops and nothing re-registers. The peer's messages are then
    ///   dropped without a word. This is the third time this project has been bitten by a
    ///   handler that was not listening.
    /// - The clocks keep running. Measured 2026-08-05: after a duel ended, the host went on
    ///   broadcasting `ClockSyncMessage` twice a second into an ordinary co-op run, and the
    ///   client logged "no message handlers are registered for that type" for every one.
    ///
    /// Hooked on run *teardown*, not run start, because the obvious alternative does not work:
    /// `OnRunCreated` runs inside `CreateForNewRun` and `OnRunLaunched` after it, so a reset in
    /// either would wipe what the other had just installed. Teardown has no such ordering to get
    /// wrong — and it fires for a non-PvP next run too, which is precisely the case that
    /// produced the flood.
    /// </summary>
    public static void OnRunEnded()
    {
        DuelSession.Reset();
        DuelClockService.Reset();
        DuelArena.Reset();
        RaceCoordinator.Reset();
        DuelDisconnect.Reset();
        DuelFlag.Disarm();
        DuelEntry.Disarm();
        DuelRendezvous.Disarm();
        DuelResult.Disarm();
        DuelResign.Disarm();
        DuelStats.Disarm();
        DuelRematch.Disarm();
        RaceProgress.Disarm();
        RaceProgressHud.Clear();
        DuelLayout.Reset();
        MaskedModifiers = null;
    }

    /// <summary>
    /// Called by whichever turn-model modifier is present, once the run exists.
    ///
    /// This replaces the `race on` console command: the race is live from run creation, so
    /// Neow is drawn under mirrored seeds and nothing needs re-seeding afterwards.
    /// </summary>
    public static void OnRunCreated(RunState runState)
    {
        // The menu entry is locked when patches are missing, but the modifiers can still be
        // ticked by hand through the plain Custom lobby — and a client can be handed them by a
        // host whose own install is fine. So the refusal lives here too, at the one point every
        // route to a PvP run passes through.
        //
        // The run itself is left alone deliberately: it carries on as an ordinary co-op run
        // rather than being torn down. Refusing to *arbitrate* is the safe failure; destroying
        // someone's run because a patch did not bind would be a worse one.
        if (!SpirePvpInit.PatchesHealthy)
        {
            Log.Error("[SpirePvp] refusing to start a PvP match: some patch classes failed to " +
                      "apply, so this client cannot arbitrate one. The run continues as normal " +
                      "co-op. Rebuild the mod — patch targets bind at compile time, so a rebuild " +
                      "will name anything the game moved.");
            return;
        }

        Log.Warn($"[SpirePvp] PvP match: turnModel={(IsTurnBased(runState) ? "turn-based" : "blitz")}, " +
                 $"raceClock={RaceClockMinutes(runState)} min, duelClock={DuelClockMinutes(runState)} min, " +
                 $"seed '{runState.Rng.StringSeed}'");

        DuelSession.ActivateRace();
        RaceCoordinator.BeginRace();

        // Bank sizes only — the clocks cannot start yet, see OnRunLaunched.
        DuelClockService.Configure(RaceClockMinutes(runState), DuelClockMinutes(runState));

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
        RaceProgress.Reset();
        RaceProgress.Arm();
        DuelResign.Reset();
        DuelResign.Arm();
        DuelStats.Reset();
        DuelStats.Arm();
        DuelRematch.Reset();
        DuelRematch.Arm();

        // Started at run creation because the *race* bank is already counting (DESIGN §9): it
        // is the deadline for reaching the arena. During the race both clocks simply run down —
        // the players act continuously and simultaneously — and only in the duel does it become
        // a true chess clock that pauses on end turn, on a fresh bank of its own. Ticking rides
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

        // **After the clocks exist**, and load-bearing: `DuelFlag.Arm` subscribes to their
        // `Flagged` events, and there is no second arming pass. Armed before Start, it found
        // both clocks null, subscribed to nothing, and set `_armed` anyway — so the banks ran
        // to zero and no one ever lost on time. That is the same shape of failure as arming a
        // message handler too late: nothing throws, the feature is simply absent.
        DuelFlag.Arm();
    }
}
