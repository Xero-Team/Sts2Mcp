using System;
using System.Collections.Generic;
using Godot;

namespace Sts2Mcp;

public static partial class McpMod
{
    /// <summary>
    /// What the current state read could not build.
    ///
    /// The state builders read the live game through reflection and swallow whatever
    /// throws, so a member the game renamed or removed simply stops appearing in the
    /// payload. A caller could not tell "this screen has no such thing right now" from
    /// "the mod broke here", and every compatibility break cost a manual bisect to find.
    /// Collecting what was swallowed turns that into one line of the response.
    ///
    /// A plain static needs no lock: both builders run inside RunOnMainThreadBlocking,
    /// so every state read - and everything it calls - happens on the game's main
    /// thread, one read at a time.
    /// </summary>
    private static readonly List<string> s_stateWarnings = new();

    /// <summary>Entries already recorded for this read, so a helper called hundreds of
    /// times (SafeGetText) reports its failure once instead of flooding the payload.</summary>
    private static readonly HashSet<string> s_stateWarningKeys = new(StringComparer.Ordinal);

    private static int s_suppressedStateWarnings;

    /// <summary>A broken screen can warn about every one of its fields; the list is a
    /// diagnostic, not a log, and past this many entries it stops being readable.</summary>
    private const int MaxStateWarnings = 50;

    /// <summary>A game exception message can be a multi-line dump; keep it to one line.</summary>
    private const int MaxWarningMessageLength = 200;

    /// <summary>True between <see cref="BeginStateWarnings"/> and <see cref="EndStateWarnings"/>.
    /// A warning raised outside a state read has no response to ride on, so it goes to the log
    /// instead of a list the next read would clear.</summary>
    private static bool s_stateWarningsActive;

    /// <summary>Contexts already printed, so a helper called per card does not flood the log.
    /// Never cleared: one line per distinct break is what a support report needs.</summary>
    private static readonly HashSet<string> s_loggedWarningKeys = new(StringComparer.Ordinal);

    private const int MaxLoggedWarnings = 100;

    private static void BeginStateWarnings()
    {
        s_stateWarnings.Clear();
        s_stateWarningKeys.Clear();
        s_suppressedStateWarnings = 0;
        s_stateWarningsActive = true;
    }

    /// <summary>
    /// Records that <paramref name="context"/> (a field or section of the state) could not
    /// be built. The stack trace is deliberately left out - the type, the message and the
    /// name of the field are what identify the break, and a trace per field would dwarf
    /// the state itself.
    /// </summary>
    private static void Warn(string context, Exception ex)
        => Warn(context, $"{ex.GetType().Name}: {ex.Message}");

    private static void Warn(string context, string message)
    {
        string entry = $"{context}: {Flatten(message)}";
        if (!s_stateWarningKeys.Add(entry))
            return;

        if (!s_stateWarningsActive)
        {
            LogSwallowed(entry);
            return;
        }

        if (s_stateWarnings.Count >= MaxStateWarnings)
        {
            s_suppressedStateWarnings++;
            return;
        }

        s_stateWarnings.Add(entry);
    }

    private static string Flatten(string message)
    {
        string oneLine = message.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return oneLine.Length > MaxWarningMessageLength
            ? oneLine[..MaxWarningMessageLength] + "..."
            : oneLine;
    }

    private static void LogSwallowed(string entry)
    {
        if (s_loggedWarningKeys.Count >= MaxLoggedWarnings)
            return;
        if (!s_loggedWarningKeys.Add(entry))
            return;

        GD.PrintErr($"[STS2 MCP] swallowed exception: {entry}");
    }

    /// <summary>
    /// Attaches the warnings to a finished state. Absent when nothing was swallowed, so
    /// a caller can treat the key's presence as "some fields below may be missing".
    /// </summary>
    private static void EndStateWarnings(Dictionary<string, object?> result)
    {
        s_stateWarningsActive = false;
        if (s_stateWarnings.Count == 0)
            return;

        var warnings = new List<string>(s_stateWarnings);
        if (s_suppressedStateWarnings > 0)
            warnings.Add($"... {s_suppressedStateWarnings} more warnings suppressed");
        result["warnings"] = warnings;
    }

    /// <summary>
    /// Wraps the singleplayer builder so every one of its ~20 return paths carries the
    /// warnings; stamping them inside the builder would miss the early menu/FTUE returns.
    /// </summary>
    private static Dictionary<string, object?> BuildGameState()
    {
        BeginStateWarnings();
        Dictionary<string, object?> result = BuildGameStateCore();
        EndStateWarnings(result);
        return result;
    }

    private static Dictionary<string, object?> BuildMultiplayerGameState(bool fullDetail)
    {
        BeginStateWarnings();
        Dictionary<string, object?> result = BuildMultiplayerGameStateCore(fullDetail);
        EndStateWarnings(result);
        return result;
    }
}
