using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// What a planned round looks like. Everything here is presentation — nothing it does can change
/// what resolves, and every call site is a place <see cref="LockInTurnModel"/> has already decided.
///
/// **Both surfaces are vanilla's own, and that is the whole design.** A buffered play and a
/// co-op play awaiting the host's ordering are the same thing seen from two models: submitted,
/// not yet resolved. The engine draws that already — `NCardPlayQueue` lifts the card out of the
/// hand and files it in a strip beside the play area, and `NEndTurnButton` puts a player's icon
/// above the button once they are ready. The lock-in model reaches neither by accident: it never
/// calls `RequestEnqueue` for a held play, so `ActionQueueSet.ActionEnqueued` never fires and the
/// queue is never told; and `EndPlayerTurnAction` does not execute until the flush, so
/// `IsPlayerReadyToEndTurn` stays false for the entire planning phase. Both are one call away.
///
/// The alternative — tinting cards where they sit in the hand — was not built. It would have been
/// our own visual language for a state the game already has one for, it shows no ordering, and it
/// leaves the card clickable, so the same card can be planned twice. Moving the node out of the
/// hand answers all three, because a card that is not in the hand cannot be dragged out of it.
/// </summary>
internal static class LockInPlanView
{
    /// <summary>
    /// Files a held play in the play queue, exactly as vanilla files a co-op play it has sent to
    /// the host.
    ///
    /// **The card is resolved from `NetCombatCard`**, for the same reason the reservation is:
    /// `PlayCardAction._card` is assigned in `ExecuteAction` and a held play has not executed.
    ///
    /// **Potions are held too** (`UsePotionAction` is `CombatPlayPhaseOnly` in combat) and the queue
    /// is a card strip with nowhere to put one, so they take the belt instead — see
    /// <see cref="ShowPlannedPotion"/>. That gap was open until 2026-08-13 and a planned potion read
    /// as a dead click.
    /// </summary>
    public static void ShowPlanned(GameAction action)
    {
        if (action is UsePotionAction potion)
        {
            ShowPlannedPotion(potion);
            return;
        }

        if (action is not PlayCardAction play)
        {
            return;
        }

        CardModel? card = play.NetCombatCard.ToCardModelOrNull();
        NCardPlayQueue? queue = NCardPlayQueue.Instance;
        if (card == null || queue == null)
        {
            return;
        }

        // A null holder is not a failure: vanilla's own path passes whatever GetCardHolder finds
        // and builds a node when there is none.
        queue.OnLocalCardPlayed(play, NPlayerHand.Instance?.GetCardHolder(card), card);

        // Nothing else will ask the hand to re-evaluate what you can now afford.
        RefreshPlannedCosts();
    }

    /// <summary>
    /// Repaints the hand's cost colours and the energy orb, because planning changes neither the
    /// combat state nor the energy — and those are the only things vanilla repaints on.
    ///
    /// **This is why the red costs never appeared, and the patch that draws them was innocent.**
    /// `DuelPlanEnergyPatch` has raised `EnergyCostTooHigh` against `Energy - ReservedEnergy` since
    /// 2026-08-12, and the route it counts on is real: `NCard` asks
    /// `CardCostHelper.GetEnergyCostColor`, which asks `CardModel.CanPlay`, which is what the patch
    /// postfixes. But a card only *re-asks* when something repaints it, and the repaint is
    /// `NPlayerHand.OnCombatStateChanged` — a combat-state event. **Planning a play changes no
    /// combat state**, because the energy is not spent until it executes, so nothing ever asked
    /// again and the colour stayed whatever it was when the card was dealt.
    ///
    /// The same gap hides the orb: `NEnergyCounter.RefreshLabel` runs on the same event, so
    /// `DuelPlannedEnergyDisplayPatch` would also never be consulted while planning.
    ///
    /// Presentation only — it asks nodes to redraw and changes nothing either simulation reads.
    /// </summary>
    public static void RefreshPlannedCosts()
    {
        // **The hand is not repainted while a batch is resolving, and that is a fix rather than an
        // optimisation.** Reported 2026-08-14: "during the turn resolution the cards in hand are
        // jumping between playable and unplayable and it's visually distracting."
        //
        // They were, and the flicker is `DuelPlanEnergyPatch`'s desync guard seen from outside. That
        // patch answers only while `ActionExecutor.CurrentlyRunningAction` is null — it must, because
        // `PlayCardAction.ExecuteAction` re-checks `CanPlay` on the card it is playing and a "no"
        // there would cancel the play. So *between* actions it reports the hand closed and every card
        // unplayable, and *during* an action it stands aside and vanilla's plain affordability answer
        // shows through. Repainting on every action start sampled both, alternately.
        //
        // Nothing is interactive during resolution, so the honest fix is to stop asking: the last
        // paint before the flush already shows the hand correctly, and it is still correct when
        // planning reopens. The orb below is repainted regardless — it is the one thing that *should*
        // move while cards resolve.
        bool resolving = DuelTurnModel.Current is IPlanningTurnModel model && model.HandIsClosed;

        NPlayerHand? hand = resolving ? null : NPlayerHand.Instance;
        if (hand != null)
        {
            foreach (NHandCardHolder holder in hand.Holders)
            {
                if (GodotObject.IsInstanceValid(holder))
                {
                    holder.UpdateCard();
                }
            }
        }

        NEnergyCounter? energy = NCombatRoom.Instance?.Ui?._energyCounter;
        if (energy != null && GodotObject.IsInstanceValid(energy))
        {
            energy.RefreshLabel();
        }
    }

    /// <summary>
    /// Greys a planned potion in the belt, using vanilla's own "used, not yet resolved" state.
    ///
    /// **A planned potion looked like a dead click**, which is the same gap the play queue closes for
    /// cards and was left open because the queue is a *card strip* with nowhere to put a potion.
    /// The belt is the right surface, and vanilla already has the exact state: `UsePotionAction` is
    /// `CombatPlayPhaseOnly` in combat, so it is buffered exactly like a card play, and the holder is
    /// greyed by `NPotionPopup` subscribing to `potion.BeforeUse` — which fires when the action
    /// **executes**. In a planning model that is a whole round later, so the potion sat there looking
    /// untouched and, worse, still clickable.
    ///
    /// `DisableUntilPotionRemoved` is what that subscription calls. It does two things and both are
    /// wanted: it sets `_disabledUntilPotionRemoved`, so the potion cannot be planned a second time,
    /// and it greys the holder after a beat. Borrowed rather than reinvented, for the same reason
    /// the card queue and the rest cue are.
    ///
    /// Restoring is <see cref="RestorePlannedPotions"/>'s job — a resolved potion leaves the belt on
    /// its own, but a cancelled batch would otherwise leave the holder grey for the rest of the duel.
    /// </summary>
    private static void ShowPlannedPotion(UsePotionAction action)
    {
        NPotionHolder? holder = HolderFor(action.PotionIndex);
        holder?.DisableUntilPotionRemoved();
        RefreshPlannedCosts();
    }

    /// <summary>
    /// Returns any still-held potion to its normal state, for the batch that never resolved.
    ///
    /// Called at a turn boundary, where the in-flight lists are cleared anyway. A potion that was
    /// actually drunk has already left the belt — `RemoveUsedPotion` empties its holder — so anything
    /// still holding a potion here is one that was planned and then cancelled, or planned in a batch
    /// that never flushed.
    /// </summary>
    public static void RestorePlannedPotions()
    {
        List<NPotionHolder>? holders = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer?._holders;
        if (holders == null)
        {
            return;
        }

        foreach (NPotionHolder holder in holders)
        {
            if (GodotObject.IsInstanceValid(holder) && holder.Potion != null)
            {
                holder.CancelPotionUseOrDiscard();
            }
        }
    }

    /// <summary>
    /// The belt slot for a potion index. `UsePotionAction.PotionIndex` indexes `Player.PotionSlots`,
    /// and the container builds one holder per slot in the same order — bounds-checked rather than
    /// assumed, since this runs on a click and a wrong guess would throw into the player's face.
    /// </summary>
    private static NPotionHolder? HolderFor(uint index)
    {
        List<NPotionHolder>? holders = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer?._holders;
        if (holders == null || index >= holders.Count)
        {
            return null;
        }

        NPotionHolder holder = holders[(int)index];
        return GodotObject.IsInstanceValid(holder) ? holder : null;
    }

    /// <summary>
    /// Repaints the icons above the end turn button.
    ///
    /// Vanilla refreshes them when a player's end turn *executes* and again at turn start
    /// (`RefreshPlayerVotes(animate: false)`), so the icons clear themselves each round and this
    /// only ever has to add one. `DuelLockInIconPatch` decides what they show.
    /// </summary>
    /// <summary>
    /// Re-asks the end turn button whether it should be live.
    ///
    /// **Needed because the press that locks you in disables it and nothing re-enables it.**
    /// `DuelUnlockButtonStatePatch` puts it back while a lock-in can still be withdrawn, but it is a
    /// postfix on `RefreshEnabled` — so something has to call that at the two moments the window
    /// opens and closes: your own lock-in, and the opponent's arriving.
    /// </summary>
    public static void RefreshEndTurnButton() =>
        NCombatRoom.Instance?.Ui.EndTurnButton?.RefreshEnabled();

    public static void RefreshLockInIcons() =>
        NCombatRoom.Instance?.Ui.EndTurnButton?._playerIconContainer?.RefreshPlayerVotes();

    /// <summary>
    /// Tells the button it is about to commit a batch rather than end the turn.
    ///
    /// The label is the only place the two-press rule is written down, which is deliberate: a
    /// button that reads *Lock In* while you hold cards and *End Turn* while you hold none teaches
    /// the rule at the moment it applies, and there is nowhere else in this UI to explain it.
    /// </summary>
    public static void ShowLockInLabel() =>
        SetLabel(new LocString("gameplay_ui", "SPIREPVP_LOCK_IN_BUTTON").GetFormattedText());

    /// <summary>
    /// Hands the turn back to the player after a batch has finished resolving.
    ///
    /// **`OnTurnStarted` is the reset, borrowed rather than reproduced.** A new planning window and
    /// a new turn want exactly the same thing from this button — the label back to vanilla's, the
    /// vote icons repainted, the button enabled if the player can still act — and vanilla already
    /// has that as one call, including the guard that does nothing outside the player's turn. That
    /// guard matters here: the watcher that drives this also fires as a turn rolls over, and this
    /// must not light the button up during the enemy turn.
    ///
    /// A player who has declared themselves finished gets the icons repainted and nothing else:
    /// the button stays dark until the turn rolls, which is what being finished means.
    /// </summary>
    public static void ReopenPlanning(bool acceptsMorePlays)
    {
        NEndTurnButton? button = NCombatRoom.Instance?.Ui.EndTurnButton;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (button == null || state == null)
        {
            return;
        }

        if (acceptsMorePlays)
        {
            button.OnTurnStarted(state);
        }
        else
        {
            RefreshLockInIcons();
        }
    }

    /// <summary>
    /// Presses the end turn button, for a turn the player has no way left to act in.
    ///
    /// `CallReleaseLogic` is vanilla's own "the button was activated" entry point, kept public for
    /// the several ways it can be — mouse, controller, long press — and it carries the guard that
    /// matters here (`CanTurnBeEnded` refuses mid-card-play).
    /// </summary>
    public static void PressEndTurn() => NCombatRoom.Instance?.Ui.EndTurnButton?.CallReleaseLogic();

    /// <summary>
    /// Points at whoever strikes first this turn.
    ///
    /// **You have to know it while you are planning, not while you are watching**, which is why it
    /// sits over the duelist for the whole turn rather than announcing itself as the batch
    /// resolves. It is the fact that changes what you plan: leading means your first card lands
    /// before theirs, so `[Strike, Block]` against `[Block, Strike]` has opposite winners depending
    /// on which of you starts.
    ///
    /// **Drawn rather than loaded.** The mod ships no arrow art, and a `Polygon2D` built in code
    /// needs no `.pck` change, no scene and no font — so this is a real indicator today and a
    /// one-line swap when there is a texture for it. It hangs off the creature's own node and is
    /// placed by `GetTopOfHitbox`, vanilla's documented anchor for "aligning UI elements to a
    /// creature's hitbox", so it follows the duelist rather than being positioned in screen space.
    ///
    /// Not the `IntentContainer`, which would have been the obvious parent: `NCreature` hides it,
    /// and an indicator inside a node something else switches off is an indicator that vanishes
    /// for reasons you will not find.
    /// </summary>
    public static void ShowInitiative(ulong leaderNetId)
    {
        ClearInitiative();

        NCombatRoom? room = NCombatRoom.Instance;
        IRunState? state = RunManager.Instance?.State;
        if (room == null || state == null || leaderNetId == 0)
        {
            return;
        }

        Creature? leader = null;
        foreach (Player player in state.Players)
        {
            if (player.NetId == leaderNetId)
            {
                leader = player.Creature;
                break;
            }
        }

        NCreature? node = leader == null ? null : room.GetCreatureNode(leader);
        if (node == null)
        {
            return;
        }

        Polygon2D arrow = new Polygon2D
        {
            Polygon = new[]
            {
                new Vector2(-26f, -52f),
                new Vector2(26f, -52f),
                new Vector2(0f, -22f),
            },
            Color = StsColors.gold,
            ZIndex = 100,
        };

        node.AddChildSafely(arrow);
        arrow.GlobalPosition = node.GetTopOfHitbox();
        _initiativeArrow = arrow;
        AddInitiativeLabel(arrow, LocalContext.NetId == leaderNetId);

        // A still triangle reads as scenery; a moving one reads as a pointer. Looped rather than
        // one-shot so it is still saying something a minute into a long planning phase.
        //
        // **One tween, two absolute steps.** The first version used two looped tweens with
        // `AsRelative()` — one up, one down on a delay — which do not cancel: they loop on their
        // own schedules and leave a net drift of 10px a cycle. Reported as the indicator sitting
        // "almost at the top of the screen" and "disappearing randomly", which is one bug: it
        // climbed off the screen and came back only when the next turn rebuilt it. Absolute targets
        // cannot drift however the loops line up.
        if (arrow.IsInsideTree())
        {
            float restY = arrow.Position.Y;
            Tween bob = arrow.CreateTween().SetLoops();
            bob.TweenProperty(arrow, "position:y", restY - 10f, 0.7)
                .SetEase(Tween.EaseType.InOut)
                .SetTrans(Tween.TransitionType.Sine);
            bob.TweenProperty(arrow, "position:y", restY, 0.7)
                .SetEase(Tween.EaseType.InOut)
                .SetTrans(Tween.TransitionType.Sine);
        }

        Log.Info($"[SpirePvp] initiative: {leaderNetId} strikes first this turn");
    }

    /// <summary>
    /// Says which of you it means, above the arrow.
    ///
    /// **The label is a duplicate of the end turn button's**, not a `Label` built from nothing. A
    /// bare Godot label renders in whatever the theme happens to supply and this game's text is
    /// `MegaLabel` everywhere; duplicating a live one inherits its font, size, outline and theme
    /// overrides for free, which is the same "borrow rather than build" the rest of this file runs
    /// on. If there is no button to copy — a state that should not happen inside a duel — the arrow
    /// simply appears without its caption rather than the indicator failing entirely.
    /// </summary>
    private static void AddInitiativeLabel(Node2D arrow, bool leaderIsLocal)
    {
        if (NCombatRoom.Instance?.Ui.EndTurnButton?._label is not MegaLabel template)
        {
            return;
        }

        if (template.Duplicate() is not MegaLabel label)
        {
            return;
        }

        string key = leaderIsLocal ? "SPIREPVP_INITIATIVE_YOU" : "SPIREPVP_INITIATIVE_THEM";
        label.Position = new Vector2(-140f, -96f);
        label.Size = new Vector2(280f, 40f);
        arrow.AddChildSafely(label);
        label.SetTextAutoSize(new LocString("gameplay_ui", key).GetFormattedText());
    }

    /// <summary>Drops the arrow. Called before redrawing it and on run teardown.</summary>
    public static void ClearInitiative()
    {
        if (_initiativeArrow != null && GodotObject.IsInstanceValid(_initiativeArrow))
        {
            _initiativeArrow.QueueFreeSafely();
        }

        _initiativeArrow = null;
    }

    private static Polygon2D? _initiativeArrow;

    private static void SetLabel(string text) =>
        NCombatRoom.Instance?.Ui.EndTurnButton?._label?.SetTextAutoSize(text);
}
