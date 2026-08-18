using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace SpirePvp.Duel;

/// <summary>
/// The line under the result banner: several phrasings per ending, one picked at random.
///
/// **The point is variety, and the constraint is that it must not cost accuracy.** A single flat
/// sentence per outcome is what the screen had first, and it was wrong in a specific way worth
/// remembering: it read every ending as an HP finish, so an agreed draw claimed time had run out
/// and a race resignation congratulated the survivor on a duel that was never fought. That is the
/// same trap `DuelClockService` and `DuelFlag` both hit — asking a question that merely
/// *correlates* with the one you mean. So the phrase sets are keyed on **outcome and reason
/// together**, and a set only ever contains lines that are true of that exact ending. Adding
/// personality by collapsing the reasons back together would reintroduce the bug it just fixed.
///
/// **The phrases are loc data, not code**, so a set grows by editing JSON. They ride in
/// `game_over_screen.json` — an existing vanilla table, because `LocManager` merges a mod's
/// tables only into tables vanilla already has, by filename, and a `spirepvp.json` would never be
/// read at all. Entries are numbered from 1 and probed until one is missing, so the count is
/// whatever is in the file and nothing declares it twice.
///
/// **Every ending keeps a hardcoded fallback line**, which is not belt-and-braces: the `.pck` is
/// the single most commonly stale thing in this project, and a stale one makes every loc lookup
/// return the raw key. Without a fallback the climax of a match would read
/// `SPIREPVP_QUOTE.wonHp.1`. A missing key has already wrecked one result screen here
/// (`DUEL_ENCOUNTER.title`, which threw inside `InitializeBannerAndQuote` and left the daily-run
/// leaderboard drawn over everything), so degrading to a plain true sentence is the behaviour to
/// want.
///
/// Picked with an ordinary <see cref="Random"/> and deliberately **not** the run RNG. Nothing
/// downstream consumes this — the match is over, the sim is finished, and the two players are
/// looking at different sets anyway since one won and one lost. Drawing from a synchronised
/// stream would imply a determinism requirement that does not exist.
/// </summary>
public static class DuelResultQuotes
{
    private const string Table = "game_over_screen";
    private const string Prefix = "SPIREPVP_QUOTE.";

    /// <summary>
    /// Stops the probe loop if `Exists` ever answers yes forever. Far above any set worth
    /// writing, so it never truncates a real one.
    /// </summary>
    private const int MaxPhrases = 32;

    /// <summary>
    /// Returns the line to show for this ending, and where it came from.
    ///
    /// `Source` names the entry that was picked (`drawAgreed 2/3`) or says outright that nothing
    /// was found — which is the difference between "the writing is bad" and "the `.pck` is
    /// stale", a question the screen alone cannot answer and the one most likely to be asked
    /// about this feature.
    /// </summary>
    public static (string Line, string Source) Pick(DuelOutcome outcome, int reason)
    {
        (string stem, string fallback) = Entry(outcome, reason);

        List<string> phrases = Load(stem);
        if (phrases.Count == 0)
        {
            return (fallback, $"{stem} fallback — no loc entries, so the .pck is stale");
        }

        int index = Random.Shared.Next(phrases.Count);
        return (phrases[index], $"{stem} {index + 1}/{phrases.Count}");
    }

    /// <summary>
    /// Which set of phrasings this ending draws from, and the one line guaranteed to be true of
    /// it even with no loc table at all.
    ///
    /// The fallbacks are the flat sentences the screen used before there were sets, and they are
    /// deliberately **not** repeated in any set. That costs nothing — nobody should see them —
    /// and buys a real property: a fallback appearing on screen is recognisable as the degraded
    /// path rather than passing for one of the written lines, so a stale `.pck` announces itself
    /// to a player and not only to the log.
    /// </summary>
    private static (string Stem, string Fallback) Entry(DuelOutcome outcome, int reason)
    {
        switch (outcome)
        {
            case DuelOutcome.Won:
                return reason switch
                {
                    DuelEndReason.Flag => ("wonFlag", "Your opponent ran out of time."),
                    DuelEndReason.Resign => ("wonResign", "Your opponent resigned."),
                    DuelEndReason.RaceDeath =>
                        ("wonRaceDeath", "Your opponent died before reaching the arena."),

                    // Only the winner ever reads this one: the player who dropped is, by
                    // definition, not looking at a screen we drew.
                    DuelEndReason.Disconnect =>
                        ("wonDisconnect", "Your opponent disconnected."),

                    _ => ("wonHp", "You won the duel.")
                };

            // A switch rather than the `== AgreedDraw ? … : raceExpired` this used to be. That
            // read correctly while there were exactly two draws and silently mislabels the third:
            // a voided match would have been announced as a race timeout, which is a specific
            // false claim about how the match ended. Same trap as every other predicate here —
            // "not the one I named" is not a condition.
            case DuelOutcome.Draw:
                return reason switch
                {
                    DuelEndReason.AgreedDraw => ("drawAgreed", "You agreed to a draw."),
                    DuelEndReason.Desync => ("drawDesync", "The match desynced. No result."),

                    // **A disconnect can now draw, and without this case it read as a race
                    // timeout** — the exact false claim the note above warns about, arriving as
                    // the third draw reason within a day of the note being written. A drop is
                    // drawn whenever the two machines have no agreed board to read from: outside
                    // the duel there is no shared HP, and inside it the two can be level. See
                    // `DuelDisconnect.DecideAfterSilence`.
                    DuelEndReason.Disconnect =>
                        ("drawDisconnect", "The connection died with nobody ahead."),

                    _ => ("drawRaceExpired",
                          "Time ran out before either of you reached the arena.")
                };

            default:
                return reason switch
                {
                    DuelEndReason.Flag => ("lostFlag", "You ran out of time."),
                    DuelEndReason.Resign => ("lostResign", "You resigned."),
                    DuelEndReason.RaceDeath =>
                        ("lostRaceDeath", "You died before reaching the arena."),

                    // **Losing to a disconnect is new and is not `lostHp`.** Until 2026-08-18 a
                    // drop only ever won you the match, because whoever remained was awarded it;
                    // now an accidental drop is settled on the HP both machines agreed on, so the
                    // player who was behind loses one. Falling through to `lostHp` would tell them
                    // their opponent won the duel, which is the wrong story about a duel that was
                    // never finished.
                    DuelEndReason.Disconnect =>
                        ("lostDisconnect", "The connection died while you were behind."),

                    _ => ("lostHp", "Your opponent won the duel.")
                };
        }
    }

    /// <summary>
    /// Collects `SPIREPVP_QUOTE.&lt;stem&gt;.1`, `.2`, … until one is missing.
    ///
    /// Stopping at the first gap rather than scanning to <see cref="MaxPhrases"/> means a
    /// mis-numbered file loses its tail quietly instead of throwing, which is the right trade for
    /// a cosmetic string — but it is also why the numbering has to stay contiguous when a phrase
    /// is added.
    /// </summary>
    private static List<string> Load(string stem)
    {
        List<string> phrases = new List<string>();

        for (int i = 1; i <= MaxPhrases; i++)
        {
            LocString line = new LocString(Table, $"{Prefix}{stem}.{i}");
            if (!line.Exists())
            {
                break;
            }

            phrases.Add(line.GetFormattedText());
        }

        return phrases;
    }
}
