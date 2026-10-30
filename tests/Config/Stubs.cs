using System;
using System.Collections.Generic;
using System.Text.Json;

// The two Godot calls the config path makes, recorded instead of executed.
namespace Godot
{
    public static class GD
    {
        public static void Print(string message)
        {
        }

        public static void PrintErr(string message)
        {
        }
    }

    public static class OS
    {
        public static List<string> Cmdline { get; set; } = new();

        public static string[] GetCmdlineArgs() => Cmdline.ToArray();
    }
}

namespace MegaCrit.Sts2.Core.Saves
{
    internal sealed class SaveStub
    {
    }
}

namespace MegaCrit.Sts2.Core.Settings
{
    internal sealed class SettingsStub
    {
    }
}

namespace Sts2Mcp.Recorder
{
    internal static class CombatRecorder
    {
        public static void Configure(JsonElement root)
        {
        }
    }
}

namespace Sts2Mcp
{
    // The production helpers live in McpMod.Helpers.cs, which needs the game; the
    // harness keeps the same contract: a failed read is absent and never fatal.
    public static partial class McpMod
    {
        internal static T? TryRead<T>(string context, Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return default;
            }
        }

        internal static bool TryDo(string context, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string? TryAction(string context, Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void Warn(string context, Exception ex)
        {
        }
    }
}
