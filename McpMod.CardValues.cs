using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Mcp;

public static partial class McpMod
{
    // Each state build owns its value mode on the serialized Godot main thread.
    // A best-effort DTO catch cannot hide a failed unpowered card preview.
    private static T WithCardValues<T>(bool unpowered, Func<T> build)
    {
        bool previous = s_unpoweredValues;
        Exception? previousFailure = s_unpoweredCardFailure;
        s_unpoweredValues = unpowered;
        s_unpoweredCardFailure = null;
        try
        {
            T? state = build();
            if (s_unpoweredCardFailure != null)
                throw s_unpoweredCardFailure;
            return state;
        }
        finally
        {
            s_unpoweredValues = previous;
            s_unpoweredCardFailure = previousFailure;
        }
    }

    private static Exception? s_unpoweredCardFailure;

    private static Exception RefuseUnpoweredCardValues(CardModel card, Exception error)
    {
        s_unpoweredCardFailure ??= new InvalidOperationException(
            $"Failed to render unpowered values for card {card.Id.Entry}: {error.Message}", error);
        return s_unpoweredCardFailure;
    }

    // Card descriptions arrive multi-line; the JSON export keeps them on one line.
    private const char NewLine = '\n';

    // Whether the current state build renders card and intent numbers WITHOUT the
    // player's standing powers and relics applied. Set for the duration of one
    // main-thread build by HandleGetState/HandleGetMultiplayerState; see the
    // `values=powered|unpowered` query parameter.
    //
    // A plain static is safe: every build runs inside RunOnMainThread, so
    // concurrent HTTP requests serialize onto the one Godot main thread.
    private static bool s_unpoweredValues;

    private static string? SafeGetCardDescription(CardModel card, PileType pile = PileType.Hand)
    {
        if (s_unpoweredValues)
        {
            try
            {
                using var scope = UnpoweredCardVars.Apply(card);
                return CleanCardDescription(card.GetDescriptionForPile(pile));
            }
            catch (Exception error) { throw RefuseUnpoweredCardValues(card, error); }
        }
        // A rendered description that cannot be produced falls back to the model's own text.
        return TryRead("card.description",
                   () => CleanCardDescription(card.GetDescriptionForPile(pile)))
               ?? SafeGetText(() => card.Description)?.Replace(NewLine, ' ');
    }

    private static string CleanCardDescription(string description)
        => StripRichTextTags(description).Replace(NewLine, ' ');

    /// <summary>
    /// Renders a card's DynamicVars at base + enchantment for the lifetime of the
    /// scope, then restores exactly what was there.
    ///
    /// CardModel.GetDescriptionForPile does no arithmetic - it prints
    /// DynamicVar.PreviewValue, which only DynamicVar.UpdateCardPreview writes, and
    /// that method's runGlobalHooks argument is precisely the "apply powers and
    /// relics" switch (DamageVar/BlockVar guard Hook.ModifyDamage/ModifyBlock behind
    /// it; PowerVar guards Hook.ModifyPowerAmountGiven). Passing false leaves the
    /// enchantment applied - the game treats an enchantment as part of the card, and
    /// so do we - while Strength, Dexterity, Vigor, Focus, Weak and every additive
    /// relic bonus drop out. CalculatedVar still runs its own base + extra * count
    /// arithmetic, which belongs to the card rather than to any power.
    ///
    /// CardModel.UpdateDynamicVarPreview cannot be used: it derives runGlobalHooks
    /// from the card's pile rather than taking it. DynamicVar.UpdateCardPreview is
    /// public, so the per-var loop is done here instead.
    ///
    /// The var set is live UI state shared with NCard, so the previous values are
    /// snapshotted and put back. Builds run on the Godot main thread, so no render
    /// can observe the window in between.
    /// </summary>
    private readonly struct UnpoweredCardVars : IDisposable
    {
        private readonly List<(DynamicVar Var, decimal Enchanted, decimal Preview)>? _saved;

        private UnpoweredCardVars(List<(DynamicVar, decimal, decimal)>? saved) => _saved = saved;

        internal static UnpoweredCardVars Apply(CardModel card)
        {
            var saved = new List<(DynamicVar, decimal, decimal)>();
            try
            {
                Unpower(card, card.DynamicVars, saved);
                EnchantmentModel? enchantment = card.Enchantment;
                if (enchantment != null)
                    Unpower(card, enchantment.DynamicVars, saved);
            }
            catch (Exception error)
            {
                Restore(saved);
                throw RefuseUnpoweredCardValues(card, error);
            }
            return new UnpoweredCardVars(saved);
        }

        private static void Unpower(
            CardModel card,
            DynamicVarSet set,
            List<(DynamicVar, decimal, decimal)> saved)
        {
            foreach (DynamicVar dynamicVar in set.Values)
                saved.Add((dynamicVar, dynamicVar.EnchantedValue, dynamicVar.PreviewValue));
            set.ClearPreview();
            foreach (DynamicVar? dynamicVar in set.Values.ToList())
                dynamicVar.UpdateCardPreview(card, CardPreviewMode.None, null, runGlobalHooks: false);
        }

        private static void Restore(List<(DynamicVar Var, decimal Enchanted, decimal Preview)>? saved)
        {
            if (saved == null) return;
            foreach ((DynamicVar? dynamicVar, decimal enchanted, decimal preview) in saved)
            {
                // DynamicVar setters only clamp and assign decimal fields.
                dynamicVar.EnchantedValue = enchanted;
                dynamicVar.PreviewValue = preview;
            }
        }

        public void Dispose() => Restore(_saved);
    }

}
