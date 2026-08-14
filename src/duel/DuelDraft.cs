using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Saves.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// M10 draft mode: no race, just a shared pool and a duel (DESIGN §7b).
///
/// Cards only for now. Relics and potions are the same alternating loop over a different pool and
/// a different screen, and are deliberately not built yet — see DESIGN §7b's build order.
///
/// # The shape, and why it is this shape
///
/// **The host owns the pool and the turn order; clients request and never decide.** Two clients
/// deriving a pool from a shared seed independently is the pattern that has bitten this project
/// repeatedly, and it buys nothing here — the pool has to be shown on both screens anyway, so it
/// may as well travel.
///
/// **Every broadcast carries the whole draft, never a delta.** See <see cref="DraftStateMessage"/>
/// for the argument; the short version is that a draft is a shared ordered sequence of decisions,
/// which is exactly what desynced this project twice, and full state has no position for the two
/// peers to disagree about.
///
/// **Handlers are armed at run start, not when the screen opens.** Five separate bugs in this
/// project have been a handler armed lazily and a peer that announced something first — and here
/// the host's opening broadcast is genuinely the first thing that happens, so a client that armed
/// on screen-open would miss the pool that tells it to open the screen.
/// </summary>
public static class DuelDraft
{
    /// <summary>
    /// Cards of each rarity in the pool, so 15 shown across common/uncommon/rare (DESIGN §7b).
    /// </summary>
    private const int PerRarity = 5;

    /// <summary>
    /// Cards each player ends with. **Seven each, and the fifteenth is deliberately never taken.**
    ///
    /// Alternating over an odd pool would hand the first picker an eighth card, and the
    /// compensation rule already spends first-pick advantage on initiative — whoever drafts first
    /// moves second. An extra card would be a second payment for the same advantage. Discarding
    /// keeps the decks symmetric while leaving the pool at 15, so every pick is still a denial.
    /// </summary>
    private const int PicksEach = 7;

    private static bool _armed;

    private static List<CardModel> _pool = new();

    private static readonly List<int> _hostPicks = new();

    private static readonly List<int> _clientPicks = new();

    private static ulong _pickerId;

    private static ulong _firstPickerId;

    private static bool _complete;

    private static NDeckCardSelectScreen? _screen;

    /// <summary>Host only: true until the client confirms it has the state. See DraftAckMessage.</summary>
    private static bool _awaitingAck;

    private static DateTime _lastBroadcast = DateTime.MinValue;

    /// <summary>Set whenever the state changed, so the next tick rebuilds the screen for it.</summary>
    private static bool _screenDirty;

    /// <summary>
    /// How long the host waits before repeating an unacknowledged draft state.
    ///
    /// Long enough not to spam a link that is merely slow, short enough that a client which armed
    /// late is picking within a second rather than staring at an empty map screen.
    /// </summary>
    private static readonly TimeSpan RebroadcastAfter = TimeSpan.FromSeconds(1);

    /// <summary>Whether a draft is running. Read by the patches that keep the map out of the way.</summary>
    public static bool IsDrafting => _pool.Count > 0 && !_complete;

    /// <summary>Who picked first — and therefore who moves *second*.</summary>
    public static ulong FirstPickerId => _firstPickerId;

    /// <summary>
    /// True once a draft has been set up for this run, and it stays true after the draft ends.
    ///
    /// **Deliberately not <see cref="IsDrafting"/>.** That one goes false the moment the last pick
    /// lands, which is *before* the arena is entered — so an initiative read at arena entry, which
    /// is exactly when it happens, would have fallen back to arrival order and silently undone the
    /// compensation rule. Ask "is this a draft run", not "is a draft on screen".
    /// </summary>
    public static bool IsDraftRun => _pool.Count > 0;

    /// <summary>
    /// Who takes the opening initiative: **whoever did not pick first**.
    ///
    /// First pick is a real advantage, so it buys the opponent the first move (DESIGN §7b). This is
    /// the whole compensation rule, and it is one line because `DuelStartMessage` already carries
    /// an initiative holder — only the input changes.
    /// </summary>
    public static ulong MovesFirstId => _firstPickerId == 0 ? 0 : Opponent(_firstPickerId);

    public static void Reset()
    {
        _pool = new List<CardModel>();
        _hostPicks.Clear();
        _clientPicks.Clear();
        _pickerId = 0;
        _firstPickerId = 0;
        _complete = false;
        _screenDirty = false;
        _awaitingAck = false;
        _lastBroadcast = DateTime.MinValue;
        CloseScreen();
    }

    /// <summary>
    /// Registers the draft handlers. Called at run start for both peers — see the class comment.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null)
        {
            return;
        }

        net.RegisterMessageHandler<DraftStateMessage>(OnState);
        net.RegisterMessageHandler<DraftPickMessage>(OnPickRequest);
        net.RegisterMessageHandler<DraftAckMessage>(OnAck);
        _armed = true;
    }

    /// <summary>
    /// Releases them. **Mod state is static and the run it belongs to is not** — the net service
    /// these were bound to is disposed with the run, so without this a second match in the same
    /// process would find `_armed` still true and silently register nothing.
    /// </summary>
    public static void Disarm()
    {
        INetGameService? net = RunManager.Instance?.NetService;
        net?.UnregisterMessageHandler<DraftStateMessage>(OnState);
        net?.UnregisterMessageHandler<DraftPickMessage>(OnPickRequest);
        net?.UnregisterMessageHandler<DraftAckMessage>(OnAck);
        _armed = false;
    }

    /// <summary>
    /// Host only: build the pool, decide who picks first, and broadcast.
    ///
    /// The client does nothing until the first <see cref="DraftStateMessage"/> arrives, which is
    /// what opens its screen — so there is no moment where the two sides are drafting from
    /// different pools, because there is no moment where the client has a pool the host did not
    /// send it.
    /// </summary>
    public static void Begin(RunState runState)
    {
        if (RunManager.Instance?.NetService.Type != NetGameType.Host)
        {
            Log.Warn("[SpirePvp] draft: waiting for the host's pool");
            return;
        }

        Player? me = LocalContext.GetMe(runState.Players);
        Player? opponent = runState.Players.FirstOrDefault(p => !LocalContext.IsMe(p));
        if (me == null || opponent == null)
        {
            Log.Error($"[SpirePvp] draft: cannot begin, me={me?.NetId}, opponent={opponent?.NetId}");
            return;
        }

        _pool = BuildPool(runState, me);
        if (_pool.Count == 0)
        {
            Log.Error("[SpirePvp] draft: the card pool came back empty — refusing to start a draft "
                      + "nobody can pick from. The run continues without one.");
            return;
        }

        // **Not the run RNG.** `State.Rng` is the shared deterministic stream both sims consume in
        // lockstep; drawing from it on the host only is precisely how a seeded stream diverges.
        // This result is *broadcast*, so it needs no determinism at all — an ordinary Random is
        // both correct and impossible to get wrong.
        _firstPickerId = new Random().Next(2) == 0 ? me.NetId : opponent.NetId;
        _pickerId = _firstPickerId;
        _complete = false;

        Log.Warn($"[SpirePvp] draft: pool of {_pool.Count} built, {_firstPickerId} picks first "
                 + $"(and therefore moves second)");

        _awaitingAck = true;
        Broadcast();
        ApplyStateLocally();
    }

    /// <summary>
    /// The pool: <see cref="PerRarity"/> of each rarity from the drafting character's own cards.
    ///
    /// **A mirror match is what makes one pool fair**, so this reads the local player's character
    /// and the two sides agree by construction rather than by agreement.
    ///
    /// `GetUnlockedCards` is the same call every reward path makes, so the co-op-only filtering
    /// `RaceNoCoopCardPoolPatch` already installs applies here too — a duel must not offer cards
    /// whose whole text is about an ally.
    /// </summary>
    private static List<CardModel> BuildPool(RunState runState, Player drafter)
    {
        List<CardModel> pool = new List<CardModel>();
        List<CardModel> available;
        try
        {
            available = drafter.Character.CardPool
                .GetUnlockedCards(runState.UnlockState, ((IRunState)runState).CardMultiplayerConstraint)
                .ToList();
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] draft: could not read the card pool for "
                      + $"{drafter.Character.Id.Entry}: {e}");
            return pool;
        }

        Random rng = new Random();
        foreach (CardRarity rarity in new[] { CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare })
        {
            List<CardModel> ofRarity = available.Where(c => c.Rarity == rarity).ToList();
            if (ofRarity.Count < PerRarity)
            {
                Log.Warn($"[SpirePvp] draft: only {ofRarity.Count} {rarity} card(s) available, "
                         + $"wanted {PerRarity} — the pool will be short");
            }

            // Shuffle then take, rather than sampling with replacement: a pool with the same card
            // twice reads as a bug even when it is not, and denial stops meaning anything.
            foreach (CardModel card in ofRarity.OrderBy(_ => rng.Next()).Take(PerRarity))
            {
                pool.Add(card.ToMutable());
            }
        }

        return pool;
    }

    /// <summary>Host only: apply a pick, whoever asked for it, then tell everyone.</summary>
    private static void OnPickRequest(DraftPickMessage message, ulong senderId)
    {
        if (RunManager.Instance?.NetService.Type != NetGameType.Host)
        {
            return;
        }

        TryPick(senderId, message.poolIndex);
    }

    /// <summary>
    /// The single place the draft is ever mutated, on the host, for both players.
    ///
    /// **The host's own pick comes through here too**, rather than taking a shortcut, so there is
    /// one implementation of the rules and one place a rule can be wrong.
    /// </summary>
    private static void TryPick(ulong pickerId, int poolIndex)
    {
        if (_complete)
        {
            Log.Warn($"[SpirePvp] draft: ignoring a pick from {pickerId} — the draft is over");
            return;
        }

        if (pickerId != _pickerId)
        {
            Log.Warn($"[SpirePvp] draft: ignoring a pick from {pickerId} — it is {_pickerId}'s turn");
            return;
        }

        if (poolIndex < 0 || poolIndex >= _pool.Count || IsTaken(poolIndex))
        {
            Log.Warn($"[SpirePvp] draft: ignoring pick {poolIndex} from {pickerId} — out of range "
                     + "or already taken");
            return;
        }

        bool byHost = pickerId == HostId();
        (byHost ? _hostPicks : _clientPicks).Add(poolIndex);

        Log.Warn($"[SpirePvp] draft: {pickerId} took {_pool[poolIndex].Id.Entry} "
                 + $"(host {_hostPicks.Count}, client {_clientPicks.Count})");

        if (_hostPicks.Count >= PicksEach && _clientPicks.Count >= PicksEach)
        {
            _complete = true;
            _pickerId = 0;
            Log.Warn("[SpirePvp] draft: complete — the fifteenth card goes unclaimed by design");
        }
        else
        {
            _pickerId = Opponent(pickerId);
        }

        Broadcast();
        ApplyStateLocally();
    }

    /// <summary>
    /// Host only: repeat the opening state until the client says it has it.
    ///
    /// **The client may not have armed when the host first broadcast.** `NetMessageBus` does not
    /// buffer for an unregistered handler — it drops and logs — and a draft begins at run launch,
    /// so the two peers arm within milliseconds of each other with no ordering guarantee either
    /// way. Every other announcement in this mod is separated from arming by a whole race.
    ///
    /// Repeating is free because the state message is complete rather than incremental: a client
    /// that receives three copies ends up exactly where one that received one does.
    ///
    /// Rides the run timer via `DuelClockHudPatch`, which is the one hook already guaranteed to
    /// run for the whole match — the same reasoning as the clock and the disconnect watchdog.
    /// </summary>
    public static void Tick()
    {
        // **Screen upkeep comes first and runs on both peers.** See EnsureScreen for why this is
        // a tick and not a push at the moment the state arrives.
        EnsureScreen();

        if (!_awaitingAck || _pool.Count == 0)
        {
            return;
        }

        if (DateTime.UtcNow - _lastBroadcast < RebroadcastAfter)
        {
            return;
        }

        Log.Info("[SpirePvp] draft: no ack yet — repeating the pool");
        Broadcast();
    }

    /// <summary>
    /// Opens the pick screen when it should be open, and puts it back if anything took it down.
    ///
    /// **Pushing it the moment the state arrives does not work, and the first playtest showed both
    /// halves of why.** The draft is set up in `OnRunLaunched`, which fires *before* the run has
    /// finished entering act 1 — and `RunManager.SetActInternal` calls `ClearScreens()` on its way
    /// to the first room. So on the client the screen opened and was swept away a moment later
    /// (`your pick (15 left)`, then a `TaskCanceledException` out of `CardsSelected`), and on the
    /// host `NOverlayStack.Instance` did not exist yet, so nothing opened at all and **nothing said
    /// so** — the absent log line being the only symptom, which is the trap this project keeps
    /// meeting.
    ///
    /// Rather than racing the run's own startup sequence, this asks every tick whether the screen
    /// that *should* be up is up. That is immune to the ordering entirely: it does not matter when
    /// the overlay stack appears or how many times something clears it, because the next tick puts
    /// the screen back. The run timer ticks about once a second, so a sweep costs a blink.
    /// </summary>
    private static void EnsureScreen()
    {
        if (!IsDrafting)
        {
            return;
        }

        // **The map has to be shut, and it is a rule rather than a z-order accident.**
        // `NOverlayStack.ShowOverlays` reads
        //
        //     if (overlayScreen != null && !NMapScreen.Instance.IsOpen)
        //
        // so vanilla deliberately keeps every overlay hidden for as long as the map is open. The
        // draft was pushed, alive and correct, and simply invisible behind it — and since a draft
        // run has nowhere on that map to go, the player could not dismiss it either.
        //
        // Closing it is also what Lucas asked for on sight: a draft should look like the deck
        // review, which is this same screen over the ordinary run backdrop rather than over a map.
        // Re-checked every tick because the map is the room here, and anything that reopens it
        // would otherwise swallow the draft again silently.
        NMapScreen? map = NMapScreen.Instance;
        if (map != null && map.IsOpen)
        {
            Log.Info("[SpirePvp] draft: closing the map — overlays stay hidden while it is open");
            map.Close();
        }

        bool alive = _screen != null
                     && GodotObject.IsInstanceValid(_screen)
                     && _screen.IsInsideTree();

        if (alive && !_screenDirty)
        {
            return;
        }

        if (NOverlayStack.Instance == null)
        {
            // Expected for the first tick or two while the run finishes entering the act. Logged
            // at Info because "the draft never appeared" must not be a silent state.
            Log.Info("[SpirePvp] draft: no overlay stack yet — will retry next tick");
            return;
        }

        if (!alive && !_screenDirty)
        {
            Log.Warn("[SpirePvp] draft: the pick screen went away — putting it back");
        }

        _screenDirty = false;
        ShowScreen();

        // Push happens with the backstop in whatever state the map left it, so ask the stack to
        // present the top screen properly now that the map is shut.
        NOverlayStack.Instance?.ShowOverlays();
    }

    /// <summary>The client has the state, so stop repeating it.</summary>
    private static void OnAck(DraftAckMessage message, ulong senderId)
    {
        if (!_awaitingAck)
        {
            return;
        }

        _awaitingAck = false;
        Log.Warn($"[SpirePvp] draft: {senderId} acknowledged the pool — drafting");
    }

    private static void Broadcast()
    {
        RunManager.Instance?.NetService?.SendMessage(new DraftStateMessage
        {
            pool = _pool.Select(c => c.ToSerializable()).ToList(),
            hostPicks = new List<int>(_hostPicks),
            clientPicks = new List<int>(_clientPicks),
            pickerId = _pickerId,
            firstPickerId = _firstPickerId,
            complete = _complete
        });

        _lastBroadcast = DateTime.UtcNow;
    }

    /// <summary>
    /// A client adopting the host's state wholesale. **No merge, no reconciliation** — the message
    /// is the truth and anything held locally is discarded, which is what makes a missed or
    /// out-of-order message a non-event.
    /// </summary>
    private static void OnState(DraftStateMessage message, ulong senderId)
    {
        if (RunManager.Instance?.NetService.Type == NetGameType.Host)
        {
            // The host authored it; adopting its own broadcast would be a no-op at best and a
            // round trip of its own state at worst.
            return;
        }

        _pool = (message.pool ?? new List<SerializableCard>())
            .Select(CardModel.FromSerializable)
            .Where(c => c != null)
            .ToList()!;
        _hostPicks.Clear();
        _hostPicks.AddRange(message.hostPicks ?? new List<int>());
        _clientPicks.Clear();
        _clientPicks.AddRange(message.clientPicks ?? new List<int>());
        _pickerId = message.pickerId;
        _firstPickerId = message.firstPickerId;
        _complete = message.complete;

        // Acked on every state, not only the first: the host stops repeating on the first ack it
        // sees, and an ack that crosses a later broadcast costs one packet and settles the same way.
        RunManager.Instance?.NetService?.SendMessage(new DraftAckMessage());

        ApplyStateLocally();
    }

    /// <summary>Redraw for whatever the state now says, on either peer.</summary>
    private static void ApplyStateLocally()
    {
        if (_complete)
        {
            Finish();
            return;
        }

        // Marked rather than pushed: the screen is built by EnsureScreen on the next tick, which is
        // what makes it survive the run's own act-entry sequence clearing screens underneath it.
        _screenDirty = true;
    }

    /// <summary>
    /// Opens or refreshes the pick screen.
    ///
    /// **Rebuilt rather than updated in place.** `NDeckCardSelectScreen` takes its card list at
    /// construction and has no way to be told the list shrank, and a draft redraws at most 14
    /// times — so the cheap correct thing is to close it and open the next one. Trying to mutate
    /// the grid would be inventing a code path vanilla does not have for a saving nobody can see.
    /// </summary>
    private static void ShowScreen()
    {
        CloseScreen();

        List<CardModel> remaining = Remaining().ToList();
        if (remaining.Count == 0)
        {
            Log.Warn("[SpirePvp] draft: nothing left in the pool to show");
            return;
        }

        if (NOverlayStack.Instance == null)
        {
            Log.Warn("[SpirePvp] draft: cannot show the pool — no overlay stack");
            return;
        }

        bool myTurn = _pickerId == LocalId();

        // (1, 1) is vanilla's "pick exactly one" — the card-reward interaction, which is what a
        // draft pick is. Not cancelable: there is no valid state where a player declines to draft,
        // and the opponent is waiting on the answer.
        CardSelectorPrefs prefs = new CardSelectorPrefs(
            new LocString("card_selection", "TO_UPGRADE"), 1, 1)
        {
            Cancelable = false
        };

        NDeckCardSelectScreen screen = NDeckCardSelectScreen.Create(remaining, prefs);
        _screen = screen;
        NOverlayStack.Instance.Push(screen);

        if (!myTurn)
        {
            // Waiting: the screen still shows the pool shrinking, which is the whole point of a
            // shared pool — you watch what they take. `DuelDraftScreenPatch` is what stops the
            // cards being clickable while it is not your turn.
            Log.Info($"[SpirePvp] draft: waiting on {_pickerId} ({remaining.Count} left)");
            return;
        }

        Log.Info($"[SpirePvp] draft: your pick ({remaining.Count} left)");
        WaitForPick(screen, remaining);
    }

    private static async void WaitForPick(NDeckCardSelectScreen screen, List<CardModel> remaining)
    {
        CardModel? chosen;
        try
        {
            chosen = (await screen.CardsSelected()).FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            // Normal: EnsureScreen closes and rebuilds this screen whenever the state changes or
            // something clears the overlay stack, and closing it cancels the pending selection.
            // The rebuild starts a fresh wait, so there is nothing to recover here.
            return;
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] draft: pick screen failed: {e}");
            return;
        }

        if (chosen == null)
        {
            return;
        }

        int poolIndex = IndexInPool(chosen, remaining);
        if (poolIndex < 0)
        {
            Log.Error($"[SpirePvp] draft: picked {chosen.Id.Entry} but it is not in the pool");
            return;
        }

        if (RunManager.Instance?.NetService.Type == NetGameType.Host)
        {
            TryPick(LocalId(), poolIndex);
            return;
        }

        // A request, not a decision — the host answers with the new state and that is what moves
        // this client's screen on.
        RunManager.Instance?.NetService?.SendMessage(new DraftPickMessage { poolIndex = poolIndex });
    }

    /// <summary>
    /// Maps the clicked card back to its pool slot.
    ///
    /// **By identity against the list this screen was built from**, not by model id: the pool can
    /// legitimately hold the same card twice across rarities, and two entries with one id would
    /// otherwise both resolve to the first slot — a pick that silently takes the wrong card.
    /// </summary>
    private static int IndexInPool(CardModel chosen, List<CardModel> remaining)
    {
        int offset = remaining.IndexOf(chosen);
        if (offset < 0)
        {
            return -1;
        }

        int seen = 0;
        for (int i = 0; i < _pool.Count; i++)
        {
            if (IsTaken(i))
            {
                continue;
            }

            if (seen == offset)
            {
                return i;
            }

            seen++;
        }

        return -1;
    }

    /// <summary>
    /// The draft is over: take what you drafted into your deck, then go to the arena.
    ///
    /// **Added to the starting deck rather than replacing it** (DESIGN §7b): a floor means a bad
    /// draft is weak rather than unplayable, and in a mirror match both sides get the identical
    /// floor, so it costs no fairness.
    ///
    /// The arena entry is the existing rendezvous, unchanged — both players announce arrival and
    /// the host flips once both are in hand. That ordering guarantee is what makes the real flow
    /// immune to everything the `duel now` shortcut kept hitting, and a draft has no reason to
    /// invent a second route into the duel.
    /// </summary>
    private static void Finish()
    {
        CloseScreen();

        RunState? runState = RunManager.Instance?.State;
        Player? me = runState == null ? null : LocalContext.GetMe(runState.Players);
        if (runState == null || me == null)
        {
            Log.Error("[SpirePvp] draft: finished with no run or no local player");
            return;
        }

        List<int> mine = LocalId() == HostId() ? _hostPicks : _clientPicks;
        int added = 0;
        foreach (int index in mine)
        {
            if (index < 0 || index >= _pool.Count)
            {
                continue;
            }

            me.Deck.AddInternal(_pool[index].ToMutable());
            added++;
        }

        Log.Warn($"[SpirePvp] draft: took {added} card(s) into the deck "
                 + $"({me.Deck.Cards.Count} total), heading for the arena");

        DuelRendezvous.ArriveLocal();
    }

    private static void CloseScreen()
    {
        if (_screen != null)
        {
            NOverlayStack.Instance?.Remove(_screen);
            _screen = null;
        }
    }

    /// <summary>Pool entries nobody has taken, in pool order.</summary>
    public static IEnumerable<CardModel> Remaining()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            if (!IsTaken(i))
            {
                yield return _pool[i];
            }
        }
    }

    /// <summary>True while the local player may click. Read by `DuelDraftScreenPatch`.</summary>
    public static bool LocalMayPick => IsDrafting && _pickerId == LocalId();

    private static bool IsTaken(int index) =>
        _hostPicks.Contains(index) || _clientPicks.Contains(index);

    private static ulong LocalId() => LocalContext.NetId ?? 0;

    private static ulong HostId()
    {
        RunState? runState = RunManager.Instance?.State;
        if (runState == null)
        {
            return 0;
        }

        // Derived from *which seat this client is*, rather than from a host id the net service
        // does not expose. Both peers reach the same answer: the host names itself, the client
        // names the other player. That keeps meaning the same seat through a rematch, which can
        // reorder `Players` and makes "player 0" wrong.
        Player? me = LocalContext.GetMe(runState.Players);
        if (me == null)
        {
            return 0;
        }

        return RunManager.Instance?.NetService.Type == NetGameType.Host
            ? me.NetId
            : Opponent(me.NetId);
    }

    private static ulong Opponent(ulong id)
    {
        RunState? runState = RunManager.Instance?.State;
        if (runState == null)
        {
            return 0;
        }

        foreach (Player player in runState.Players)
        {
            if (player.NetId != id)
            {
                return player.NetId;
            }
        }

        return 0;
    }
}
