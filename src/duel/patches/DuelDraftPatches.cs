using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using SpirePvp.Modifiers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
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
        if (!DuelDraft.IsDraftRun)
        {
            return true;
        }

        // **The draft takes the click outright and vanilla's selection never runs.** That is what
        // lets the screen be built once: vanilla's flow selects, previews and then *completes*,
        // and a completed screen removes itself — which is what made every pick a teardown and
        // rebuild, reported as "a janky black screen refresh for every pick". It also sidesteps
        // the preview, which reads `selectedCard.Pile.Type` and breaks on a card that is in no
        // pile because you have not drafted it yet.
        //
        // Returning false for a pick that cannot be made is deliberate rather than lazy: on the
        // opponent's turn, and on a card already taken, a click should do nothing at all.
        DuelDraft.SubmitPick(card);
        return false;
    }
}

/// <summary>
/// In a draft lobby the client does not choose a character — it is given the host's.
///
/// **A mirror is the premise the shared pool rests on.** One pool is built from one character and
/// shown to both players, so two different characters means the client drafts cards it cannot play
/// into a deck of another colour. `DuelDraft.Begin` refuses outright when it sees that, which is the
/// safe failure but not the answer: reported 2026-08-14 as a Defect pool with an Ironclad client,
/// and then as "the client's character selection should be completely ignored".
///
/// So it is, here, rather than by asking the player to remember. `SelectCharacter` is the same entry
/// point the character buttons use, so this leaves the lobby in exactly the state a click would
/// have: the local character set on the lobby, the button visuals updated, and the change synced to
/// the host like any other.
///
/// **Hooked on arrival as well as on change**, because a message that only fires on *change* cannot
/// carry initial state — the standing rule in this project, and the fifth thing it has caught. A
/// client that joins after the host has already chosen gets one `PlayerConnected` and no
/// `PlayerChanged`, so hooking only the latter would mirror every character except the one the host
/// picked before anyone was listening.
///
/// Host-side this does nothing: the host is the one being copied.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen))]
public static class DuelDraftMirrorPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NCustomRunScreen.PlayerConnected))]
    public static void AfterConnected(NCustomRunScreen __instance, StartRunLobbyPlayer player)
    {
        Log.Warn($"[SpirePvp] lobby telemetry: PlayerConnected {player.id} = "
                 + $"{player.character?.Id.Entry ?? "none"}");
        if (__instance._lobby != null)
        {
            DumpLobby(__instance._lobby, "PlayerConnected");
        }

        if (__instance._lobby != null)
        {
            MirrorNow(__instance);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NCustomRunScreen.PlayerChanged))]
    public static void AfterChanged(NCustomRunScreen __instance, StartRunLobbyPlayer player)
    {
        Log.Warn($"[SpirePvp] lobby telemetry: PlayerChanged {player.id} = "
                 + $"{player.character?.Id.Entry ?? "none"}");
        if (__instance._lobby != null)
        {
            DumpLobby(__instance._lobby, "PlayerChanged");
        }

        // **`MirrorNow`, not `MirrorHost(player)`.** The change that matters is not always the
        // host's: when the client's own random resolved, the event was for the *local* player, so a
        // per-player mirror returned early and never noticed the two had diverged. Re-asserting the
        // whole thing on any change is one comparison and cannot miss.
        if (__instance._lobby != null)
        {
            MirrorNow(__instance);
        }
    }

    /// <summary>
    /// Re-runs the mirror for whatever the lobby currently holds.
    ///
    /// **Called from the modifiers refresh, and that is the fix for the first attempt failing.**
    /// Hooking `PlayerConnected`/`PlayerChanged` alone assumed the format was already known when a
    /// character arrived, and it is not: the two travel on different schedules, so a client that
    /// learns the characters before `MatchFormatDraft` is ticked bails out of the gate below and
    /// never hears about it again. Measured 2026-08-14 — host IRONCLAD, client DEFECT, and not one
    /// `mirroring the host` line in either log.
    ///
    /// Same family as hooking arrival *and* change: whichever of the two facts lands last has to be
    /// the one that triggers the work, so both have to trigger it.
    /// </summary>
    public static void MirrorNow(NCustomRunScreen screen)
    {
        StartRunLobby? lobby = screen._lobby;
        if (lobby == null)
        {
            return;
        }

        // Re-derived each refresh so that leaving a draft lobby releases the character lock, rather
        // than leaving a client unable to choose in the next Custom run it joins.
        NCustomRunModifiersList? list = screen._modifiersList;
        DraftLobbyActive = list != null
                           && list.GetModifiersTickedOn().Any(m => m is MatchFormatDraft);

        DumpLobby(lobby, "modifiers refresh");

        ResolvePendingRandom(screen, lobby);

        foreach (StartRunLobbyPlayer player in lobby.Players)
        {
            if (player.id != lobby.LocalPlayer.id)
            {
                MirrorHost(screen, player);
            }
        }
    }

    /// <summary>
    /// Rolls a Random the host chose *before* the format was set to Draft.
    ///
    /// **This broke a match on 2026-08-17 and the log has the whole sequence:**
    ///
    ///     PlayerChanged 1 = RANDOM_CHARACTER          (draft=False — the format was still Race)
    ///     draft=True at modifiers refresh             (Draft ticked, Random already sitting there)
    ///     host is on random — waiting for it to resolve
    ///     PlayerChanged 1 = SILENT                    (vanilla resolves it AT RUN START)
    ///     mirroring the host — taking SILENT          (a beat too late)
    ///     run seeded with 1(me)=SILENT, 1001=IRONCLAD
    ///     draft: refusing to start — SILENT and IRONCLAD
    ///
    /// `DuelDraftRandomClickPatch` rolls at the click, which is right and is not enough: it only
    /// fires while a draft lobby is *already* active, so Random chosen first and Draft ticked second
    /// leaves a live `RANDOM_CHARACTER` in the lobby. That resolves inside `BeginRunLocally`, and the
    /// client's mirror then lands after `run already starting`, which is the exact timing this whole
    /// area exists to avoid.
    ///
    /// **Same family as the rule this project already had, arriving from the other direction:** a
    /// message that only fires on *change* cannot carry initial state, so the arrival has to be
    /// hooked too. Here it is a *click* that cannot carry a later format change, so the format
    /// change has to re-ask. Whichever of the two facts lands last must trigger the work.
    ///
    /// Host only. A client is never allowed to be on Random in a draft — see `MirrorHost`, where two
    /// peers on Random is two separate rolls rather than a mirror.
    ///
    /// Deferred, and re-checked inside the deferral, for the reason documented on the mirror itself:
    /// `SelectCharacter` sends `LobbyPlayerChangedCharacterMessage`, and a send made from inside the
    /// handling of a lobby message does not survive. Re-checking rather than latching a flag keeps
    /// repeated refreshes from queueing a second roll.
    /// </summary>
    private static void ResolvePendingRandom(NCustomRunScreen screen, StartRunLobby lobby)
    {
        if (!DraftLobbyActive
            || lobby.NetService.Type == NetGameType.Client
            || lobby.LocalPlayer.character?.Id.Entry != "RANDOM_CHARACTER")
        {
            return;
        }

        Log.Warn("[SpirePvp] draft lobby: the lobby is holding Random and Draft is ticked — rolling "
                 + "it now, because a draft cannot use the run-start resolution");

        Callable.From(() =>
        {
            StartRunLobby? now = screen._lobby;
            if (now == null
                || !DraftLobbyActive
                || now.NetService.Type == NetGameType.Client
                || now.LocalPlayer.character?.Id.Entry != "RANDOM_CHARACTER")
            {
                return;
            }

            DuelDraftRandomRollPatch.RollAndSelect(screen);
        }).CallDeferred();
    }

    /// <summary>
    /// True only while <see cref="MirrorHost"/> is driving `SelectCharacter` itself, so the block
    /// below can tell our own assignment from a player's click.
    /// </summary>
    private static bool _mirroring;

    /// <summary>Exposed so the lock patch can let our own assignment through.</summary>
    internal static bool IsMirroring => _mirroring;

    /// <summary>
    /// Whether the lobby currently on screen is configured as a draft.
    ///
    /// Cached from the last modifier refresh rather than read from the screen, because the lock now
    /// sits on `StartRunLobby` and has no screen to ask. Set false on every refresh that is not a
    /// draft, so backing out of a draft lobby releases the lock.
    /// </summary>
    internal static bool DraftLobbyActive { get; private set; }

    /// <summary>
    /// Lets go of everything this class remembers, at run teardown.
    ///
    /// **Mod state is static and the run it belongs to is not** — the rule this project has been
    /// caught by more than any other. Reported 2026-08-14: the first draft of a session worked,
    /// then returning to the main menu and starting a second gave mismatched characters and no
    /// draft again. `DraftLobbyActive` and `_lastDump` both survived the first run, so the second
    /// lobby started with the first one's answers: the dump suppressed its opening line as
    /// unchanged, and the lock could be live before the new lobby had said anything about its
    /// format.
    ///
    /// `_mirroring` is cleared too. It is only ever true inside one call, but a mirror interrupted
    /// by a teardown would leave it stuck true and silently disable the lock for the whole next
    /// match.
    /// </summary>
    public static void Reset()
    {
        DraftLobbyActive = false;
        _mirroring = false;
        _lastDump = string.Empty;
    }

    /// <summary>
    /// Prints what each peer believes the lobby holds, once per refresh and only when it changes.
    ///
    /// **Added after the fourth failed fix, which is three too many.** Each attempt was reasoned
    /// from the decompile and each was right about the thing it changed; what was never established
    /// is the one fact that decides all of them — whether the client's character change reaches the
    /// *host's* record at all. The client's own log says it mirrored (`taking REGENT`) and the host
    /// then created the run as `REGENT and IRONCLAD`, so the two peers disagree and nothing so far
    /// has said which side drops it.
    ///
    /// Both peers print, so the two lines can be diffed directly — the host's view of the client is
    /// the authority, since the run is seeded from it.
    ///
    /// Note `RANDOM_CHARACTER` appears in the client's mirror log, which no fix has accounted for:
    /// the host can sit on a random-character placeholder that resolves later
    /// (`DuelRandomCharacterPatch`), so a mirror taken while it is unresolved copies the
    /// placeholder. Whether that is this bug or a second one is exactly what this line will settle.
    /// </summary>
    internal static void DumpLobby(StartRunLobby lobby, string where)
    {
        string state = string.Join(", ", lobby.Players.Select(p =>
            $"{p.id}{(p.id == lobby.LocalPlayer.id ? "(me)" : "")}="
            + $"{p.character?.Id.Entry ?? "none"}"));

        _lastDump = state;
        Log.Warn($"[SpirePvp] lobby telemetry [{lobby.NetService.Type}] draft={DraftLobbyActive} "
                 + $"at {where}: {state}  <- diff against the other client's line");
    }

    private static string _lastDump = string.Empty;

    private static void MirrorHost(NCustomRunScreen screen, StartRunLobbyPlayer player)
    {
        StartRunLobby? lobby = screen._lobby;
        if (lobby == null || lobby.NetService.Type != NetGameType.Client)
        {
            return;
        }

        // Only mirror a draft lobby. A race match is free to be cross-character and always has been.
        NCustomRunModifiersList? modifiers = screen._modifiersList;
        if (modifiers == null || !modifiers.GetModifiersTickedOn().Any(m => m is MatchFormatDraft))
        {
            // Not a draft lobby *yet*. `MirrorNow` re-asks on every modifier change, which is what
            // covers the format arriving after the characters.
            return;
        }

        DraftLobbyActive = true;

        // The player who changed is us, or has no character yet: nothing to copy.
        if (player.id == lobby.LocalPlayer.id || player.character == null)
        {
            return;
        }

        // **Never put the client on Random. Measured, after believing the opposite.**
        //
        // `DuelRandomCharacterPatch` says `BeginRunLocally` resolves `RandomCharacter` from an `Rng`
        // seeded off the run seed, so both peers would land on the same character. That reading was
        // wrong, and the log settles it — one lobby, both peers on Random:
        //
        //     PlayerChanged 1    = IRONCLAD     (the host's random resolved)
        //     PlayerChanged 1001 = SILENT       (the client's resolved, independently)
        //
        // Two players on Random are two *separate* rolls, which is the opposite of a mirror. So the
        // client stays on whatever real character it holds while the host sits on Random, and takes
        // the host's roll when it lands — the resolution arrives as an ordinary `PlayerChanged`
        // carrying a real character, which this mirrors like any other.
        if (player.character.Id.Entry == "RANDOM_CHARACTER")
        {
            Log.Info("[SpirePvp] draft lobby: host is on random — waiting for it to resolve, "
                     + "because a second random roll is not a mirror");
            return;
        }

        if (lobby.LocalPlayer.character != null
            && lobby.LocalPlayer.character.Id.Equals(player.character.Id))
        {
            return;
        }

        NCharacterSelectButton? button = screen._charButtonContainer.GetChildren()
            .OfType<NCharacterSelectButton>()
            .FirstOrDefault(b => b._character != null && b._character.Id.Equals(player.character.Id));

        if (button == null)
        {
            Log.Warn($"[SpirePvp] draft lobby: no character button for {player.character.Id.Entry}, "
                     + "cannot mirror the host");
            return;
        }

        Log.Warn($"[SpirePvp] draft lobby: mirroring the host — taking {player.character.Id.Entry}");

        // **Deferred out of the message handler, and the telemetry is what showed why.** This runs
        // from a postfix on `PlayerChanged`, i.e. inside the client's handling of a lobby message,
        // and `SetLocalCharacter` sends one. The logs had it exactly:
        //
        //     [Client] SetLocalCharacter -> NECROBINDER ... => ALLOWED
        //     [Client] 1=NECROBINDER, 1001(me)=NECROBINDER
        //     [Host]   1(me)=NECROBINDER, 1001=IRONCLAD
        //
        // Allowed, applied locally, and never seen by the host — a send made from inside a receive
        // does not survive. Four fixes argued about *whether* the change happened; none of them
        // could see that it happened and went nowhere, which is the one thing the instrumentation
        // was added to answer.
        //
        // `CallDeferred` puts it at the end of the frame, outside the handler, where it is an
        // ordinary send. `_mirroring` is set inside the deferred call rather than around the
        // scheduling, or it would be false again by the time the lock is consulted.
        CharacterModel character = player.character;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(button))
            {
                return;
            }

            _mirroring = true;
            try
            {
                screen.SelectCharacter(button, character);
            }
            catch (Exception e)
            {
                Log.Error($"[SpirePvp] draft lobby: mirror failed: {e}");
            }
            finally
            {
                _mirroring = false;
            }
        }).CallDeferred();
    }
}

/// <summary>
/// In a draft lobby the client cannot change its character at all — blocked at the sync point.
///
/// **The first version blocked `NCustomRunScreen.SelectCharacter` and that was the wrong
/// chokepoint.** It is the UI path, so the client's *screen* obeyed while the host's lobby record
/// did not, and the run is seeded from the host's record. Measured 2026-08-14: the client showed
/// Necrobinder, the host's remote-player panel showed Regent, the run was created as
/// `NECROBINDER and REGENT`, and `DuelDraft.Begin` refused — which is why "the draft overlay didn't
/// come up" and the "distracting visual bug" turned out to be the same fault. The host's panel was
/// telling the truth.
///
/// `StartRunLobby.SetLocalCharacter` is the real one: it changes the local record *and* sends
/// `LobbyPlayerChangedCharacterMessage`, so it is the single place a character choice becomes real
/// to anyone else. Blocking here cannot be routed around by a path that reaches the lobby some
/// other way.
///
/// Same lesson as `CombatState.GetOpponentsOf` over `HittableEnemies`: patch the chokepoint that
/// carries the decision, not the surface that happens to be in front of it.
///
/// `_mirroring` lets our own assignment through, and it is the only thing that can move a draft
/// client's character.
/// </summary>
[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.SetLocalCharacter))]
public static class DuelDraftCharacterLockPatch
{
    public static bool Prefix(StartRunLobby __instance, CharacterModel character)
    {
        bool mirroring = DuelDraftMirrorPatch.IsMirroring;
        bool client = __instance.NetService.Type == NetGameType.Client;
        bool draft = DuelDraftMirrorPatch.DraftLobbyActive;
        bool allowed = mirroring || !client || !draft;

        // **Random in a draft is rolled at the screen, not here** — see `DuelDraftRandomRollPatch`.
        // This method is the lobby's, and the lobby has no buttons: intercepting the roll at this
        // level set the character correctly and left the *marker* sitting on Random, because
        // nothing had told the screen which button was now selected. Reported 2026-08-14: "host
        // indicator on random and client indicator on whatever's actually getting picked".

        return allowed;
    }
}

/// <summary>
/// The relic round takes the click itself, exactly as the card round does.
///
/// `NChooseARelicSelection.SelectHolder` completes the screen and hands the relic back through
/// `RelicsSelected()`. That is right for a boss reward — one choice, then the screen is done — and
/// wrong for a draft, where the same screen has to survive the opponent's turn and where the host
/// decides whether a pick is legal at all.
///
/// So the click becomes a request and vanilla's completion never runs. The screen is rebuilt from
/// the survivors when the host's next state arrives, which is also what makes a pick visible to
/// both players.
///
/// A string target for the same reason as the card one: `SelectHolder` is private, and while the
/// publicizer exposes private members the parameter type keeps `nameof` from being usable here
/// without pulling in the holder type. Listed with the others in HANDOFF.
/// </summary>
[HarmonyPatch(typeof(NChooseARelicSelection), "SelectHolder")]
public static class DuelDraftRelicPatch
{
    public static bool Prefix(NRelicBasicHolder relicHolder)
    {
        if (!DuelDraft.IsDraftRun)
        {
            return true;
        }

        RelicModel? model = relicHolder?.Relic?.Model;
        if (model != null)
        {
            DuelDraft.SubmitRelicPick(model);
        }

        return false;
    }
}

/// <summary>
/// Makes a remote player's character marker appear when vanilla's reference comparison misses it.
///
/// Reported 2026-08-14: *"host indicator isn't showing at all on client side in lobby select."*
///
/// `NCustomRunScreen.RefreshButtonSelectionForPlayer` decides which button carries a remote
/// player's marker with
///
///     else if (player.character == item.Character)
///
/// — **reference equality between `CharacterModel` instances**. That holds whenever both sides are
/// the same canonical model out of `ModelDb`, and fails the moment one of them is not: a mutable
/// copy, or an instance rebuilt from a lobby message, is a different object with the same identity.
/// This is the same trap `DuelHostFlow` documents for `ModifierModel.IsEquivalent`, where a
/// canonical preset silently matched no tickbox and the lobby came up empty with no error anywhere.
///
/// So this postfix asks the question vanilla meant: **compare by `Id`.** It only ever *adds* a
/// marker, and only when the reference pass left the player without one, so a lobby where vanilla
/// already worked is untouched.
///
/// It also logs what it saw, because the failure mode is an absent marker and an absent log line —
/// the combination this project has repeatedly mistaken for a patch that never applied.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen), "RefreshButtonSelectionForPlayer")]
public static class DuelLobbyRemoteMarkerPatch
{
    public static void Postfix(NCustomRunScreen __instance, StartRunLobbyPlayer player)
    {
        StartRunLobby? lobby = __instance._lobby;
        if (lobby == null || player.id == lobby.LocalPlayer.id || player.character == null)
        {
            return;
        }

        List<NCharacterSelectButton> buttons = __instance._charButtonContainer.GetChildren()
            .OfType<NCharacterSelectButton>()
            .ToList();

        NCharacterSelectButton? holder =
            buttons.FirstOrDefault(b => b.RemoteSelectedPlayers.Contains(player.id));

        NCharacterSelectButton? match = buttons.FirstOrDefault(
            b => b._character != null && b._character.Id.Equals(player.character.Id));

        // **Logged whether or not it did anything.** An earlier version returned silently when
        // vanilla had already placed a marker, so a run producing zero lines was indistinguishable
        // between "vanilla placed it every time" and "this never ran" — the ambiguity this project
        // has repeatedly mistaken for a patch that failed to apply.
        Log.Warn($"[SpirePvp] lobby telemetry: remote marker for {player.id} "
                 + $"({player.character.Id.Entry}) — vanilla put it on "
                 + $"{holder?._character?.Id.Entry ?? "NOTHING"}, id match is "
                 + $"{match?._character?.Id.Entry ?? "NOTHING"}");

        if (holder == null)
        {
            match?.OnRemotePlayerSelected(player.id);
        }
    }
}

/// <summary>
/// Rolls Random into a real character at the moment it is clicked, in a draft lobby.
///
/// **Random cannot survive to run start in a draft.** The host's roll resolves *as the run begins* —
///
///     PlayerChanged 1 = SILENT                 (the host's roll, at run start)
///     SetLocalCharacter -> SILENT ... ALLOWED  (the client mirrors it)
///     Player 1001 tried to change character while run was already starting! Ignoring
///
/// — so the mirror can never land, and both peers rolling separately is two rolls rather than one
/// (measured: host IRONCLAD, client SILENT). Refusing Random outright was the first answer; Lucas
/// asked to keep it, which is fair — the option is wanted, only its timing was unusable.
///
/// So the click rolls immediately and the lobby is handed a *real* character, which travels as an
/// ordinary pick the mirror already knows how to follow. By run start there is nothing left to
/// resolve.
///
/// **Patched here rather than at `StartRunLobby.SetLocalCharacter`, and that is the whole fix for
/// the second report.** The lobby has no buttons: rolling at that level set the character and left
/// both indicators wrong — the host's marker stayed on Random while the client's moved to the
/// rolled character. `SelectCharacter` owns the button visuals *and* calls into the lobby, so doing
/// it here keeps the two in step by construction.
///
/// **This prefix is now the backstop rather than the usual path** — see
/// <see cref="DuelDraftRandomClickPatch"/>, which takes the click one level higher because a second
/// Random click never reached this method at all. Kept, and kept logging, so the next log says
/// which of the two paths a click actually travelled.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.SelectCharacter))]
public static class DuelDraftRandomRollPatch
{
    public static bool Prefix(NCustomRunScreen __instance, CharacterModel characterModel)
    {
        // No re-entry guard is needed: the roll goes out through `NCharacterSelectButton.Select`
        // with a real character, so this prefix never sees `RANDOM_CHARACTER` twice.
        if (characterModel == null
            || characterModel.Id.Entry != "RANDOM_CHARACTER"
            || !DuelDraftMirrorPatch.DraftLobbyActive)
        {
            return true;
        }

        Log.Warn("[SpirePvp] draft lobby: Random reached SelectCharacter — the backstop path fired");
        RollAndSelect(__instance);
        return false;
    }

    /// <summary>
    /// Picks a character that is not the one already selected, and puts the lobby on it.
    ///
    /// **The current selection is excluded on purpose.** Lucas asked for Random to be a re-roll you
    /// can lean on — click it again, get someone else — and over five characters a plain roll
    /// repeats often enough to read as the button having failed. Excluding the incumbent makes
    /// every click visibly do something, which is the whole of what the control is for.
    /// </summary>
    public static void RollAndSelect(NCustomRunScreen screen)
    {
        CharacterModel? current = screen._selectedButton?._character;

        List<NCharacterSelectButton> buttons = screen._charButtonContainer.GetChildren()
            .OfType<NCharacterSelectButton>()
            .Where(b => b._character != null
                        && b._character.Id.Entry != "RANDOM_CHARACTER"
                        && !b.IsLocked)
            .ToList();

        List<NCharacterSelectButton> candidates = buttons
            .Where(b => current == null || b._character.Id.Entry != current.Id.Entry)
            .ToList();

        // Falling back to the whole list keeps a one-character lobby working rather than silently
        // refusing: with nobody else to roll, re-picking the incumbent is the honest answer.
        NCharacterSelectButton? rolled = (candidates.Count > 0 ? candidates : buttons)
            .OrderBy(_ => Guid.NewGuid())
            .FirstOrDefault();

        if (rolled?._character == null)
        {
            Log.Warn("[SpirePvp] draft lobby: Random clicked but there is nothing to roll — ignoring");
            return;
        }

        Log.Warn($"[SpirePvp] draft lobby: rolling Random now — {rolled._character.Id.Entry} "
                 + $"(was {current?.Id.Entry ?? "nothing"})");

        // **The roll is shown, not concealed — for now, and deliberately.** Concealing it was
        // attempted three ways in one session and each attempt leaked somewhere else: the marker
        // under the rolled button, the character name in the player list, the portrait behind a
        // dimmed icon. The half-built version was worse than none, because a lobby that hides the
        // name and shows the face is misleading rather than secret.
        //
        // `docs/DRAFT_LOBBY.md` has the requirements and the design that makes concealment work
        // properly — it needs the *lobby* to genuinely hold Random on both peers, with the real
        // character decided at run creation, rather than a display layer over a resolved value.

        // **`SelectCharacter`, not `rolled.Select()`, and this was measured rather than reasoned.**
        // Calling `Select()` is the tidier-looking choice — it is vanilla's own path, and it is the
        // only place `_isSelected = true` is set, which is what the pulsing outline and the button
        // saturation read. It was tried on 2026-08-17 and **made the control worse**: repeat clicks
        // went from six rolls in six presses to one roll from several, so something downstream of a
        // real `Select()` stops the next click reaching this patch at all.
        //
        // What is left is the version with the better measurement. Every Random click rolls, the
        // lobby record changes, and both players' icons follow it — only the button's own
        // highlight does not move. See `docs/DRAFT_LOBBY.md`: this control has now had three
        // attempts and is deliberately parked rather than given a fourth.
        screen.SelectCharacter(rolled, rolled._character);
    }
}

/// <summary>
/// Takes the Random button's click at the button, so a second click re-rolls.
///
/// **Reported 2026-08-17: you have to click Random, then a real character, before Random works
/// again.** The log says why, and says it by *absence* — a second Random click produces no
/// `rolling Random now` line at all, so <see cref="DuelDraftRandomRollPatch"/> never ran:
///
///     draft lobby: rolling Random now — REGENT      (clicked Random)
///     lobby telemetry: PlayerChanged 1 = DEFECT     (clicked Defect by hand — no roll line)
///     draft lobby: rolling Random now — NECROBINDER (clicked Random, works again)
///
/// `NCharacterSelectButton.Select` opens with `if (!_isSelected)`, so the click is swallowed one
/// level above `SelectCharacter` and nothing downstream can see it. Exactly which term leaves the
/// Random button believing it is still selected does not matter, and chasing it would be the
/// fourth display-layer fix in a file that already has a document explaining why those fail: the
/// gate is above us, so take the click above the gate.
///
/// Same shape as `CombatState.GetOpponentsOf` over `HittableEnemies`, and as moving the character
/// lock down from `SelectCharacter` to `SetLocalCharacter` — patch the point that actually carries
/// the decision, not the one that happens to be nearby.
///
/// Guarded on `IsRandom` and a live draft lobby, so every other character button and every
/// non-draft screen keeps vanilla's behaviour untouched. The delegate cast is the second guard:
/// `NCharacterSelectScreen` builds these buttons too and has its own dedicated random button.
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectButton), nameof(NCharacterSelectButton.Select))]
public static class DuelDraftRandomClickPatch
{
    public static bool Prefix(NCharacterSelectButton __instance)
    {
        if (!__instance.IsRandom
            || __instance.IsLocked
            || !DuelDraftMirrorPatch.DraftLobbyActive
            || __instance._delegate is not NCustomRunScreen screen)
        {
            return true;
        }

        DuelDraftRandomRollPatch.RollAndSelect(screen);
        return false;
    }
}

