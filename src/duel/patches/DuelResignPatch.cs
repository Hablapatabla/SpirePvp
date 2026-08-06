using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Turns "abandon run" into "resign" for a PvP match.
///
/// `RunManager.Abandon` is the single chokepoint for every abandon route — the pause menu's
/// Give Up button reaches it through `NAbandonRunConfirmPopup`, so the confirmation dialog
/// still guards against a misclick and we inherit it for free.
///
/// The prefix skips vanilla entirely when it handles the resignation, because vanilla's next
/// two steps are `RunAbandonedMessage` and `NetService.Disconnect`. Letting those run would
/// tear down the result screen we just put up, and would tell the opponent "the host abandoned
/// the game" instead of "you won" — which is the behaviour being removed. See `DuelResign` for
/// why the connection is deliberately left up.
///
/// `Abandon` returns `void`, so skipping it needs no `__result` — but that is a fact worth
/// stating rather than assuming, because a skipping prefix on an `async Task` that omits
/// `__result` is this project's most expensive recurring bug (HANDOFF, "things that will bite
/// you"). Checked against the decompiled signature: `public void Abandon()`.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Abandon))]
public static class DuelResignPatch
{
    public static bool Prefix()
    {
        // Resign returns false when there is nothing to resign — an ordinary co-op or solo run,
        // or a match already decided — and then vanilla's abandon runs untouched. Guarding on
        // the condition rather than on the caller is deliberate: there is more than one way to
        // reach Abandon, and a new one should behave correctly without being enumerated here.
        return !DuelResign.Resign();
    }
}
