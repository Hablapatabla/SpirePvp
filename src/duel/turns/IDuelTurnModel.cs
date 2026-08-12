using MegaCrit.Sts2.Core.GameActions;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// When a duel's actions are allowed to execute. The one thing that differs between the two turn
/// models (DESIGN §3.1b).
///
/// **The difference is only ever *when*, never *what*.** Retargeting, the win condition, powers and
/// damage-over-time are identical under both models — they operate on `Creature` and have no idea
/// a turn model exists. So model B is not a rewrite of the duel; it is a gate in front of the same
/// action stream, and this interface is that gate.
///
/// The choice rides on `DuelStartMessage` alongside the clocks, so the host's configuration is
/// authoritative for the whole duel — same reasoning as every other duel parameter. Keeping it
/// behind one policy object rather than scattering `if (blitz)` through the patches is DESIGN's
/// instruction and the reason M8 is not expected to touch the combat patches at all.
/// </summary>
public interface IDuelTurnModel
{
    /// <summary>What to call this in a log line.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this action must be held back rather than submitted now.
    ///
    /// Asked from a prefix on `ActionQueueSynchronizer.RequestEnqueue`, which is the one place
    /// every play passes through on its way to the shared queue. **Vanilla already defers actions
    /// there**, in that same method: an action marked `CombatPlayPhaseOnly` requested during the
    /// enemy turn is parked in `_requestedActionsWaitingForPlayerTurn` and flushed at player-turn
    /// start. So the mechanism this needs is proven engine behaviour rather than something the mod
    /// invents; a lock-in model changes only the *release condition*.
    /// </summary>
    bool ShouldDefer(GameAction action);
}
