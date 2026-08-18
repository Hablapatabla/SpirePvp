using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
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

        // **Beginning a race twice would poison the restore, silently.** The snapshot below is
        // what EndRace hands back to the duel, and a second pass would capture the values this
        // method had just written — so the "vanilla" state remembered for the duel becomes
        // "sync disabled, checksums off", and the duel then runs uncoupled with divergence
        // detection dead. Nothing would report that; it would surface later as a desync nobody
        // could account for.
        //
        // Cheap to hit: `race on` is a debug command and typing it twice is the obvious mistake,
        // which is exactly how the arena's double-entry was found.
        if (_raceActive)
        {
            Log.Warn("[SpirePvp] race mode already on — ignoring a second start");
            return;
        }

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
    /// <summary>
    /// Forgets that a race was running, without touching the run — which by this point may be
    /// half torn down.
    ///
    /// **Required by the guard in <see cref="BeginRace"/>, and the reason is the trap this
    /// project keeps walking into.** `EndRace` is called from exactly two places: the arena, and
    /// `race off`. A run that ends *before* the arena — abandoned, resigned mid-race, race clock
    /// expired — reaches neither, so `_raceActive` would stay true for the life of the process
    /// and the *next* match's `BeginRace` would decline to start a race at all. Silently: the run
    /// would look normal and simply behave as co-op.
    ///
    /// So this is the release half of a static flag whose run is not static, called from
    /// `DuelMatch.OnRunEnded`. Deliberately **not** `EndRace`: that restores synchronizer state
    /// through `RunManager.Instance`, which is exactly what teardown is in the middle of
    /// disposing.
    /// </summary>
    public static void Reset()
    {
        _raceActive = false;
    }

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
    /// The draft's equivalent of the reset <see cref="EndRace"/> does for a race.
    ///
    /// **A draft never ran a race, so `EndRace` early-returns and its
    /// <see cref="ResetSynchronizerCounters"/> is skipped — and that reset is the one part of
    /// `EndRace` a draft still needs.** The rest of `EndRace` restores combat sync, checksums and
    /// hooks that only a race turned off, so it is correctly gated on `_raceActive`; the counter
    /// reset is not, because both formats couple back together for the duel.
    ///
    /// **Measured 2026-08-18.** Each player obtains their own drafted relics locally and decoupled.
    /// BIIIG_HUG's on-obtain gathers a player choice, so the host that drafted it reserved choice
    /// id 0 (`next is 1`) while the client that did not stayed at 0. Nothing reconciles that — the
    /// pre-combat state sync covers serialized state and RNG, not the synchronizer counters (see
    /// <see cref="ResetSynchronizerCounters"/>) — so the duel's first checksum compared 1 against 0
    /// and the host kicked the client for StateDivergence. Both full state dumps were identical in
    /// every other line; the only difference was `Choice IDs: 1` vs empty.
    ///
    /// Called from `DuelArena` at arena entry, after the rooms are exited, so it lands on quiesced
    /// state at the same point the race path resets from. The action / reward / hook counters a
    /// draft never touched are still 0, so their fast-forward is a no-op.
    /// </summary>
    public static void ResetDraftSynchronizerCounters(RunManager run)
    {
        ResetSynchronizerCounters(run);
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
    /// message and no ordering hazard, and it covers the console duel paths for free.
    ///
    /// **Resetting a counter is not enough on its own, and the missing half cost a match.** An
    /// earlier version of this comment finished "the duel is a fresh combat entered with empty
    /// queues, so nothing is still holding one of the old ids". The queues are empty; the
    /// *synchronizer* is not. `PlayerChoiceSynchronizer` keeps a `_receivedChoices` list of
    /// choices that arrived from a peer with nobody waiting for them, matched later by
    /// `(choiceId, senderId)` alone — and `FastForwardChoiceIds` only touches the counter, so
    /// those entries survive the reset with ids the duel is about to hand out again.
    ///
    /// The race fills that list by construction: a card reward pick travels as a player choice,
    /// so every reward either player takes is broadcast, stored by the peer, and never consumed —
    /// the two runs are decoupled and nobody is waiting. See <see cref="ClearStaleReceivedChoices"/>.
    /// </summary>
    private static void ResetSynchronizerCounters(RunManager run)
    {
        int playerCount = run.State?.Players.Count ?? 0;
        run.PlayerChoiceSynchronizer.FastForwardChoiceIds(Enumerable.Repeat(0u, playerCount).ToList());
        ClearStaleReceivedChoices(run);

        // Sized off the synchronizer's own state rather than the player count: FastForward
        // indexes into _rewardStates, so a longer list would throw.
        int rewardStateCount = run.RewardsSetSynchronizer.GetNextRewardIds().Count();
        run.RewardsSetSynchronizer.FastForwardRewardIds(Enumerable.Repeat(0, rewardStateCount).ToList());

        run.ActionQueueSet.FastForwardNextActionId(0);
        run.ActionQueueSynchronizer.FastForwardHookId(0);

        Log.Warn($"[SpirePvp] duel: reset {playerCount} choice / {rewardStateCount} reward / action / hook " +
                 "counters — the race diverged them and the state sync does not cover them");
    }

    /// <summary>
    /// Drops the peer choices the race left sitting in `PlayerChoiceSynchronizer`, which the
    /// counter reset above would otherwise hand to the duel under reused ids.
    ///
    /// **Measured 2026-08-12, and it ended a match in a way that read as a network fault.** The
    /// client took two card rewards during the race, which the host stored as choices 0 and 1
    /// (`indexes 1`, `indexes 2` — nobody was waiting for them, so `OnReceivePlayerChoice` parked
    /// them). The counters were then zeroed here. In the duel the client played Photon Cut, whose
    /// `OnPlay` ends in `CardSelectCmd.FromHand`; the host reserved choice id 0 for the client,
    /// found the *race's* choice 0 already in the list — `Was going to wait for remote choice 0
    /// but we've already received it` — and handed a reward index to a card asking for a card:
    /// `InvalidOperationException: Tried to get combat cards from player choice result of type
    /// Index!`. The host's put-back never happened, the client's did, and the very next checksum
    /// diverged. The host kicked the client for `StateDivergence`, and *both* players were then
    /// shown a victory by disconnect (fixed separately in <see cref="Duel.DuelDisconnect"/>).
    ///
    /// Note what made it look random: it needs a duel card that gathers a player choice, and only
    /// the *peer's* choices are stored this way — the host consumes its own locally. So it fires
    /// on the first choosing card the client plays, and never on the host's.
    ///
    /// Everything in the list is stale by construction at this point. A race-era choice has no
    /// consumer in the duel: the phase it belonged to is over, and the duel's own waits are all
    /// reserved after this call. The completed/pending split is logged rather than acted on
    /// because pending entries should not exist here at all — `RaceIgnoreRemoteActionsPatch`
    /// discards the opponent's actions, so nothing in a race ever awaits a remote choice — and a
    /// nonzero count means that premise has changed and wants reading, not silent handling.
    /// </summary>
    private static void ClearStaleReceivedChoices(RunManager run)
    {
        List<PlayerChoiceSynchronizer.ReceivedChoice> stale = run.PlayerChoiceSynchronizer._receivedChoices;
        if (stale.Count == 0)
        {
            return;
        }

        int dropped = stale.Count;
        int pending = stale.Count(c => !c.completionSource.Task.IsCompleted);
        string detail = string.Join(", ", stale.Select(c => $"{c.senderId}#{c.choiceId}"));
        stale.Clear();

        Log.Warn($"[SpirePvp] duel: dropped {dropped} stale peer choice(s) held by the race " +
                 $"({detail}) — {pending} still pending. They are keyed by id alone, and the duel " +
                 "reuses those ids from 0.");
    }
}
