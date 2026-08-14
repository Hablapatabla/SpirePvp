using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using SpirePvp.Duel.Turns;

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
/// So for the duration of the duel both clients read the same level, whatever either of them
/// prefers.
///
/// **`Fast`, changed from `Normal` on 2026-08-13.** Reported after the first turn-based playtest:
/// *"let's speed up how quick the cards play on turn end, because it's like one every 3 seconds,
/// kinda painfully slow — I turned fast mode on mid-combat and it stayed slow."* The staying-slow
/// half is this patch working as designed; the speed half was this patch overreaching.
///
/// Normal was originally chosen "because the same report asked for a *feelable* delay". That
/// conflated two mechanisms that arrived in the same week. **The feelable delay is `DuelPace`'s
/// beat**, which is a `Cmd.Wait` of the model's own `BeatSeconds` and is *not* scaled by Fast Mode —
/// `Cmd.Wait` does not shorten at `Fast`, it only skips outright at `Instant`. So the readable gap
/// after each play is identical at `Fast` and at `Normal`, and all the pin was buying by choosing
/// `Normal` was slower vanilla animations *inside* that gap. The pacing survives the change; only
/// the card's own flight time shortens.
///
/// **Never `Instant`, and this is the trap in the neighbourhood.** `Cmd.Wait` skips entirely at
/// `Instant`, so pinning there would silently delete `DuelPace`'s beat — the exact unreadable round
/// the beat exists to prevent, arrived at through a setting rather than through a code change.
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
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        // **Turn-based honours the player's own setting; real-time does not.** Lucas, 2026-08-14:
        // "I don't see why it would have any actual gameplay or balance impacts like it might in
        // real time mode."
        //
        // That is right, and it is right for a reason worth writing down rather than assumed. The
        // pin exists because vanilla sizes almost every wait through Fast Mode, so a faster setting
        // buys reaction time — decisive in real time, where both players are acting continuously.
        // In a resolving batch nobody can act at all: the hand is closed and the round is a replay
        // of plays already committed.
        //
        // The one advantage left is that a faster client finishes resolving sooner and starts
        // planning sooner — and that is **self-correcting**, because `DuelClockService` asks the
        // turn model whether you are done deciding: planning reopening locally starts your clock.
        // You get the extra thinking time and you pay for it, which is what a chess clock is for.
        //
        // **`Instant` is still clamped, and that is not a preference.** `Cmd.Wait` returns
        // immediately at `Instant`, so `DuelPace`'s beat would not merely shorten — it would vanish,
        // taking the readable gap between cards with it. A display setting must not delete a
        // mechanic; that rule is what this file was written to enforce and it still holds.
        if (DuelTurnModel.Current is LockInTurnModel)
        {
            if (__result == FastModeType.Instant)
            {
                __result = FastModeType.Fast;
            }

            return;
        }

        __result = FastModeType.Fast;
    }
}
