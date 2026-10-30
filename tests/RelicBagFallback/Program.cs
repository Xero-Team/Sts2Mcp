using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Mcp;

const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
const BindingFlags Methods = BindingFlags.Static | BindingFlags.NonPublic;
var bag = new RelicGrabBag();
var deques = (Dictionary<RelicRarity,List<RelicModel>>)typeof(RelicGrabBag).GetField("_deques",Fields)!.GetValue(bag)!;
foreach (var rarity in new[] {RelicRarity.Common,RelicRarity.Uncommon,RelicRarity.Rare,RelicRarity.Shop})
    deques[rarity] = [];
var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
typeof(Player).GetField("<RelicGrabBag>k__BackingField",Fields)!.SetValue(player,bag);
var export = typeof(McpMod).GetMethod("BuildRelicGrabBag",Methods)!;
var result = (Dictionary<string,object?>?)export.Invoke(null,[player]);
if (result == null || !result.TryGetValue("mp_fallback",out var empty) || empty is not List<string> {Count:0})
    throw new Exception("An exhausted real game bag must export an explicit empty fallback queue");
var read = typeof(McpMod).GetMethod("ReadMpFallback",Methods)!;
if (read.Invoke(null,[new object()]) != null)
    throw new Exception("Missing reflection data must stay unknown");
// The same actual bag becomes unreadable if its private fallback queue is absent.
typeof(RelicGrabBag).GetField("_mpFallbackDequeue",Fields)!.SetValue(bag,null);
if (export.Invoke(null,[player]) != null)
    throw new Exception("An unreadable fallback queue must refuse the whole bag export");
Console.WriteLine("3 real game relic bag export checks passed.");
