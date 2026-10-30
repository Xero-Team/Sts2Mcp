using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;

namespace Sts2Mcp;

public static partial class McpMod
{
    private static readonly ConditionalWeakTable<object, CrystalSphereTrace> s_crystalSphereTraces = new();

    private const string CrystalSphereMinigameType =
        "MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Constructed by ConditionalWeakTable.GetOrCreateValue, which the analyzer cannot see.")]
    private sealed class CrystalSphereTrace
    {
        public Dictionary<string, object?>? RngAtStart;
        public readonly List<Dictionary<string, object?>> Clicks = new();
        public int InFlight;
    }

    /// <summary>
    /// Records the event Rng a minigame is built from, before placement spends it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Harmony discovers TargetMethods/Prefix/Postfix by attribute, not by a C# call.")]
    [HarmonyPatch]
    private static class CrystalSphereConstructorPatch
    {
        // Resolved by name, so a rename upstream leaves this patch unapplied
        // rather than throwing out of PatchAll and taking every other patch with it.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = AccessTools.TypeByName(CrystalSphereMinigameType);
            ConstructorInfo? constructor = type == null ? null : AccessTools.GetDeclaredConstructors(type).Find(c => c.GetParameters().Length == 3);
            if (constructor == null)
            {
                GD.Print("[STS2 MCP] CrystalSphereMinigame constructor not found - minigame trace inactive");
                yield break;
            }

            yield return constructor;
        }

        private static void Prefix(object __instance, object rng)
        {
            TryDo(() =>
            {
                CrystalSphereTrace trace = s_crystalSphereTraces.GetOrCreateValue(__instance);
                trace.RngAtStart = RngCoords(rng);
            });
            // Diagnostics must never be able to break the event.
        }
    }

    /// <summary>
    /// Records every divination in order, and whether one is still resolving.
    /// </summary>
    /// <remarks>
    /// <c>CellClicked</c> is async: it awaits each cell it clears, and a curse
    /// reveal awaits a card preview of about two seconds. A client that posts
    /// its next click before the last one finishes would interleave the two
    /// reveal sequences, so the trace says when one is in flight.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Harmony discovers TargetMethods/Prefix/Postfix by attribute, not by a C# call.")]
    [HarmonyPatch]
    private static class CrystalSphereClickPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = AccessTools.TypeByName(CrystalSphereMinigameType);
            MethodInfo? method = type == null ? null : AccessTools.DeclaredMethod(type, "CellClicked");
            if (method == null)
            {
                GD.Print("[STS2 MCP] CrystalSphereMinigame.CellClicked not found - minigame trace inactive");
                yield break;
            }

            yield return method;
        }

        private static void Prefix(object __instance, object clickedCell, out CrystalSphereTrace? __state)
        {
            // Tracing is best-effort: a click that cannot be read leaves the postfix with
            // no state, which is the same as an untraced click.
            __state = TryReadQuiet<CrystalSphereTrace?>(() =>
            {
                if (!s_crystalSphereTraces.TryGetValue(__instance, out CrystalSphereTrace? trace))
                    return null;
                string? tool = GetInstanceMemberValue(__instance, "CrystalSphereTool")?.ToString();
                trace.Clicks.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool"] = string.Equals(tool, "Big", StringComparison.Ordinal) ? "big" : "small",
                    ["x"] = GetInstanceMemberValue(clickedCell, "X"),
                    ["y"] = GetInstanceMemberValue(clickedCell, "Y"),
                });
                Interlocked.Increment(ref trace.InFlight);
                return trace;
            });
        }

        private static void Postfix(ref Task __result, CrystalSphereTrace? __state)
        {
            if (__state == null)
                return;
            __result = SettledAsync(__result, __state);
        }

        // The click task belongs to the game and its continuation must resume on the main
        // thread (it touches the trace and the minigame), so ConfigureAwait(false) is wrong
        // here even though the analyzer only sees a foreign task.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "VSTHRD003:Avoid awaiting foreign Tasks",
            Justification = "The task is the game's own click handler; observing it without starting it is the point.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
            Justification = "The continuation decrements trace state that the game's main thread reads.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0004:Use Task.ConfigureAwait(false)",
            Justification = "Same as CA2007: the continuation belongs on the main thread.")]
        private static async Task SettledAsync(Task click, CrystalSphereTrace trace)
        {
            try
            {
                await click;
            }
            finally
            {
                Interlocked.Decrement(ref trace.InFlight);
            }
        }
    }

    /// <summary>
    /// The trace block for a screen's minigame, or null when it cannot be read.
    /// </summary>
    private static Dictionary<string, object?>? BuildCrystalSphereTrace(NCrystalSphereScreen screen)
        => TryRead("crystal_sphere.trace", () => BuildCrystalSphereTraceCore(screen));

    private static Dictionary<string, object?>? BuildCrystalSphereTraceCore(NCrystalSphereScreen screen)
    {
        s_crystalSphereEntityField ??= typeof(NCrystalSphereScreen)
            .GetField("_entity", BindingFlags.NonPublic | BindingFlags.Instance);
        object? minigame = s_crystalSphereEntityField?.GetValue(screen);
        if (minigame == null)
            return null;
        object? divinations = GetInstanceMemberValue(minigame, "DivinationCount");
        if (divinations == null)
            return null;
        bool traced = s_crystalSphereTraces.TryGetValue(minigame, out CrystalSphereTrace? trace) && trace.RngAtStart != null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rng_at_start"] = traced ? trace!.RngAtStart : null,
            ["divinations_left"] = divinations,
            ["clicks"] = traced ? new List<Dictionary<string, object?>>(trace!.Clicks) : new List<Dictionary<string, object?>>(),
            ["click_in_flight"] = traced && Volatile.Read(ref trace!.InFlight) > 0,
            ["trace_complete"] = traced,
        };
    }
}
