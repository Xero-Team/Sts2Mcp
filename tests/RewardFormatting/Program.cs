using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Sts2Mcp.Tests.RewardFormatting;

/// <summary>
/// Pins the reward markdown the mod emits without the game: the formatter is pure
/// over the reward dictionary, so every case below runs the real shipped code.
/// </summary>
internal static class Program
{
    private static readonly MethodInfo s_formatRewards =
        typeof(McpMod).GetMethod("FormatRewardsMarkdown", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("McpMod.FormatRewardsMarkdown not found");

    private static readonly MethodInfo s_formatCardReward =
        typeof(McpMod).GetMethod("FormatCardRewardMarkdown", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("McpMod.FormatCardRewardMarkdown not found");

    private static int s_failures;

    private static int Main()
    {
        // Formatting must not depend on the host locale; Turkish is the classic trap
        // for casing, and reward text has no locale-specific numbers anyway.
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("tr-TR");
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        Rewards(Items(), "empty list", "## Rewards", "No rewards available.", "**Can proceed:** No");
        Rewards(null, "absent items", "## Rewards", "No rewards available.");

        Rewards(Items(Item(0, "gold", "Gold", goldAmount: 25)),
            "gold amount", "- [0] **gold**: Gold (25 gold)");

        Rewards(Items(Item(0, "potion", "Potion", potionDescription: "Deal 2 damage.")),
            "potion description", "- [0] **potion**: Potion - Deal 2 damage.");

        Rewards(Items(Item(0, "potion", "Potion", potionName: "Fire Potion")),
            "potion name fallback", "- [0] **potion**: Potion (Fire Potion)");

        Rewards(Items(Item(0, "relic", "Relic")),
            "unknown item type", "- [0] **relic**: Relic");

        Rewards(Items(Item(0, "card", "Card", rewardId: "REWARD_1", selectionState: "offered")),
            "reward id and state", "\u00B7 `REWARD_1` \u00B7 offered");

        Rewards(Items(Item(0, "card", "Card", rewardId: "REWARD_1")),
            "absent selection state reads unknown", "\u00B7 `REWARD_1` \u00B7 unknown");

        var rewards = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["items"] = Items(Item(0, "gold", "Gold")),
            ["screen_revision"] = 7,
            ["can_proceed"] = true,
            ["selection_history"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal)
                {
                    ["reward_id"] = "REWARD_1",
                    ["selection_state"] = "claimed",
                    ["claimable"] = false,
                    ["skipped"] = false,
                },
            },
        };
        RewardsDict(rewards, "revision, proceed and history",
            "**Screen revision:** `7`.",
            "**Can proceed:** Yes",
            "`REWARD_1`: claimed, claimable=False, skipped=False, consumed=unknown, last=unknown ");

        // A null value in the dictionary must not leak the literal "null" into markdown.
        string noNulls = Render(s_formatRewards, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["items"] = Items(Item(0, "gold", "Gold", goldAmount: null, potionDescription: null, potionName: null)),
            ["can_proceed"] = false,
        });
        Check(!noNulls.Contains("null", StringComparison.Ordinal), "null reward values stay out of markdown", noNulls);

        CardReward(new Dictionary<string, object?>(StringComparer.Ordinal),
            "card reward empty", "## Card Reward Selection", "**Can skip:** No");

        CardReward(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["parent_reward_id"] = "REWARD_2",
                ["selection_state"] = "offered",
                ["cards"] = new List<Dictionary<string, object?>>
                {
                    new(StringComparer.Ordinal)
                    {
                        ["index"] = 0,
                        ["name"] = "Strike",
                        ["cost"] = 1,
                        ["type"] = "Attack",
                        ["rarity"] = "Basic",
                        ["description"] = "Deal 6 damage.",
                        ["keywords"] = new List<string> { "Strike" },
                        ["upgrade_preview"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["cost"] = 0,
                            ["star_cost"] = 2,
                            ["description"] = "Deal 9 damage.",
                        },
                    },
                },
                ["can_skip"] = true,
            },
            "card reward body",
            "**Parent reward:** `REWARD_2` \u00B7 selection_state: offered",
            "- [0] **Strike** (1 energy) [Attack] Basic [Strike] - Deal 6 damage.",
            "\u2192 **Upgraded**: (0 energy + 2 star) - Deal 9 damage.",
            "**Can skip:** Yes");

        CardReward(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["alternatives"] = new List<Dictionary<string, object?>>
                {
                    new(StringComparer.Ordinal) { ["index"] = 1, ["option_id"] = "SACRIFICE", ["title"] = "Pael's Wing", ["enabled"] = true },
                    new(StringComparer.Ordinal) { ["index"] = 2, ["option_id"] = "REROLL", ["title"] = "", ["enabled"] = false },
                },
            },
            "card reward alternatives",
            "`SACRIFICE` \u2014 Pael's Wing",
            "- [2] `REROLL` *(disabled)*");

        if (s_failures > 0)
        {
            Console.WriteLine($"FAIL {s_failures} reward formatting check(s)");
            return 1;
        }

        Console.WriteLine("ALL OK (reward formatting)");
        return 0;
    }

    private static void Rewards(List<Dictionary<string, object?>>? items, string name, params string[] expected)
    {
        var rewards = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (items is not null)
        {
            rewards["items"] = items;
        }

        RewardsDict(rewards, name, expected);
    }

    private static void RewardsDict(Dictionary<string, object?> rewards, string name, params string[] expected)
        => Check(Render(s_formatRewards, rewards), name, expected);

    private static void CardReward(Dictionary<string, object?> reward, string name, params string[] expected)
        => Check(Render(s_formatCardReward, reward), name, expected);

    private static string Render(MethodInfo method, Dictionary<string, object?> value)
    {
        var builder = new StringBuilder();
        method.Invoke(null, new object?[] { builder, value });
        return builder.ToString();
    }

    private static void Check(string markdown, string name, params string[] expected)
    {
        string missing = string.Join(
            " | missing: ",
            expected.Where(line => !markdown.Contains(line, StringComparison.Ordinal)));
        Check(missing.Length == 0, name, markdown + (missing.Length == 0 ? "" : $"\nmissing: {missing}"));
    }

    private static void Check(bool passed, string name, string detail)
    {
        if (passed)
        {
            Console.WriteLine($"ok   {name}");
            return;
        }

        s_failures++;
        Console.WriteLine($"FAIL {name}\n{detail}");
    }

    private static List<Dictionary<string, object?>> Items(params Dictionary<string, object?>[] items)
        => items.ToList();

    private static Dictionary<string, object?> Item(
        int index,
        string type,
        string description,
        int? goldAmount = null,
        string? potionDescription = null,
        string? potionName = null,
        string? rewardId = null,
        string? selectionState = null)
    {
        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = index,
            ["type"] = type,
            ["description"] = description,
        };
        if (goldAmount is int gold)
        {
            item["gold_amount"] = gold;
        }

        if (potionDescription is not null)
        {
            item["potion_description"] = potionDescription;
        }

        if (potionName is not null)
        {
            item["potion_name"] = potionName;
        }

        if (rewardId is not null)
        {
            item["reward_id"] = rewardId;
        }

        if (selectionState is not null)
        {
            item["selection_state"] = selectionState;
        }

        return item;
    }
}
