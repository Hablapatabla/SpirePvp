using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel.Turns;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Owns the chess clocks for a match (DESIGN §3.2).
///
/// Run-scoped, not duel-scoped: the clock starts at run start and survives room transitions.
/// M5's race phase needs no retrofit — it is already running by then.
///
/// **Two phases, two banks, two meanings** — the phase rules settled 2026-08-05 by playing
/// both; the split into two banks decided the same day (DESIGN §9):
///   race — a global countdown on the race bank. Reach the arena before it empties. Both
///          clocks run continuously and never pause, so they stay identical. Stopping one
///          while the other ran would measure nothing: the players are in separate combats
///          and never wait on each other.
///   duel — a real chess clock on a *fresh* duel bank. Here you *do* wait on each other, so
///          ending your turn stops your clock while your opponent's keeps running.
///
/// The duel bank is granted new at duel start rather than inherited from the race's
/// remainder, so arriving at the arena early buys you nothing in the fight and the two phases
/// are timed independently — a match is configured as, say, a 10-minute race followed by a
/// 2-minute duel. One shared number could only rush the race or drag out the duel, because an
/// act and a duel are not the same length of thing.
///
/// Host-authoritative in both phases: nothing pauses during the race, and in the duel the
/// host sees both players' end-turn state directly. Sync carries each clock's paused flag so
/// a client's prediction stops when the owner's does, rather than counting a stopped clock
/// down and snapping it back twice a second.
///
/// Either bank may be 0, which makes that phase untimed — nobody can lose on time there, and
/// the top bar shows nothing rather than a frozen zero. Both 0 is the default, so the mod
/// stays inert for anyone who has not opted in.
///
/// Time is measured from wall-clock deltas rather than accumulated ticks, so display
/// granularity and flag accuracy do not depend on how often the UI refreshes, and slow
/// frames cannot make the clock drift.
/// </summary>
public static class DuelClockService
{
    /// <summary>Bank per player for reaching the arena. Zero means the race is untimed.</summary>
    public static double RaceBankMs { get; private set; }

    /// <summary>Bank per player for the duel, granted fresh at duel start. Zero means untimed.</summary>
    public static double DuelBankMs { get; private set; }

    /// <summary>True when either phase is timed, i.e. there is a clock worth running at all.</summary>
    public static bool Enabled => RaceBankMs > 0 || DuelBankMs > 0;

    /// <summary>
    /// The bank the *current* phase runs on. Zero means this phase is untimed, and everything
    /// downstream — ticking, flagging, the top bar, the host's sync broadcast — sits out.
    ///
    /// The deck review counts as duel: `DuelRendezvous` flips the phase before opening it,
    /// precisely so that studying the opponent's deck is charged to the duel bank.
    /// </summary>
    public static double CurrentBankMs => _duelBankGranted ? DuelBankMs : RaceBankMs;

    /// <summary>
    /// Which bank we are on is `_duelBankGranted`, **not** the phase — they are not the same
    /// question once a match can end during the race.
    ///
    /// A race clock running out moves the phase straight to `Complete` without ever passing
    /// through `DuelActive`, so a phase test answers "duel" for a match whose duel never
    /// happened. That is what put `YOU 0:00 · OPP 0:00` on the result screen after a race
    /// timeout: two duel clocks reported for a duel nobody played. Keyed on the grant, the same
    /// case reads as the single race countdown that actually expired.
    /// </summary>
    private static bool DuelPhaseStarted => DuelSession.Phase == DuelPhase.DuelActive;

    private const double SyncIntervalMs = 500;

    private static DuelClock? _local;
    private static DuelClock? _opponent;
    private static DateTime _lastTick;
    private static DateTime _lastSync;
    private static bool _running;
    private static bool _duelBankGranted;

    /// <summary>
    /// True once the duel bank has replaced the race bank. **This, not the phase, is what
    /// distinguishes a race-clock expiry from a duel-clock flag** — the two mean opposite
    /// things (a draw versus a loss) and a match can reach `Complete` without ever passing
    /// through `DuelActive`. Keying the top bar on the phase instead is exactly what put
    /// `YOU 0:00 · OPP 0:00` on a result screen for a duel nobody played.
    /// </summary>
    public static bool DuelBankGranted => _duelBankGranted;

    public static DuelClock? Local => _local;

    public static DuelClock? Opponent => _opponent;

    /// <summary>
    /// Set both banks, in minutes. 0 in either turns that phase's clock off. Resets any
    /// running clocks.
    /// </summary>
    public static void Configure(double raceMinutes, double duelMinutes)
    {
        RaceBankMs = Math.Max(0, raceMinutes) * 60_000;
        DuelBankMs = Math.Max(0, duelMinutes) * 60_000;
        _local = null;
        _opponent = null;
        _running = false;
        _duelBankGranted = false;
    }

    /// <summary>
    /// Begin the match clocks on the race bank. Safe to call more than once; only the first
    /// call after a Configure creates the banks.
    ///
    /// The clocks are created even when the race is untimed, because the duel may not be: they
    /// sit at zero and untouched until <see cref="GrantDuelBankIfEntered"/> fills them.
    /// </summary>
    public static void Start(ulong localId, ulong opponentId)
    {
        if (!Enabled || _local != null)
        {
            return;
        }

        _local = new DuelClock(localId, RaceBankMs);
        _opponent = new DuelClock(opponentId, RaceBankMs);
        _lastTick = DateTime.UtcNow;
        _running = true;

        if (RaceBankMs > 0)
        {
            _local.Start();
            _opponent.Start();
        }

        Log.Warn($"[SpirePvp] clocks started: race {RaceBankMs / 60000:0.##} min, " +
                 $"duel {DuelBankMs / 60000:0.##} min each");
    }

    /// <summary>
    /// Swap the race bank for a fresh duel bank the first time the duel phase is seen.
    ///
    /// Fresh, not a remainder (DESIGN §9): arriving at the arena early does not buy duel time,
    /// and the race clock stands on its own as a deadline. Both clients grant it off the same
    /// phase flip — which the host drives, and which both sides reach from the same rendezvous —
    /// so this needs no message of its own; the host's ClockSyncMessage corrects the sliver of
    /// skew between them like it corrects any other.
    /// </summary>
    private static void GrantDuelBankIfEntered()
    {
        // DuelActive only. `Complete` must not trigger it: a race timeout lands there without a
        // duel, and granting a fresh duel bank onto the result screen would replace the expired
        // race clock the player is looking at.
        if (_duelBankGranted || !DuelPhaseStarted || _local == null || _opponent == null)
        {
            return;
        }

        _duelBankGranted = true;
        _local.Refill(DuelBankMs);
        _opponent.Refill(DuelBankMs);

        Log.Warn($"[SpirePvp] duel begins: fresh bank of {DuelBankMs / 60000:0.##} min each" +
                 (DuelBankMs > 0 ? "" : " (untimed)"));
    }

    public static void Stop()
    {
        _running = false;
        _local?.Pause();
        _opponent?.Pause();
    }

    /// <summary>
    /// Back to inert. Clears the configured bank as well as the clocks, so a run that never
    /// configures one — an ordinary co-op run started after a duel — cannot inherit the last
    /// match's. `Enabled` staying true was enough on its own to keep the top bar showing a
    /// clock and, on the host, to keep broadcasting ClockSyncMessage into a run that had no
    /// idea what it was.
    /// </summary>
    public static void Reset()
    {
        RaceBankMs = 0;
        DuelBankMs = 0;
        _local = null;
        _opponent = null;
        _running = false;
        _duelBankGranted = false;
    }

    /// <summary>
    /// Advance both clocks by real elapsed time and apply phase rules. Driven from the
    /// top-bar timer refresh, but independent of its cadence.
    /// </summary>
    public static void Tick()
    {
        if (!Enabled || !_running || _local == null || _opponent == null)
        {
            return;
        }

        // The run can end without a duel result — abandoning it, the host quitting, a
        // disconnect — and none of those routes goes through DuelResult.DeclareWinner, which is
        // the only thing that used to stop the clocks. Measured 2026-08-06: abandoning a race
        // left the host broadcasting ClockSyncMessage twice a second for 21 seconds into a
        // disconnected service, 46 error lines, and the client logging "no message handlers are
        // registered" for each one. Stop on any run that is no longer in progress.
        if (RunManager.Instance?.IsInProgress != true)
        {
            Stop();
            return;
        }

        // Before the untimed bail-out below, so a match with an untimed race and a timed duel
        // still gets its duel bank.
        GrantDuelBankIfEntered();

        DateTime now = DateTime.UtcNow;
        double deltaMs = (now - _lastTick).TotalMilliseconds;

        // Advanced even when this phase is untimed. Otherwise the whole untimed stretch would
        // still be sitting in the delta and get charged to the next phase in a single tick.
        _lastTick = now;

        if (deltaMs <= 0 || CurrentBankMs <= 0)
        {
            return;
        }

        ApplyPhaseRules();

        _local.Tick(deltaMs);
        _opponent.Tick(deltaMs);

        BroadcastSyncIfHost(now);
    }

    /// <summary>
    /// Host → all, roughly twice a second. Clients keep ticking locally between syncs so the
    /// display stays smooth, and snap to these values when they arrive.
    ///
    /// This corrects a start-time offset as much as any drift: each client starts its clock
    /// when its own duel entry completes, and those moments differ by the round-trip plus
    /// room-entry timing. Without correction that offset persists for the whole match —
    /// about a second over a minute in practice.
    /// </summary>
    private static void BroadcastSyncIfHost(DateTime now)
    {
        INetGameService? net = RunManager.Instance?.NetService;
        if (net?.Type != NetGameType.Host)
        {
            return;
        }

        // A second belt to the Tick guard above, for the window where the run still reports
        // in-progress but the transport has already gone. Sending then is a logged error per
        // message, twice a second, and the peer is not there to receive it anyway.
        if (!net.IsConnected)
        {
            return;
        }

        if ((now - _lastSync).TotalMilliseconds < SyncIntervalMs)
        {
            return;
        }

        _lastSync = now;

        net.SendMessage(new ClockSyncMessage
        {
            playerA = _local!.PlayerId,
            playerARemainingMs = (int)_local.RemainingMs,
            playerAPaused = !_local.IsRunning,
            playerB = _opponent!.PlayerId,
            playerBRemainingMs = (int)_opponent.RemainingMs,
            playerBPaused = !_opponent.IsRunning
        });
    }

    /// <summary>
    /// Apply an incoming clock report.
    ///
    /// The host's authoritative pair; clients snap both clocks to it. Host-owned in both
    /// phases — nothing pauses during the race, and in the duel the host sees both players'
    /// end-turn state directly.
    /// </summary>
    public static void ApplySync(ClockSyncMessage message)
    {
        if (RunManager.Instance?.NetService?.Type == NetGameType.Host)
        {
            return;
        }

        Correct(message.playerA, message.playerARemainingMs, message.playerAPaused);
        Correct(message.playerB, message.playerBRemainingMs, message.playerBPaused);
    }

    /// <summary>
    /// Mirror the owner's running state so local prediction moves the same way theirs does.
    /// Without this a paused clock keeps counting down here and jumps back on the next sync.
    /// </summary>
    private static void ApplyReportedPause(DuelClock clock, bool paused)
    {
        if (paused)
        {
            clock.Pause();
        }
        else
        {
            clock.Start();
        }
    }

    private static void Correct(ulong playerId, int remainingMs, bool paused)
    {
        if (_local != null && _local.PlayerId == playerId)
        {
            _local.CorrectTo(remainingMs);
            ApplyReportedPause(_local, paused);
        }
        else if (_opponent != null && _opponent.PlayerId == playerId)
        {
            _opponent.CorrectTo(remainingMs);
            ApplyReportedPause(_opponent, paused);
        }
    }

    /// <summary>
    /// During a duel a player's clock stops once they have declared end turn. Outside a duel
    /// (the race) both clocks simply run.
    /// </summary>
    private static void ApplyPhaseRules()
    {
        if (_local == null || _opponent == null)
        {
            return;
        }

        // RACE — a global countdown, not a chess clock. The players are in separate combats
        // and never wait on each other, so stopping one clock while the other runs would
        // measure nothing. Both simply count down: reach the arena before the bank empties.
        // Starting together and never pausing is what keeps the two values identical, which
        // is what "global timer" means here. Spending it is not a duel cost — the duel gets a
        // bank of its own — but running it out is still a loss.
        // DRAFT — alternating picks, so it is a real chess clock and the race rule below is wrong
        // for it. Reported 2026-08-20: *"both clocks were just continuously counting down as
        // opposed to chess-clock like behavior in which your clock stops counting while the
        // opponent is thinking."*
        //
        // **A draft is not a small race, it is the opposite of one.** The race branch below runs
        // both banks together because the two players are in separate combats and never wait on
        // each other — nobody is idle, so charging both is measuring something real. In a draft the
        // picks alternate: exactly one player is deciding and the other can do nothing but watch.
        // Charging the watcher bills them for the opponent's thinking, which is the precise thing a
        // chess clock exists to prevent, and over a 15-card draft it is most of the bank.
        //
        // The same shape as the deck-review branch below, and for the same reason: whoever is not
        // on the move is not spending. Between rounds — cards done, relics not yet dealt — nobody
        // may pick and both stop, which is right: that gap is the mod dealing, not either player
        // deliberating.
        if (DuelDraft.IsDraftRun && !DuelSession.IsDuelActive)
        {
            bool localOnTheMove = DuelDraft.LocalMayPick;
            bool opponentOnTheMove = DuelDraft.IsDrafting && !localOnTheMove;

            ApplyReadyState(_local, !localOnTheMove);
            ApplyReadyState(_opponent, !opponentOnTheMove);
            return;
        }

        if (!DuelSession.IsDuelActive)
        {
            _local.Start();
            _opponent.Start();
            return;
        }

        // DECK REVIEW — the duel phase has begun but the arena has not loaded. Confirming the
        // review is the same commitment as ending a turn — you are done deciding and are waiting
        // on them — so it stops your clock while theirs keeps running. Without this, confirming
        // early bought you nothing and the review was a pure loss of time to whoever read faster.
        //
        // Checked *before* the combat state, not as its null case. An earlier version keyed this
        // on `state == null` and never fired: finishing a combat does not exit its room —
        // `ProceedFromTerminalRewardsScreen` just opens the map screen over it — so the boss
        // combat's turn state is still hanging around while the review is on screen. That sent
        // the review down the branch below, which read end-turn readiness off a combat that had
        // already ended, found nobody ready, and dutifully ran both clocks forever.
        if (DuelEntry.IsChoosing)
        {
            ApplyReadyState(_local, DuelEntry.LocalReady);

            // The host owns both clocks, so it has to stop theirs too when they confirm —
            // otherwise the player who reads faster is still charged for the wait.
            ApplyReadyState(_opponent, DuelEntry.OpponentReady);
            return;
        }

        // DUEL — now a real chess clock, because now you *do* wait on each other. Ending your
        // turn stops your clock while your opponent's keeps running.
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance.IsInProgress)
        {
            // Between confirming the review and the arena finishing its load. Nothing live to
            // read readiness from, so leave both clocks exactly as the review left them rather
            // than restarting one for the length of a room transition.
            return;
        }

        // **A card selection is thinking time, and a chess clock charges thinking time.** Lucas,
        // 2026-08-13: "the time spent picking your choice should count towards your timer and your
        // opponent's should freeze." Without this the pair of rules disagreed — `DuelChoiceStallPatch`
        // now holds the whole batch while someone is choosing, and `IsDoneDeciding` counts
        // *resolution* as committed for both, so a choice would have frozen both clocks and made
        // deliberation free for the one player still making a decision.
        //
        // Read from the same state the stall reads, deliberately: two answers to "who is deciding"
        // is the shape of half the bugs in this file.
        ulong chooser = Patches.DuelChoiceStallPatch.PlayerGatheringChoice();
        if (chooser != 0)
        {
            foreach (Player player in state.Players)
            {
                DuelClock clock = LocalContext.IsMe(player) ? _local : _opponent;
                ApplyReadyState(clock, player.NetId != chooser);
            }

            return;
        }

        foreach (Player player in state.Players)
        {
            DuelClock clock = LocalContext.IsMe(player) ? _local : _opponent;
            ApplyReadyState(clock, IsDoneDeciding(player));
        }
    }

    /// <summary>
    /// Whether this player is finished deciding, which is what a chess clock actually measures.
    ///
    /// **In blitz that is end-turn readiness; in the lock-in model it is not, and asking the wrong
    /// one meant the turn-based clock never paused at all.** `IsPlayerReadyToEndTurn` only becomes
    /// true when `EndPlayerTurnAction` *executes*, which under that model is at the flush — a
    /// moment before the turn rolls. So you locked in, sat watching your opponent think, and were
    /// charged for every second of it, which is precisely the thing a chess clock exists to stop.
    /// It went unnoticed because the turn-based sessions so far were played on `Duel Clock: Off`.
    ///
    /// **Third instance of the same trap** (HANDOFF: the phase test in this file, then the
    /// duplicate of it in `DuelFlag`): a condition that correlates with the one you mean, until a
    /// new mode separates them. The condition meant is "has this player committed their round".
    ///
    /// Resolution counts as committed for *both*, because nobody is deciding while the round
    /// plays out — and with `DuelPace` giving each card a readable beat, that stretch is now
    /// seconds long rather than instant. Charging it to the players would make a slower, more
    /// legible duel a more expensive one.
    /// </summary>
    private static bool IsDoneDeciding(Player player)
    {
        if (DuelTurnModel.Current is not LockInTurnModel lockIn)
        {
            return CombatManager.Instance.IsPlayerReadyToEndTurn(player);
        }

        return lockIn.ResolvingBatch
               || (LocalContext.IsMe(player) ? lockIn.LockedIn : lockIn.OpponentLockedIn);
    }

    /// <summary>Declared done ⇒ clock stops; still deciding ⇒ clock runs.</summary>
    private static void ApplyReadyState(DuelClock clock, bool isReady)
    {
        if (isReady)
        {
            clock.Pause();
        }
        else
        {
            clock.Start();
        }
    }

    /// <summary>
    /// Label text for the top bar, and it changes with the phase because the phases mean
    /// different things.
    ///
    /// Race — one number: "7:42". Both clocks are identical here by construction (they start
    /// together and neither ever pauses), so "YOU 7:42 · OPP 7:42" would be two ways of saying
    /// the same thing and an invitation to compare numbers that cannot differ. It is a shared
    /// deadline to reach the arena, so it reads as one.
    ///
    /// Duel — both: "YOU 2:31 · OPP 1:47". Now they genuinely diverge, and the gap between
    /// them is the whole tension.
    /// </summary>
    public static string? FormatForHud()
    {
        // CurrentBankMs, not Enabled: with the two banks split, half a match can be untimed —
        // an untimed race followed by a 2-minute duel is a normal configuration — and showing
        // a frozen 0:00 through the phase nobody agreed to time would read as a broken clock.
        if (CurrentBankMs <= 0 || _local == null || _opponent == null)
        {
            return null;
        }

        // Once the duel bank has been granted the two-clock readout stays up, result screen
        // included: the duel is over but its final times are the last thing worth showing, and
        // dropping back to a single number there would silently relabel the winner's clock as a
        // shared countdown. A match that ended before the duel began never granted it, and
        // correctly shows the one race clock that ran out.
        if (!_duelBankGranted)
        {
            return Format(_local.RemainingMs);
        }

        return $"YOU {Format(_local.RemainingMs)} · OPP {Format(_opponent.RemainingMs)}";
    }

    private static string Format(double ms)
    {
        // "0:00.0" here was a leftover from the tenths format the comment below describes
        // removing, and zero is not a rare value to render: an expired race clock is exactly
        // zero and sits on the result screen for as long as the player looks at it, which is
        // the one place the label is read rather than glanced at.
        if (ms <= 0)
        {
            return "0:00";
        }

        // Always m:ss. An earlier version switched to tenths under ten seconds, which made
        // the label change shape exactly when you are most likely to be staring at it.
        TimeSpan span = TimeSpan.FromMilliseconds(ms);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
