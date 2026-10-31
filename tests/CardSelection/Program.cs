using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Sts2Mcp.Tests.CardSelection;

/// <summary>
/// Pins the card-selection context the mod publishes over HTTP: which screen a
/// selection came from, which effect the caller expects, and the state dictionary
/// the next state read carries. Pure mapping plus one synthetic patch call.
/// </summary>
internal static class Program
{
    private static readonly BindingFlags Private =
        BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly MethodInfo s_surface =
        typeof(McpMod).GetMethod("SelectionSurface", Private)
        ?? throw new InvalidOperationException("McpMod.SelectionSurface not found");

    private static readonly MethodInfo s_originKind =
        typeof(McpMod).GetMethod("SelectionOriginKind", Private)
        ?? throw new InvalidOperationException("McpMod.SelectionOriginKind not found");

    private static readonly MethodInfo s_effect =
        typeof(McpMod).GetMethod("SelectionEffect", Private)
        ?? throw new InvalidOperationException("McpMod.SelectionEffect not found");

    private static readonly MethodInfo s_prefix =
        typeof(McpMod).GetNestedType("CardSelectionContextPatch", BindingFlags.NonPublic)
            ?.GetMethod("Prefix", Private)
        ?? throw new InvalidOperationException("CardSelectionContextPatch.Prefix not found");

    private static readonly MethodInfo s_addContext =
        typeof(McpMod).GetMethod("AddCardSelectionContext", Private)
        ?? throw new InvalidOperationException("McpMod.AddCardSelectionContext not found");

    private static int s_failures;

    private static int Main()
    {
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        // --- SelectionSurface: the method-name prefix decides the screen -------------
        Surface("FromHandForDiscard", "hand");
        Surface("FromHandForUpgrade", "hand");
        Surface("FromCombatPileForExhaust", "combat_pile");
        Surface("FromChooseACard", "generated_choice");
        Surface("FromChooseABundle", "generated_bundle");
        Surface("FromDeckForUpgrade", "deck");
        Surface("FromSomethingElse", "simple_grid");

        // --- SelectionOriginKind: the model namespace decides the kind --------------
        Origin(typeof(MegaCrit.Sts2.Core.Models.Cards.Anger), "card");
        Origin(typeof(MegaCrit.Sts2.Core.Models.Potions.FirePotion), "potion");
        Origin(typeof(MegaCrit.Sts2.Core.Models.Powers.StrengthPower), "power");
        Origin(typeof(MegaCrit.Sts2.Core.Models.Relics.Anchor), "relic");
        Origin(typeof(MegaCrit.Sts2.Core.Models.Monsters.Cultist), "monster");
        Origin(typeof(MegaCrit.Sts2.Core.Models.Events.PunchOff), "event");
        Origin(typeof(Sts2Mcp.Tests.CardSelection.Unrelated), "model");
        Equal(s_originKind.Invoke(null, new object?[] { null }), null, "null type has no origin kind");

        // --- SelectionEffect: exact method first, then the origin type -------------
        Effect("FromHandForDiscard", null, "discard");
        Effect("FromHandForUpgrade", null, "upgrade");
        Effect("FromDeck", "Headbutt", "discard_to_draw_top");
        Effect("FromDeck", "Ashwater", "exhaust");
        Effect("FromDeck", "EntropyPower", "transform");
        Effect("FromDeck", "Begone", "hand_transform_minion_strike");
        Effect("FromDeck", "Guards", "hand_transform_minion_sacrifice");
        Effect("FromDeck", "KnowledgeDemon", "knowledge_curse");
        Effect("FromDeck", "Acrobatics", "discard");
        Effect("FromChooseACard", "UnknownCard", "generated_to_hand");
        Effect("FromDeck", "UnknownCard", "origin_specific");
        Effect("FromChooseACard", null, "generated_to_hand");

        // --- Prefix -> AddCardSelectionContext: the published state -----------------
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        s_addContext.Invoke(null, new object?[] { state });
        Equal(state.Count, 0, "no active selection adds nothing");

        var card = new MegaCrit.Sts2.Core.Models.Cards.Anger { IsUpgraded = true };
        card.Id.Entry = "ANGER";
        var prefs = new MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs
        {
            MinSelect = 1,
            MaxSelect = 2,
            RequireManualConfirmation = true,
            Cancelable = false,
        };
        var pile = new MegaCrit.Sts2.Core.Entities.Cards.CardPile
        {
            Type = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
        };
        long firstId = Select(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromHandForDiscard", prefs, card, pile);

        state = new Dictionary<string, object?>(StringComparer.Ordinal);
        s_addContext.Invoke(null, new object?[] { state });
        Equal(state["selection_id"], firstId, "selection id is published");
        Equal(state["selection_entry_point"], "FromHandForDiscard", "entry point");
        Equal(state["selection_surface"], "hand", "surface");
        Equal(state["selection_effect"], "discard", "effect");
        Equal(state["min_select"], 1, "min select");
        Equal(state["max_select"], 2, "max select");
        Equal(state["manual_confirmation"], true, "manual confirmation");
        Equal(state["cancelable"], false, "cancelable");
        Equal(state["source_pile"], "hand", "source pile");
        Equal(state["origin_id"], "ANGER", "origin id");
        Equal(state["origin_type"], "Anger", "origin type");
        Equal(state["origin_kind"], "card", "origin kind");
        Equal(state["origin_upgraded"], true, "origin upgraded");

        // A second selection replaces the context and gets a higher id, so a client
        // holding an old id can tell it is stale.
        long secondId = Select(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromDeck", null, null, null);
        state = new Dictionary<string, object?>(StringComparer.Ordinal);
        s_addContext.Invoke(null, new object?[] { state });
        Equal(secondId > firstId, true, "selection id increases");
        Equal(state["selection_id"], secondId, "newest selection id wins");
        Equal(state["min_select"], 1, "absent prefs default min");
        Equal(state["max_select"], 1, "absent prefs default max");
        Equal(state["manual_confirmation"], false, "absent prefs default manual");
        Equal(state["cancelable"], false, "absent prefs default cancelable");
        Equal(state.ContainsKey("source_pile"), false, "absent pile omits the key");
        Equal(state.ContainsKey("origin_id"), false, "absent source omits origin id");
        Equal(state.ContainsKey("origin_type"), false, "absent source omits origin type");
        Equal(state.ContainsKey("origin_kind"), false, "absent source omits origin kind");
        Equal(state["origin_upgraded"], false, "absent source is not upgraded");

        if (s_failures > 0)
        {
            Console.WriteLine($"FAIL {s_failures} card-selection check(s)");
            return 1;
        }

        Console.WriteLine("ALL OK (card selection context)");
        return 0;
    }

    private static long Select(Type commandType, string methodName, object? prefs, object? source, object? pile)
    {
        MethodInfo method = commandType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{commandType.Name}.{methodName} stub missing");
        object?[] args = { prefs!, source!, pile! };
        s_prefix.Invoke(null, new object?[] { method, args });
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        s_addContext.Invoke(null, new object?[] { state });
        return (long)state["selection_id"]!;
    }

    private static void Surface(string methodName, string expect)
        => Equal(s_surface.Invoke(null, new object?[] { methodName }), expect, $"surface {methodName}");

    private static void Origin(Type? type, string? expect)
        => Equal(s_originKind.Invoke(null, new object?[] { type }), expect, $"origin kind {type?.Name ?? "null"}");

    private static void Effect(string methodName, string? originType, string expect)
        => Equal(s_effect.Invoke(null, new object?[] { methodName, originType }), expect,
            $"effect {methodName}/{originType ?? "null"}");

    private static void Equal<T>(T actual, T expected, string name)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            Console.WriteLine($"ok   {name}");
            return;
        }

        s_failures++;
        Console.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
    }
}
