using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed class CrystalSphereTrace
    {
        public Dictionary<string, object?>? RngAtStart;
        public readonly List<Dictionary<string, object?>> Clicks = new();
        public int InFlight;
    }

    private static readonly ConditionalWeakTable<object, CrystalSphereTrace> CrystalSphereTraces = new();

    private const string CrystalSphereMinigameType =
        "MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame";

    /// <summary>
    /// Records the event Rng a minigame is built from, before placement spends it.
    /// </summary>
    [HarmonyPatch]
    private static class CrystalSphereConstructorPatch
    {
        // Resolved by name, so a rename upstream leaves this patch unapplied
        // rather than throwing out of PatchAll and taking every other patch with it.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName(CrystalSphereMinigameType);
            var constructor = type == null ? null : AccessTools.GetDeclaredConstructors(type).Find(c => c.GetParameters().Length == 3);
            if (constructor == null)
            {
                GD.Print("[STS2 MCP] CrystalSphereMinigame constructor not found - minigame trace inactive");
                yield break;
            }

            yield return constructor;
        }

        private static void Prefix(object __instance, object rng)
        {
            try
            {
                var trace = CrystalSphereTraces.GetOrCreateValue(__instance);
                trace.RngAtStart = RngCoords(rng);
            }
            catch
            {
                // Diagnostics must never be able to break the event.
            }
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
    [HarmonyPatch]
    private static class CrystalSphereClickPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName(CrystalSphereMinigameType);
            var method = type == null ? null : AccessTools.DeclaredMethod(type, "CellClicked");
            if (method == null)
            {
                GD.Print("[STS2 MCP] CrystalSphereMinigame.CellClicked not found - minigame trace inactive");
                yield break;
            }

            yield return method;
        }

        private static void Prefix(object __instance, object clickedCell, out CrystalSphereTrace? __state)
        {
            __state = null;
            try
            {
                if (!CrystalSphereTraces.TryGetValue(__instance, out var trace))
                    return;
                var tool = GetInstanceMemberValue(__instance, "CrystalSphereTool")?.ToString();
                trace.Clicks.Add(new Dictionary<string, object?>
                {
                    ["tool"] = tool == "Big" ? "big" : "small",
                    ["x"] = GetInstanceMemberValue(clickedCell, "X"),
                    ["y"] = GetInstanceMemberValue(clickedCell, "Y"),
                });
                Interlocked.Increment(ref trace.InFlight);
                __state = trace;
            }
            catch
            {
                __state = null;
            }
        }

        private static void Postfix(ref Task __result, CrystalSphereTrace? __state)
        {
            if (__state == null)
                return;
            __result = Settled(__result, __state);
        }

        private static async Task Settled(Task click, CrystalSphereTrace trace)
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
    {
        try
        {
            _crystalSphereEntityField ??= typeof(NCrystalSphereScreen)
                .GetField("_entity", BindingFlags.NonPublic | BindingFlags.Instance);
            var minigame = _crystalSphereEntityField?.GetValue(screen);
            if (minigame == null)
                return null;
            var divinations = GetInstanceMemberValue(minigame, "DivinationCount");
            if (divinations == null)
                return null;
            var traced = CrystalSphereTraces.TryGetValue(minigame, out var trace) && trace.RngAtStart != null;
            return new Dictionary<string, object?>
            {
                ["rng_at_start"] = traced ? trace!.RngAtStart : null,
                ["divinations_left"] = divinations,
                ["clicks"] = traced ? new List<Dictionary<string, object?>>(trace!.Clicks) : new List<Dictionary<string, object?>>(),
                ["click_in_flight"] = traced && Volatile.Read(ref trace!.InFlight) > 0,
                ["trace_complete"] = traced,
            };
        }
        catch
        {
            return null;
        }
    }
}
