using System.Linq;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Mcp;

public static partial class McpMod
{
    // LastingCandy.IsAllowed reads every player's own UnlockState, while the
    // multiplayer run's shared UnlockState reports the party maximum.
    private static bool HasFirstRunIronclad(RunState runState)
    {
        return runState.Players.Any(player => player?.Character is Ironclad
            && player.UnlockState?.NumberOfRuns == 0);
    }
}
