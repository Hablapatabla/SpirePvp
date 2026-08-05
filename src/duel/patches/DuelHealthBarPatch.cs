using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
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

        Creature? creature = __instance._creature;
        if (creature == null || !creature.IsPlayer || LocalContext.IsMe(creature.Player))
        {
            return true;
        }

        // The opponent's bar stays up; skip the original hide.
        return false;
    }
}
