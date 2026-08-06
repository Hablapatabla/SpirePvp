using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Points the duel badges at artwork that exists.
///
/// `NBadge.Create(id, rarity)` resolves its icon as
/// `ui/game_over_screen/badge_<id.ToLowerInvariant()>.png`, in a vanilla directory a mod cannot
/// write to — so our own ids would resolve to files that are not there. That is not a harmless
/// missing image: `AssetCache` logs a miss, attempts the load, fails, and **never caches the
/// failure**, so every repaint retries a throwing resource lookup. Exactly the mechanism that
/// made the arena's room icon 19 errors a session and a plausible cause of frame hitching
/// (`DuelRoomIconPatch`, same lesson).
///
/// So the ids stay ours — which is what gives the badges duel-specific titles and descriptions
/// out of our own `badges.json` — while the icons map onto vanilla badges whose art already
/// suits the meaning. Borrowing the picture is not a compromise here so much as a correct
/// reading: vanilla's damage-leader art already means "you did the most damage".
///
/// **This is the seam for real art.** When badge artwork ships in the `.pck`, this map changes
/// to point at `res://SpirePvp/...` and nothing else moves.
///
/// The patch is deliberately narrow. `GetImagePath` resolves *every* image in the game, so it
/// matches one exact prefix and returns immediately otherwise — a broad rewrite here would be a
/// spectacular way to break unrelated art.
/// </summary>
[HarmonyPatch(typeof(ImageHelper), nameof(ImageHelper.GetImagePath))]
public static class DuelBadgeIconPatch
{
    private const string BadgePrefix = "ui/game_over_screen/badge_spirepvp_";

    /// <summary>
    /// Our badge id (lowercased, without the shared prefix) → the vanilla badge whose icon we
    /// borrow. Vanilla ids confirmed against `BadgePool.CreateAll`.
    /// </summary>
    private static readonly Dictionary<string, string> IconFor = new Dictionary<string, string>
    {
        ["aggressor"] = "damage_leader",
        ["efficient"] = "speedy",
        ["flawless"] = "perfect",
        ["survivor"] = "healer",
        ["elite_hunter"] = "elite",
        ["prospector"] = "money_money",
        ["minimalist"] = "tiny_deck"
    };

    public static void Postfix(string innerPath, ref string __result)
    {
        if (innerPath == null || !innerPath.StartsWith(BadgePrefix, StringComparison.Ordinal))
        {
            return;
        }

        string key = innerPath
            .Substring(BadgePrefix.Length)
            .Replace(".png", string.Empty);

        if (IconFor.TryGetValue(key, out string? vanilla))
        {
            // Built literally rather than by calling GetImagePath again — that would re-enter
            // this very patch. It would terminate (the rewritten path does not match the
            // prefix), but a patch that recurses through its own target is a trap for whoever
            // edits the prefix next.
            __result = $"res://images/ui/game_over_screen/badge_{vanilla}.png";
        }
    }
}
