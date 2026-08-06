using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
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
///
/// **The guard reads the run's declared modifiers, never the masked view.** `DuelMatch.IsPvpRun`
/// answers from `MaskedModifiers ?? runState.Modifiers`, and this patch is what sets the mask —
/// so asking it here is circular. Concretely: if a mask is ever in place when this runs, the
/// list this patch would blank is already `Array.Empty`, so it would park *that* in
/// `MaskedModifiers` and every `IsPvpRun` from then on answers "not a PvP run" — including the
/// next player's Neow, which then falls into vanilla's modifier branch and offers nothing at
/// all. Every bail-out below therefore names itself in the log, because an empty option list is
/// indistinguishable in game from Neow being skipped.
/// </summary>
[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class DuelNeowOptionsPatch
{
    private static readonly FieldInfo? _modifiersField =
        AccessTools.Field(typeof(RunState), "<Modifiers>k__BackingField");

    public static void Prefix(Neow __instance, out IReadOnlyList<ModifierModel>? __state)
    {
        __state = null;

        // Each guard below is a silent opt-out, and an opt-out here is indistinguishable in game
        // from Neow being skipped entirely — so each one says which it was. This is a per-player
        // event, so expect one line per player, per run.
        RunState? runState = __instance.Owner?.RunState as RunState;
        if (runState == null)
        {
            Log.Error($"[SpirePvp] Neow: owner has no RunState ({__instance.Owner?.RunState?.GetType().Name ?? "null"}); " +
                      "leaving vanilla's modifier branch alone.");
            return;
        }

        if (_modifiersField == null)
        {
            Log.Error("[SpirePvp] Neow: RunState.<Modifiers>k__BackingField did not resolve — " +
                      "Modifiers is no longer an auto-property. Neow will offer nothing.");
            return;
        }

        // Ask what the run *declares*, not what it is currently pretending. Using the masked
        // answer here is circular: this patch is what installs the mask.
        if (!DuelMatch.IsPvpRunUnmasked(runState))
        {
            return;
        }

        // A mask already in place means either a call still in flight or one that leaked. Either
        // way, overwriting it with the list we are about to blank would park an *empty* list in
        // MaskedModifiers, and every IsPvpRun after that answers "not a PvP run".
        if (DuelMatch.MaskedModifiers != null)
        {
            Log.Error("[SpirePvp] Neow: modifiers are already masked on entry — a previous " +
                      "GenerateInitialOptions did not restore them. Skipping to avoid masking an empty list.");
            return;
        }

        // Only step aside when every modifier is ours; otherwise vanilla's branch is correct.
        foreach (ModifierModel modifier in runState.Modifiers)
        {
            if (modifier is not DuelModifierBase)
            {
                Log.Warn($"[SpirePvp] Neow: run carries a non-duel modifier ({modifier.GetType().Name}); " +
                         "leaving vanilla's modifier branch alone, as designed.");
                return;
            }
        }

        __state = runState.Modifiers;
        Log.Warn($"[SpirePvp] Neow: hiding {__state.Count} duel modifier(s) so vanilla rolls its blessings.");
        _modifiersField.SetValue(runState, Array.Empty<ModifierModel>());

        // The lie is for vanilla only. Without this the mod's own IsPvpRun goes false for the
        // duration — and Neow rolls its blessings inside that window, so the co-op-only Massive
        // Scroll stopped being filtered out. See DuelMatch.MaskedModifiers.
        DuelMatch.MaskedModifiers = __state;
    }

    public static void Finalizer(Neow __instance, IReadOnlyList<ModifierModel>? __state)
    {
        if (__state == null || _modifiersField == null)
        {
            return;
        }

        DuelMatch.MaskedModifiers = null;

        if (__instance.Owner?.RunState is RunState runState)
        {
            _modifiersField.SetValue(runState, __state);
        }
        else
        {
            // Nothing to restore them onto. The run is now carrying an empty modifier list: not a
            // PvP match any more as far as every downstream reader is concerned, and silently so.
            Log.Error("[SpirePvp] Neow: could not restore the duel modifiers — the event's owner " +
                      "lost its RunState. This run is no longer a PvP match; abandon it.");
        }
    }
}
