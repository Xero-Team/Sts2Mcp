using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Mcp;

public static partial class McpMod
{
    private static readonly FieldInfo s_historyPlayerTurnNumbersField = typeof(CombatHistoryEntry)
        .GetField("_playerTurnNumbers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CombatHistoryEntry).FullName, "_playerTurnNumbers");

    // Emotion Chip queries HappenedLastPlayerTurn after the next owner turn
    // starts. Seed that future query from this owner's current turn number,
    // including damage on the enemy side. HappenedThisTurn would exclude it.
    private static bool DamageNotFullyBlockedThisOwnerTurn(Player player, IEnumerable<DamageReceivedEntry> entries)
    {
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("Owner-turn damage history requires an active player combat state");
        foreach (DamageReceivedEntry entry in entries)
        {
            if (entry.Receiver != player.Creature || entry.Result.WasFullyBlocked)
                continue;
            IReadOnlyDictionary<ulong, int> turns = s_historyPlayerTurnNumbersField.GetValue(entry) as IReadOnlyDictionary<ulong, int>
                ?? throw new InvalidOperationException("Combat history player turn numbers are unavailable");
            if (turns.TryGetValue(player.NetId, out int captured) && captured == turn)
                return true;
        }
        return false;
    }
}
