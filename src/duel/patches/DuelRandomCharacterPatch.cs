using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Puts Random back on the character row in a duel lobby.
///
/// **Missing rather than extra**, and vanilla refuses it deliberately:
/// `NCustomRunScreen.PlayerChanged` throws `"Random character is not currently allowed in custom!"`
/// on a random resolution, and a duel lobby *is* a Custom lobby. Scoped 2026-08-12 and built
/// 2026-08-14.
///
/// **The mechanism needs nothing built, which is what makes this small.**
/// `StartRunLobby.BeginRunLocally` already resolves a `RandomCharacter` into a real one using an
/// `Rng` seeded from the run seed — so both clients resolve to the *same* character with no message
/// crossing the wire, which is exactly the property a PvP lobby needs and the reason this is worth
/// having rather than dangerous.
///
/// Two halves were missing, both on the screen:
///
/// 1. **No button to press.** `ModelDb.AllCharacters` holds the five real characters and not
///    `RandomCharacter`, so `InitCharacterButtons` never builds one — `NCharacterSelectScreen` has a
///    dedicated `_randomCharacterButton` in its own scene instead. So the button is built here from
///    the same `char_select_button.tscn` vanilla uses, and the focus-neighbour chain is rewired
///    afterwards: vanilla wires it by child index in the same method, and would leave a button
///    appended later unreachable by controller.
/// 2. **The resolution callback throws.** Prefixed below.
///
/// **Built always, shown only in a duel.** The screen initialises its buttons once, before the
/// lobby's modifiers are known, so "is this a duel" cannot be answered at that moment — and this
/// mod's standing rule is to be inert outside a PvP match. `DuelLobbyPanel.SetRandomVisible` is
/// called from the same refresh that titles the screen, where the answer is known and is
/// re-answered every time it changes.
///
/// **Unplayed in the one place that matters most:** whether `RefreshButtonSelectionForPlayer` draws
/// a *remote* player's Random pick sensibly, and whether resolution reaches run creation in the
/// right order — `DuelMatch.OnRunCreated` and the RNG mirroring both read characters, and the
/// resolution happens inside `BeginRunLocally`. Those are two-client questions and a log will not
/// answer them.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.InitCharacterButtons))]
public static class DuelRandomCharacterButtonPatch
{
    /// <summary>The name the button is given, so the refresh can find it again.</summary>
    public const string ButtonName = "SpirePvpRandomCharacterButton";

    public static void Postfix(NCustomRunScreen __instance)
    {
        try
        {
            CharacterModel random = ModelDb.Character<RandomCharacter>();

            NCharacterSelectButton button = PreloadManager.Cache
                .GetScene("res://scenes/screens/char_select/char_select_button.tscn")
                .Instantiate<NCharacterSelectButton>(PackedScene.GenEditState.Disabled);

            button.Name = ButtonName;
            __instance._charButtonContainer.AddChildSafely(button);
            button.Init(random, __instance);
            button.Visible = false;

            // Vanilla wires these by child index inside the method we just ran, so a button appended
            // after it is skipped. Redone over every child rather than only the new one: the
            // last real character's right-neighbour pointed at itself and now has somewhere to go.
            Control container = __instance._charButtonContainer;
            for (int i = 0; i < container.GetChildCount(); i++)
            {
                Control child = container.GetChild<Control>(i);
                child.FocusNeighborLeft = i > 0
                    ? container.GetChild<Control>(i - 1).GetPath()
                    : child.GetPath();
                child.FocusNeighborRight = i < container.GetChildCount() - 1
                    ? container.GetChild<Control>(i + 1).GetPath()
                    : child.GetPath();
            }

            Log.Info("[SpirePvp] duel lobby: random character button added (hidden until a duel)");
        }
        catch (Exception e)
        {
            // A lobby that cannot offer Random is worse than one that throws while building itself.
            Log.Error($"[SpirePvp] duel lobby: could not add the random character button — {e.Message}");
        }
    }
}

/// <summary>
/// Stops vanilla refusing a random resolution while the lobby is a duel.
///
/// The throw is a policy rather than a safeguard — the resolution path underneath it is seeded from
/// the run seed and is identical on both clients. Gated on the lobby being a duel rather than
/// removed: vanilla disabled this for plain Custom deliberately, and this mod does not get to
/// overrule that for runs it has nothing to do with.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.PlayerChanged))]
public static class DuelRandomCharacterAllowedPatch
{
    public static bool Prefix(NCustomRunScreen __instance, StartRunLobbyPlayer player,
        bool isRandomCharacterResolution)
    {
        if (!isRandomCharacterResolution)
        {
            return true;
        }

        bool isDuel = __instance.Lobby != null
                      && DuelMatch.HasTurnModel(__instance.Lobby.Modifiers);

        if (!isDuel)
        {
            return true;
        }

        // Replaces the method rather than merely skipping the throw: everything after it is the two
        // lines below, and letting vanilla run would hit the exception first.
        Log.Warn("[SpirePvp] duel lobby: allowing a random character resolution");
        __instance._remotePlayerContainer.OnPlayerChanged(player);
        __instance.RefreshButtonSelectionForPlayer(player);
        return false;
    }
}
