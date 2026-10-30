using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Mcp;

public static partial class McpMod
{
    // One process-wide generation covers every player's bag. Reading it adds
    // no bag traversal to the main state endpoint; mutations are rare and pay
    // one atomic increment. It intentionally never resets, so a response from
    // an earlier run cannot accidentally match a later run in this process.
    private static long s_relicBagGeneration = 1;

    private static readonly FieldInfo? s_relicDequesField =
        typeof(RelicGrabBag).GetField("_deques", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? s_relicFallbackField =
        typeof(RelicGrabBag).GetField("_mpFallbackDequeue", BindingFlags.Instance | BindingFlags.NonPublic);

    private static long RelicBagGeneration => Interlocked.Read(ref s_relicBagGeneration);

    private static void MarkRelicBagsChanged()
    {
        Interlocked.Increment(ref s_relicBagGeneration);
    }

    private static int RelicBagCount(RelicGrabBag bag)
    {
        int count = 0;
        if (s_relicDequesField?.GetValue(bag) is Dictionary<RelicRarity, List<RelicModel>> deques)
            count += deques.Values.Sum(deque => deque.Count);
        if (s_relicFallbackField?.GetValue(bag) is List<RelicModel> fallback)
            count += fallback.Count;
        return count;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Harmony discovers TargetMethods/Prefix/Postfix by attribute, not by a C# call.")]
    [HarmonyPatch]
    private static class RelicBagVoidMutationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(RelicGrabBag.Populate),
                nameof(RelicGrabBag.Remove),
                nameof(RelicGrabBag.MoveToFallback),
                nameof(RelicGrabBag.LoadFromSerializable),
            };
            return AccessTools.GetDeclaredMethods(typeof(RelicGrabBag))
                .Where(method => names.Contains(method.Name) && !method.ContainsGenericParameters);
        }

        private static void Postfix()
        {
            MarkRelicBagsChanged();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Harmony discovers TargetMethods/Prefix/Postfix by attribute, not by a C# call.")]
    [HarmonyPatch]
    private static class RelicBagPullMutationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(RelicGrabBag.PullFromFront),
                nameof(RelicGrabBag.PullFromBack),
            };
            // PullFromFront's two-argument overload delegates to the filtered
            // overload. Patch only the widest overload so one removal advances
            // the generation once.
            return AccessTools.GetDeclaredMethods(typeof(RelicGrabBag))
                .Where(method => names.Contains(method.Name))
                .GroupBy(method => method.Name, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(method => method.GetParameters().Length).First());
        }

        private static void Postfix(RelicModel? __result)
        {
            if (__result != null)
                MarkRelicBagsChanged();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Harmony discovers TargetMethods/Prefix/Postfix by attribute, not by a C# call.")]
    [HarmonyPatch(typeof(RelicGrabBag), "RemoveDisallowedRelicsFromDeques")]
    private static class RelicBagPruneMutationPatch
    {
        private static void Prefix(RelicGrabBag __instance, out int __state)
        {
            __state = RelicBagCount(__instance);
        }

        private static void Postfix(RelicGrabBag __instance, int __state)
        {
            // This internal query only removes entries. Comparing the five
            // deque lengths avoids hashing or walking any relic IDs.
            if (RelicBagCount(__instance) != __state)
                MarkRelicBagsChanged();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Harmony discovers TargetMethods/Prefix/Postfix by attribute, not by a C# call.")]
    [HarmonyPatch(typeof(RelicGrabBag), "RefreshRarity")]
    private static class RelicBagRefreshMutationPatch
    {
        private static void Postfix()
        {
            MarkRelicBagsChanged();
        }
    }
}
