using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2Mcp.Core.Contracts;

namespace Sts2Mcp;

public static partial class McpMod
{
    private const string RunNotInProgressErrorCode = "run_not_in_progress";
    private const string RunStateUnavailableErrorCode = "run_state_unavailable";

    private static void HandleGetSettings(HttpListenerResponse response)
        => RunHandler(response, () => SendReadResultJson(response, RunOnMainThreadBlocking(BuildSettings)),
            "Failed to read settings", "settings_read_failed");

    /// <summary>
    /// Reads the saved settings into <see cref="SettingsResponse"/>. Returns <see cref="ErrorResponse"/>
    /// when the save manager or either save object is missing; the transport maps that to a status.
    /// </summary>
    internal static object BuildSettings()
    {
        SaveManager sm = SaveManager.Instance;
        if (sm == null)
            return new ErrorResponse { Error = "Save manager is not available", ErrorCode = "save_manager_unavailable" };

        SettingsSave settings = sm.SettingsSave;
        PrefsSave prefs = sm.PrefsSave;
        if (settings == null || prefs == null)
            return new ErrorResponse { Error = "Settings data is not available", ErrorCode = "settings_data_unavailable" };

        return new SettingsResponse
        {
            Display = new SettingsDisplay
            {
                Fullscreen = settings.Fullscreen,
                Resolution = $"{settings.WindowSize.X}x{settings.WindowSize.Y}",
                FpsLimit = settings.FpsLimit,
                Vsync = settings.VSync.ToString(),
                Msaa = settings.Msaa,
                AspectRatio = settings.AspectRatioSetting.ToString(),
                TargetDisplay = settings.TargetDisplay,
                LimitFpsBackground = settings.LimitFpsInBackground,
            },
            Audio = new SettingsAudio
            {
                Master = settings.VolumeMaster,
                Bgm = settings.VolumeBgm,
                Sfx = settings.VolumeSfx,
                Ambience = settings.VolumeAmbience,
            },
            Gameplay = new SettingsGameplay
            {
                FastMode = prefs.FastMode.ToString(),
                ScreenShake = prefs.ScreenShakeOptionIndex,
                ShowRunTimer = prefs.ShowRunTimer,
                ShowCardIndices = prefs.ShowCardIndices,
                TextEffects = prefs.TextEffectsEnabled,
                LongPress = prefs.IsLongPressEnabled,
            },
            Mods = new SettingsMods { Enabled = settings.ModSettings?.PlayerAgreedToModLoading ?? false },
            Language = settings.Language,
            SkipIntro = settings.SkipIntroLogo,
        };
    }

    private static void HandleGetBestiary(HttpListenerResponse response)
        => RunHandler(response, () => SendJson(response, RunOnMainThreadBlocking(BuildBestiary)),
            "Failed to build bestiary", "bestiary_build_failed");

    private static void HandleGetGlossaryCards(HttpListenerResponse response)
        => RunHandler(response, () => SendGlossaryJson(response, RunOnMainThreadBlocking(() => BuildGlossaryPayload("cards", BuildGlossaryCards))),
            "Failed to build glossary", "glossary_build_failed");

    private static void HandleGetGlossaryKeywords(HttpListenerResponse response)
        => RunHandler(response, () => SendGlossaryJson(response, RunOnMainThreadBlocking(() => BuildGlossaryPayload("keywords", BuildGlossaryKeywords))),
            "Failed to build glossary", "glossary_build_failed");

    private static void HandleGetGlossaryPotions(HttpListenerResponse response)
        => RunHandler(response, () => SendGlossaryJson(response, RunOnMainThreadBlocking(() => BuildGlossaryPayload("potions", BuildGlossaryPotions))),
            "Failed to build glossary", "glossary_build_failed");

    private static void HandleGetGlossaryRelics(HttpListenerResponse response)
        => RunHandler(response, () => SendGlossaryJson(response, RunOnMainThreadBlocking(() => BuildGlossaryPayload("relics", BuildGlossaryRelics))),
            "Failed to build glossary", "glossary_build_failed");

    private sealed class GlossaryPayload
    {
        public string Kind { get; init; } = "";
        public object Data { get; init; } = new();
        public int ProfileId { get; init; }
        public string? ProgressPath { get; init; }
        public string? ResolvedProgressPath { get; init; }
        public string ProfileRoot { get; init; } = "";
        public string SaveScope { get; init; } = "vanilla";
        public string NetType { get; init; } = "";
        public List<Dictionary<string, object?>> Players { get; init; } = new();
    }

    private static GlossaryPayload BuildGlossaryPayload(string kind, Func<object> buildData)
    {
        int profileId = SaveManager.Instance?.CurrentProfileId ?? 0;
        string? progressPath = GetProfileProgressPath(profileId);
        string? resolvedProgressPath = ResolveProfileProgressPath(profileId);
        string profileRoot = GetProfileRootFromProgressPath(progressPath, profileId);

        object data = buildData();
        if (data is Dictionary<string, object?> error && error.TryGetValue("error_code", out _))
        {
            return new GlossaryPayload
            {
                Kind = kind,
                Data = error,
                ProfileId = profileId,
                ProgressPath = progressPath,
                ResolvedProgressPath = resolvedProgressPath,
                ProfileRoot = profileRoot,
                SaveScope = GetSaveScope(profileRoot),
                NetType = SafeGetNetTypeName()
            };
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        List<Dictionary<string, object?>> players = runState?.Players.Select((player, index) => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = index,
            ["character"] = player.Character?.Id.Entry,
            ["character_name"] = SafeGetText(() => player.Character?.Title)
        }).ToList() ?? new List<Dictionary<string, object?>>();

        return new GlossaryPayload
        {
            Kind = kind,
            Data = data,
            ProfileId = profileId,
            ProgressPath = progressPath,
            ResolvedProgressPath = resolvedProgressPath,
            ProfileRoot = profileRoot,
            SaveScope = GetSaveScope(profileRoot),
            NetType = SafeGetNetTypeName(),
            Players = players
        };
    }

    private static string SafeGetNetTypeName()
    {
        return TryRead(() => RunManager.Instance?.NetService?.Type.ToString() ?? "") ?? "";
    }

    private static void SendGlossaryJson(HttpListenerResponse response, GlossaryPayload payload)
    {
        object data = payload.Data;
        if (data is Dictionary<string, object?> error
            && error.TryGetValue("error_code", out object? errorCode))
        {
            response.StatusCode = (errorCode as string) switch
            {
                RunNotInProgressErrorCode => 409,
                RunStateUnavailableErrorCode => 503,
                _ => 500
            };

            error["kind"] = payload.Kind;
            error["scope"] = "active_run";
            error["profile_id"] = payload.ProfileId;
            error["progress_path"] = NormalizePathForJson(payload.ProgressPath);
            error["resolved_progress_path"] = NormalizePathForJson(payload.ResolvedProgressPath);
            error["profile_root"] = NormalizePathForJson(payload.ProfileRoot);
            error["save_scope"] = payload.SaveScope;
            error["net_type"] = payload.NetType;
            SendJson(response, data);
            return;
        }

        if (data is not List<Dictionary<string, object?>> items)
        {
            SendJson(response, data);
            return;
        }

        SendJson(response, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["kind"] = payload.Kind,
            ["scope"] = "active_run",
            ["count"] = items.Count,
            ["profile_id"] = payload.ProfileId,
            ["progress_path"] = NormalizePathForJson(payload.ProgressPath),
            ["resolved_progress_path"] = NormalizePathForJson(payload.ResolvedProgressPath),
            ["profile_root"] = NormalizePathForJson(payload.ProfileRoot),
            ["save_scope"] = payload.SaveScope,
            ["current_run"] = BuildCurrentRunContext(
                isRunInProgress: true,
                profileId: payload.ProfileId,
                saveScope: payload.SaveScope,
                progressPath: payload.ProgressPath,
                profileRoot: payload.ProfileRoot),
            ["net_type"] = payload.NetType,
            ["players"] = payload.Players,
            ["items"] = items
        });
    }

    private static Dictionary<string, object?> GlossaryError(string message, string errorCode)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "error",
            ["error"] = message,
            ["error_code"] = errorCode
        };
    }

    internal static object BuildGlossaryCards()
    {
        if (RunManager.Instance?.IsInProgress != true)
            return GlossaryError("No run in progress.", RunNotInProgressErrorCode);

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return GlossaryError("Could not read run state.", RunStateUnavailableErrorCode);

        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (CardPoolModel pool in ModelDb.AllSharedCardPools)
            AddCardsFromPool(pool, SafeGetText(() => pool.Title) ?? pool.Id.Entry, result, seen);

        foreach (Player player in runState.Players)
        {
            CardPoolModel? pool = player.Character?.CardPool;
            if (pool == null)
                continue;
            string poolName = SafeGetText(() => pool.Title) ?? "Unknown";
            AddCardsFromPool(pool, poolName, result, seen);
        }

        return SortDictionaryListByStringField(result, "id");
    }

    private static void AddCardsFromPool(
        CardPoolModel pool,
        string poolName,
        List<Dictionary<string, object?>> result,
        HashSet<string> seen)
    {
        foreach (CardModel card in pool.AllCards)
        {
            string id = card.Id.Entry;
            if (seen.Contains(id))
                continue;
            seen.Add(id);

            CardModel? upgradedPreview = SafeBuildUpgradedCardPreview(card);
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["cost"] = GetCostDisplay(card),
                ["star_cost"] = GetStarCostDisplay(card),
                ["description"] = SafeGetCardDescription(card),
                ["rarity"] = card.Rarity.ToString(),
                ["pool"] = poolName,
                ["is_upgraded"] = card.IsUpgraded,
                ["is_upgradable"] = card.IsUpgradable,
                ["current_upgrade_level"] = card.CurrentUpgradeLevel,
                ["max_upgrade_level"] = card.MaxUpgradeLevel,
                ["upgrade_preview_type"] = card.UpgradePreviewType.ToString(),
                ["upgrade_preview_cost"] = upgradedPreview != null ? GetCostDisplay(upgradedPreview) : null,
                ["upgrade_preview_star_cost"] = upgradedPreview != null ? GetStarCostDisplay(upgradedPreview) : null,
                ["upgrade_preview_description"] = SafeGetCardUpgradePreviewDescription(card),
                ["keywords"] = BuildHoverTips(card.HoverTips)
            });
        }
    }

    internal static object BuildGlossaryRelics()
    {
        if (RunManager.Instance?.IsInProgress != true)
            return GlossaryError("No run in progress.", RunNotInProgressErrorCode);

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return GlossaryError("Could not read run state.", RunStateUnavailableErrorCode);

        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        SharedRelicPool sharedPool = ModelDb.RelicPool<SharedRelicPool>();
        AddRelicsFromPool(sharedPool, "Shared", result, seen);

        foreach (CharacterModel? character in runState.Players.Select(player => player.Character))
        {
            if (character == null || character.RelicPool == null)
                continue;
            RelicPoolModel pool = character.RelicPool;
            string poolName = SafeGetText(() => character.Title) ?? "Unknown";
            AddRelicsFromPool(pool, poolName, result, seen);
        }

        return SortDictionaryListByStringField(result, "id");
    }

    private static void AddRelicsFromPool(
        RelicPoolModel pool,
        string poolName,
        List<Dictionary<string, object?>> result,
        HashSet<string> seen)
    {
        foreach (RelicModel relic in pool.AllRelics)
        {
            string id = relic.Id.Entry;
            if (seen.Contains(id))
                continue;
            seen.Add(id);

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["rarity"] = relic.Rarity.ToString(),
                ["pool"] = poolName,
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
        }
    }

    internal static object BuildGlossaryPotions()
    {
        if (RunManager.Instance?.IsInProgress != true)
            return GlossaryError("No run in progress.", RunNotInProgressErrorCode);

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return GlossaryError("Could not read run state.", RunStateUnavailableErrorCode);

        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        SharedPotionPool sharedPool = ModelDb.PotionPool<SharedPotionPool>();
        AddPotionsFromPool(sharedPool, "Shared", result, seen);

        foreach (CharacterModel? character in runState.Players.Select(player => player.Character))
        {
            if (character == null || character.PotionPool == null)
                continue;
            PotionPoolModel pool = character.PotionPool;
            string poolName = SafeGetText(() => character.Title) ?? "Unknown";
            AddPotionsFromPool(pool, poolName, result, seen);
        }

        return SortDictionaryListByStringField(result, "id");
    }

    private static void AddPotionsFromPool(
        PotionPoolModel pool,
        string poolName,
        List<Dictionary<string, object?>> result,
        HashSet<string> seen)
    {
        foreach (PotionModel potion in pool.AllPotions)
        {
            string id = potion.Id.Entry;
            if (seen.Contains(id))
                continue;
            seen.Add(id);

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = SafeGetText(() => potion.Title),
                ["description"] = SafeGetText(() => potion.DynamicDescription),
                ["rarity"] = potion.Rarity.ToString(),
                ["target_type"] = potion.TargetType.ToString(),
                ["usage"] = potion.Usage.ToString(),
                ["pool"] = poolName,
                ["keywords"] = BuildHoverTips(potion.ExtraHoverTips)
            });
        }
    }

    internal static object BuildGlossaryKeywords()
    {
        if (RunManager.Instance?.IsInProgress != true)
            return GlossaryError("No run in progress.", RunNotInProgressErrorCode);

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return GlossaryError("Could not read run state.", RunStateUnavailableErrorCode);

        var keywords = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (CardPoolModel pool in ModelDb.AllSharedCardPools)
            foreach (CardModel card in pool.AllCards)
                AddKeywordTips(card.HoverTips, keywords);
        foreach (RelicModel relic in ModelDb.RelicPool<SharedRelicPool>().AllRelics)
            AddKeywordTips(relic.HoverTipsExcludingRelic, keywords);
        foreach (PotionModel potion in ModelDb.PotionPool<SharedPotionPool>().AllPotions)
            AddKeywordTips(potion.ExtraHoverTips, keywords);

        foreach (CharacterModel? character in runState.Players.Select(player => player.Character))
        {
            CardPoolModel? cardPool = character?.CardPool;
            if (cardPool != null)
            {
                foreach (CardModel card in cardPool.AllCards)
                    AddKeywordTips(card.HoverTips, keywords);
            }

            RelicPoolModel? relicPool = character?.RelicPool;
            if (relicPool != null)
            {
                foreach (RelicModel relic in relicPool.AllRelics)
                    AddKeywordTips(relic.HoverTipsExcludingRelic, keywords);
            }

            PotionPoolModel? potionPool = character?.PotionPool;
            if (potionPool != null)
            {
                foreach (PotionModel potion in potionPool.AllPotions)
                    AddKeywordTips(potion.ExtraHoverTips, keywords);
            }
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<string, string> kv in keywords.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = kv.Key,
                ["description"] = kv.Value
            });
        }

        return result;
    }

    private static void AddKeywordTips(IEnumerable<IHoverTip>? tips, Dictionary<string, string> keywords)
    {
        if (tips == null)
            return;

        foreach (IHoverTip tip in IHoverTip.RemoveDupes(tips))
        {
            if (tip is not HoverTip ht)
                continue;

            // Both reads already treat a throwing hover-tip getter (state transitions) as
            // absent, so an unstable tip is skipped without a second guard.
            string? title = SafeGetText(() => ht.Title);
            if (!string.IsNullOrEmpty(title))
                keywords.TryAdd(title, SafeGetText(() => ht.Description) ?? "");
        }
    }

    internal static object BuildBestiary()
    {
        List<Dictionary<string, object?>> monsters = BuildBestiaryMonsters();
        List<Dictionary<string, object?>> encounters = BuildBestiaryEncounters(monsters);

        List<Dictionary<string, object?>> orderedMonsters = monsters
            .OrderBy(entry => entry.TryGetValue("id", out object? id) ? id?.ToString() : "", StringComparer.Ordinal)
            .ToList();
        List<Dictionary<string, object?>> orderedEncounters = encounters
            .OrderBy(entry => entry.TryGetValue("id", out object? id) ? id?.ToString() : "", StringComparer.Ordinal)
            .ToList();

        return BestiaryPayload(orderedMonsters, orderedEncounters);
    }

    private static Dictionary<string, object?> BestiaryPayload(
        List<Dictionary<string, object?>> monsters, List<Dictionary<string, object?>> encounters)
        => new(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["kind"] = "bestiary",
            ["source"] = "reflected MonsterModel and EncounterModel metadata",
            ["monster_count"] = monsters.Count,
            ["encounter_count"] = encounters.Count,
            ["monsters"] = monsters,
            ["encounters"] = encounters
        };

    private static List<Dictionary<string, object?>> BuildBestiaryMonsters()
    {
        BindingFlags bindFlags = BestiaryBindingFlags();
        var monsters = new List<Dictionary<string, object?>>();
        foreach (Type type in typeof(MonsterModel).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(MonsterModel)) || type.FullName!.Contains('+'))
                continue;

            Dictionary<string, object?> entry = BestiaryEntry(type);

            TryDo(() =>
            {
                object instance = RuntimeHelpers.GetUninitializedObject(type);
                object? minHp = type.GetProperty("MinInitialHp")?.GetValue(instance);
                object? maxHp = type.GetProperty("MaxInitialHp")?.GetValue(instance);
                if (minHp != null)
                    entry["min_hp"] = minHp;
                if (maxHp != null)
                    entry["max_hp"] = maxHp;
            });

            var moves = new List<string>();
            foreach (MethodInfo method in type.GetMethods(bindFlags))
            {
                if (method.Name.EndsWith("Move", StringComparison.Ordinal)
                    && method.DeclaringType == type
                    && !string.Equals(method.Name, "PerformMove", StringComparison.Ordinal)
                    && !string.Equals(method.Name, "RollMove", StringComparison.Ordinal)
                    && !string.Equals(method.Name, "SetMoveImmediate", StringComparison.Ordinal))
                {
                    moves.Add(method.Name.Replace("Move", ""));
                }
            }
            AddBestiaryList(entry, "moves", moves);

            monsters.Add(entry);
        }
        return monsters;
    }

    private static List<Dictionary<string, object?>> BuildBestiaryEncounters(
        List<Dictionary<string, object?>> monsters)
    {
        var encounters = new List<Dictionary<string, object?>>();
        foreach (Type type in typeof(EncounterModel).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(EncounterModel)) || type.FullName!.Contains('+'))
                continue;

            Dictionary<string, object?> entry = BestiaryEntry(type);

            TryDo(() =>
            {
                object instance = RuntimeHelpers.GetUninitializedObject(type);
                object? roomType = type.GetProperty("RoomType")?.GetValue(instance);
                object? isWeak = type.GetProperty("IsWeak")?.GetValue(instance);
                object? minGold = type.GetProperty("MinGoldReward")?.GetValue(instance);
                object? maxGold = type.GetProperty("MaxGoldReward")?.GetValue(instance);
                if (roomType != null)
                    entry["room_type"] = roomType.ToString();
                if (isWeak != null)
                    entry["is_weak"] = isWeak;
                if (minGold != null)
                    entry["min_gold"] = minGold;
                if (maxGold != null)
                    entry["max_gold"] = maxGold;
            });

            string baseName = type.Name.Replace("Normal", "").Replace("Weak", "").Replace("Elite", "").Replace("Boss", "");
            var matchingMonsters = new List<string>();
            foreach (Dictionary<string, object?> monsterEntry in monsters)
            {
                string monsterClass = monsterEntry["class"] as string ?? "";
                if (baseName.Contains(monsterClass) || monsterClass.Contains(baseName.TrimEnd('s')))
                    matchingMonsters.Add(monsterClass);
            }
            AddBestiaryList(entry, "likely_monsters", matchingMonsters);

            encounters.Add(entry);
        }
        return encounters;
    }

    private static System.Reflection.BindingFlags BestiaryBindingFlags()
        => System.Reflection.BindingFlags.Public
           | System.Reflection.BindingFlags.NonPublic
           | System.Reflection.BindingFlags.Instance;

    /// <summary>One reflected model, identified by its class name and slugified id.</summary>
    private static Dictionary<string, object?> BestiaryEntry(Type type)
        => new(StringComparer.Ordinal)
        {
            ["id"] = ModelId.SlugifyCategory(type.Name),
            ["class"] = type.Name,
        };

    /// <summary>Sorts, de-duplicates and stores a name list, only when there is one.</summary>
    private static void AddBestiaryList(
        Dictionary<string, object?> entry, string key, List<string> names)
    {
        if (names.Count == 0)
            return;
        entry[key] = names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }
}
