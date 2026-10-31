using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace Sts2Mcp;

public static partial class McpMod
{
    private static readonly object s_runHistoryMembersLock = new();
    private static Type? s_runHistoryMembersType;
    private static MemberInfo[]? s_runHistoryMembers;

    private static MethodInfo? s_progressPathForProfile;

    private static void HandleGetCompendium(HttpListenerRequest request, HttpListenerResponse response)
    {
        // Main's dispatch shape: the snapshot needs the game's main thread, and
        // pretty printing is a transport concern, so it stays here rather than in
        // the snapshot builder the fork shared with other callers.
        bool pretty = WantsPretty(request);
        RunHandler(response, () =>
        {
            CompendiumSnapshot snapshot = RunOnMainThreadBlocking(BuildCompendiumSnapshot);
            SendReadResultJson(response, BuildCompendiumResponse(snapshot), pretty);
        }, "Failed to build compendium", "compendium_build_failed", pretty);
    }

    internal static object BuildCompendium()
    {
        return BuildCompendiumResponse(BuildCompendiumSnapshot());
    }

    private static CompendiumSnapshot BuildCompendiumSnapshot()
    {
        ProgressState? progress = SaveManager.Instance?.Progress;
        SaveManager? saveManager = SaveManager.Instance;
        if (progress == null || saveManager == null)
            return new CompendiumSnapshot { ErrorMessage = "No profile data available.", ErrorCode = "profile_data_unavailable" };

        int profileId = saveManager.CurrentProfileId;
        string? progressPath = GetProfileProgressPath(profileId);
        string? resolvedProgressPath = ResolveProfileProgressPath(profileId);
        string profileRoot = GetProfileRootFromProgressPath(progressPath, profileId);

        var globalStats = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddProfileTotals(globalStats, progress);

        return new CompendiumSnapshot
        {
            ProfileId = profileId,
            ProgressPath = progressPath,
            ResolvedProgressPath = resolvedProgressPath,
            ProfileRoot = profileRoot,
            SaveScope = GetSaveScope(profileRoot),
            IsRunInProgress = RunManager.Instance?.IsInProgress == true,
            DiscoveredCards = SortedDiscoveredIds(progress.DiscoveredCards),
            DiscoveredRelics = SortedDiscoveredIds(progress.DiscoveredRelics),
            DiscoveredPotions = SortedDiscoveredIds(progress.DiscoveredPotions),
            CardStats = BuildCardStatRows(progress.CardStats),
            CharacterStats = BuildCharacterStatRows(progress.CharacterStats),
            EncounterStats = BuildEncounterStats(progress),
            EnemyStats = BuildEnemyStats(progress),
            RunHistoryProgressMembers = BuildRunHistoryProgressMembers(progress),
            GlobalStats = globalStats
        };
    }

    private static Dictionary<string, object?> BuildCompendiumResponse(CompendiumSnapshot snapshot)
    {
        if (snapshot.ErrorMessage != null)
            return Error(snapshot.ErrorMessage, snapshot.ErrorCode);

        Dictionary<string, object?> runHistory = BuildRunHistorySection(snapshot);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["kind"] = "compendium",
            ["profile_id"] = snapshot.ProfileId,
            ["progress_path"] = NormalizePathForJson(snapshot.ProgressPath),
            ["resolved_progress_path"] = NormalizePathForJson(snapshot.ResolvedProgressPath),
            ["profile_root"] = NormalizePathForJson(snapshot.ProfileRoot),
            ["save_scope"] = snapshot.SaveScope,
            ["current_run"] = BuildCurrentRunContext(snapshot),
            ["source"] = "SaveManager.Progress plus model metadata endpoints",
            ["sections"] = BuildCompendiumSections(snapshot, runHistory)
        };
    }

    private sealed class CompendiumSnapshot
    {
        public string? ErrorMessage { get; init; }
        public string? ErrorCode { get; init; }
        public int ProfileId { get; init; }
        public string? ProgressPath { get; init; }
        public string? ResolvedProgressPath { get; init; }
        public string ProfileRoot { get; init; } = "";
        public string SaveScope { get; init; } = "vanilla";
        public bool IsRunInProgress { get; init; }
        public List<string> DiscoveredCards { get; init; } = new();
        public List<string> DiscoveredRelics { get; init; } = new();
        public List<string> DiscoveredPotions { get; init; } = new();
        public List<Dictionary<string, object?>> CardStats { get; init; } = new();
        public List<Dictionary<string, object?>> CharacterStats { get; init; } = new();
        public List<Dictionary<string, object?>> EncounterStats { get; init; } = new();
        public List<Dictionary<string, object?>> EnemyStats { get; init; } = new();
        public Dictionary<string, object?> RunHistoryProgressMembers { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, object?> GlobalStats { get; init; } = new(StringComparer.Ordinal);
    }

    private static List<Dictionary<string, object?>> BuildEncounterStats(dynamic progress)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (dynamic kv in progress.EncounterStats)
            result.Add(BuildFightStatsEntry(kv.Key.Entry, kv.Value));
        return SortDictionaryListByStringField(result, "id");
    }

    private static List<Dictionary<string, object?>> BuildEnemyStats(dynamic progress)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (dynamic kv in progress.EnemyStats)
            result.Add(BuildFightStatsEntry(kv.Key.Entry, kv.Value));
        return SortDictionaryListByStringField(result, "id");
    }

    private static Dictionary<string, object?> BuildFightStatsEntry(string id, dynamic stats)
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["total_wins"] = stats.TotalWins,
            ["total_losses"] = stats.TotalLosses
        };

        var byCharacter = new List<Dictionary<string, object?>>();
        foreach (dynamic fs in stats.FightStats)
        {
            byCharacter.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["character"] = fs.Character.Entry,
                ["wins"] = fs.Wins,
                ["losses"] = fs.Losses
            });
        }
        if (byCharacter.Count > 0)
            entry["by_character"] = byCharacter
                .OrderBy(item => item.TryGetValue("character", out object? value) ? value?.ToString() : "", StringComparer.Ordinal)
                .ToList();

        return entry;
    }

    private static List<Dictionary<string, object?>> SortDictionaryListByStringField(
        List<Dictionary<string, object?>> items,
        string field)
    {
        return items
            .OrderBy(item => item.TryGetValue(field, out object? value) ? value?.ToString() : "", StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, object?> BuildRunHistoryProgressMembers(object progress)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (MemberInfo member in GetRunHistoryMembers(progress.GetType()))
        {
            TryDo(() =>
            {
                object? value = member switch
                {
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(progress),
                    FieldInfo field => field.GetValue(progress),
                    _ => null
                };
                if (value != null)
                    values[member.Name] = ToJsonSafe(value, 0, 50);
            });
        }
        return values;
    }

    private static MemberInfo[] GetRunHistoryMembers(Type progressType)
    {
        lock (s_runHistoryMembersLock)
        {
            if (s_runHistoryMembersType == progressType && s_runHistoryMembers != null)
                return s_runHistoryMembers;

            s_runHistoryMembersType = progressType;
            s_runHistoryMembers = progressType
                .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(member => member.Name.Contains("RunHistory", StringComparison.OrdinalIgnoreCase)
                    || member.Name.Contains("RunHistories", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return s_runHistoryMembers;
        }
    }

    private static Dictionary<string, object?> BuildRunHistorySection(CompendiumSnapshot snapshot)
    {
        Dictionary<string, object?> values = snapshot.RunHistoryProgressMembers;
        string? historyDirectory = FindRunHistoryDirectory(snapshot);
        if (historyDirectory != null)
        {
            List<FileInfo> files = Directory.GetFiles(historyDirectory, "*.run")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ui_label"] = "Run History",
                ["status"] = files.Count > 0 ? "exposed" : "exposed_empty",
                ["source"] = "Profile save history files",
                ["history_path"] = NormalizePathForJson(historyDirectory),
                ["entry_count"] = files.Count,
                ["entries"] = files.Take(20).Select(file => BuildRunHistoryEntry(file, snapshot.ProfileId, snapshot.SaveScope)).ToList(),
                ["progress_members"] = values.Count > 0 ? values : null,
                ["limitation"] = files.Count > 20
                    ? "Only the 20 most recent run files are summarized; use history_path to inspect older local run files."
                    : "Run history is summarized from the active profile's saved .run files."
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ui_label"] = "Run History",
            ["status"] = values.Count > 0 ? "partially_exposed" : "exposed_empty",
            ["source"] = "SaveManager.Progress reflected run-history members",
            ["entries"] = values,
            ["limitation"] = values.Count > 0
                ? "Run history is serialized from discovered progress members and capped for response size."
                : "No run-history files or typed run-history members were found for the active profile."
        };
    }

    private static Dictionary<string, object?> BuildRunHistoryEntry(FileInfo file, int profileId, string saveScope)
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Path.GetFileNameWithoutExtension(file.Name),
            ["run_id"] = $"{saveScope}:profile{profileId}:{Path.GetFileNameWithoutExtension(file.Name)}",
            ["file_name"] = file.Name,
            ["size_bytes"] = file.Length,
            ["last_write_time_utc"] = file.LastWriteTimeUtc
        };

        // A run file that will not parse still reports its name, size and timestamp.
        string? parseFailure = TryAction("compendium.run_history_entry", () =>
        {
            using FileStream stream = file.OpenRead();
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            CopyJsonScalar(root, entry, "start_time");
            CopyJsonScalar(root, entry, "run_time");
            CopyJsonScalar(root, entry, "game_mode");
            CopyJsonScalar(root, entry, "ascension");
            CopyJsonScalar(root, entry, "win");
            CopyJsonScalar(root, entry, "was_abandoned");
            CopyJsonScalar(root, entry, "killed_by_encounter");
            CopyJsonScalar(root, entry, "killed_by_event");
            CopyJsonScalar(root, entry, "seed");
            CopyJsonScalar(root, entry, "build_id");

            if (root.TryGetProperty("acts", out JsonElement acts) && acts.ValueKind == JsonValueKind.Array)
                entry["acts"] = acts.EnumerateArray().Select(GetJsonValue).ToList();

            if (root.TryGetProperty("players", out JsonElement players) && players.ValueKind == JsonValueKind.Array)
            {
                entry["players"] = players.EnumerateArray().Select(player =>
                {
                    var playerEntry = new Dictionary<string, object?>(StringComparer.Ordinal);
                    CopyJsonScalar(player, playerEntry, "id");
                    CopyJsonScalar(player, playerEntry, "character");
                    if (player.TryGetProperty("deck", out JsonElement deck) && deck.ValueKind == JsonValueKind.Array)
                        playerEntry["deck_count"] = deck.GetArrayLength();
                    if (player.TryGetProperty("relics", out JsonElement relics) && relics.ValueKind == JsonValueKind.Array)
                        playerEntry["relic_count"] = relics.GetArrayLength();
                    if (player.TryGetProperty("potions", out JsonElement potions) && potions.ValueKind == JsonValueKind.Array)
                        playerEntry["potion_count"] = potions.GetArrayLength();
                    return playerEntry;
                }).ToList();
            }

            entry["map_point_count"] = CountMapPoints(root);
        });
        if (parseFailure != null)
            entry["parse_error"] = parseFailure;

        return entry;
    }

    private static Dictionary<string, object?>? BuildActiveRunContext()
    {
        SaveManager saveManager = SaveManager.Instance;
        if (saveManager == null)
            return null;

        int profileId = saveManager.CurrentProfileId;
        string? progressPath = GetProfileProgressPath(profileId);
        string profileRoot = GetProfileRootFromProgressPath(progressPath, profileId);
        return BuildCurrentRunContext(
            RunManager.Instance?.IsInProgress == true,
            profileId,
            GetSaveScope(profileRoot),
            progressPath,
            profileRoot);
    }

    private static Dictionary<string, object?>? BuildCurrentRunContext(
        bool isRunInProgress,
        int profileId,
        string saveScope,
        string? progressPath,
        string profileRoot)
    {
        if (!isRunInProgress)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_in_progress"] = true,
            ["profile_id"] = profileId,
            ["progress_path"] = NormalizePathForJson(progressPath),
            ["resolved_progress_path"] = NormalizePathForJson(ResolveProfileProgressPath(profileId)),
            ["profile_root"] = NormalizePathForJson(profileRoot),
            ["save_scope"] = saveScope,
            ["id_format"] = "{save_scope}:profile{profile_id}:{start_time}"
        };

        string? currentRunPath = ResolveCurrentRunPath(progressPath, profileRoot);
        if (currentRunPath == null || !File.Exists(currentRunPath))
        {
            result["limitation"] = "Run is in progress, but current_run.save was not found yet.";
            return result;
        }

        result["save_path"] = NormalizePathForJson(currentRunPath);

        // The context above (paths, ids) is still usable when the save will not parse.
        string? parseFailure = TryAction("compendium.current_run", () =>
        {
            using FileStream stream = File.OpenRead(currentRunPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            CopyJsonScalar(root, result, "start_time");
            CopyJsonScalar(root, result, "save_time");
            CopyJsonScalar(root, result, "run_time");
            CopyJsonScalar(root, result, "game_mode");
            CopyJsonScalar(root, result, "ascension");
            CopyJsonScalar(root, result, "current_act_index");
            CopyJsonScalar(root, result, "schema_version");
            CopyJsonScalar(root, result, "platform_type");

            if (root.TryGetProperty("rng", out JsonElement rng)
                && rng.ValueKind == JsonValueKind.Object
                && rng.TryGetProperty("seed", out JsonElement seed))
                result["seed"] = GetJsonValue(seed);

            if (result.TryGetValue("start_time", out object? startTime) && startTime != null)
                result["run_id"] = $"{saveScope}:profile{profileId}:{startTime}";
        });
        if (parseFailure != null)
            result["parse_error"] = parseFailure;

        return result;
    }

    private static Dictionary<string, object?>? BuildCurrentRunContext(CompendiumSnapshot snapshot)
        => BuildCurrentRunContext(snapshot.IsRunInProgress, snapshot.ProfileId, snapshot.SaveScope, snapshot.ProgressPath, snapshot.ProfileRoot);

    private static Dictionary<string, object?> BuildCompendiumSections(
        CompendiumSnapshot snapshot,
        Dictionary<string, object?>? runHistory)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["card_library"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ui_label"] = "Card Library",
                ["status"] = "exposed",
                ["source"] = "/api/v1/profile card_stats and discovered_cards",
                ["detail_endpoint"] = "/api/v1/glossary/cards",
                ["detail_endpoint_requires_run"] = true,
                ["discovered_ids"] = snapshot.DiscoveredCards,
                ["stats"] = snapshot.CardStats
            },
            ["relic_collection"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ui_label"] = "Relic Collection",
                ["status"] = "partially_exposed",
                ["source"] = "/api/v1/profile discovered_relics",
                ["detail_endpoint"] = "/api/v1/glossary/relics",
                ["detail_endpoint_requires_run"] = true,
                ["discovered_ids"] = snapshot.DiscoveredRelics,
                ["limitation"] = "Profile exposes discovered relic IDs; per-relic obtained counts are not exposed by a typed API here."
            },
            ["potion_lab"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ui_label"] = "Potion Lab",
                ["status"] = "partially_exposed",
                ["source"] = "/api/v1/profile discovered_potions",
                ["detail_endpoint"] = "/api/v1/glossary/potions",
                ["detail_endpoint_requires_run"] = true,
                ["discovered_ids"] = snapshot.DiscoveredPotions,
                ["limitation"] = "Profile exposes discovered potion IDs; per-potion lab UI metadata is not exposed by a typed API here."
            },
            ["bestiary"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ui_label"] = "Bestiary",
                ["status"] = "locked_in_ui",
                ["source"] = "/api/v1/bestiary model metadata",
                ["detail_endpoint"] = "/api/v1/bestiary",
                ["encounter_stats"] = snapshot.EncounterStats,
                ["enemy_stats"] = snapshot.EnemyStats,
                ["limitation"] = "The current game UI labels Bestiary as future/locked; the endpoint exposes reflected model metadata and profile fight stats when available."
            },
            ["character_stats"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ui_label"] = "Character Stats",
                ["status"] = "exposed",
                ["source"] = "/api/v1/profile characters and global totals",
                ["characters"] = snapshot.CharacterStats,
                ["global"] = snapshot.GlobalStats
            },
            ["run_history"] = runHistory
        };
    }

    private static int CountMapPoints(JsonElement root)
    {
        if (!root.TryGetProperty("map_point_history", out JsonElement acts) || acts.ValueKind != JsonValueKind.Array)
            return 0;

        int count = 0;
        foreach (JsonElement act in acts.EnumerateArray())
        {
            if (act.ValueKind == JsonValueKind.Array)
                count += act.GetArrayLength();
        }
        return count;
    }

    private static void CopyJsonScalar(JsonElement source, Dictionary<string, object?> target, string propertyName)
    {
        if (source.TryGetProperty(propertyName, out JsonElement property))
            target[propertyName] = GetJsonValue(property);
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out double doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private static string? FindRunHistoryDirectory(CompendiumSnapshot snapshot)
    {
        string? saveDirectory = GetSaveDirectoryFromProgressPath(snapshot.ProgressPath);
        if (saveDirectory != null)
        {
            string historyPath = Path.Combine(saveDirectory, "history");
            if (Directory.Exists(historyPath))
                return historyPath;
        }

        foreach (string saveRoot in EnumerateSaveRoots())
        {
            string historyPath = Path.Combine(saveRoot, snapshot.ProfileRoot, "saves", "history");
            if (Directory.Exists(historyPath))
                return historyPath;
        }

        return null;
    }

    private static string GetProfileRootFromProgressPath(string? progressPath, int profileId)
    {
        string? normalized = progressPath?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            string moddedProfile = $"modded/profile{profileId}";
            if (normalized.Contains($"{moddedProfile}/", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals(moddedProfile, StringComparison.OrdinalIgnoreCase))
                return $"modded/profile{profileId}";

            string profile = $"profile{profileId}";
            if (normalized.Contains($"/{profile}/", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith($"{profile}/", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals(profile, StringComparison.OrdinalIgnoreCase))
                return $"profile{profileId}";
        }

        return $"profile{profileId}";
    }

    private static string GetSaveScope(string profileRoot)
    {
        return profileRoot.StartsWith("modded/", StringComparison.OrdinalIgnoreCase)
            ? "modded"
            : "vanilla";
    }

    private static IEnumerable<string> EnumerateSaveRoots()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> steamRoots = EnumerateSteamDataRoots().ToList();

        string? activeSteamId = GetActiveSteamId();
        if (!string.IsNullOrWhiteSpace(activeSteamId))
        {
            foreach (string? steamRoot in steamRoots)
            {
                string accountRoot = Path.Combine(steamRoot, activeSteamId);
                if (Directory.Exists(accountRoot) && yielded.Add(accountRoot))
                    yield return accountRoot;
            }
        }

        // Main's early exit: once the active Steam account resolved, do not read
        // other accounts' saves. The fork dropped this and always walked the rest.
        if (yielded.Count > 0)
            yield break;

        foreach (string? steamRoot in steamRoots)
        {
            foreach (string? accountRoot in Directory.GetDirectories(steamRoot)
                         .Where(yielded.Add)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                yield return accountRoot;
        }
    }

    private static IEnumerable<string> EnumerateSteamDataRoots()
    {
        var candidates = new List<string?>();

        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        candidates.Add(Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            candidates.Add(Path.Combine(home, ".local", "share"));

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in candidates)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string steamRoot = Path.Combine(root, "SlayTheSpire2", "steam");
            if (Directory.Exists(steamRoot) && yielded.Add(steamRoot))
                yield return steamRoot;
        }
    }

    private static string? GetActiveSteamId()
    {
        return TryRead(() =>
        {
            if (!SteamInitializer.Initialized)
                return null;

            // v0.111 dropped the SteamUtil.GetSteamID64 helper this used to reflect
            // into. PlatformUtil is the game's own way to ask the same question, and
            // a direct call is compile-checked instead of silently reporting "no
            // active account" when the helper moves again.
            ulong steamId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
            return steamId == 0 ? null : steamId.ToString(CultureInfo.InvariantCulture);
        });
    }

    /// <summary>
    /// `ProgressSaveManager.GetProgressPathForProfile`, called across game
    /// versions. v0.111 gave it an optional `bool? forceModState`; C# bakes an
    /// optional argument into the call site, so a direct call emits a two-
    /// parameter memberref that does not resolve on the main branch. The arity
    /// is therefore picked at runtime — passing null asks for the same path the
    /// one-argument version returned.
    /// </summary>
    private static string? GetProfileProgressPath(int profileId)
    {
        return TryRead(() =>
        {
            MethodInfo? method = s_progressPathForProfile ??= typeof(ProgressSaveManager).GetMethod(
                "GetProgressPathForProfile",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return null;

            object?[] args = method.GetParameters().Length == 1
                ? new object?[] { profileId }
                : new object?[] { profileId, null };
            return method.Invoke(null, args) as string;
        });
    }

    private static string? ResolveProfileProgressPath(int profileId)
    {
        string? progressPath = GetProfileProgressPath(profileId);
        if (!string.IsNullOrWhiteSpace(progressPath) && Path.IsPathRooted(progressPath))
            return progressPath;

        string profileRoot = GetProfileRootFromProgressPath(progressPath, profileId);

        foreach (string saveRoot in EnumerateSaveRoots())
        {
            string absolutePath = Path.Combine(saveRoot, profileRoot, "saves", "progress.save");
            if (File.Exists(absolutePath))
                return absolutePath;
        }

        return progressPath;
    }

    private static string? ResolveCurrentRunPath(string? progressPath, string profileRoot)
    {
        string? saveDirectory = GetSaveDirectoryFromProgressPath(progressPath);
        if (saveDirectory != null)
        {
            string currentRunPath = Path.Combine(saveDirectory, "current_run.save");
            if (File.Exists(currentRunPath))
                return currentRunPath;
        }

        foreach (string saveRoot in EnumerateSaveRoots())
        {
            string absolutePath = Path.Combine(saveRoot, profileRoot, "saves", "current_run.save");
            if (File.Exists(absolutePath))
                return absolutePath;
        }

        return null;
    }

    private static string? GetSaveDirectoryFromProgressPath(string? progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath) || !Path.IsPathRooted(progressPath))
            return null;

        string? saveDirectory = Path.GetDirectoryName(progressPath);
        return !string.IsNullOrWhiteSpace(saveDirectory) && Directory.Exists(saveDirectory)
            ? saveDirectory
            : null;
    }

    private static object? ToJsonSafe(object? value, int depth, int maxItems)
    {
        if (value == null || depth > 4)
            return value?.ToString();
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;
        if (value is Enum)
            return value.ToString();

        Type type = value.GetType();
        PropertyInfo? entryProperty = type.GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public);
        if (entryProperty?.GetValue(value) is string entry)
            return entry;

        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            int count = 0;
            foreach (DictionaryEntry item in dictionary)
            {
                if (count++ >= maxItems)
                    break;
                result[item.Key?.ToString() ?? "null"] = ToJsonSafe(item.Value, depth + 1, maxItems);
            }
            return result;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var result = new List<object?>();
            int count = 0;
            foreach (object? item in enumerable)
            {
                if (count++ >= maxItems)
                    break;
                result.Add(ToJsonSafe(item, depth + 1, maxItems));
            }
            return result;
        }

        var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;
            TryDo(() =>
            {
                obj[property.Name] = ToJsonSafe(property.GetValue(value), depth + 1, maxItems);
            });
        }
        return obj.Count > 0 ? obj : value.ToString();
    }
}
