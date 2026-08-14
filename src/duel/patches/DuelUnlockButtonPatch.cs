using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Lets the end turn button take a lock-in back, while the opponent has not committed.
///
/// **Open since 2026-08-12 and closed by a decision rather than by code** (Lucas, 2026-08-14):
/// backing out is allowed for exactly as long as the opponent has not locked in. That is safe as a
/// competitive rule because you learn nothing by unlocking — neither player can see the other's plan
/// — and once they are in, the round is already resolving.
///
/// # Why the button could not do this on its own
///
/// `NEndTurnButton.CallReleaseLogic` decides between ending and undoing with
/// `CombatManager.IsPlayerReadyToEndTurn(me)`, which under this model is **false until the flush** —
/// the `EndPlayerTurnAction` does not execute until the round resolves. So pressing again would
/// enqueue a *second* `EndPlayerTurnAction` rather than an undo. And it does not get that far
/// anyway: the same method calls `SetState(State.Disabled)` on the click, so the button is dead from
/// the moment you lock in.
///
/// Hence a prefix rather than a nudge to the state machine: while a lock-in can be taken back, this
/// takes the press entirely and calls <see cref="LockInTurnModel.Unlock"/>, which recalls the plays
/// and returns the cards to the hand. Vanilla's branch never runs, so no action of either kind is
/// enqueued — which matters, because an `UndoEndPlayerTurnAction` here would be undoing an end turn
/// the engine does not believe has happened.
///
/// **Re-enabling is the other half.** The button is left `Disabled` by the press that locked in, so
/// `DuelUnlockButtonStatePatch` below puts it back while the window is open — otherwise the rule
/// exists and there is no way to reach it.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton.CallReleaseLogic))]
public static class DuelUnlockButtonPatch
{
    public static bool Prefix()
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not LockInTurnModel model)
        {
            return true;
        }

        if (!model.CanUnlock)
        {
            return true;
        }

        Log.Warn("[SpirePvp] lock-in: end turn pressed while locked in — taking it back");
        model.Unlock();
        return false;
    }
}

/// <summary>
/// Keeps the end turn button alive while a lock-in can still be withdrawn.
///
/// `CallReleaseLogic` disables the button on the press that locks you in, and nothing re-enables it
/// until the round resolves — which is correct for a commitment and wrong for one that can be taken
/// back. This re-enables it for exactly the window <see cref="LockInTurnModel.CanUnlock"/> describes,
/// so the button reads as live precisely when pressing it would do something.
///
/// A postfix on the button's own refresh rather than a poke from the model: the state machine is
/// vanilla's, several things drive it, and setting the state from outside its refresh is how you get
/// a button that flickers between enabled and disabled — the failure this session has already spent
/// two rounds on with card visuals.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton.RefreshEnabled))]
public static class DuelUnlockButtonStatePatch
{
    public static void Postfix(NEndTurnButton __instance)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not LockInTurnModel model)
        {
            return;
        }

        if (model.CanUnlock)
        {
            __instance.SetState(NEndTurnButton.State.Enabled);
        }
    }
}
