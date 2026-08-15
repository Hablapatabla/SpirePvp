using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// A draft run skips Neow, using vanilla's own no-Neow branch.
///
/// **A Neow blessing and a drafted loadout are two answers to the same question.** Letting both
/// happen would mean a match whose starting decks differ by something neither player drafted, in a
/// mode whose entire premise is that the only difference between the two decks is what was picked
/// from one shared pool.
///
/// `RunManager` decides between the two openings on a single field:
///
///     if (currentActIndex == 0 &amp;&amp; State.ExtraFields.StartedWithNeow)
///         await EnterMapCoord(State.Map.StartingMapPoint.coord);   // Neow
///     else
///         await EnterRoomInternal(new MapRoom());                  // straight to the map
///
/// So this is not a suppression at all — it is picking the branch vanilla already has for a run
/// that starts without Neow, which is the project's standing preference: *where vanilla has a real
/// path, prefer it to correcting the other one.* The run opens on the map screen, and the draft
/// goes up over it.
///
/// **Ordering is why this hangs off `SetStartedWithNeowFlag` rather than off `OnRunCreated`.**
/// `InitializeNewRun` calls this flag setter and *then* loops the modifiers calling
/// `OnRunCreated`, so a postfix here runs before `DuelMatch.OnRunCreated` and before map
/// generation reads the flag — which it does, to decide whether the starting map point is a
/// Monster node. Setting it later would leave the flag and the map disagreeing.
///
/// `State.Modifiers` is populated by this point (the loop immediately after iterates it), so
/// asking `IsDraftMatch` here is safe.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetStartedWithNeowFlag))]
public static class DuelDraftNeowPatch
{
    public static void Postfix(RunManager __instance)
    {
        RunState? state = __instance.State;
        if (state == null || !DuelMatch.IsPvpRun(state) || !DuelMatch.IsDraftMatch(state))
        {
            return;
        }

        if (!state.ExtraFields.StartedWithNeow)
        {
            return;
        }

        state.ExtraFields.StartedWithNeow = false;
        Log.Warn("[SpirePvp] draft: skipping Neow — the draft is where this run's deck comes from");
    }
}

/// <summary>
/// Keeps the draft's cards unclickable while it is not your turn.
///
/// The pool is deliberately on screen for both players the whole time — watching what they take is
/// the point of a shared pool — so the screen is open when you cannot act, and without this the
/// grid would happily accept a click and then sit there while the host ignored the request.
///
/// A prefix on `OnCardClicked` rather than a disabled screen: the screen still has to scroll, hover
/// and inspect, all of which a player wants while deciding what to take next.
///
/// **A string target, and only the second one in the mod.** The standing rule is `nameof`, so that
/// a game update which moves a method is a build error naming it rather than a runtime
/// `PATCH FAILED`. The publicizer exposes private members but not `protected` ones, and
/// `OnCardClicked` is `protected override` — so `nameof` does not compile here. The other exception
/// is `Neow.GenerateInitialOptions`, which is virtual. Both are listed in HANDOFF; if a third
/// appears, that is a sign the publicizer settings are worth revisiting rather than a pattern to
/// follow.
/// </summary>
[HarmonyPatch(typeof(NDeckCardSelectScreen), "OnCardClicked")]
public static class DuelDraftScreenPatch
{
    public static bool Prefix(CardModel card)
    {
        // **`IsDraftRun`, not `IsDrafting`.** The final pool is deliberately left on screen after
        // the last pick, so that a draft run does not stare at a black game area while it waits for
        // the arena — and `IsDrafting` is already false by then, which would have made every card
        // on that screen clickable again.
        if (!DuelDraft.IsDraftRun || DuelDraft.LocalMayPick)
        {
            return true;
        }

        return false;
    }
}

/// <summary>
/// A draft pick is a click, not a click-then-confirm — and the confirm step is what broke the cards.
///
/// **The symptom was "they look fine in the draft, they break when you click".** That is precise and
/// it names the method: with `MinSelect == MaxSelect == 1`, `OnCardClicked` calls
/// `PreviewSelection` the instant a card is chosen, and `PreviewSelection` ends with
///
///     nCard.UpdateVisuals(selectedCard.Pile.Type, CardPreviewMode.Normal);
///
/// `CardModel.Pile` is `_owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))`. A pool card is
/// registered with the run and owned, but it is deliberately **in no pile** — it is a card you have
/// not taken yet — so `Pile` is null and the preview renders a card that never got its visuals.
/// Hence "broken card", and hence every previewed card looking like the same wrong one.
///
/// This is the third distinct fault behind one report, and the first two were mine: the cards were
/// never registered with the run (fixed by going through `RunState.CreateCard`'s steps), and before
/// that they had no owner. Each fix was real and each left the screen still broken, because the
/// preview needs something none of them supply.
///
/// **Skipping the preview is the fix rather than giving the card a pile.** Putting pool cards into
/// a pile to satisfy a getter would mean fifteen cards sitting in one of the player's real piles
/// while they are still on offer — a card you have not drafted appearing in your deck is a far
/// worse bug than the one being fixed. And the preview is not wanted anyway: it exists so a campfire
/// upgrade can be inspected and confirmed, where a draft pick is already deliberate and the opponent
/// is waiting. Clicking a card takes it, which is what the screen's own heading promises.
///
/// `CheckIfSelectionComplete` is what the confirm button would have called, so this is vanilla's own
/// completion, reached one step earlier.
/// </summary>
[HarmonyPatch(typeof(NDeckCardSelectScreen), "PreviewSelection", new System.Type[] { })]
public static class DuelDraftPreviewPatch
{
    public static bool Prefix(NDeckCardSelectScreen __instance)
    {
        if (!DuelDraft.IsDraftRun)
        {
            return true;
        }

        __instance.CheckIfSelectionComplete();
        return false;
    }
}
