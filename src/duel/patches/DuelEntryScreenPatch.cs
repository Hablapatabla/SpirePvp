using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Turns the vanilla deck view into the duel's entry screen while <see cref="DuelEntry"/> is
/// waiting on confirmations.
///
/// NCardsViewScreen has a single _backButton whose handler, OnReturnButtonPressed, closes the
/// screen. During duel entry it becomes the confirm instead: relabelled, and toggling ready
/// rather than closing. Toggling — not committing — because confirming stays revocable until
/// the opponent confirms too.
///
/// Everything else about the screen is left alone: it is still the real deck view, with the
/// real sorters, showing the opponent's real pile via ShowScreen(opponentPlayer).
/// </summary>
[HarmonyPatch(typeof(NCardsViewScreen))]
public static class DuelEntryScreenPatch
{
    private const string ReadyLabel = "START DUEL";
    private const string WaitingLabel = "WAITING… (CLICK TO CANCEL)";

    /// <summary>Swallow the press and toggle readiness instead of closing the screen.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("OnReturnButtonPressed")]
    public static bool BeforeReturnPressed(NCardsViewScreen __instance)
    {
        if (!DuelEntry.IsChoosing)
        {
            return true;
        }

        DuelEntry.ToggleReady();
        Relabel(__instance);
        return false;
    }

    /// <summary>Relabel once the screen is up.</summary>
    [HarmonyPostfix]
    [HarmonyPatch("_Ready")]
    public static void AfterReady(NCardsViewScreen __instance)
    {
        if (DuelEntry.IsChoosing)
        {
            Relabel(__instance);
        }
    }

    private static void Relabel(NCardsViewScreen screen)
    {
        NButton? button = screen._backButton;
        if (button == null)
        {
            return;
        }

        // NButton exposes no label API — its text lives in a child Label placed by the scene,
        // and the name varies by button. Walk for the first one rather than guessing a path.
        Label? label = FindLabel(button);
        if (label != null)
        {
            label.Text = DuelEntry.LocalReady ? WaitingLabel : ReadyLabel;
        }
    }

    private static Label? FindLabel(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Label label)
            {
                return label;
            }

            Label? nested = FindLabel(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
