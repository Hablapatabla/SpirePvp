using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M1 (DESIGN §3.1 group 2, I1): duel win condition.
/// With an empty enemy side, vanilla IsCombatEnding reports victory immediately — this
/// patch must override that while the duel is active: combat ends only when a player
/// creature dies, and the survivor wins.
///
/// Currently a guarded no-op skeleton: it applies cleanly and does nothing outside a duel.
/// TODO(M1): read CombatManager.IsCombatEnding (~line 395 in the decompiled source) and
/// implement the duel-mode result; route the loser through a duel result flow instead of
/// the vanilla run-loss flow (see the pendingLoss handling near line 1256).
/// </summary>
[HarmonyPatch(typeof(CombatManager), "IsCombatEnding")]
public static class DuelWinConditionPatch
{
    public static void Postfix(ref bool __result)
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }
        // TODO(M1): __result = either duelist's creature is dead. For now, log once so the
        // M1 spike can confirm the patch is live in-game.
        Log.Info("[SpirePvp] DuelWinConditionPatch hit while duel active (skeleton no-op)");
    }
}
