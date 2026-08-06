using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SpirePvp.Duel;

/// <summary>
/// The yes/no popup for a draw offer, and the notice when one is declined.
///
/// Built on `NGenericPopup`, vanilla's own confirmation popup — `Create()` plus
/// `WaitForConfirmation(body, header, noButton, yesButton)` returning `Task&lt;bool&gt;` is
/// exactly the shape a draw offer needs, and it carries the game's styling, modal handling and
/// controller focus for free. This is the same instinct as the clock borrowing `NRunTimer` and
/// the arena borrowing the map's vote portraits: reuse the vanilla surface rather than build a
/// scene, since the mod ships no UI assets.
///
/// The strings live in `SpirePvp/localization/eng/gameplay_ui.json`. That file name is not
/// arbitrary — `LocManager` merges a mod's tables only into tables vanilla already has, by
/// filename, so a new table called `spirepvp.json` would never be read. `gameplay_ui` is the
/// table the pause menu already uses, so our keys ride along in it. Same reason the modifier
/// names live in `modifiers.json`.
/// </summary>
public static class DuelDrawPrompt
{
    private const string Table = "gameplay_ui";

    /// <summary>Ask the local player whether to accept the opponent's draw offer.</summary>
    public static void Show()
    {
        NGenericPopup? popup = NGenericPopup.Create();
        if (popup == null)
        {
            return;
        }

        // Null between screens. Dropping the popup is the right failure: the offer is still on
        // the wire and the opponent can offer again, whereas throwing here would take out
        // whatever screen transition is in flight.
        if (NModalContainer.Instance == null)
        {
            return;
        }

        NModalContainer.Instance.Add(popup);

        TaskHelper.RunSafely(AwaitAnswer(popup));
    }

    private static async Task AwaitAnswer(NGenericPopup popup)
    {
        bool accepted = await popup.WaitForConfirmation(
            new LocString(Table, "SPIREPVP_DRAW.body"),
            new LocString(Table, "SPIREPVP_DRAW.header"),
            new LocString(Table, "SPIREPVP_DRAW.decline"),
            new LocString(Table, "SPIREPVP_DRAW.accept"));

        DuelResign.RespondToDraw(accepted);
    }

    /// <summary>Tell the offering player their draw was turned down.</summary>
    public static void ShowDeclined() =>
        ShowNotice("SPIREPVP_DRAW_DECLINED.header", "SPIREPVP_DRAW_DECLINED.body");

    /// <summary>Confirm to the offering player that the offer went out.</summary>
    public static void ShowSent() =>
        ShowNotice("SPIREPVP_DRAW_SENT.header", "SPIREPVP_DRAW_SENT.body");

    /// <summary>
    /// A one-button popup. `WaitForConfirmation` hides the No button when the no-label is null,
    /// so the same call serves both a question and a notice.
    /// </summary>
    private static void ShowNotice(string headerKey, string bodyKey)
    {
        NGenericPopup? popup = NGenericPopup.Create();
        if (popup == null)
        {
            return;
        }

        // Null between screens. Dropping the popup is the right failure: the offer is still on
        // the wire and the opponent can offer again, whereas throwing here would take out
        // whatever screen transition is in flight.
        if (NModalContainer.Instance == null)
        {
            return;
        }

        NModalContainer.Instance.Add(popup);

        TaskHelper.RunSafely(popup.WaitForConfirmation(
            new LocString(Table, bodyKey),
            new LocString(Table, headerKey),
            null,
            new LocString(Table, "SPIREPVP_DRAW.ok")));
    }
}
