using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Refuses to open the map once a run has no map left to walk.
///
/// **Reported 2026-08-17: the map was opened from the top bar during the deck review and there was
/// no way back.** Not a stuck screen — a hidden one. `NOverlayStack.ShowOverlays` reads
///
///     if (overlayScreen != null &amp;&amp; !NMapScreen.Instance.IsOpen)
///
/// so vanilla deliberately hides *every* overlay for as long as the map is open. The deck review
/// was alive and correct underneath the whole time. `DuelDraft.EnsureScreen` already learned this
/// the same way and closes the map on the run timer; the deck review has no such tick.
///
/// **So z-order is not the fix, and could not have been.** The review is not behind the map, it is
/// switched off by it — a lower `ZIndex` changes nothing when `Visible` is false. Lucas offered
/// both that and "disable opening map entirely"; the second is the only one that addresses the
/// mechanism, and it is also the honest description of the state: past the race there is nowhere on
/// that map to go, so a map screen is a dead end however it is layered.
///
/// # Two windows, and the split is deliberately uneven
///
/// - **`DuelSession.IsDuelActive` — refuse every open.** This is the deck review and the duel. The
///   phase opens at the deck review rather than at arena entry (see `DuelRendezvous`), which is
///   exactly the window the report landed in, and it covers both formats. Nothing should be opening
///   a map here at all: the arena is entered by `DuelArena`, not by travelling to a map point, and
///   anything that did reopen it would hide the duel the same way it hid the review.
///
/// - **A draft run — refuse every open, including the run's own start-up.** With `StartedWithNeow`
///   cleared the run enters a plain `NMapRoom`, whose `_Ready` calls `Open()`; the draft used to
///   ride over that and `DuelDraft.EnsureScreen` closed the map a tick later. Asked for 2026-08-17:
///   *"ideally the game just loads straight into the empty rest site background we have and then the
///   draft overlay comes down over it."* Refusing the open is how that happens — a map that never
///   opens needs no closing, and `NOverlayStack.ShowOverlays` gates on `IsOpen`, which now simply
///   stays false.
///
/// **Checked rather than assumed, because this changes the one path in draft mode that is
/// playtested.** `NMapRoom._Ready` uses the return value for exactly one call,
/// `SetTravelEnabled(true)`, which sets a flag on a screen nobody is looking at; the two lines
/// around it — disabling the top bar's map button and adding the act banner — do not touch it. So
/// the refusal is inert beyond not drawing a map. `ReopenMap`, on capstone close, is refused the
/// same way and for the same reason.
///
/// `isOpenedFromTopBar` is still read, but now only for the trace: it is vanilla's own way of
/// saying a *player* asked. `NTopBarMapButton` is the only caller in the game that passes `true`,
/// and every room passes `false`.
///
/// Everything else — normal play, the race phase, a run with no duel in it — passes straight
/// through, so the mod stays inert where Lucas's invariant requires it.
///
/// `Open` returns the screen it opened, so a refusal returns the instance rather than null: callers
/// use it fluently, and a null here would trade a hidden screen for a crash.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class DuelMapLockPatch
{
    public static bool Prefix(NMapScreen __instance, bool isOpenedFromTopBar, ref NMapScreen __result)
    {
        bool duel = DuelSession.IsDuelActive;
        bool draft = DuelDraft.IsDraftRun;

        // **Traced whether or not it is refused, and that is the point.** Tested 2026-08-17: the
        // map button did nothing during the deck review and this patch logged nothing either, so
        // all that was observed is a button that happened to be inert — `OnRelease` never reached
        // `Open`. That is not the same as the trap being closed, and the difference is invisible
        // without a line here. Player-initiated opens only, so a race's room-by-room map opens stay
        // out of the log.
        if (isOpenedFromTopBar && DuelSession.Phase != DuelPhase.Inactive)
        {
            Log.Info($"[SpirePvp] map: top-bar open reached NMapScreen.Open (duel={duel} "
                     + $"draft={draft} alreadyOpen={__instance.IsOpen})");
        }

        if (!duel && !draft)
        {
            return true;
        }

        // Already open is vanilla's own early return, and closing it is not this patch's business —
        // `DuelDraft` does that deliberately on its own tick.
        if (__instance.IsOpen)
        {
            return true;
        }

        Log.Info($"[SpirePvp] map: refused to open (duel={duel} draft={draft} "
                 + $"topBar={isOpenedFromTopBar}) — this run has no map left to walk, and an open "
                 + "map hides every overlay including the deck review");

        __result = __instance;
        return false;
    }
}
