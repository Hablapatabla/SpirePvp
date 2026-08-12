using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ancients;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Drops Neow's talking beat in a PvP run, keeping the blessings.
///
/// `NEventRoom` builds an ancient's dialogue and hands it to
/// `NAncientEventLayout.SetDialogue`, then calls `SetOptions` separately. The two are
/// independent, so suppressing the dialogue leaves the blessing choice fully intact — which is
/// the whole point: the Neow *decision* is part of the match, its greeting is not.
///
/// A race starts with the clock already running, so several seconds of scripted greeting is
/// dead time both players pay simultaneously and can do nothing about.
///
/// Passing an empty list rather than skipping the call keeps the layout initialised — it still
/// gets to lay itself out with no lines, which is a state it already handles, instead of being
/// left holding whatever the previous event set.
/// </summary>
[HarmonyPatch(typeof(NAncientEventLayout), nameof(NAncientEventLayout.SetDialogue))]
public static class RaceSkipNeowDialoguePatch
{
    private static readonly List<AncientDialogueLine> None = new List<AncientDialogueLine>();

    public static void Prefix(ref IReadOnlyList<AncientDialogueLine> lines)
    {
        if (DuelMatch.IsPvpRun(RunManager.Instance?.State))
        {
            lines = None;
        }
    }
}
