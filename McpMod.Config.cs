using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2_MCP;

public static partial class McpMod
{
    public const int DefaultPort = 15526;
    private const string ConfigFileName = "STS2_MCP.conf";

    /// <summary>
    /// Values read from STS2_MCP.conf. Every field has a safe default so a
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
        public bool InstantMode = false;
    }

    private static ModConfig _config = new();

    /// <summary>
    /// Loads STS2_MCP.conf from the mod directory, creating it with defaults if
    /// absent. Unknown keys are ignored; missing or invalid keys fall back to
    /// their defaults. Loading never throws.
    /// </summary>
    private static ModConfig LoadConfig()
    {
        var config = new ModConfig();
        try
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (modDir == null) return WithResolvedPort(config);

            string configPath = Path.Combine(modDir, ConfigFileName);
            if (!File.Exists(configPath))
            {
                TryWriteDefaultConfig(configPath, config);
                return WithResolvedPort(config);
            }

            string content = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // The combat recorder reads its keys ("record", "record_dir") from the same
            // file; keep feeding it the whole document so its options stay where the
            // user wrote them.
            Recorder.CombatRecorder.Configure(root);

            if (root.TryGetProperty("port", out var portElem)
                && portElem.TryGetInt32(out int port)
                && port is > 0 and <= 65535)
            {
                config.Port = port;
            }
            else
            {
                GD.PrintErr($"[STS2 MCP] Invalid or missing 'port' in {configPath}, using default {DefaultPort}");
            }

            if (root.TryGetProperty("instant_mode", out var instantElem)
                && instantElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.InstantMode = instantElem.GetBoolean();
            }

            return WithResolvedPort(config);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to load config: {ex.Message}, using defaults");
            return WithResolvedPort(new ModConfig());
        }
    }

    /// <summary>
    /// Lets a launch argument override the configured port, so several game
    /// processes can run side by side without editing the file.
    /// </summary>
    private static ModConfig WithResolvedPort(ModConfig config)
    {
        config.Port = McpPortResolver.Resolve(Godot.OS.GetCmdlineArgs(), config.Port);
        return config;
    }

    private static void TryWriteDefaultConfig(string configPath, ModConfig config)
    {
        try
        {
            var defaultConfig = new Dictionary<string, object>
            {
                ["port"] = config.Port,
                ["instant_mode"] = config.InstantMode,
            };
            string json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);
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
        if (_config.InstantMode == enabled) return;
        _config.InstantMode = enabled;
        SaveConfig();
    }

    /// <summary>
    /// Writes the current in-memory config back to STS2_MCP.conf, preserving all
    /// known keys. Best-effort and never fatal.
    /// </summary>
    private static void SaveConfig()
    {
        try
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (modDir == null) return;

            string configPath = Path.Combine(modDir, ConfigFileName);

            // Merge into the file instead of rewriting it: the recorder's "record" and
            // "record_dir" keys are hand-edited and must survive an instant_mode toggle.
            var data = new Dictionary<string, object?>();
            try
            {
                if (File.Exists(configPath))
                {
                    using var existing = JsonDocument.Parse(File.ReadAllText(configPath));
                    if (existing.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in existing.RootElement.EnumerateObject())
                        {
                            if (prop.Name is "port" or "instant_mode")
                                continue;
                            data[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] Config not readable, rewriting from defaults: {ex.Message}");
            }

            data["port"] = _config.Port;
            data["instant_mode"] = _config.InstantMode;
            File.WriteAllText(configPath, JsonSerializer.Serialize(data, _jsonOptions));
            GD.Print($"[STS2 MCP] Config saved (instant_mode={_config.InstantMode})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to save config: {ex.Message}");
        }
    }
}