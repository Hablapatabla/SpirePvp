using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Holds animation speed level between the two players for the length of a duel.
///
/// **Fast Mode is a personal preference that changes how long everything takes, and in a duel that
/// is an advantage.** Vanilla sizes almost every wait through it — `Cmd.Wait` skips outright at
/// `Instant`, `Cmd.CustomScaledWait` and `CardPileCmd` pick shorter timings at `Fast` — so a player
/// on Instant watches the board settle while their opponent is still watching a card fly. In a
/// real-time duel that is reaction time bought from a settings screen, and in any duel it decides
/// how long the loser of an exchange spends unable to read it. Raised by Lucas 2026-08-12: "maybe
/// fast mode should be fixed across host and client? otherwise there's an advantage?"
///
/// So for the duration of the duel both clients read `Normal`, whatever either of them prefers.
/// Normal rather than Fast because the same report asked for a *feelable* delay — the pacing exists
/// to make a play readable, and the faster settings are exactly what was reported as making plays
/// feel instantaneous.
///
/// **The getter, not the stored value.** Writing the preference would mean writing it back
/// afterwards and getting that right through every route out of a duel — a disconnect, a
/// resignation, a crash — and a mod that leaves someone's settings changed is a bad mod. Reading
/// through a patch is reversible by construction: stop returning the override and the player's own
/// setting is simply there again.
///
/// Two things this deliberately does not do. It does not touch the **race**, though the same
/// argument applies to a timed race — that is a bigger imposition on how a whole act feels and
/// wants its own decision rather than being smuggled in here. And it does not stop the settings
/// screen showing `Normal` if someone opens it mid-duel; the pref underneath is untouched, and the
/// screen is not where a duel is decided.
/// </summary>
[HarmonyPatch(typeof(PrefsSave), nameof(PrefsSave.FastMode), MethodType.Getter)]
public static class DuelFastModePatch
{
    public static void Postfix(ref FastModeType __result)
    {
        if (DuelSession.IsDuelActive)
        {
            __result = FastModeType.Normal;
        }
    }
}
