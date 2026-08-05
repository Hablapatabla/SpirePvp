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

        DeactivateRemotePlayerHooks(run);

        Log.Warn("[SpirePvp] race mode ON — combat state sync and checksums disabled");
    }

    /// <summary>
    /// Stops the opponent's relics, cards and potions firing inside *our* run — blocker 5,
    /// and the cause of a black screen entering the first combat after a Neow bonus.
    ///
    /// Room and run hooks iterate at the *run* level: `RunState.IterateHookListeners` walks
    /// every player's deck, relics and potions, not just the ones in the current combat. So
    /// `Hook.AfterRoomEntered` fired the *absent opponent's* Divine Right, which called
    /// `PlayerCmd.GainStars(..., base.Owner)` for a player whose `Creature.CombatState` is
    /// null — they were never enrolled in this combat (RaceSoloCombatPatch) — and the null
    /// combat state NREd inside the hook iterator. The throw escaped through StartCombat, so
    /// the room never finished loading.
    ///
    /// Patching Divine Right specifically would be whack-a-mole: *any* relic or card hook
    /// belonging to the absent player has the same problem, and we would meet them one crash
    /// at a time.
    ///
    /// Vanilla already has the exact concept — `IsActiveForHooks`, which it clears via
    /// `DeactivateHooks()` when a player dies, meaning "still in the run, but must not
    /// participate in hooks". Every iterator checks it first. Applying it to remote players
    /// for the duration of the race fixes the whole family at once.
    ///
    /// Each client deactivates only its *remote* players, so both players' own relics keep
    /// working normally in their own run.
    ///
    /// Self-healing for the duel: `SyncWithSerializedPlayer` — which
    /// `CombatStateSynchronizer.WaitForSync` runs on duel entry — restores
    /// `IsActiveForHooks = Creature.IsAlive`. EndRace restores it explicitly anyway rather
    /// than relying on that.
    /// </summary>
    private static void DeactivateRemotePlayerHooks(RunManager run)
    {
        RunState? state = run.State;
        if (state == null)
        {
            return;
        }

        foreach (Player player in state.Players)
        {
            if (!LocalContext.IsMe(player))
            {
                player.DeactivateHooks();
                Log.Info($"[SpirePvp] race: hooks deactivated for remote player {player.NetId}");
            }
        }
    }

    private static void ReactivateAllPlayerHooks(RunManager run)
    {
        RunState? state = run.State;
        if (state == null)
        {
            return;
        }

        foreach (Player player in state.Players)
        {
            if (player.Creature.IsAlive)
            {
                player.ActivateHooks();
            }
        }
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
        ReactivateAllPlayerHooks(run);
        Log.Warn("[SpirePvp] race mode OFF — state sync and player hooks restored");
    }
}
