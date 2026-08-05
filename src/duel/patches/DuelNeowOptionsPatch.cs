using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Modifiers;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Gives a PvP run its normal Neow back.
///
/// `Neow.GenerateInitialOptions` branches on `RunState.Modifiers.Count <= 0`: with no modifiers
/// you get the usual three blessings, and with *any* modifier you instead get only the options
/// those modifiers supply. That is right for vanilla custom runs — Draft's Neow option is how
/// you draft a deck — but our six modifiers are match configuration, not gameplay content, and
/// supply no options at all. The result was an empty list, which in game looks exactly like
/// Neow being skipped.
///
/// The design wants the opposite: the same Neow for both players is a premise of the match
/// (§1), and it is already mirrored, because `RaceMirrorRngPatch` seeds both players
/// identically before Neow rolls.
///
/// Rather than reimplement ~70 lines of blessing generation, this hides the duel modifiers for
/// the duration of the call so vanilla takes its normal branch, then restores them. A finalizer
/// does the restore so a throw inside cannot leave the run without its modifiers — losing those
/// would silently downgrade the match to a plain co-op run.
///
/// Deliberately conditional: a run mixing duel modifiers with real ones (say Draft) keeps
/// vanilla's behaviour, because those genuinely do have Neow options worth offering.
/// </summary>
[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class DuelNeowOptionsPatch
{
    private static readonly FieldInfo? _modifiersField =
        AccessTools.Field(typeof(RunState), "<Modifiers>k__BackingField");

    public static void Prefix(Neow __instance, out IReadOnlyList<ModifierModel>? __state)
    {
        __state = null;

        RunState? runState = __instance.Owner?.RunState as RunState;
        if (runState == null || _modifiersField == null || !DuelMatch.IsPvpRun(runState))
        {
            return;
        }

        // Only step aside when every modifier is ours; otherwise vanilla's branch is correct.
        foreach (ModifierModel modifier in runState.Modifiers)
        {
            if (modifier is not DuelModifierBase)
            {
                return;
            }
        }

        __state = runState.Modifiers;
        _modifiersField.SetValue(runState, Array.Empty<ModifierModel>());
    }

    public static void Finalizer(Neow __instance, IReadOnlyList<ModifierModel>? __state)
    {
        if (__state == null || _modifiersField == null)
        {
            return;
        }

        if (__instance.Owner?.RunState is RunState runState)
        {
            _modifiersField.SetValue(runState, __state);
        }
    }
}
