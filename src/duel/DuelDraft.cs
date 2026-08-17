using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens;
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

    /// <summary>Relics shown, and taken — 10 in the pool, 5 each, drafted to exhaustion.</summary>
    private const int RelicPoolSize = 10;

    private const int RelicPicksEach = 5;

    /// <summary>
    /// Which round is running. Each is an independent alternating draft over its own pool, which is
    /// what lets three rounds share one loop, one message and one set of turn rules.
    /// </summary>
    private enum Stage
    {
        Cards = 0,
        Relics = 1,
        Complete = 3
    }

    private static Stage _stage = Stage.Cards;

    private static List<RelicModel> _relicPool = new();

    private static bool _armed;

    private static List<CardModel> _pool = new();

    private static readonly List<int> _hostPicks = new();

    private static readonly List<int> _clientPicks = new();

    private static ulong _pickerId;

    private static ulong _firstPickerId;

    private static bool _complete;

    private static NDeckCardSelectScreen? _screen;

    /// <summary>The campfire scene drawn behind the draft. See ShowBackdrop.</summary>
    private static Control? _backdrop;

    /// <summary>Host only: true until the client confirms it has the state. See DraftAckMessage.</summary>
    private static bool _awaitingAck;

    private static DateTime _lastBroadcast = DateTime.MinValue;

    /// <summary>Set whenever the state changed, so the next tick rebuilds the screen for it.</summary>
    private static bool _screenDirty;

    /// <summary>
    /// How many of our own picks in the running round have already been applied. See ApplyOwnPicks.
    /// Reset when a round advances, since the pick list is reset with it.
    /// </summary>
    private static int _appliedPicks;

    /// <summary>
    /// How long the host waits before repeating an unacknowledged draft state.
    ///
    /// Long enough not to spam a link that is merely slow, short enough that a client which armed
    /// late is picking within a second rather than staring at an empty map screen.
    /// </summary>
    private static readonly TimeSpan RebroadcastAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Dev shortcut: take our own picks automatically, in pool order (`duel draft`).
    ///
    /// Local-only and one player at a time, so the host still arbitrates every pick and the
    /// alternation is untouched — this is a fast typist, not a second code path into the draft.
    /// That distinction is the whole reason it is safe, and it is the one `duel now` got wrong
    /// twice before being routed back through the real rendezvous.
    /// </summary>
    public static bool AutoPick { get; set; }

    /// <summary>Whether a draft is running. Read by the patches that keep the map out of the way.</summary>
    public static bool IsDrafting => CurrentPoolSize > 0 && !_complete;

    /// <summary>How many entries the running round's pool holds.</summary>
    private static int CurrentPoolSize => _stage == Stage.Relics ? _relicPool.Count : _pool.Count;

    /// <summary>How many each player takes in the running round.</summary>
    private static int CurrentPicksEach => _stage == Stage.Relics ? RelicPicksEach : PicksEach;

    /// <summary>
    /// True once a draft has been set up for this run, and it stays true after the draft ends.
    ///
    /// **Deliberately not <see cref="IsDrafting"/>.** That one goes false the moment the last pick
    /// lands, which is *before* the arena is entered — so an initiative read at arena entry, which
    /// is exactly when it happens, would have fallen back to arrival order and silently undone the
    /// compensation rule. Ask "is this a draft run", not "is a draft on screen".
    /// </summary>
    public static bool IsDraftRun => _pool.Count > 0 || _relicPool.Count > 0;

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
        _relicPool = new List<RelicModel>();
        _stage = Stage.Cards;
        _hostPicks.Clear();
        _clientPicks.Clear();
        _pickerId = 0;
        _firstPickerId = 0;
        _complete = false;
        _screenDirty = false;
        _appliedPicks = 0;
        _awaitingAck = false;
        AutoPick = false;
        CloseBackdrop();
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

        // **The other end of the lobby telemetry.** The lobby lines say what each peer believed
        // before the run; this says what the run was actually seeded with. If the lobby agreed and
        // this does not, the lobby record is not what seeds the run and the search moves a layer
        // down — which is the possibility four fixes never separated.
        Log.Warn("[SpirePvp] lobby telemetry: run seeded with "
                 + string.Join(", ", runState.Players.Select(p =>
                       $"{p.NetId}{(LocalContext.IsMe(p) ? "(me)" : "")}={p.Character.Id.Entry}")));

        Player? me = LocalContext.GetMe(runState.Players);
        Player? opponent = runState.Players.FirstOrDefault(p => !LocalContext.IsMe(p));
        if (me == null || opponent == null)
        {
            Log.Error($"[SpirePvp] draft: cannot begin, me={me?.NetId}, opponent={opponent?.NetId}");
            return;
        }

        // **The mirror is a premise, not a preference, and nothing was checking it.** The pool is
        // built from one character and shown to both players, so two different characters means the
        // client drafts cards it cannot play into a deck of a different colour — reported 2026-08-14
        // with a Defect pool and an Ironclad client.
        //
        // Refusing is the safe failure while the lobby cannot yet force the match to mirror: a run
        // that continues without a draft is recoverable, and a duel decided by which side got a
        // legal deck is not. Same reasoning as `SpirePvpInit.PatchesHealthy` refusing to arbitrate.
        if (!me.Character.Id.Equals(opponent.Character.Id))
        {
            Log.Error($"[SpirePvp] draft: refusing to start — this is a mirror mode and the two "
                      + $"players are {me.Character.Id.Entry} and {opponent.Character.Id.Entry}. "
                      + "The lobby is supposed to force this — if you see this line, the mirror "
                      + "did not take, and the host's own lobby record is the one to trust.");
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
                pool.Add(Register(card.ToMutable(), drafter, runState));
            }
        }

        return pool;
    }

    /// <summary>
    /// Puts a pool card through the rest of vanilla's card creation, which is not optional.
    ///
    /// **This is `RunManager.EnterRoom` again, in miniature.** `RunState.CreateCard` is three lines
    /// and the whole of what makes a usable card:
    ///
    ///     CardModel cardModel = canonicalCard.ToMutable();
    ///     AddCard(cardModel, owner);
    ///     cardModel.AfterCreated();
    ///
    /// The pool did the first line, invented its own version of the second (assigning `Owner` by
    /// hand) and skipped the third entirely — a vanilla creation path reimplemented in one line,
    /// inheriting every omission silently, which is exactly the trap `DuelArena` spent six
    /// omissions learning.
    ///
    /// **All three reported symptoms were this one gap**, getting more specific each time: every
    /// cost reading 1, every description reading as an error string, and finally every card drawing
    /// as True Grit. One unregistered, un-initialised model rendering as a fallback — not three
    /// bugs, and not the `Owner` half alone, which was true as far as it went and fixed nothing.
    ///
    /// `AddCard` is what actually establishes ownership, so `Owner` is no longer assigned here.
    /// Each peer registers its own copies with its own run: the host's pool belongs to the host's
    /// player and the client's to the client's, which is right — they are two presentations of one
    /// agreed list, and the seven each side keeps go into that side's deck.
    /// </summary>
    private static CardModel Register(CardModel card, Player? owner, RunState? runState)
    {
        if (owner == null || runState == null)
        {
            return card;
        }

        try
        {
            runState.AddCard(card, owner);
            card.AfterCreated();
        }
        catch (Exception e)
        {
            Log.Warn($"[SpirePvp] draft: could not register {card.Id.Entry} with the run: {e.Message}");
        }

        return card;
    }

    /// <summary>
    /// The relic pool: the character's own relics plus the shared ones, minus anything they hold.
    ///
    /// **Both pools, because a duel has no shop, no chest and no boss reward.** In a normal run a
    /// player meets shared relics constantly and character relics rarely; a draft is the only source
    /// there is, so offering only one pool would silently delete half the relic game.
    ///
    /// **Filtered against what the player already has.** Relics are not stackable in general, and a
    /// pool offering the starter relic back is a wasted slot at best.
    ///
    /// **Map-only relics are filtered out, by what they listen to rather than by name** — see
    /// <see cref="IsDeadInADuel"/>. A duel has no map, shop, rest site or event, so a relic whose
    /// every override is about those is a wasted slot in a ten-card pool.
    /// </summary>
    private static List<RelicModel> BuildRelicPool(RunState runState, Player drafter)
    {
        List<RelicModel> pool = new List<RelicModel>();
        try
        {
            HashSet<string> held = drafter.Relics.Select(r => r.Id.Entry).ToHashSet();

            List<RelicModel> available = drafter.Character.RelicPool
                .GetUnlockedRelics(runState.UnlockState)
                .Concat(ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(runState.UnlockState))
                .Where(r => !held.Contains(r.Id.Entry))
                .Where(r => !IsDeadInADuel(r))
                .GroupBy(r => r.Id.Entry)
                .Select(g => g.First())
                .ToList();

            Random rng = new Random();
            foreach (RelicModel relic in available.OrderBy(_ => rng.Next()).Take(RelicPoolSize))
            {
                pool.Add(relic.ToMutable());
            }
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] draft: could not build a relic pool: {e}");
            return new List<RelicModel>();
        }

        return pool;
    }

    /// <summary>
    /// Whether a relic can do nothing at all in a duel, decided by the hooks it overrides.
    ///
    /// **By behaviour, not by name.** Enumerating "the rest site relics" is the hand-maintained list
    /// the AoE fix rejected on principle: it is wrong the day the game adds a relic, and nothing
    /// tells you. What is actually true of a dead relic is that *everything it overrides is about a
    /// place a duel never goes* — a map, a shop, a rest site, an event, a floor transition. That is
    /// a question reflection can answer and a list cannot.
    ///
    /// **Conservative in the direction that matters.** A relic is only excluded when it overrides at
    /// least one hook and **every** one of them is map-only. A relic that overrides nothing is kept,
    /// because plenty work through properties rather than hooks and a silent stat relic is fine; a
    /// relic that overrides one combat hook and five map hooks is kept, because the one combat hook
    /// is the whole point of it. **A dead relic is a bad pick; a missing good relic is a worse
    /// pool.**
    ///
    /// The result is cached per type: the pool is rebuilt on a broadcast and reflection over a few
    /// hundred relics is not something to redo on a timer.
    /// </summary>
    private static bool IsDeadInADuel(RelicModel relic)
    {
        Type type = relic.GetType();
        if (_deadRelicCache.TryGetValue(type, out bool cached))
        {
            return cached;
        }

        bool dead;
        try
        {
            List<string> overridden = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly)
                .Where(m => m.IsVirtual && m.GetBaseDefinition().DeclaringType != type)
                .Select(m => m.Name)
                .Distinct()
                .ToList();

            dead = overridden.Count > 0 && overridden.All(MapOnlyHooks.Contains);
        }
        catch (Exception e)
        {
            // Reflection failing is not a reason to shrink someone's relic pool.
            Log.Warn($"[SpirePvp] draft: could not inspect {relic.Id.Entry}, keeping it: {e.Message}");
            dead = false;
        }

        _deadRelicCache[type] = dead;
        if (dead)
        {
            Log.Info($"[SpirePvp] draft: {relic.Id.Entry} does nothing outside a map — not offered");
        }

        return dead;
    }

    /// <summary>
    /// Answers keyed by relic *type*, and **deliberately not cleared between runs**.
    ///
    /// Everything else in this class is released in <see cref="Reset"/>, because mod state is static
    /// and the run it belongs to is not — the rule this project has been caught by most. This one is
    /// the exception and it is worth saying why: the answer is a property of the relic class itself,
    /// which cannot change while the process lives. Clearing it would only pay for the same
    /// reflection again on the next match.
    /// </summary>
    private static readonly Dictionary<Type, bool> _deadRelicCache = new();

    /// <summary>
    /// Hooks that only ever fire somewhere a duel does not go.
    ///
    /// Deliberately a list of *hooks* rather than of relics: hooks are the engine's own vocabulary
    /// for "when does this happen", they change far less often than the relic roster, and a hook
    /// added by a game update simply is not on this list, which fails toward keeping a relic.
    /// </summary>
    private static readonly HashSet<string> MapOnlyHooks = new()
    {
        "AfterActEntered",
        "AfterMapGenerated",
        "AfterRestSiteHeal",
        "AfterRestSiteSmith",
        "AfterRoomEntered",
        "BeforeRoomEntered",
        "ModifyExtraRestSiteHealText",
        "ModifyGeneratedMap",
        "ModifyGeneratedMapLate",
        "ModifyMerchantCardCreationResults",
        "ModifyMerchantCardPool",
        "ModifyMerchantCardRarity",
        "ModifyMerchantPrice",
        "ModifyNextEvent",
        "ModifyOddsIncreaseForUnrolledRoomType",
        "ModifyRestSiteHealAmount"
    };

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

        if (poolIndex < 0 || poolIndex >= CurrentPoolSize || IsTaken(poolIndex))
        {
            Log.Warn($"[SpirePvp] draft: ignoring pick {poolIndex} from {pickerId} — out of range "
                     + "or already taken");
            return;
        }

        bool byHost = pickerId == HostId();
        (byHost ? _hostPicks : _clientPicks).Add(poolIndex);

        string taken = _stage == Stage.Relics
            ? _relicPool[poolIndex].Id.Entry
            : _pool[poolIndex].Id.Entry;

        Log.Warn($"[SpirePvp] draft [{_stage}]: {pickerId} took {taken} "
                 + $"(host {_hostPicks.Count}, client {_clientPicks.Count})");

        if (_hostPicks.Count >= CurrentPicksEach && _clientPicks.Count >= CurrentPicksEach)
        {
            // **The finished round is broadcast before it is torn down, and that ordering is
            // load-bearing.** `AdvanceStage` clears the pick lists, and the peers apply their own
            // picks *from the broadcast* — so advancing first would publish an empty list and the
            // last round's picks would reach nobody's deck. Two sends, and the first one is the
            // one that pays for the round.
            _pickerId = 0;
            Broadcast();
            ApplyStateLocally();

            AdvanceStage();
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

        // One pick per tick while auto-drafting, so the alternation still runs and both screens
        // still show the pool shrinking — a burst submitted at once would be refused for every pick
        // but the first anyway, since the host hands the turn back only after it accepts one.
        if (AutoPick && LocalMayPick)
        {
            AutoPickOne();
        }

        // The backdrop deliberately outlives the draft — it carries the deck review too, which is
        // the gap that was black before — but it must not follow anyone into the arena. Taken down
        // on the phase rather than on the last pick, because the phase is the condition that
        // actually means "the scenery is wrong now"; guard on the condition, not on a route.
        if (DuelSession.IsDuelActive)
        {
            CloseBackdrop();
        }

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

        bool alive = _stage == Stage.Relics
            ? _relicScreen != null
              && GodotObject.IsInstanceValid(_relicScreen)
              && _relicScreen.IsInsideTree()
            : _screen != null
              && GodotObject.IsInstanceValid(_screen)
              && _screen.IsInsideTree();

        // A relic round redraws on every pick rather than marking in place, so a state change is a
        // rebuild here where it is only a repaint for cards.
        if (_stage == Stage.Relics && _screenDirty)
        {
            alive = false;
        }

        if (alive)
        {
            // **The screen is never rebuilt for a pick.** It used to be, and every pick cost a
            // full teardown and rebuild — reported as "a janky black screen refresh for every
            // pick". The pool is fixed for the whole draft, so the grid can be built once and only
            // its highlights and its status line change.
            if (_screenDirty)
            {
                _screenDirty = false;
                RefreshVisuals();
            }

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
        RefreshVisuals();
    }

    /// <summary>Takes the first thing left in the pool, for `duel draft`.</summary>
    private static void AutoPickOne()
    {
        for (int i = 0; i < CurrentPoolSize; i++)
        {
            if (IsTaken(i))
            {
                continue;
            }

            Log.Info($"[SpirePvp] draft: auto-picking index {i}");
            if (RunManager.Instance?.NetService.Type == NetGameType.Host)
            {
                TryPick(LocalId(), i);
            }
            else
            {
                RunManager.Instance?.NetService?.SendMessage(new DraftPickMessage { poolIndex = i });
            }

            return;
        }
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

    /// <summary>
    /// Host only: this round is finished, so set up the next one.
    ///
    /// **Initiative alternates between rounds.** Whoever picked second in the cards round picks
    /// first in the relics round, which stops one player leading every round off a single coin
    /// flip — the same reasoning M9 applies to turns inside a duel, and it costs one line because
    /// the round's first picker is already a field.
    ///
    /// The pick lists are cleared rather than kept per round: each peer has already applied its own
    /// picks from the broadcast (see ApplyOwnPicks), so the lists have done their job and a fresh
    /// round wants fresh indices into a fresh pool.
    /// </summary>
    private static void AdvanceStage()
    {
        RunState? runState = RunManager.Instance?.State;
        Player? me = runState == null ? null : LocalContext.GetMe(runState.Players);

        _firstPickerId = Opponent(_firstPickerId);
        _pickerId = _firstPickerId;
        _hostPicks.Clear();
        _clientPicks.Clear();
        _appliedPicks = 0;

        if (_stage == Stage.Cards && runState != null && me != null)
        {
            _relicPool = BuildRelicPool(runState, me);
            if (_relicPool.Count > 0)
            {
                _stage = Stage.Relics;
                Log.Warn($"[SpirePvp] draft: cards done — {_relicPool.Count} relic(s) up, "
                         + $"{_firstPickerId} picks first");
                return;
            }

            Log.Warn("[SpirePvp] draft: no relics available, skipping the relic round");
        }

        _stage = Stage.Complete;
        _complete = true;
        _pickerId = 0;
        Log.Warn("[SpirePvp] draft: all rounds complete");
    }

    private static void Broadcast()
    {
        RunManager.Instance?.NetService?.SendMessage(new DraftStateMessage
        {
            stage = (int)_stage,
            pool = _pool.Select(c => c.ToSerializable()).ToList(),
            relicPool = _relicPool.Select(r => r.ToSerializable()).ToList(),
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

        Player? me = RunManager.Instance?.State == null
            ? null
            : LocalContext.GetMe(RunManager.Instance.State.Players);

        // **Built once.** Later broadcasts carry the same pool, so rebuilding would hand the grid
        // fresh `CardModel` instances every pick — new nodes, a new screen, and a re-registration of
        // fifteen cards with the run each time. The pool is fixed for the whole draft; only the
        // picks and the turn move.
        _stage = (Stage)message.stage;

        // The relic pool arrives when the round changes, so it is adopted whenever it is new rather
        // than only once — unlike the cards, which exist from the first broadcast.
        if (_relicPool.Count != (message.relicPool?.Count ?? 0))
        {
            _relicPool = (message.relicPool ?? new List<SerializableRelic>())
                .Select(RelicModel.FromSerializable)
                .Where(r => r != null)
                .ToList()!;
        }

        if (_pool.Count == 0)
        {
            _pool = (message.pool ?? new List<SerializableCard>())
                .Select(CardModel.FromSerializable)
                .Where(c => c != null)
                .Select(c => Register(c!, me, RunManager.Instance?.State))
                .ToList();
        }
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
        // **Applied before the completion check, not after it.** Anything picked in the round that
        // just ended has to reach its owner before the draft is declared over, or the last card and
        // the last relic of a match would be dropped on the floor.
        ApplyOwnPicks();

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

        if (_stage == Stage.Relics)
        {
            ShowRelicScreen();
            return;
        }

        if (_pool.Count == 0 || NOverlayStack.Instance == null)
        {
            Log.Warn($"[SpirePvp] draft: cannot show the pool (cards={_pool.Count}, "
                     + $"overlays={(NOverlayStack.Instance == null ? "none" : "ok")})");
            return;
        }

        // **The whole pool, including cards already taken.** Removing them was what forced a rebuild
        // per pick, and it also threw away the thing a shared pool is for: you want to see what they
        // took, not just what is left. Taken cards stay in place and are marked instead — see
        // RefreshVisuals.
        //
        // (1, 1) keeps vanilla's "pick exactly one" shape, but nothing in vanilla's selection flow
        // actually runs: `DuelDraftScreenPatch` takes the click and submits the pick itself, so the
        // screen never completes and never has to be rebuilt.
        CardSelectorPrefs prefs = new CardSelectorPrefs(DraftTitle(), 1, 1)
        {
            Cancelable = false
        };

        ShowBackdrop();

        NDeckCardSelectScreen screen = NDeckCardSelectScreen.Create(new List<CardModel>(_pool), prefs);
        _screen = screen;
        NOverlayStack.Instance.Push(screen);
        Log.Info($"[SpirePvp] draft: pool shown ({_pool.Count} cards)");
    }

    /// <summary>
    /// The relic round's screen: vanilla's own choose-a-relic row, showing only what is left.
    ///
    /// **Unlike the card grid this one is rebuilt per pick**, and that is a deliberate difference
    /// rather than an oversight. `NChooseARelicSelection` builds its row from the list it is handed
    /// and has no per-relic marking to borrow — there is no relic equivalent of `HighlightCard` —
    /// so "keep them all and mark the taken ones" has nothing to mark with. Ten picks of a shrinking
    /// row is a far smaller cost than inventing a relic grid, and the row visibly shortening is
    /// itself a readable signal of what has gone.
    ///
    /// `ShowScreen` rather than `RelicSelectCmd.FromChooseARelicScreen`: the command routes the pick
    /// through `PlayerChoiceSynchronizer`, which is the exact mechanism the full-state design exists
    /// to stay away from.
    /// </summary>
    private static void ShowRelicScreen()
    {
        List<RelicModel> remaining = new List<RelicModel>();
        for (int i = 0; i < _relicPool.Count; i++)
        {
            if (!IsTaken(i))
            {
                remaining.Add(_relicPool[i]);
            }
        }

        if (remaining.Count == 0)
        {
            Log.Warn("[SpirePvp] draft: no relics left to show");
            return;
        }

        ShowBackdrop();
        _relicScreen = NChooseARelicSelection.ShowScreen(remaining);
        Log.Info($"[SpirePvp] draft [relics]: {remaining.Count} left, "
                 + $"{(LocalMayPick ? "your pick" : "waiting on " + _pickerId)}");
    }

    private static NChooseARelicSelection? _relicScreen;

    /// <summary>
    /// Submits a relic pick. Called by `DuelDraftRelicPatch` when a holder is clicked.
    ///
    /// Resolved by identity against the pool, so two copies of one relic id could never collapse
    /// onto the same slot — the same reason the card pick resolves by position rather than by id.
    /// </summary>
    public static void SubmitRelicPick(RelicModel relic)
    {
        if (!LocalMayPick || _stage != Stage.Relics)
        {
            return;
        }

        int poolIndex = _relicPool.IndexOf(relic);
        if (poolIndex < 0 || IsTaken(poolIndex))
        {
            return;
        }

        if (RunManager.Instance?.NetService.Type == NetGameType.Host)
        {
            TryPick(LocalId(), poolIndex);
            return;
        }

        RunManager.Instance?.NetService?.SendMessage(new DraftPickMessage { poolIndex = poolIndex });
    }

    /// <summary>
    /// Marks who has taken what, and says whose turn it is. Runs on every state change.
    ///
    /// **Two marks, because a shared pool has two owners.** Cards you drafted keep vanilla's own
    /// selection highlight, which is the affirmative "this is yours" the engine already draws.
    /// Cards the opponent drafted are dimmed instead — gone from your options without being gone
    /// from the screen, so the pool still reads as a pool and you can see what they built.
    ///
    /// Deliberately not a red/blue pair: red means damage everywhere else in this game, and a red
    /// border on a card someone happily drafted fights that. Highlight-versus-dim also survives
    /// colour-blindness, which a hue pair on its own does not.
    /// </summary>
    private static void RefreshVisuals()
    {
        if (_stage == Stage.Relics)
        {
            // The relic row has no marking to refresh — it is rebuilt with the survivors instead.
            return;
        }

        if (_screen == null || !GodotObject.IsInstanceValid(_screen))
        {
            return;
        }

        NCardGrid? grid = _screen._grid;
        if (grid == null)
        {
            return;
        }

        List<int> mine = LocalId() == HostId() ? _hostPicks : _clientPicks;

        for (int i = 0; i < _pool.Count; i++)
        {
            NCard? node = grid.GetCardNode(_pool[i]);
            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            if (mine.Contains(i))
            {
                grid.HighlightCard(_pool[i]);
                node.Modulate = Colors.White;
            }
            else if (IsTaken(i))
            {
                grid.UnhighlightCard(_pool[i]);
                node.Modulate = TakenByOpponent;
            }
            else
            {
                grid.UnhighlightCard(_pool[i]);
                node.Modulate = Colors.White;
            }
        }

        if (_screen._infoLabel != null)
        {
            // The status line rather than the title, because the title is fixed at construction and
            // the whole point of building the screen once is that it is never reconstructed.
            _screen._infoLabel.Text = _complete
                ? Text("SPIREPVP_DRAFT.done", "Draft complete - waiting for the arena")
                : LocalMayPick
                    ? Text("SPIREPVP_DRAFT.yourTurn", "Your pick")
                    : Text("SPIREPVP_DRAFT.theirTurn", "Opponent is picking");
        }
    }

    /// <summary>How a card the opponent drafted is drawn: still there, plainly not yours.</summary>
    private static readonly Color TakenByOpponent = new Color(0.45f, 0.45f, 0.5f, 0.85f);

    /// <summary>
    /// Takes a pick from the click handler. Validates locally only to avoid obvious no-ops — the
    /// host is still the one that decides, and a pick it does not accept simply changes nothing.
    /// </summary>
    public static void SubmitPick(CardModel card)
    {
        if (!LocalMayPick)
        {
            return;
        }

        int poolIndex = _pool.IndexOf(card);
        if (poolIndex < 0 || IsTaken(poolIndex))
        {
            return;
        }

        if (RunManager.Instance?.NetService.Type == NetGameType.Host)
        {
            TryPick(LocalId(), poolIndex);
            return;
        }

        RunManager.Instance?.NetService?.SendMessage(new DraftPickMessage { poolIndex = poolIndex });
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
        // **Closed now that there is scenery behind it.** This deliberately stayed up for a while:
        // closing it used to leave the game area black until the arena loaded, because a draft run
        // has closed the map and nothing else was drawing. The campfire backdrop fixed that, and it
        // outlives the draft on purpose — so the pool can go, and the gap it was covering is now
        // covered by something meant to be looked at.
        //
        // Leaving it up was visible in the end: the deck review drew over a still-present pool.
        // What was asked for is the calmer version — the campfire from the first pick until the
        // duel starts, with the pool and then the review on top of it.
        CloseScreen();

        RunState? runState = RunManager.Instance?.State;
        Player? me = runState == null ? null : LocalContext.GetMe(runState.Players);
        if (runState == null || me == null)
        {
            Log.Error("[SpirePvp] draft: finished with no run or no local player");
            return;
        }

        // The cards went in as they were picked (see SyncDeck), so this only reports and moves on.
        Log.Warn($"[SpirePvp] draft: complete — deck is {me.Deck.Cards.Count} cards, heading for the arena");

        DuelRendezvous.ArriveLocal();
    }

    /// <summary>
    /// Puts the act's campfire scene behind the draft, instead of nothing.
    ///
    /// **A draft run closes the map, so there was no room behind the overlay and the game area was
    /// plain black.** Asked for on sight: *"can we have a dim campfire background?"* — and the act
    /// already builds exactly that for its rest sites, as a `Control`, in one call. Borrowed rather
    /// than invented, like the play queue and the end-turn button before it: it is the act you are
    /// actually in, so it matches the duel that follows.
    ///
    /// Dimmed, because it is scenery and the cards are the screen. The overlay's own backstop still
    /// sits on top of this, so the tint only has to take it from "a room" to "a room behind glass".
    ///
    /// Parented to the overlay stack and moved to the back, so it is torn down with the draft and
    /// cannot outlive it into the arena.
    /// </summary>
    private static void ShowBackdrop()
    {
        if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
        {
            return;
        }

        NOverlayStack? overlays = NOverlayStack.Instance;
        ActModel? act = RunManager.Instance?.State?.Act;
        if (overlays == null || act == null)
        {
            return;
        }

        try
        {
            Control backdrop = act.CreateRestSiteBackground();
            backdrop.Name = "SpirePvpDraftBackdrop";
            backdrop.Modulate = new Color(0.55f, 0.55f, 0.6f, 1f);
            backdrop.MouseFilter = Control.MouseFilterEnum.Ignore;
            overlays.AddChildSafely(backdrop);
            overlays.MoveChildSafely(backdrop, 0);
            _backdrop = backdrop;
            Log.Info($"[SpirePvp] draft: campfire backdrop up ({act.Id.Entry})");
        }
        catch (Exception e)
        {
            // Scenery. A missing or unloadable scene must not cost anyone the draft.
            Log.Warn($"[SpirePvp] draft: no backdrop ({e.Message}) — the draft runs without one");
        }
    }

    private static void CloseBackdrop()
    {
        if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
        {
            _backdrop.QueueFree();
        }

        _backdrop = null;
    }

    private static void CloseScreen()
    {
        if (_screen != null)
        {
            NOverlayStack.Instance?.Remove(_screen);
            _screen = null;
        }

        if (_relicScreen != null)
        {
            if (GodotObject.IsInstanceValid(_relicScreen))
            {
                NOverlayStack.Instance?.Remove(_relicScreen);
            }

            _relicScreen = null;
        }
    }

    /// <summary>
    /// The screen's heading, and a missing key must not be able to take the draft down with it.
    ///
    /// `LocManager` throws for a key it does not have, and the key ships in the `.pck` while the
    /// code that reads it ships in the DLL — so a client that rebuilt without re-exporting has the
    /// call and not the string. That split killed a net message on 2026-08-13 and would throw here
    /// inside the screen build, i.e. no draft at all.
    /// </summary>
    private static LocString DraftTitle()
    {
        LocString? loc = LocString.GetIfExists("card_selection", "SPIREPVP_DRAFT.title");
        if (loc != null)
        {
            return loc;
        }

        Log.Warn("[SpirePvp] draft: loc key SPIREPVP_DRAFT.title is missing — the .pck is stale. "
                 + "Re-export it; the heading will read as a card upgrade until you do.");
        return new LocString("card_selection", "TO_UPGRADE");
    }

    /// <summary>Plain text for the status line, with the same stale-pack guard as the heading.</summary>
    private static string Text(string key, string fallback)
    {
        LocString? loc = LocString.GetIfExists("card_selection", key);
        return loc?.GetFormattedText() ?? fallback;
    }

    /// <summary>
    /// Puts our own picks into the deck as they are made, rather than in one lump at the end.
    ///
    /// **Asked for on sight — "I'd like to see them being added to my deck as I click them"** — and
    /// it is also the more honest model: a drafted card is yours the moment the host confirms the
    /// pick, so the deck should say so. Nothing here can double-add, because `_appliedPicks` counts
    /// what has already gone in and the pick list only ever grows.
    ///
    /// **`InvokeCardAddFinished` is the half that makes it visible.** The top-bar deck counter
    /// caches its value and only refreshes on the pile's `CardAddFinished` — the same vanilla quirk
    /// HANDOFF records for cards added by console, where the card is really there and the label is
    /// simply stale. That is why the deck read 11 after a whole draft.
    /// </summary>
    private static void ApplyOwnPicks()
    {
        RunState? runState = RunManager.Instance?.State;
        Player? me = runState == null ? null : LocalContext.GetMe(runState.Players);
        if (me == null)
        {
            return;
        }

        List<int> mine = LocalId() == HostId() ? _hostPicks : _clientPicks;
        bool addedCard = false;

        while (_appliedPicks < mine.Count)
        {
            int index = mine[_appliedPicks];
            _appliedPicks++;

            if (_stage == Stage.Relics)
            {
                if (index < 0 || index >= _relicPool.Count)
                {
                    continue;
                }

                // **`RelicCmd.Obtain`, not `AddRelicInternal`.** Obtain is the real grant: it records
                // the choice, removes the relic from the grab bags so it cannot be offered again,
                // animates it into the inventory and awaits `AfterObtained` — which is how a relic
                // with an on-pickup effect actually applies it. Reaching past it to the internal add
                // is the same shortcut that made draft cards render as True Grit.
                RelicModel relic = _relicPool[index];
                TaskHelper.RunSafely(RelicCmd.Obtain(relic, me));
                Log.Info($"[SpirePvp] draft: {relic.Id.Entry} obtained");
                continue;
            }

            if (index < 0 || index >= _pool.Count)
            {
                continue;
            }

            me.Deck.AddInternal(_pool[index]);
            addedCard = true;
            Log.Info($"[SpirePvp] draft: {_pool[index].Id.Entry} joined the deck "
                     + $"({me.Deck.Cards.Count} cards)");
        }

        if (addedCard)
        {
            me.Deck.InvokeCardAddFinished();
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
