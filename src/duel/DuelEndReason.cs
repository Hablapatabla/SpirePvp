namespace SpirePvp.Duel;

/// <summary>
/// Why a match ended, as carried on <see cref="Net.DuelResultMessage"/>.
///
/// These live here rather than as private consts next to their first use because they are a
/// wire format: the host writes one and every client switches on it, so a number that means
/// two things is a match that ends differently on the two screens.
///
/// They had already drifted. `DuelResultMessage.reason` was documented as
/// "0 = HP, 1 = flag, 2 = concede, 3 = disconnect" while `DuelFlag` used 2 for a race-clock
/// expiry — so the only written-down meaning of 2 was wrong, and the one place that read it
/// happened to agree with the code rather than the comment. Nothing broke, because resigning
/// did not exist yet and nothing ever sent a 2 meaning concede. Adding resign is exactly the
/// change that would have made those two collide.
/// </summary>
public static class DuelEndReason
{
    /// <summary>A duelist died. The ordinary way a duel ends.</summary>
    public const int Hp = 0;

    /// <summary>A duel clock hit zero — sudden death (DESIGN §9).</summary>
    public const int Flag = 1;

    /// <summary>
    /// The race deadline passed with neither player at the arena. Always a draw: both race
    /// banks start together and never pause, so they empty in the same tick.
    /// </summary>
    public const int RaceExpired = 2;

    /// <summary>
    /// A player resigned — tipping the king over. Abandoning the run during a PvP match is
    /// this, not a disconnect: the abandoning player loses and the other wins.
    /// </summary>
    public const int Resign = 3;

    /// <summary>Both players agreed to a draw.</summary>
    public const int AgreedDraw = 4;

    /// <summary>
    /// The opponent's connection went away and did not come back — a loss for whoever dropped.
    ///
    /// **Appended rather than inserted**, like every code here: these are positional on the wire
    /// and renumbering one silently changes what an older build hears.
    ///
    /// Distinct from <see cref="Resign"/> on purpose even though both end "they stopped playing".
    /// A resignation is a decision and the connection deliberately stays up; a disconnect is an
    /// accident, arrives through an entirely different route, and is the one ending the loser may
    /// never see — so the wording, and any future reconnect window, has to be able to tell them
    /// apart.
    /// </summary>
    public const int Disconnect = 5;
}
