using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.addons.mega_text;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Makes the pause menu speak the language of a match: Resign, and Offer Draw.
///
/// Three changes, all only in a PvP run:
///
/// 1. **Give Up is relabelled "Resign."** The button already does the right thing once
///    `DuelResignPatch` is in — it reaches `RunManager.Abandon` through the confirmation
///    popup — but "Give Up" describes deleting a run, and this now awards your opponent a win.
///
/// 2. **Give Up is revealed to the client.** Vanilla hides it for clients
///    (`_giveUpButton.Visible = NetService.Type != NetGameType.Client`) because
///    `RunLobby.AbandonRun` throws for anyone who is not the host. That reasoning does not
///    apply to a resignation, which never calls it — `DuelResign` skips vanilla's abandon
///    entirely. Without this the client has no way to resign at all, and the asymmetry would be
///    absurd: only one player could concede.
///
/// 3. **An Offer Draw button is added**, cloned from Resign so it inherits the scene's styling
///    and layout without any `.pck` work. `Duplicate()` copies the node but not the script's
///    signal connections, which is what we want — we wire our own handler and nothing carries
///    over from the button it was cloned from.
///
/// Vanilla's focus-neighbour wiring runs over its own fixed six-button array and therefore does
/// not know about ours, so the draw button is reachable by mouse but not in the controller focus
/// ring. Left alone deliberately: fixing it means rewriting vanilla's neighbour chain, and this
/// project has no controller testing to verify it against.
///
/// **Hooked on `Initialize`, not `_Ready`.** `_Ready` fires when the node is built, which is not
/// guaranteed to be after `DuelMatch.OnRunLaunched` — and a PvP check that runs too early
/// answers "no" and silently leaves the buttons off, which is this project's single most
/// repeated bug shape. `Initialize(IRunState)` is the method vanilla itself uses to decide Give
/// Up's enabled state per run, so it is by construction run-aware and always after `_Ready`
/// (it dereferences the button fields `_Ready` assigns).
/// </summary>
[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
public static class DuelPauseMenuPatch
{
    private const string DrawButtonName = "SpirePvpOfferDraw";

    public static void Postfix(NPauseMenu __instance)
    {
        // Read the run directly rather than caching a flag: the pause menu is built once per
        // run, but a mod-static "is this PvP" set at run start has already outlived a run once
        // in this project's history and reported the previous match's answer.
        try
        {
            if (!DuelResign.CanResign)
            {
                // Not "return" — restore. Mod state outliving the run it belonged to is this
                // codebase's most expensive recurring bug (HANDOFF: "Mod state is static; the
                // run it belongs to is not"), and if this node is ever reused across runs, an
                // ordinary co-op run would inherit a button reading "Resign" that deletes the
                // run, plus a draw offer with nobody to send it to. Cheap to make impossible.
                RestoreVanilla(__instance);
                return;
            }

            RelabelGiveUpAsResign(__instance);
            AddDrawButton(__instance);
        }
        catch (Exception e)
        {
            // A pause menu that throws in _Ready is a pause menu the player cannot open, which
            // would strand them in the run with no way out — strictly worse than not having the
            // buttons. Log and leave vanilla's menu standing.
            Log.Error($"[SpirePvp] pause menu: could not add duel buttons: {e.Message}");
        }
    }

    /// <summary>
    /// Put the menu back the way vanilla builds it. `RefreshLabels` is vanilla's own method for
    /// writing every button caption from its LocString, so it undoes the relabel without this
    /// patch having to know what the original text was.
    /// </summary>
    private static void RestoreVanilla(NPauseMenu menu)
    {
        Control? draw = menu._buttonContainer?.GetNodeOrNull<Control>(DrawButtonName);
        draw?.QueueFree();

        menu.RefreshLabels();
    }

    private static void RelabelGiveUpAsResign(NPauseMenu menu)
    {
        NPauseMenuButton giveUp = menu._giveUpButton;
        giveUp.Visible = true;
        giveUp.Enable();
        giveUp.GetNode<MegaLabel>("Label")
              .SetTextAutoSize(new LocString("gameplay_ui", "PAUSE_MENU.SPIREPVP_RESIGN").GetFormattedText());
    }

    private static void AddDrawButton(NPauseMenu menu)
    {
        Control container = menu._buttonContainer;

        // _Ready can run more than once across a run's screens; never stack two of them.
        if (container.GetNodeOrNull<Control>(DrawButtonName) != null)
        {
            return;
        }

        if (menu._giveUpButton.Duplicate() is not NPauseMenuButton draw)
        {
            return;
        }

        draw.Name = DrawButtonName;
        container.AddChild(draw);

        // Immediately under Resign, so the two ways to end a match sit together.
        container.MoveChild(draw, menu._giveUpButton.GetIndex() + 1);

        draw.Visible = true;
        draw.Enable();
        draw.GetNode<MegaLabel>("Label")
            .SetTextAutoSize(new LocString("gameplay_ui", "PAUSE_MENU.SPIREPVP_OFFER_DRAW").GetFormattedText());

        draw.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(OnOfferDrawPressed));
    }

    private static void OnOfferDrawPressed(NButton _)
    {
        if (DuelResign.DrawOfferPending)
        {
            return;
        }

        DuelResign.OfferDraw();
        DuelDrawPrompt.ShowSent();
    }
}
