using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// What a planned round looks like. Everything here is presentation — nothing it does can change
/// what resolves, and every call site is a place <see cref="LockInTurnModel"/> has already decided.
///
/// **Both surfaces are vanilla's own, and that is the whole design.** A buffered play and a
/// co-op play awaiting the host's ordering are the same thing seen from two models: submitted,
/// not yet resolved. The engine draws that already — `NCardPlayQueue` lifts the card out of the
/// hand and files it in a strip beside the play area, and `NEndTurnButton` puts a player's icon
/// above the button once they are ready. The lock-in model reaches neither by accident: it never
/// calls `RequestEnqueue` for a held play, so `ActionQueueSet.ActionEnqueued` never fires and the
/// queue is never told; and `EndPlayerTurnAction` does not execute until the flush, so
/// `IsPlayerReadyToEndTurn` stays false for the entire planning phase. Both are one call away.
///
/// The alternative — tinting cards where they sit in the hand — was not built. It would have been
/// our own visual language for a state the game already has one for, it shows no ordering, and it
/// leaves the card clickable, so the same card can be planned twice. Moving the node out of the
/// hand answers all three, because a card that is not in the hand cannot be dragged out of it.
/// </summary>
internal static class LockInPlanView
{
    /// <summary>
    /// Files a held play in the play queue, exactly as vanilla files a co-op play it has sent to
    /// the host.
    ///
    /// **The card is resolved from `NetCombatCard`**, for the same reason the reservation is:
    /// `PlayCardAction._card` is assigned in `ExecuteAction` and a held play has not executed.
    ///
    /// Potions are held too (`UsePotionAction` is `CombatPlayPhaseOnly` in combat) and have no
    /// presentation here — the queue is a card strip. A planned potion still looks like nothing
    /// happened; it is a smaller version of the same gap and wants its own answer.
    /// </summary>
    public static void ShowPlanned(GameAction action)
    {
        if (action is not PlayCardAction play)
        {
            return;
        }

        CardModel? card = play.NetCombatCard.ToCardModelOrNull();
        NCardPlayQueue? queue = NCardPlayQueue.Instance;
        if (card == null || queue == null)
        {
            return;
        }

        // A null holder is not a failure: vanilla's own path passes whatever GetCardHolder finds
        // and builds a node when there is none.
        queue.OnLocalCardPlayed(play, NPlayerHand.Instance?.GetCardHolder(card), card);
    }

    /// <summary>
    /// Repaints the icons above the end turn button.
    ///
    /// Vanilla refreshes them when a player's end turn *executes* and again at turn start
    /// (`RefreshPlayerVotes(animate: false)`), so the icons clear themselves each round and this
    /// only ever has to add one. `DuelLockInIconPatch` decides what they show.
    /// </summary>
    public static void RefreshLockInIcons() =>
        NCombatRoom.Instance?.Ui.EndTurnButton?._playerIconContainer?.RefreshPlayerVotes();

    /// <summary>
    /// Tells the button it is about to commit a batch rather than end the turn.
    ///
    /// The label is the only place the two-press rule is written down, which is deliberate: a
    /// button that reads *Lock In* while you hold cards and *End Turn* while you hold none teaches
    /// the rule at the moment it applies, and there is nowhere else in this UI to explain it.
    /// </summary>
    public static void ShowLockInLabel() =>
        SetLabel(new LocString("gameplay_ui", "SPIREPVP_LOCK_IN_BUTTON").GetFormattedText());

    /// <summary>
    /// Hands the turn back to the player after a batch has finished resolving.
    ///
    /// **`OnTurnStarted` is the reset, borrowed rather than reproduced.** A new planning window and
    /// a new turn want exactly the same thing from this button — the label back to vanilla's, the
    /// vote icons repainted, the button enabled if the player can still act — and vanilla already
    /// has that as one call, including the guard that does nothing outside the player's turn. That
    /// guard matters here: the watcher that drives this also fires as a turn rolls over, and this
    /// must not light the button up during the enemy turn.
    ///
    /// A player who has declared themselves finished gets the icons repainted and nothing else:
    /// the button stays dark until the turn rolls, which is what being finished means.
    /// </summary>
    public static void ReopenPlanning(bool acceptsMorePlays)
    {
        NEndTurnButton? button = NCombatRoom.Instance?.Ui.EndTurnButton;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (button == null || state == null)
        {
            return;
        }

        if (acceptsMorePlays)
        {
            button.OnTurnStarted(state);
        }
        else
        {
            RefreshLockInIcons();
        }
    }

    private static void SetLabel(string text) =>
        NCombatRoom.Instance?.Ui.EndTurnButton?._label?.SetTextAutoSize(text);
}
