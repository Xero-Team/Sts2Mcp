using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using Sts2Mcp.Core.Contracts;

namespace Sts2Mcp;

public static partial class McpMod
{
    public const int DefaultPort = 15526;
    private const string ConfigFileName = "Sts2Mcp.conf";
    private const string DotEnvFileName = ".env";
    private const string PortEnvVar = "STS2_PORT";

    private static ModConfig s_config = new();

    /// <summary>
    /// Values read from Sts2Mcp.conf. Every field has a safe default so a
    /// missing/partial/corrupt file never prevents the mod from starting.
    /// </summary>
    internal sealed class ModConfig
    {
        public int Port = DefaultPort;

        /// <summary>
        /// When true, the mod forces the game's FastMode preference to Instant on
        /// startup so animations resolve immediately. This means get-state polling
        /// no longer observes intermediate animation states, reducing token usage
        /// for AI agents. When false the in-game setting is left untouched.
        /// </summary>
        public bool InstantMode;
    }

    /// <summary>
    /// Loads Sts2Mcp.conf from the mod directory, creating it with defaults if
    /// absent. Unknown keys are ignored; missing or invalid keys fall back to
    /// their defaults. Loading never throws.
    /// </summary>
    private static ModConfig LoadConfig()
    {
        // A config the mod cannot read (bad JSON, unreadable file) falls back to defaults;
        // the read helper records what failed.
        ModConfig? loaded = TryRead<ModConfig?>("config.load", LoadConfigCore);
        if (loaded != null)
            return loaded;

        GD.PrintErr("[STS2 MCP] Failed to load config, using defaults");
        return WithResolvedPort(new ModConfig());
    }

    private static ModConfig LoadConfigCore()
    {
        var config = new ModConfig();
        string? modDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (modDir == null)
            return WithResolvedPort(config);

        string configPath = Path.Combine(modDir, ConfigFileName);
        if (!File.Exists(configPath))
        {
            TryWriteDefaultConfig(configPath, config);
            return WithResolvedPort(config);
        }

        string content = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(content);
        JsonElement root = doc.RootElement;

        // The combat recorder reads its keys ("record", "record_dir") from the same
        // file; keep feeding it the whole document so its options stay where the
        // user wrote them.
        Recorder.CombatRecorder.Configure(root);

        if (root.TryGetProperty("port", out JsonElement portElem)
            && portElem.TryGetInt32(out int port)
            && port is > 0 and <= 65535)
        {
            config.Port = port;
        }
        else
        {
            GD.PrintErr($"[STS2 MCP] Invalid or missing 'port' in {configPath}, using default {DefaultPort}");
        }

        if (root.TryGetProperty("instant_mode", out JsonElement instantElem)
            && instantElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            config.InstantMode = instantElem.GetBoolean();
        }

        return WithResolvedPort(config);
    }

    /// <summary>
    /// Resolves the listening port. This is the ONLY port resolver in the mod; the
    /// dotenv support the AI-Ascension fork added lives here rather than in a second
    /// loader. Precedence, highest first:
    ///
    ///   1. the <c>--sts2-mcp-port=&lt;port&gt;</c> launch argument (McpPortResolver)
    ///   2. dotenv: <c>STS2_PORT</c> in the process environment, then <c>.env</c> next to
    ///      Sts2Mcp.dll, then <c>.env</c> in the game's current directory
    ///   3. the <c>port</c> key of Sts2Mcp.conf
    ///   4. <see cref="DefaultPort"/> (15526)
    ///
    /// A malformed value at any level is logged and skipped so the next source wins.
    /// </summary>
    private static ModConfig WithResolvedPort(ModConfig config)
    {
        int configuredPort = config.Port;
        if (TryResolveDotEnvPort(out int dotEnvPort))
            configuredPort = dotEnvPort;

        config.Port = McpPortResolver.Resolve(Godot.OS.GetCmdlineArgs(), configuredPort);
        return config;
    }

    private static bool TryResolveDotEnvPort(out int port)
    {
        if (TryLoadPortFromEnvironment(out port))
            return true;

        string? modDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);

        return TryLoadPortFromDotEnv(modDir, out port);
    }

    private static bool TryLoadPortFromEnvironment(out int port)
    {
        string? configured = System.Environment.GetEnvironmentVariable(PortEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
        {
            port = 0;
            return false;
        }

        return TryParseConfiguredPort(configured, PortEnvVar, out port);
    }

    private static bool TryLoadPortFromDotEnv(string? modDir, out int port)
    {
        foreach (string envPath in GetDotEnvPaths(modDir))
        {
            try
            {
                if (!File.Exists(envPath))
                    continue;

                foreach (string line in File.ReadLines(envPath))
                {
                    string stripped = line.Trim();
                    if (stripped.Length == 0
                        || stripped.StartsWith('#')
                        || !stripped.Contains('='))
                        continue;

                    int separator = stripped.IndexOf('=');
                    string key = stripped[..separator].Trim();
                    if (key.StartsWith("export ", StringComparison.Ordinal))
                        key = key["export ".Length..].Trim();

                    if (!string.Equals(key, PortEnvVar, StringComparison.Ordinal))
                        continue;

                    string value = ParseDotEnvValue(stripped[(separator + 1)..]);
                    return TryParseConfiguredPort(value, $"{envPath}:{PortEnvVar}", out port);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                GD.PrintErr($"[STS2 MCP] Failed to read {envPath}: {ex.Message}");
            }
        }

        port = 0;
        return false;
    }

    private static IEnumerable<string> GetDotEnvPaths(string? modDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(modDir))
        {
            string modEnvPath = Path.Combine(modDir, DotEnvFileName);
            if (seen.Add(modEnvPath))
                yield return modEnvPath;
        }

        string cwdEnvPath = Path.Combine(System.Environment.CurrentDirectory, DotEnvFileName);
        if (seen.Add(cwdEnvPath))
            yield return cwdEnvPath;
    }

    /// <summary>
    /// Reads one dotenv value: a quoted value ends at its closing quote and may contain
    /// <c>#</c>; an unquoted value ends at a <c>#</c> comment.
    /// </summary>
    private static string ParseDotEnvValue(string rawValue)
    {
        string value = rawValue.Trim();
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"'))
        {
            char quote = value[0];
            int closingQuoteIndex = value.IndexOf(quote, 1);
            if (closingQuoteIndex != -1)
                return value[1..closingQuoteIndex];
        }

        int commentIndex = value.IndexOf('#');
        if (commentIndex >= 0)
            value = value[..commentIndex].TrimEnd();

        return value;
    }

    private static bool TryParseConfiguredPort(string value, string source, out int port)
    {
        if (int.TryParse(value.Trim(), CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535)
            return true;

        GD.PrintErr($"[STS2 MCP] Invalid port value from {source}: {value}; using next configured port source");
        port = 0;
        return false;
    }

    private static void TryWriteDefaultConfig(string configPath, ModConfig config)
    {
        try
        {
            var defaultConfig = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["port"] = config.Port,
                ["instant_mode"] = config.InstantMode,
            };
            string json = JsonSerializer.Serialize(defaultConfig, ContractJson.Options);
            File.WriteAllText(configPath, json);
            GD.Print($"[STS2 MCP] Created default config at {configPath}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            GD.Print($"[STS2 MCP] No config found at {configPath}; using defaults");
        }
    }

    /// <summary>
    /// Persists the instant_mode state back to the config file so an in-game
    /// toggle stays in sync with the conf (and survives restarts). No-op when the
    /// value is unchanged. Called from the settings-UI Harmony patches, which run
    /// on the main thread.
    /// </summary>
    private static void SetInstantModeConfig(bool enabled)
    {
        if (s_config.InstantMode == enabled)
            return;
        s_config.InstantMode = enabled;
        SaveConfig();
    }

    /// <summary>
    /// Writes the current in-memory config back to Sts2Mcp.conf, preserving all
    /// known keys. Best-effort and never fatal.
    /// </summary>
    private static void SaveConfig()
    {
        string? failure = TryAction("config.save", () =>
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (modDir == null)
                return;

            string configPath = Path.Combine(modDir, ConfigFileName);

            // Merge into the file instead of rewriting it: the recorder's "record" and
            // "record_dir" keys are hand-edited and must survive an instant_mode toggle.
            var data = new Dictionary<string, object?>(StringComparer.Ordinal);
            // An unreadable config is rewritten from defaults, so the unknown keys simply
            // start out empty; the read helper records why.
            TryDo("config.merge", () =>
            {
                if (!File.Exists(configPath))
                    return;

                using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(configPath));
                if (existing.RootElement.ValueKind != JsonValueKind.Object)
                    return;

                foreach (JsonProperty prop in existing.RootElement.EnumerateObject())
                {
                    if (prop.Name is "port" or "instant_mode")
                        continue;
                    data[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }
            });

            data["port"] = s_config.Port;
            data["instant_mode"] = s_config.InstantMode;
            File.WriteAllText(configPath, JsonSerializer.Serialize(data, ContractJson.Options));
            GD.Print($"[STS2 MCP] Config saved (instant_mode={s_config.InstantMode})");
        });
        if (failure != null)
            GD.PrintErr($"[STS2 MCP] Failed to save config: {failure}");
    }
}
