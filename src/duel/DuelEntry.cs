using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// The duel's entry flow: look at your opponent's deck, confirm, fight.
///
/// Rather than a deck *panel* shown once the duel starts, the decklist IS the entry screen —
/// the vanilla deck view, pointed at the opponent's player, with its button relabelled
/// START DUEL. That makes the information rule (DESIGN §1: opponent's decklist is revealed)
/// self-enforcing, since there is no way into the arena that does not go through it, and the
/// confirm doubles as the ready handshake.
///
/// Confirming is revocable until the opponent confirms as well, so this is a negotiation and
/// not a commitment. The clocks keep running throughout: studying their deck costs you time
/// you would rather spend in the duel, which is the intended pressure.
///
/// Entering is host-decided, matching how the flag is resolved: the host notices both sides
/// are ready and broadcasts DuelStartMessage; every client enters on that message and nothing
/// else. Two clients independently deciding to enter a room is exactly the kind of race that
/// would desync the sim.
/// </summary>
public static class DuelEntry
{
    internal const string ModVersion = "0.1.0";

    private static bool _armed;
    private static bool _localReady;
    private static bool _opponentReady;
    private static Player? _opponent;
    private static NDeckCardSelectScreen? _screen;

    public static bool IsChoosing { get; private set; }

    public static bool LocalReady => _localReady;

    /// <summary>
    /// Whether the opponent has confirmed. Read by <see cref="DuelClockService"/>: the host owns
    /// both clocks, so it needs their readiness as well as ours to stop the right one.
    /// </summary>
    public static bool OpponentReady => _opponentReady;

    /// <summary>Opens the opponent's deck as the duel's entry screen.</summary>
    public static bool Open()
    {
        // Openable from two places now, and they differ in what exists. The arena rendezvous
        // opens this from the *map*, where there is no combat at all; the legacy `duel start`
        // path opens it mid-combat. So resolve the opponent from the combat when there is one
        // and fall back to the run otherwise.
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        Player? me = state != null
            ? LocalContext.GetMe(state)
            : LocalContext.GetMe(RunManager.Instance?.State?.Players ?? (IEnumerable<Player>)Array.Empty<Player>());
        _opponent = FindOpponent(state, me);

        if (_opponent == null)
        {
            return false;
        }

        Arm();
        _localReady = false;
        _opponentReady = false;
        IsChoosing = true;

        // The campfire-style grid, not the deck view. MinSelect != MaxSelect is what enables
        // this screen's right-hand confirm button (RefreshConfirmButtonVisibility), so (0, 1)
        // gives a live confirm with nothing selected — which is what we want, since nothing
        // here is selectable. DuelEntryScreenPatch blocks card clicks and repurposes the
        // button.
        CardSelectorPrefs prefs = new CardSelectorPrefs(
            new LocString("card_selection", "TO_UPGRADE"), 0, 1)
        {
            Cancelable = false
        };

        // Their deck as *they* reported it on arrival, not as our copy of their Player remembers
        // it. The race decouples the two runs, so every card the opponent gained during it is
        // invisible here — the entry screen was showing a deck missing cards they went on to play
        // in the duel, because the pre-combat sync that fixes local state does not run until arena
        // entry, after this screen. See DuelArrivedMessage.deck.
        //
        // The fallback is the legacy `duel start` path, which never passes through the arena and
        // so has no arrival message; it enters from a live combat, where state is already synced
        // and the local copy is right.
        IReadOnlyList<CardModel> cards = DuelRendezvous.OpponentDeck ?? _opponent.Deck.Cards;
        Log.Warn($"[SpirePvp] duel entry — opponent deck: {cards.Count} cards " +
                 $"({(DuelRendezvous.OpponentDeck != null ? "reported on arrival" : "local copy")})");

        NDeckCardSelectScreen screen = NDeckCardSelectScreen.Create(cards.ToList(), prefs);
        if (NOverlayStack.Instance == null)
        {
            IsChoosing = false;
            return false;
        }

        _screen = screen;
        NOverlayStack.Instance.Push(screen);

        Log.Warn("[SpirePvp] duel entry — showing opponent deck");
        return true;
    }

    /// <summary>The START DUEL button was pressed. Toggles, because confirming is revocable.</summary>
    public static void ToggleReady()
    {
        if (!IsChoosing)
        {
            return;
        }

        _localReady = !_localReady;
        Log.Warn($"[SpirePvp] duel entry — local ready = {_localReady}");

        RunManager.Instance.NetService.SendMessage(new DuelReadyMessage
        {
            modVersion = ModVersion,
            isReady = _localReady
        });

        TryStart();
    }

    /// <summary>
    /// Registers the ready/start handlers. Called at run start as well as on Open, because a
    /// client that has not opened the screen yet still has to hear DuelStartMessage — arming
    /// only on Open left it deaf to the very message that puts it in the arena.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        INetGameService net = RunManager.Instance.NetService;
        net.RegisterMessageHandler<DuelReadyMessage>(OnReady);
        net.RegisterMessageHandler<DuelStartMessage>(OnStart);
        _armed = true;
    }

    /// <summary>Releases the handlers and the armed flag so the next run re-arms. See DuelMatch.OnRunEnded.</summary>
    public static void Disarm()
    {
        INetGameService? net = RunManager.Instance?.NetService;
        net?.UnregisterMessageHandler<DuelReadyMessage>(OnReady);
        net?.UnregisterMessageHandler<DuelStartMessage>(OnStart);

        _localReady = false;
        _opponentReady = false;
        _opponent = null;
        _screen = null;
        IsChoosing = false;
        _armed = false;
    }

    private static void OnReady(DuelReadyMessage message, ulong senderId)
    {
        if (message.modVersion != ModVersion)
        {
            Log.Warn($"[SpirePvp] opponent runs mod version {message.modVersion}, we run {ModVersion} — " +
                     "message ids are positional, so a duel between mismatched versions is unsafe.");
            return;
        }

        _opponentReady = message.isReady;
        RefreshCaption();
        TryStart();
    }

    /// <summary>
    /// Interim way to show the opponent's confirmation state: the caption carries it. The
    /// confirm button is a single check-mark icon, so it can only express *your* state — and
    /// you need to know whether they are still reading. Replaced by the opponent's portrait
    /// on the button in the M6 asset pass (DESIGN §6).
    /// </summary>
    public static void RefreshCaption()
    {
        if (_screen?._infoLabel == null)
        {
            return;
        }

        _screen._infoLabel.Text = _opponentReady
            ? "Your opponent's deck.   —   Opponent is READY."
            : "Your opponent's deck.   —   Opponent is still looking.";

        // Mild green wash on the caption when they have confirmed, so their state reads at a
        // glance rather than by reading the sentence. Deliberately softer than the confirm
        // button's own green: that one is your state and should stay the louder of the two.
        _screen._infoLabel.Modulate = _opponentReady
            ? new Color(0.62f, 1f, 0.68f)
            : Colors.White;
    }

    /// <summary>Host only: both sides ready, so tell everyone to enter.</summary>
    private static void TryStart()
    {
        if (!_localReady || !_opponentReady)
        {
            return;
        }

        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        // Empty by design. The clocks and turn model are modifiers on the run, so both clients
        // already agree on them; this message exists only to make one client, not two, decide
        // the moment the arena is entered. See DuelStartMessage.
        RunManager.Instance.NetService.SendMessage(new DuelStartMessage());

        Begin();
    }

    private static void OnStart(DuelStartMessage message, ulong senderId)
    {
        Begin();
    }

    private static void Begin()
    {
        if (!IsChoosing)
        {
            return;
        }

        IsChoosing = false;

        // The screen was pushed onto NOverlayStack, so it has to come off the same stack.
        // Closing NCapstoneContainer here did nothing, leaving the grid on top of the duel
        // and swallowing every click.
        if (_screen != null)
        {
            NOverlayStack.Instance?.Remove(_screen);
            _screen = null;
        }

        DuelArena.Enter();
    }

    private static Player? FindOpponent(ICombatState? state, Player? me)
    {
        if (me == null)
        {
            return null;
        }

        if (state != null)
        {
            foreach (Creature creature in state.PlayerCreatures)
            {
                Player? owner = creature.Player;
                if (owner != null && owner.NetId != me.NetId)
                {
                    return owner;
                }
            }
        }

        // No combat — we are at the arena node on the map. The run still knows both players.
        RunState? run = RunManager.Instance?.State;
        if (run == null)
        {
            return null;
        }

        foreach (Player player in run.Players)
        {
            if (player.NetId != me.NetId)
            {
                return player;
            }
        }

        return null;
    }
}
