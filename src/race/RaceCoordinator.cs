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
    private static bool _raceActive;

    public static void BeginRace()
    {
        RunManager run = RunManager.Instance;
        _raceActive = true;

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

        // Hook deactivation is NOT done here. It needs to know which player is local, and
        // LocalContext.NetId is not assigned until RunManager.Launch — later than the
        // modifier's AfterRunCreated. DuelMatch.OnRunLaunched does it once identity exists.
        //
        // Re-seeding is likewise gone: a lobby-configured match carries its modifier before
        // CreateForNewRun seeds anyone, so RaceMirrorRngPatch already mirrors at source.

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
    public static void DeactivateRemotePlayerHooks(RunState? state)
    {
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

    /// <summary>
    /// Puts the shared-state machinery back the way BeginRace found it. The duel is fully
    /// coupled and depends on the pre-combat state sync the race turned off, so this must run
    /// before `DuelArena` starts that sync — not after the arena has loaded.
    ///
    /// Idempotent, and a no-op when no race ran. That guard matters: the legacy `duel start`
    /// path enters the arena from an ordinary co-op run, where "restore the saved values" would
    /// mean writing the defaults of two fields BeginRace never filled in — switching checksums
    /// off for a duel that had them on.
    /// </summary>
    public static void EndRace()
    {
        if (!_raceActive)
        {
            return;
        }

        _raceActive = false;
        RunManager run = RunManager.Instance;
        run.CombatStateSynchronizer.IsDisabled = _combatSyncWasDisabled;
        run.ChecksumTracker.IsEnabled = _checksumsWereEnabled;
        ReactivateAllPlayerHooks(run);
        ResetSynchronizerCounters(run);
        Log.Warn("[SpirePvp] race mode OFF — state sync and player hooks restored");
    }

    /// <summary>
    /// Zeroes the run-level synchronizer counters that the race pulled apart.
    ///
    /// `CombatStateSynchronizer` reconciles each player's serialized state, the run RNG and the
    /// shared relic grab bag — and nothing else. These four counters live on the *synchronizers*,
    /// not on the run: each one is bumped locally whenever a client reserves a choice, generates
    /// a reward set, enqueues an action or mints a hook action. Two clients playing their own
    /// runs therefore drift apart by construction, and nothing in the duel's state sync brings
    /// them back.
    ///
    /// The drift is invisible while checksums are off and fatal the moment they come back on: the
    /// first checksum of the duel compares them and the host drops the client for
    /// StateDivergence. Measured 2026-08-05 — the two state dumps were identical in every
    /// creature, card, pile, HP and RNG seed, and differed only in `Choice IDs 1,1` vs `0,2` and
    /// `Reward IDs 1,0` vs `0,1`. Action and hook ids are in the same dump and would have
    /// diverged on the first card played, so they are reset here too rather than found later.
    ///
    /// Zeroed rather than adopting the host's values: both sides reach the same answer with no
    /// message and no ordering hazard, and it covers the console duel paths for free. The
    /// counters only need to agree with each other from here on — the duel is a fresh combat
    /// entered with empty queues, so nothing is still holding one of the old ids.
    /// </summary>
    private static void ResetSynchronizerCounters(RunManager run)
    {
        int playerCount = run.State?.Players.Count ?? 0;
        run.PlayerChoiceSynchronizer.FastForwardChoiceIds(Enumerable.Repeat(0u, playerCount).ToList());

        // Sized off the synchronizer's own state rather than the player count: FastForward
        // indexes into _rewardStates, so a longer list would throw.
        int rewardStateCount = run.RewardsSetSynchronizer.GetNextRewardIds().Count();
        run.RewardsSetSynchronizer.FastForwardRewardIds(Enumerable.Repeat(0, rewardStateCount).ToList());

        run.ActionQueueSet.FastForwardNextActionId(0);
        run.ActionQueueSynchronizer.FastForwardHookId(0);

        Log.Warn($"[SpirePvp] duel: reset {playerCount} choice / {rewardStateCount} reward / action / hook " +
                 "counters — the race diverged them and the state sync does not cover them");
    }
}
