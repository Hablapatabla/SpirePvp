using MegaCrit.Sts2.Core.Models;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// A turn model that holds plays before submitting them, and therefore owes the player the same
/// three things.
///
/// **Both of the models that defer need this, for the same reasons and with different timing.** The
/// lock-in model holds a whole batch until both players commit; the tick-paced model holds
/// everything past the first play until a cooldown lets it go. What follows from holding is
/// identical either way:
///
/// - **energy has to be reserved**, because it is spent when a play *executes*, and a held play has
///   not — without it you queue more than you can pay for and watch the surplus fizzle;
/// - **held cards have to be visible**, or a click looks like nothing happened;
/// - **a held card must not be planned twice**, and must not be drawn as unaffordable against its
///   own reservation.
///
/// So the patches that provide those ask for this interface rather than for a concrete model. They
/// were written against `LockInTurnModel` when it was the only thing that deferred, and a second
/// deferring model is exactly the moment that stops being an implementation detail.
/// </summary>
public interface IPlanningTurnModel : IDuelTurnModel
{
    /// <summary>Energy already promised to held plays, which the player may not spend twice.</summary>
    int ReservedEnergy { get; }

    /// <summary>Whether this card is already held, so it is not charged against its own reservation.</summary>
    bool IsPlanned(CardModel card);

    /// <summary>
    /// The readable gap left after each of this model's plays resolves, in seconds.
    ///
    /// **A model's pacing is part of what it is.** A lock-in batch is a sequence you read after the
    /// fact, so it can afford a long beat; a paced real-time duel is a live exchange you are meant
    /// to answer, so its beat has to sit near its own cooldown or the queue simply backs up behind
    /// the animation.
    /// </summary>
    float BeatSeconds { get; }

    /// <summary>
    /// The gap before a play that belongs to the *other* duelist, in seconds.
    ///
    /// **The beat is a pause on the one shared `ActionExecutor`, so by construction it delays
    /// whatever comes next regardless of whose card it is.** Reported 2026-08-13 and it is a real
    /// fault rather than a preference: *"I saw Silent's strike still in the air when I played
    /// Ironclad's defend and it still didn't resolve."* Their answer was waiting out the reading gap
    /// that existed so that *they* could read the strike — charged to the player who had already
    /// read it and replied. The dwell was per-stream where it needed to be per-player.
    ///
    /// It cannot be made concurrent: the engine executes actions strictly one at a time, and that
    /// serial stream is the deterministic sim the checksums are taken over. So the opponent's card
    /// can never overlap yours — the only question is how long after yours it lands, and this is
    /// that number.
    ///
    /// **The two models want opposite answers, which is why this is on the model.** A paced
    /// real-time duel is a live exchange, so an answer should land as soon as the card it answers
    /// has finished moving. A lock-in round is a *replay* — it interleaves both players' cards by
    /// design, host first, alternating — so shortening the cross-player gap there would drain the
    /// round at nearly full speed again, which is the exact unreadability `DuelPace` was built to
    /// fix. `LockInTurnModel` therefore returns its full beat here and changes nothing.
    /// </summary>
    float CrossPlayerBeatSeconds { get; }

    /// <summary>
    /// The host's ruling on who takes the opening initiative, from `DuelStartMessage`.
    /// </summary>
    void SetInitiative(ulong netId);

    /// <summary>
    /// Who strikes first this turn: whoever reached the arena first, alternating each turn (M9).
    ///
    /// **Both models need it, for different jobs.** The lock-in model orders its interleaved batch
    /// by it; the paced model breaks ties inside a tick with it, which is what stops the host's
    /// shorter path to its own queue from deciding trades. Same rule, so the race's reward means the
    /// same thing in both modes.
    /// </summary>
    ulong CurrentLeader { get; }

    /// <summary>
    /// Whether the hand is closed to new plays entirely, rather than merely short of energy.
    ///
    /// True for the lock-in model while a batch is committed or resolving. **Always false for a
    /// paced model**, where the whole point is that you may keep queueing — the cooldown decides
    /// when a play *leaves*, never whether you may make one.
    /// </summary>
    bool HandIsClosed { get; }
}
