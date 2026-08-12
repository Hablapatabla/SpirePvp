using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using SpirePvp.Duel;

namespace SpirePvp.Race;

/// <summary>
/// M5 spike entry: <c>race on</c> / <c>race off</c>.
///
/// **Local-only, for the same reason DuelConsoleCmd is** (see its comment for the measurement).
/// A networked console command is enqueued into the shared action stream, and each side assigns
/// action ids from its own counter — so any asymmetry in which copies get executed renumbers one
/// side permanently. `race on` has exactly the shape that produces that asymmetry, since it changes
/// whether peer action traffic is being dropped at all.
///
/// The original reasoning was that both clients must stop synchronizing at the same instant, or one
/// would travel alone while the other waited on a vote that never comes. That is still true and is
/// still satisfied: both players type it, which is what a two-player debug command was always going
/// to require.
/// </summary>
public class RaceConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "race";

    public override string Args => "'on'|'off'";

    public override string Description =>
        "SpirePvp: decouple the two clients so each traverses the shared map on its own " +
        "(M5 spike). 'off' restores normal co-op party movement.";

    public override bool IsNetworked => false;

    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "on";

        if (mode == "off")
        {
            RaceCoordinator.EndRace();
            DuelSession.Reset();
            return new CmdResult(success: true, "Race mode off — party movement restored.");
        }

        if (mode != "on")
        {
            return new CmdResult(success: false, "Usage: race on | race off");
        }

        DuelSession.ActivateRace();
        RaceCoordinator.BeginRace();
        Log.Warn("[SpirePvp] race mode ON");
        RaceCoordinator.LogSeedDiagnostics();
        return new CmdResult(success: true,
            "Race mode on — pick map nodes independently. Watch the log for state divergence errors.");
    }
}
