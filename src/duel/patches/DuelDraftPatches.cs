using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using SpirePvp.Modifiers;
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
    public static void AfterConnected(NCustomRunScreen __instance, StartRunLobbyPlayer player) =>
        MirrorHost(__instance, player);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NCustomRunScreen.PlayerChanged))]
    public static void AfterChanged(NCustomRunScreen __instance, StartRunLobbyPlayer player) =>
        MirrorHost(__instance, player);

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

        DumpLobby(lobby);

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
    private static void DumpLobby(StartRunLobby lobby)
    {
        string state = string.Join(", ", lobby.Players.Select(p =>
            $"{p.id}{(p.id == lobby.LocalPlayer.id ? "(me)" : "")}="
            + $"{p.character?.Id.Entry ?? "none"}"));

        if (state == _lastDump)
        {
            return;
        }

        _lastDump = state;
        Log.Warn($"[SpirePvp] draft lobby [{lobby.NetService.Type}] draft={DraftLobbyActive} "
                 + $"players: {state}  <- diff this against the other client's line");
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

        // **Never mirror the random-character placeholder.** It is not a character — it resolves to
        // one later — so copying it puts the client on a placeholder that will resolve
        // independently, i.e. to a different character, which is the opposite of a mirror. Seen in
        // the client's log as `taking RANDOM_CHARACTER` before it took the real one.
        if (player.character.Id.Entry == "RANDOM_CHARACTER")
        {
            Log.Info("[SpirePvp] draft lobby: host is on random — waiting for it to resolve");
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
        _mirroring = true;
        try
        {
            screen.SelectCharacter(button, player.character);
        }
        finally
        {
            _mirroring = false;
        }
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
    public static bool Prefix(StartRunLobby __instance)
    {
        if (DuelDraftMirrorPatch.IsMirroring)
        {
            return true;
        }

        if (__instance.NetService.Type != NetGameType.Client
            || !DuelDraftMirrorPatch.DraftLobbyActive)
        {
            return true;
        }

        Log.Info("[SpirePvp] draft lobby: ignoring a character change — a draft mirrors the host");
        return false;
    }
}
