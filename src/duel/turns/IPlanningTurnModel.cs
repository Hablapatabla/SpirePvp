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
    /// Whether the hand is closed to new plays entirely, rather than merely short of energy.
    ///
    /// True for the lock-in model while a batch is committed or resolving. **Always false for a
    /// paced model**, where the whole point is that you may keep queueing — the cooldown decides
    /// when a play *leaves*, never whether you may make one.
    /// </summary>
    bool HandIsClosed { get; }
}
