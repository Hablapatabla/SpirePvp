using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using SpirePvp.Duel;

namespace SpirePvp.Race;

/// <summary>
/// M5 spike entry: <c>race on</c> / <c>race off</c>.
///
/// Networked for the same reason DuelConsoleCmd is: both clients must stop synchronizing at
/// the same instant. If one side decoupled first it would travel alone while the other was
/// still waiting on a vote that will never come.
/// </summary>
public class RaceConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "race";

    public override string Args => "'on'|'off'";

    public override string Description =>
        "SpirePvp: decouple the two clients so each traverses the shared map on its own " +
        "(M5 spike). 'off' restores normal co-op party movement.";

    public override bool IsNetworked => true;

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
        return new CmdResult(success: true,
            "Race mode on — pick map nodes independently. Watch the log for state divergence errors.");
    }
}
