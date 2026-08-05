using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Race;

/// <summary>
/// M5 spike (DESIGN §4, I3): lets the two clients run independently through the same seeded
/// map instead of moving as a co-op party.
///
/// The state-sync half is genuinely trivial — both switches are public settable bools, and
/// vanilla's own NMultiplayerTest debug screen flips the first one. What the spike is really
/// testing is the other two patches in this folder, which cover the parts of the engine that
/// assume the party is co-located.
///
/// Deliberately NOT disabled here: ActChangeSynchronizer's rendezvous at act boundaries. The
/// race wants that barrier — it is where both players converge for the duel.
/// </summary>
public static class RaceCoordinator
{
    private static bool _combatSyncWasDisabled;
    private static bool _checksumsWereEnabled;

    public static void BeginRace()
    {
        RunManager run = RunManager.Instance;

        // Remember vanilla's values rather than assuming, so EndRace restores rather than
        // guesses — the duel needs both of these back on to stay deterministic.
        _combatSyncWasDisabled = run.CombatStateSynchronizer.IsDisabled;
        _checksumsWereEnabled = run.ChecksumTracker.IsEnabled;

        // Pre-combat state sync broadcasts every player's serialized state and waits for
        // everyone. During a race the peers are in different rooms and have genuinely
        // divergent state, so waiting is both pointless and a deadlock risk.
        run.CombatStateSynchronizer.IsDisabled = true;

        // Divergence is the *point* during a race, so checksum comparison would fire
        // constantly. Re-enabled for the duel, which is fully coupled again.
        run.ChecksumTracker.IsEnabled = false;

        Log.Warn("[SpirePvp] race mode ON — combat state sync and checksums disabled");
    }

    /// <summary>
    /// Answers I4 with data instead of inference.
    ///
    /// Card rewards come from <c>player.PlayerRng.Rewards</c>, and
    /// <c>Player.InitializeSeed</c> seeds that with
    /// <c>hash(runSeed) + GetPlayerSlotIndex(this)</c> — so on paper the two players' rewards
    /// must differ, and I4 exists to remove that offset for mirror-match fairness. Playtesting
    /// says the rewards already match, which can only be true if both clients hand the local
    /// player the same slot index.
    ///
    /// Rather than keep reading code, print the run seed and each player's slot and RNG seed
    /// on both clients. If the local player's seed is identical across the two logs, the
    /// mirroring is already happening and I4 is unnecessary.
    /// </summary>
    public static void LogSeedDiagnostics()
    {
        RunState? state = RunManager.Instance.State;
        if (state == null)
        {
            Log.Warn("[SpirePvp] seed diagnostics: no run state");
            return;
        }

        Log.Warn($"[SpirePvp] seed diag: run seed '{state.Rng.StringSeed}'");
        foreach (Player player in state.Players)
        {
            Log.Warn($"[SpirePvp] seed diag: netId={player.NetId} slot={state.GetPlayerSlotIndex(player)} " +
                     $"playerRngSeed={player.PlayerRng.Seed} isMe={LocalContext.IsMe(player)}");
        }
    }

    public static void EndRace()
    {
        RunManager run = RunManager.Instance;
        run.CombatStateSynchronizer.IsDisabled = _combatSyncWasDisabled;
        run.ChecksumTracker.IsEnabled = _checksumsWereEnabled;
        Log.Warn("[SpirePvp] race mode OFF — state sync restored");
    }
}
