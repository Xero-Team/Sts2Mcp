using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Achievements;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace Sts2Mcp;

public static partial class McpMod
{
    private static void HandleGetProfile(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        RunHandler(response, () => SendReadResultJson(response, RunOnMainThreadBlocking(BuildProfile), pretty),
            "Failed to build profile", "profile_build_failed", pretty);
    }

    private static void HandleGetProfiles(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        RunHandler(response, () => SendReadResultJson(response, RunOnMainThreadBlocking(BuildProfilesSummary), pretty),
            "Failed to get profiles", "profiles_read_failed", pretty);
    }

    private static void HandlePostProfiles(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        ActionRequest? actionRequest = TryReadActionRequest(
            request, response, pretty, "Missing 'action' field. Use: switch, delete");
        if (actionRequest == null)
            return;

        Dictionary<string, JsonElement> parsed = actionRequest.Parsed;
        string action = actionRequest.Action;
        if (!parsed.TryGetValue("profile_id", out JsonElement idElem))
        {
            SendError(response, 400, "Missing 'profile_id' field. Use a profile slot 1-3.", "missing_profile_id");
            return;
        }
        if (idElem.ValueKind != JsonValueKind.Number)
        {
            SendError(response, 400, "'profile_id' field must be a number", "invalid_profile_id_type");
            return;
        }
        if (!idElem.TryGetInt32(out int profileId))
        {
            SendError(response, 400, "'profile_id' field must be a 32-bit integer", "invalid_profile_id_type");
            return;
        }

        RunHandler(response, () => SendProfileActionJson(response,
            EnsureStatus(RunOnMainThreadBlocking(() => ExecuteProfileAction(action, profileId))), pretty),
            "Profile action failed", "profile_action_failed", pretty);
    }

    private static void SendProfileActionJson(
        HttpListenerResponse response, Dictionary<string, object?> result, bool pretty = false)
    {
        if (result.TryGetValue("status", out object? status) && string.Equals(status as string, "error", StringComparison.Ordinal))
        {
            response.StatusCode = result.TryGetValue("error_code", out object? errorCode)
                ? (errorCode as string) switch
                {
                    "invalid_profile_id" or "unknown_profile_action" => 400,
                    "run_in_progress" or "active_profile_delete" => 409,
                    "save_manager_unavailable" => 503,
                    _ => 400
                }
                : 400;
        }
        SendJson(response, result, pretty);
    }

    private static Dictionary<string, object?> BuildProfilesSummary()
    {
        SaveManager sm = SaveManager.Instance;
        if (sm == null)
            return Error("Save manager is not available", "save_manager_unavailable");

        var profiles = new List<Dictionary<string, object?>>();
        for (int i = 1; i <= 3; i++)
        {
            var profileData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = i,
                ["profile_id"] = i,
                ["is_current"] = i == sm.CurrentProfileId,
            };

            string? path = null;
            string? resolvedPath = null;
            string profileRoot = $"profile{i}";
            TryDo(() =>
            {
                path = GetProfileProgressPath(i);
                resolvedPath = ResolveProfileProgressPath(i);
                profileRoot = GetProfileRootFromProgressPath(path, i);
            });
            profileData["has_data"] = resolvedPath != null && File.Exists(resolvedPath);
            profileData["path"] = NormalizePathForJson(path);
            profileData["resolved_path"] = NormalizePathForJson(resolvedPath);
            profileData["progress_path"] = NormalizePathForJson(path);
            profileData["resolved_progress_path"] = NormalizePathForJson(resolvedPath);
            profileData["profile_root"] = NormalizePathForJson(profileRoot);
            profileData["save_scope"] = GetSaveScope(profileRoot);

            profiles.Add(profileData);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["kind"] = "profiles",
            ["current_profile_id"] = sm.CurrentProfileId,
            ["count"] = profiles.Count,
            ["profiles"] = profiles
        };
    }

    private static Dictionary<string, object?> ExecuteProfileAction(string action, int profileId)
    {
        SaveManager sm = SaveManager.Instance;
        if (sm == null)
            return Error("Save manager is not available", "save_manager_unavailable");
        if (profileId is < 1 or > 3)
            return Error("profile_id must be 1-3", "invalid_profile_id");

        return action.Trim().ToLowerInvariant() switch
        {
            "switch" => ExecuteProfileSwitch(sm, profileId),
            "delete" => ExecuteProfileDelete(sm, profileId),
            _ => Error($"Unknown action: {action}. Use: switch, delete", "unknown_profile_action"),
        };
    }

    private static Dictionary<string, object?> ExecuteProfileSwitch(SaveManager sm, int profileId)
    {
        if (RunManager.Instance?.IsInProgress == true)
            return Error("Cannot switch profiles during a run", "run_in_progress");

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root != null)
        {
            if (TrySwitchProfileViaOpenScreen(tree.Root, profileId))
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = $"Switch requested for profile {profileId} (via UI)",
                    ["target_profile_id"] = profileId,
                    ["current_profile_id"] = sm.CurrentProfileId
                };
            }

            NMainMenu? mainMenu = FindFirst<NMainMenu>(tree.Root);
            if (mainMenu != null && IsNodeVisible(mainMenu))
            {
                mainMenu.OpenProfileScreen();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = "Opened profile screen. Send switch again to select profile."
                };
            }
        }

        sm.SwitchProfileId(profileId);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Switched to profile {profileId} (requires restart)",
            ["current_profile_id"] = sm.CurrentProfileId
        };
    }

    private static Dictionary<string, object?> ExecuteProfileDelete(SaveManager sm, int profileId)
    {
        if (profileId == sm.CurrentProfileId)
            return Error("Cannot delete the active profile", "active_profile_delete");

        sm.DeleteProfile(profileId);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Deleted profile {profileId}"
        };
    }

    private static bool TrySwitchProfileViaOpenScreen(Node root, int profileId)
    {
        NProfileScreen? profileScreen = FindFirst<NProfileScreen>(root);
        if (profileScreen == null || !IsNodeVisible(profileScreen))
            return false;

        var buttons = GetInstanceFieldValue(profileScreen, "_profileButtons") as System.Collections.IEnumerable;
        if (buttons == null)
            return false;

        foreach (object? btn in buttons)
        {
            object? btnId = GetInstanceFieldValue(btn, "_profileId");
            if (btnId is not int id || id != profileId)
                continue;

            MethodInfo? switchMethod = btn.GetType().GetMethod(
                "SwitchToThisProfile",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            switchMethod?.Invoke(btn, null);
            return switchMethod != null;
        }

        return false;
    }

    internal static object BuildProfile()
    {
        SaveManager? saveManager = SaveManager.Instance;
        ProgressState? progress = saveManager?.Progress;
        if (saveManager == null || progress == null)
            return Error("No profile data available.", "profile_data_unavailable");

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        int profileId = saveManager.CurrentProfileId;
        string? progressPath = GetProfileProgressPath(profileId);
        string? resolvedProgressPath = ResolveProfileProgressPath(profileId);
        string profileRoot = GetProfileRootFromProgressPath(progressPath, profileId);
        result["status"] = "ok";
        result["kind"] = "profile";
        result["profile_id"] = profileId;
        result["progress_path"] = NormalizePathForJson(progressPath);
        result["resolved_progress_path"] = NormalizePathForJson(resolvedProgressPath);
        result["profile_root"] = NormalizePathForJson(profileRoot);
        result["save_scope"] = GetSaveScope(profileRoot);
        result["current_run"] = BuildActiveRunContext();

        result["characters"] = BuildCharacterStatRows(progress.CharacterStats);
        result["card_stats"] = BuildCardStatRows(progress.CardStats);
        result["encounter_stats"] = BuildEncounterStatRows(progress.EncounterStats);
        result["enemy_stats"] = BuildEnemyStatRows(progress.EnemyStats);
        result["ancient_stats"] = BuildAncientStatRows(progress.AncientStats);
        AddDiscoveredIds(result, progress);
        result["achievements"] = BuildAchievementList(progress.UnlockedAchievements);
        result["epochs"] = BuildEpochList(progress.Epochs);
        AddProfileTotals(result, progress);
        return result;
    }

    private static List<Dictionary<string, object?>> BuildCharacterStatRows(
        IReadOnlyDictionary<ModelId, CharacterStats> stats)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<ModelId, CharacterStats> kv in stats.OrderBy(kv => kv.Key.Entry, StringComparer.Ordinal))
        {
            CharacterStats row = kv.Value;
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = kv.Key.Entry,
                ["max_ascension"] = row.MaxAscension,
                ["preferred_ascension"] = row.PreferredAscension,
                ["total_wins"] = row.TotalWins,
                ["total_losses"] = row.TotalLosses,
                ["fastest_win_time"] = row.FastestWinTime,
                ["best_win_streak"] = row.BestWinStreak,
                ["current_win_streak"] = row.CurrentWinStreak,
                ["playtime"] = row.Playtime
            });
        }
        return rows;
    }

    private static List<Dictionary<string, object?>> BuildCardStatRows(
        IReadOnlyDictionary<ModelId, CardStats> stats)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<ModelId, CardStats> kv in stats.OrderBy(kv => kv.Key.Entry, StringComparer.Ordinal))
        {
            CardStats row = kv.Value;
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = kv.Key.Entry,
                ["times_picked"] = row.TimesPicked,
                ["times_skipped"] = row.TimesSkipped,
                ["times_won"] = row.TimesWon,
                ["times_lost"] = row.TimesLost
            });
        }
        return rows;
    }

    private static List<Dictionary<string, object?>> BuildEncounterStatRows(
        IReadOnlyDictionary<ModelId, EncounterStats> stats)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<ModelId, EncounterStats> kv in stats.OrderBy(kv => kv.Key.Entry, StringComparer.Ordinal))
        {
            rows.Add(BuildFightStatRow(kv.Key.Entry, kv.Value.TotalWins, kv.Value.TotalLosses, kv.Value.FightStats));
        }
        return rows;
    }

    private static List<Dictionary<string, object?>> BuildEnemyStatRows(
        IReadOnlyDictionary<ModelId, EnemyStats> stats)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<ModelId, EnemyStats> kv in stats.OrderBy(kv => kv.Key.Entry, StringComparer.Ordinal))
        {
            rows.Add(BuildFightStatRow(kv.Key.Entry, kv.Value.TotalWins, kv.Value.TotalLosses, kv.Value.FightStats));
        }
        return rows;
    }

    private static Dictionary<string, object?> BuildFightStatRow(
        string id,
        int totalWins,
        int totalLosses,
        IEnumerable<FightStats> fightStats)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["total_wins"] = totalWins,
            ["total_losses"] = totalLosses
        };
        var byCharacter = new List<Dictionary<string, object?>>();
        foreach (FightStats fs in fightStats)
        {
            byCharacter.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["character"] = fs.Character.Entry,
                ["wins"] = fs.Wins,
                ["losses"] = fs.Losses
            });
        }
        if (byCharacter.Count > 0)
            row["by_character"] = SortDictionaryListByStringField(byCharacter, "character");
        return row;
    }

    private static List<Dictionary<string, object?>> BuildAncientStatRows(
        IReadOnlyDictionary<ModelId, AncientStats> stats)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<ModelId, AncientStats> kv in stats.OrderBy(kv => kv.Key.Entry, StringComparer.Ordinal))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = kv.Key.Entry,
                ["total_visits"] = kv.Value.TotalVisits,
                ["total_wins"] = kv.Value.TotalWins,
                ["total_losses"] = kv.Value.TotalLosses
            };
            var byCharacter = new List<Dictionary<string, object?>>();
            foreach (AncientCharacterStats cs in kv.Value.CharStats)
            {
                byCharacter.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["character"] = cs.Character.Entry,
                    ["wins"] = cs.Wins,
                    ["losses"] = cs.Losses
                });
            }
            if (byCharacter.Count > 0)
                row["by_character"] = SortDictionaryListByStringField(byCharacter, "character");
            rows.Add(row);
        }
        return rows;
    }

    private static void AddDiscoveredIds(Dictionary<string, object?> result, ProgressState progress)
    {
        result["discovered_cards"] = SortedDiscoveredIds(progress.DiscoveredCards);
        result["discovered_relics"] = SortedDiscoveredIds(progress.DiscoveredRelics);
        result["discovered_potions"] = SortedDiscoveredIds(progress.DiscoveredPotions);
        result["discovered_events"] = SortedDiscoveredIds(progress.DiscoveredEvents);
        result["discovered_acts"] = SortedDiscoveredIds(progress.DiscoveredActs);
    }

    private static List<string> SortedDiscoveredIds(IReadOnlySet<ModelId> ids)
        => ids.Select(id => id.Entry)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static List<Dictionary<string, object?>> BuildAchievementList(
        IReadOnlyDictionary<Achievement, long> unlocked)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<Achievement, long> kv in unlocked)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = kv.Key,
                ["unlocked_at"] = kv.Value
            });
        }
        return SortDictionaryListByStringField(rows, "id");
    }

    private static List<Dictionary<string, object?>> BuildEpochList(IReadOnlyList<SerializableEpoch> epochs)
        => epochs
            .OrderBy(e => e.Id?.ToString(), StringComparer.Ordinal)
            .Select(e => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = e.Id,
                ["state"] = e.State.ToString(),
                ["obtained"] = e.ObtainDate
            }).ToList();

    private static void AddProfileTotals(Dictionary<string, object?> result, ProgressState progress)
    {
        result["total_playtime"] = progress.TotalPlaytime;
        result["total_unlocks"] = progress.TotalUnlocks;
        result["current_score"] = progress.CurrentScore;
        result["floors_climbed"] = progress.FloorsClimbed;
        result["architect_damage"] = progress.ArchitectDamage;
        result["total_wins"] = progress.Wins;
        result["total_losses"] = progress.Losses;
        result["fastest_victory"] = progress.FastestVictory;
        result["best_win_streak"] = progress.BestWinStreak;
        result["number_of_runs"] = progress.NumberOfRuns;
    }
}
