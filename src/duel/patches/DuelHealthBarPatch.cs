using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Keeps the opponent's health bar on screen for the whole duel.
///
/// In co-op a remote player's bar is hover-only: NCreature.OnFocus animates it in and
/// OnUnfocus animates it out, gated on _isRemotePlayerOrPet. That is reasonable for
/// teammates standing next to you and useless in a duel, where the opponent's HP is the
/// single most important number on screen.
///
/// Patching AnimateOut rather than OnUnfocus is deliberate: OnUnfocus also tears down the
/// select reticle, hover tips and target-manager state, so skipping it wholesale would leak
/// UI. Suppressing just the hide leaves all of that intact.
///
/// Covers the opponent's *pets* as well as the opponent — vanilla's own flag here is
/// `_isRemotePlayerOrPet`, so a summon's bar hides on the same rule, and the opponent's Osty
/// sitting across the arena with no visible HP is the same missing information.
/// </summary>
[HarmonyPatch(typeof(NCreatureStateDisplay), nameof(NCreatureStateDisplay.AnimateOut))]
public static class DuelHealthBarPatch
{
    public static bool Prefix(NCreatureStateDisplay __instance)
    {
        if (!DuelSession.IsDuelActive)
        {
            return true;
        }

        if (!DuelLayout.BelongsToOpponent(__instance._creature))
        {
            return true;
        }

        // The opponent's bar stays up; skip the original hide.
        return false;
    }
}
