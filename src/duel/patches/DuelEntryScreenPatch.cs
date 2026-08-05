using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Turns the campfire-style card grid into the duel's entry screen while
/// <see cref="DuelEntry"/> is waiting on confirmations.
///
/// NDeckCardSelectScreen is the screen used to pick a card at a rest site, and it already has
/// the shape we want: a full grid of cards with a confirm button on the right
/// (NConfirmButton, node %Confirm) separate from the back button. Its confirm is enabled by
/// RefreshConfirmButtonVisibility whenever MinSelect != MaxSelect, which is why DuelEntry
/// passes (0, 1) — a live confirm with nothing selected.
///
/// Two interceptions make it view-only:
///   OnCardClicked   — swallowed, so the opponent's cards cannot be selected or highlighted.
///   PreviewSelection — the confirm handler; toggles readiness instead of completing the
///                      selection, because confirming stays revocable until the opponent
///                      confirms too.
///
/// Everything else is the real screen: real grid, real card previews on hover, showing the
/// opponent's actual deck.
/// </summary>
[HarmonyPatch(typeof(NDeckCardSelectScreen))]
public static class DuelEntryScreenPatch
{
    private const string ReadyLabel = "START DUEL";
    private const string WaitingLabel = "WAITING…";

    /// <summary>The opponent's deck is for reading, not picking from.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("OnCardClicked", typeof(CardModel))]
    public static bool BeforeCardClicked()
    {
        return !DuelEntry.IsChoosing;
    }

    /// <summary>Confirm pressed. Toggle rather than complete — confirming is revocable.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("PreviewSelection", new Type[0])]
    public static bool BeforePreviewSelection(NDeckCardSelectScreen __instance)
    {
        if (!DuelEntry.IsChoosing)
        {
            return true;
        }

        DuelEntry.ToggleReady();
        Relabel(__instance);
        return false;
    }

    /// <summary>Same, for the button-signal overload.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("PreviewSelection", typeof(NButton))]
    public static bool BeforePreviewSelectionFromButton(NDeckCardSelectScreen __instance)
    {
        return BeforePreviewSelection(__instance);
    }

    /// <summary>
    /// Label the confirm once the overlay is up. Deliberately hooks AfterOverlayShown, which
    /// NDeckCardSelectScreen declares itself: ConnectSignalsAndInitGrid lives on the base
    /// NCardGridSelectionScreen, and Harmony resolves [HarmonyPatch(typeof(X))] against
    /// methods declared on X only — naming an inherited method throws "Undefined target
    /// method" at PatchAll, which aborts the remaining patches in the assembly.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch("AfterOverlayShown")]
    public static void AfterShown(NDeckCardSelectScreen __instance)
    {
        if (DuelEntry.IsChoosing)
        {
            Relabel(__instance);
        }
    }

    private static void Relabel(NDeckCardSelectScreen screen)
    {
        // The buttons expose no label API — their text is a scene-placed child Label whose
        // node name varies, so walk for the first one rather than guessing a path.
        Label? label = FindLabel(screen._confirmButton);
        if (label != null)
        {
            label.Text = DuelEntry.LocalReady ? WaitingLabel : ReadyLabel;
        }
    }

    private static Label? FindLabel(Node? node)
    {
        if (node == null)
        {
            return null;
        }

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
