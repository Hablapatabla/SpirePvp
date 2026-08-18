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

        foreach (StartRunLobbyPlayer player in lobby.Players)
        {
            if (player.id != lobby.LocalPlayer.id)
            {
                MirrorHost(screen, player);
            }
        }
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
    /// <summary>Set while this patch is re-entering `SetLocalCharacter` with a rolled character.</summary>
    private static bool _mirroringForRandom;

    public static bool Prefix(StartRunLobby __instance, CharacterModel character)
    {
        bool mirroring = DuelDraftMirrorPatch.IsMirroring || _mirroringForRandom;
        bool client = __instance.NetService.Type == NetGameType.Client;
        bool draft = DuelDraftMirrorPatch.DraftLobbyActive;
        bool allowed = mirroring || !client || !draft;

        // **Random is kept for drafts, but resolved when it is picked rather than at run start.**
        //
        // Left alone it cannot be mirrored at all: the host's roll lands *as the run begins* —
        //
        //     PlayerChanged 1 = SILENT                 (the host's roll, at run start)
        //     SetLocalCharacter -> SILENT ... ALLOWED  (the client mirrors it)
        //     Player 1001 tried to change character while run was already starting! Ignoring
        //
        // — and both peers rolling separately is two rolls, measured the same evening (host
        // IRONCLAD, client SILENT). Refusing it outright was the first answer and Lucas asked to
        // keep the option, which is fair: the button is wanted, only its timing is unusable.
        //
        // So a draft turns Random into "roll now": the host picks a real character immediately and
        // that travels as an ordinary pick, which the mirror already handles. The player gets what
        // they asked for — a character they did not choose — and the race disappears entirely,
        // because by run start there is nothing left to resolve.
        //
        // Client-side it stays refused, but the button is hidden there anyway by the lock.
        if (draft && !mirroring && character != null && character.Id.Entry == "RANDOM_CHARACTER")
        {
            CharacterModel? rolled = ModelDb.AllCharacters
                .Where(c => c.Id.Entry != "RANDOM_CHARACTER")
                .OrderBy(_ => Guid.NewGuid())
                .FirstOrDefault();

            if (rolled == null)
            {
                Log.Warn("[SpirePvp] draft lobby: Random picked but no character to roll — refusing");
                return false;
            }

            Log.Warn($"[SpirePvp] draft lobby: rolling Random now — {rolled.Id.Entry}. A draft "
                     + "cannot use the run-start resolution, so it happens at the click instead.");

            // Re-entered through the same method so the roll is an ordinary pick: it syncs, the
            // client mirrors it, and nothing downstream knows Random was involved. `mirroring`
            // carries it past this guard on the way back in.
            _mirroringForRandom = true;
            try
            {
                __instance.SetLocalCharacter(rolled);
            }
            finally
            {
                _mirroringForRandom = false;
            }

            return false;
        }

        // **Every character change, on either peer, allowed or not.** This is the line that answers
        // the question four fixes could not: whether a change the client makes ever becomes real.
        // It prints on the way *in*, so a change that is allowed here and still missing from the
        // host's record is a delivery problem rather than a lobby one.
        Log.Warn($"[SpirePvp] lobby telemetry: SetLocalCharacter -> {character?.Id.Entry ?? "null"} "
                 + $"[{__instance.NetService.Type}] mirroring={mirroring} draft={draft} "
                 + $"=> {(allowed ? "ALLOWED" : "BLOCKED")}");

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

        // **Logged whether or not it did anything, and the previous version was not.** It returned
        // silently when vanilla had already placed a marker, so a run producing zero lines was
        // indistinguishable between "vanilla placed it every time" and "this never ran" — the exact
        // ambiguity that has repeatedly been mistaken here for a patch that failed to apply. It
        // produced zero lines, the icon was still missing, and that told us nothing.
        Log.Warn($"[SpirePvp] lobby telemetry: remote marker for {player.id} "
                 + $"({player.character.Id.Entry}) — vanilla put it on "
                 + $"{holder?._character?.Id.Entry ?? "NOTHING"}, id match is "
                 + $"{match?._character?.Id.Entry ?? "NOTHING"}, buttons: "
                 + $"{string.Join(",", buttons.Select(b => b._character?.Id.Entry ?? "?"))}");

        if (holder == null)
        {
            match?.OnRemotePlayerSelected(player.id);
        }
    }
}
