using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Mcp;

// The card pools the game rolls against are STATIC for the duration of a run:
// CardPoolModel.GetUnlockedCards is a pure function of the unlock state and the
// multiplayer constraint, and CardFactory's combat/transform filters read only
// per-card model flags (Pool, Type, Rarity, CanBeGeneratedInCombat). Serving
// them here instead of on every state poll keeps the ~10 Hz state build off the
// pool enumeration entirely.
//
// Run-scoped, NOT profile-scoped: in multiplayer `player.UnlockState` is the
// union of every player's unlock state, and the constraint follows the player
// count. Clients must refetch when a run starts.
public static partial class McpMod
{
    // Order is load-bearing: the game's `Rng.NextItem` indexes straight into
    // the list GetUnlockedCards returns, so the declaration order of each
    // pool's GenerateAllCards() IS the mapping from roll to card. Never sort.
    private static readonly object s_cardPoolsCacheLock = new();
    private static UnlockState? s_cardPoolsCacheUnlocks;
    private static CardMultiplayerConstraint s_cardPoolsCacheConstraint;
    private static Dictionary<string, object?>? s_cardPoolsCache;

    private static void HandleGetCardPools(HttpListenerResponse response)
        => RunHandler(response, () => SendJson(response, RunOnMainThreadBlocking(BuildCardPools)),
            "Failed to build card pools", "cardpools_build_failed");

    internal static object GetCardPools()
    {
        return BuildCardPools();
    }

    private static Dictionary<string, object?> BuildCardPools()
    {
        UnlockState? unlockState = ResolveCardPoolUnlockState(out CardMultiplayerConstraint constraint, out string? source);
        if (unlockState == null)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["error"] = "No profile or run data available." };

        lock (s_cardPoolsCacheLock)
        {
            // The UnlockState instance is stable for a run, so reference
            // equality is a sound cache key and costs nothing to check.
            if (s_cardPoolsCache != null
                && ReferenceEquals(s_cardPoolsCacheUnlocks, unlockState)
                && s_cardPoolsCacheConstraint == constraint)
            {
                return s_cardPoolsCache;
            }
        }

        Dictionary<string, object?> pools = BuildPoolsCatalogue(unlockState, constraint);
        Dictionary<string, object?> characters = BuildCharacterCatalogue(out List<string>? characterPoolOrder);

        // `UnlockState.CharacterCardPools`: the character pools this profile has
        // unlocked, in `ModelDb.AllCharacters` order. Prismatic Gem widens every
        // card reward to exactly these, while `characters` above lists every
        // character in the game whether unlocked or not.
        // Absent rather than guessed: a client then refuses Prismatic Gem's
        // widened reward instead of naming cards from the wrong pools.
        List<string>? unlockedCharacterPoolOrder = TryRead(() => unlockState.CharacterCardPools
            .Select(pool => pool?.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .Distinct(StringComparer.Ordinal)
            .ToList());

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["multiplayer_constraint"] = constraint.ToString(),
            ["characters"] = characters,
            ["character_pool_order"] = characterPoolOrder,
            ["unlocked_character_pool_order"] = unlockedCharacterPoolOrder,
            ["pools"] = pools
        };

        lock (s_cardPoolsCacheLock)
        {
            s_cardPoolsCacheUnlocks = unlockState;
            s_cardPoolsCacheConstraint = constraint;
            s_cardPoolsCache = result;
        }
        return result;
    }

    private static Dictionary<string, object?> BuildPoolsCatalogue(UnlockState unlockState, CardMultiplayerConstraint constraint)
    {
        var pools = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (CardPoolModel pool in ModelDb.AllCardPools)
        {
            string title = pool.Title;
            if (string.IsNullOrWhiteSpace(title) || pools.ContainsKey(title))
                continue;

            // A modded or malformed pool must not take down the response;
            // its cards simply have no derivable domain client-side.
            List<CardModel>? cards = TryRead(() => pool.GetUnlockedCards(unlockState, constraint).ToList());
            if (cards == null)
                continue;

            pools[title] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["is_colorless"] = pool.IsColorless,
                ["cards"] = cards.Select(BuildCardPoolEntry).ToList()
            };
        }
        return pools;
    }

    private static Dictionary<string, object?> BuildCharacterCatalogue(out List<string> characterPoolOrder)
    {
        var characters = new Dictionary<string, object?>(StringComparer.Ordinal);
        characterPoolOrder = new List<string>();
        foreach (CharacterModel character in ModelDb.AllCharacters)
        {
            string? name = SafeGetText(() => character.Title);
            string? poolTitle = character.CardPool?.Title;
            if (!string.IsNullOrWhiteSpace(name))
                characters[name] = poolTitle;
            if (!string.IsNullOrWhiteSpace(poolTitle) && !characterPoolOrder.Contains(poolTitle))
                characterPoolOrder.Add(poolTitle);
        }
        return characters;
    }

    private static Dictionary<string, object?> BuildCardPoolEntry(CardModel card)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = card.Id.Entry,
            ["name"] = SafeGetText(() => card.Title) ?? card.Id.Entry,
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            // A card's own Pool can differ from the pool list it appears in
            // (character starting decks are concatenated into ModelDb.AllCards),
            // and GetDefaultTransformationOptions reads THIS one. Export it so
            // clients never have to invert the pool -> cards mapping.
            ["pool"] = SafeGetCardPoolTitle(card),
            // CardFactory.FilterForPlayerCount drops the constraint that does
            // not match the lobby, so anything replaying a generation roll
            // (shop shelves, card rewards) needs it to index the same list the
            // game did.
            ["multiplayer_constraint"] = card.MultiplayerConstraint.ToString(),
            // Jackpot narrows its domain with
            // `c.EnergyCost.Canonical == 0 && !c.EnergyCost.CostsX`, which is
            // the model's PRINTED cost and not any live discount. Nothing else
            // on the card exposes it, so export both halves of that predicate
            // rather than leaving a client to guess a cost from card text.
            ["energy_cost"] = SafeCanonicalEnergyCost(card),
            ["costs_x"] = SafeCostsX(card),
            ["can_be_generated_in_combat"] = SafeCanBeGeneratedInCombat(card)
        };
    }

    // CardModel.EnergyCost is lazily constructed off virtual members, so a
    // modded or malformed card can throw here. A null cost reads client-side as
    // "unknown", which keeps such a card out of a cost-narrowed domain rather
    // than admitting it at a guessed cost.
    private static int? SafeCanonicalEnergyCost(CardModel card)
        => TryRead(() => card.EnergyCost?.Canonical);

    private static bool SafeCostsX(CardModel card)
        => TryRead<bool?>(() => card.EnergyCost?.CostsX) ?? false;

    private static string? SafeGetCardPoolTitle(CardModel card)
        => TryRead(() => card.Pool?.Title);

    // CanBeGeneratedInCombat is `virtual`, so an override is free to consult
    // run context that does not exist outside a combat. Match the game's own
    // default when reading it throws.
    private static bool SafeCanBeGeneratedInCombat(CardModel card)
        => TryRead<bool?>(() => card.CanBeGeneratedInCombat) ?? true;

    // Prefers the run's unlock state, which in multiplayer is the union across
    // players and therefore wider than the local profile's. Falls back to the
    // local profile between runs so the endpoint stays useful at the menu.
    private static UnlockState? ResolveCardPoolUnlockState(
        out CardMultiplayerConstraint constraint,
        out string source)
    {
        if (RunManager.Instance?.IsInProgress == true)
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Player? player = runState != null ? LocalContext.GetMe(runState) : null;
            if (runState != null && player != null)
            {
                // Read through Player.RunState (typed IRunState): the
                // constraint is an interface default member derived from
                // Players.Count and is not visible on RunState itself.
                constraint = player.RunState.CardMultiplayerConstraint;
                source = "run";
                return player.UnlockState;
            }
        }

        ProgressState? progress = SaveManager.Instance?.Progress;
        if (progress == null)
        {
            constraint = CardMultiplayerConstraint.SingleplayerOnly;
            source = "none";
            return null;
        }

        // IRunState.CardMultiplayerConstraint reports SingleplayerOnly for a
        // player count of 1, so that is the right stand-in with no run loaded.
        constraint = CardMultiplayerConstraint.SingleplayerOnly;
        source = "profile";
        return new UnlockState(progress);
    }
}
