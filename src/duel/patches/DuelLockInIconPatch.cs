using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Who has locked in, shown where vanilla already shows who has ended their turn.
///
/// `NEndTurnButton` puts a player's icon above the button while
/// `CombatManager.IsPlayerReadyToEndTurn` is true, which is co-op's answer to "who are we waiting
/// for". Under the lock-in model that flag does not turn on until `EndPlayerTurnAction` executes —
/// at the flush, a whole round after the click — so the one stretch of the round where the question
/// is live is the one stretch the button has nothing to say. This answers it from the model
/// instead, for both seats: yours the moment you lock in, theirs the moment the model learns they
/// have (their end turn arriving on the host, their message on a client).
///
/// **Borrowed rather than built, for the same reason `DuelRematchPatch` borrows the vote marker:**
/// with no chat and two people staring at a planning phase, the only thing either wants to know is
/// whether the other is done, and the game already has a shape that means exactly that. Note it is
/// the second place this mod shows an opponent's intent on a screen where they are otherwise
/// invisible, and both were wanted — see the result screen's cursor note in HANDOFF.
///
/// Only ever adds an icon. Vanilla clears them at turn start, so a round's icons cannot outlive it.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton.ShouldDisplayPlayerIcon))]
public static class DuelLockInIconPatch
{
    public static void Postfix(Player player, ref bool __result)
    {
        if (__result || !DuelSession.IsDuelActive)
        {
            return;
        }

        if (DuelTurnModel.Current is not LockInTurnModel model)
        {
            return;
        }

        __result = LocalContext.IsMe(player) ? model.LockedIn : model.OpponentLockedIn;
    }
}
