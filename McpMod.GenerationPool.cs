using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Mcp;

public static partial class McpMod
{
    // The build actually loaded: a fixed provenance label must never make a caller trust a
    // different one.
    private static readonly Lazy<string> s_generationAssemblySha256 = new(() =>
    {
        using FileStream stream = File.OpenRead(typeof(CardModel).Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    });

    private static Dictionary<string, object?> BuildGenerationPool(Player player)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["known"] = false,
            ["cards"] = new List<Dictionary<string, object?>>(),
            ["filter_version"] = 1,
            ["filter_source"] = "CardPoolModel.GetUnlockedCards(Player.UnlockState,IRunState.CardMultiplayerConstraint);CardFactory.FilterForPlayerCount;CardFactory.FilterForCombat;OrobicAcid.OnUse:Attack,Skill,Power"
        };
        string? failure = TryAction("player.generation_pool", () =>
        {
            IRunState run = player.RunState;
            result["character_id"] = player.Character.Id.Entry;
            result["player_id"] = player.NetId.ToString();
            result["players"] = run.Players.Count;
            result["multiplayer_constraint"] = run.CardMultiplayerConstraint.ToString();
            result["game_assembly_sha256"] = s_generationAssemblySha256.Value;
            if (run.Players.Count < 1 || !run.Players.Contains(player))
                throw new InvalidOperationException("\u5BFE\u8C61player\u304Crun.Players\u306B\u5B58\u5728\u3057\u306A\u3044");

            IEnumerable<CardModel> unlocked = player.Character.CardPool.GetUnlockedCards(player.UnlockState, run.CardMultiplayerConstraint);
            // The same condition as the private FilterForPlayerCount (sts2-9cb4f1ad).
            CardMultiplayerConstraint excluded = run.Players.Count > 1
                ? CardMultiplayerConstraint.SingleplayerOnly : CardMultiplayerConstraint.MultiplayerOnly;
            List<Dictionary<string, object?>> cards = CardFactory.FilterForCombat(unlocked.Where(c => c.MultiplayerConstraint != excluded))
                .Where(c => c.Type is CardType.Attack or CardType.Skill or CardType.Power)
                .OrderBy(c => c.Id.Entry, StringComparer.Ordinal)
                .Select(c => new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = c.Id.Entry, ["type"] = c.Type.ToString() })
                .ToList();
            // Do not touch the draw, CreateCard, or the RNG; only flag `known` after the
            // enumeration has finished.
            result["cards"] = cards;
            result["known"] = true;
        });
        if (failure != null)
            result["reason"] = failure;
        return result;
    }
}
