using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SpirePvp.Net;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// What the opponent has committed and you have not yet seen resolve — M8.5 slice 3, and the piece
/// that makes pacing worth having.
///
/// **Without it the mode's premise does not hold.** Paced real-time exists so that a play is a
/// readable event you can answer. But a play only becomes visible when the host *releases* it, which
/// leaves at most one beat of warning — not enough to read, let alone answer. Meanwhile the plays
/// themselves sit in `DuelPlayScheduler`'s pool, ordered and irrevocable, for as long as a burst
/// takes to drain. This puts that pool on the screen.
///
/// **It reveals only what cannot be taken back.** A play reaches the pool by being clicked, and
/// nothing removes it but resolution — so this exposes no intention the opponent could still change
/// their mind about, which is the line DESIGN §1 draws. It is still a deliberate change to the
/// information rules, and was decided as one.
///
/// # Why text rather than the play queue
///
/// The obvious surface is vanilla's own `NCardPlayQueue`, which is what `LockInPlanView` borrows for
/// *your* plays. It does not work here, for two reasons found while building this:
///
/// - **It has no by-model entry point.** Every public method keys on a `PlayCardAction`
///   (`OnLocalCardPlayed`, `RemoveCardFromQueueForCancellation`, `UpdateCardBeforeExecution`), and a
///   pending play of the opponent's has no action on this client — that is the whole point of it
///   being pending. Using it would mean fabricating a `PlayCardAction` purely for presentation, then
///   suppressing the real one on arrival to avoid filing the card twice.
/// - **A second node for a live `CardModel` is a known hazard here.** `NCard.GetNodeForCard` resolves
///   `hand.GetCard(card) ?? playQueue.GetCardNode(card) ?? …`, so a duplicate node for a card that is
///   also in the opponent's hand can be picked up by something that wanted the real one — the same
///   family as the hand-selection bug where a queued card was pulled into a selection grid.
///
/// So this draws the card *titles* over the opponent instead: no fabricated actions, no second node
/// for any model, nothing that a lookup can mistake for the real thing. The upgrade path, if card art
/// is ever wanted, is to render the portrait texture directly rather than to build an `NCard`.
///
/// # Shape
///
/// Host-authoritative, like everything else that decides order: only the host has the pool, so only
/// the host publishes, and it draws its own copy from the same call rather than from a message it
/// does not receive. Full state every time — see <see cref="DuelPendingPlaysMessage"/>.
/// </summary>
internal static class DuelIncoming
{
    private static bool _armed;

    private static MegaLabel? _label;

    /// <summary>Rate-limits the stale-pack warning: it would otherwise fire on every pool change.</summary>
    private static bool _captionKeyMissingLogged;

    /// <summary>
    /// Armed at run start with every other handler, never on first use.
    ///
    /// The rule this project has paid for five times: the peer can announce something before you
    /// act, and a message with no handler registered is dropped in silence. The host publishes on
    /// its first booking, which can easily precede anything the client does.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        RunManager.Instance.NetService.RegisterMessageHandler<DuelPendingPlaysMessage>(OnPendingPlays);
        _armed = true;
    }

    /// <summary>Releases the handler and the label. See DuelMatch.OnRunEnded.</summary>
    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelPendingPlaysMessage>(OnPendingPlays);
        Clear();
        _armed = false;
        _captionKeyMissingLogged = false;
    }

    /// <summary>
    /// Host only: publish the pool as it now stands, and draw our own copy of it.
    ///
    /// **Drawn directly rather than by round-tripping our own broadcast**, because a host does not
    /// receive its own message — the same reason `DuelPlayScheduler` books the host's plays instead
    /// of letting them travel.
    /// </summary>
    public static void Publish(List<SerializablePendingPlay> plays)
    {
        RunManager? run = RunManager.Instance;
        if (run == null || !DuelSession.IsDuelActive)
        {
            return;
        }

        run.NetService.SendMessage(new DuelPendingPlaysMessage { plays = plays });
        Apply(plays);
    }

    private static void OnPendingPlays(DuelPendingPlaysMessage message, ulong senderId) =>
        Apply(message.plays ?? new List<SerializablePendingPlay>());

    /// <summary>
    /// Draws the opponent's share of the pool over them, and nothing at all when it is empty.
    ///
    /// Your own pending plays are deliberately not drawn from here: they are already in the play
    /// queue via `LockInPlanView.ShowPlanned`, which is vanilla's own surface and shows them as soon
    /// as they are clicked.
    /// </summary>
    private static void Apply(List<SerializablePendingPlay> plays)
    {
        ulong me = LocalContext.NetId ?? 0UL;
        List<string> theirs = new List<string>();
        foreach (SerializablePendingPlay play in plays)
        {
            if (play.owner != me && !string.IsNullOrEmpty(play.cardName))
            {
                theirs.Add(play.cardName);
            }
        }

        if (theirs.Count == 0)
        {
            Clear();
            return;
        }

        Show(string.Join(", ", theirs));
    }

    private static void Show(string text)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        IRunState? state = RunManager.Instance?.State;
        if (room == null || state == null)
        {
            return;
        }

        if (_label == null || !GodotObject.IsInstanceValid(_label))
        {
            _label = BuildLabel(room, state);
        }

        _label?.SetTextAutoSize(Caption(text));
    }

    /// <summary>
    /// The caption, and **it must not be able to throw** — which it was, measured 2026-08-13 on the
    /// first run of this feature.
    ///
    /// `LocManager` raises `LocException` for a key it cannot find, and this is called from a net
    /// message handler: the exception propagated into `NetMessageBus`, which logged it and **dropped
    /// the whole message**, so the caption never updated at all. Nine of them in one match.
    ///
    /// The missing key was the ordinary `.pck` staleness — `client.ps1` never re-exports, so the
    /// client was reading the committed pack while the host had a fresh one — and that will keep
    /// happening: a loc key is added in the same commit as the code that reads it, and the two ship
    /// in different files. So the fallback is the fix, not the re-export. A caption is worth losing
    /// its prefix over; it is not worth losing the message that carries the opponent's plays.
    /// </summary>
    private static string Caption(string cards)
    {
        try
        {
            LocString loc = new LocString("gameplay_ui", "SPIREPVP_INCOMING");
            loc.Add("Cards", cards);
            return loc.GetFormattedText();
        }
        catch (Exception e)
        {
            if (!_captionKeyMissingLogged)
            {
                _captionKeyMissingLogged = true;
                Log.Warn("[SpirePvp] incoming: SPIREPVP_INCOMING is missing from gameplay_ui — the "
                         + $"pack is stale, showing bare card names ({e.GetType().Name})");
            }

            return cards;
        }
    }

    /// <summary>
    /// Builds the caption over the opponent, borrowing the end turn button's label the same way the
    /// initiative caption does — font, outline and theme for free, and no new asset in the `.pck`.
    /// </summary>
    private static MegaLabel? BuildLabel(NCombatRoom room, IRunState state)
    {
        ulong me = LocalContext.NetId ?? 0UL;
        Creature? opponent = null;
        foreach (Player player in state.Players)
        {
            if (player.NetId != me)
            {
                opponent = player.Creature;
                break;
            }
        }

        NCreature? node = opponent == null ? null : room.GetCreatureNode(opponent);
        if (node == null || room.Ui.EndTurnButton?._label is not MegaLabel template)
        {
            return null;
        }

        if (template.Duplicate() is not MegaLabel label)
        {
            return null;
        }

        // Above the initiative arrow's caption rather than on top of it: the arrow sits at -52 and
        // its own caption at -96, so this clears both.
        label.Position = new Vector2(-160f, -140f);
        label.Size = new Vector2(320f, 40f);
        label.Modulate = StsColors.gold;
        node.AddChildSafely(label);
        return label;
    }

    /// <summary>Drops the caption. Idempotent, and safe on a node that has already gone.</summary>
    public static void Clear()
    {
        if (_label != null && GodotObject.IsInstanceValid(_label))
        {
            // Built with Duplicate, so it never came from the node pool — plain QueueFree, never
            // QueueFreeSafely, which would hand it to a pool that never issued it.
            _label.QueueFree();
        }

        _label = null;
    }
}
