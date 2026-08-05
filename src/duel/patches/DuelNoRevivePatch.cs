using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Stops the loser being resurrected the moment they lose.
///
/// CombatManager.EndCombatInternal calls Player.ReviveBeforeCombatEnd() on every player,
/// which heals anyone dead back to 1 HP. In co-op that is correct — a downed teammate gets
/// back up when the fight ends — but in a duel it undoes the result: you kill the opponent
/// and they stand straight back up.
///
/// Vanilla's reason for reviving before combat ends is relic bookkeeping (dead players are
/// unsubscribed from the hook bus, so AfterCombatEnd relics like Centennial Puzzle would not
/// reset). That still matters for the winner, so this only suppresses the revive while a
/// duel is active — and M6's result flow, which decides what actually happens to the loser,
/// should revisit it.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.ReviveBeforeCombatEnd))]
public static class DuelNoRevivePatch
{
    public static bool Prefix()
    {
        // Returning false skips the original; the loser stays down.
        return !DuelSession.IsDuelActive;
    }
}
