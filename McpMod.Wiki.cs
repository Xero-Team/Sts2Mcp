using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Mcp;

public static partial class McpMod
{
    private const int DefaultWikiSearchLimit = 10;
    private const int MaxWikiSearchLimit = 50;

    // A listing is not a search: the 50-result search cap exists to keep a fuzzy
    // query from dumping the catalog, and an explicit request for the whole list
    // has already asked for that.  Building a static card catalog is the case
    // this serves -- 543 fuzzy queries, one per card, was the alternative, and
    // whatever never ranked in a query's top 50 was silently absent from it.
    private const int MaxWikiListLimit = 5000;

    // The rendered catalog depends only on the catalog and the profile's discovery set, so one
    // entry per (scope, item type, discovery stamp) serves every query; see BuildWikiCandidatePool.
    private static readonly BoundedCache<string, List<WikiCandidate>> s_wikiCandidateCache = new(capacity: 8);

    private static void HandleGetWiki(HttpListenerRequest request, HttpListenerResponse response)
    {
        string query = request.QueryString["query"] ?? request.QueryString["q"] ?? "";
        string itemType = request.QueryString["type"] ?? request.QueryString["item_type"] ?? "all";
        string scope = request.QueryString["scope"] ?? "discovered";

        int limit = ParseWikiLimit(request.QueryString["limit"], string.IsNullOrWhiteSpace(query));
        bool pretty = WantsPretty(request);

        RunHandler(response,
            () => SendJson(response, RunOnMainThreadBlocking(() => BuildWikiSearch(query, itemType, limit, scope)), pretty),
            "Failed to search wiki", null, pretty);
    }

    internal static object SearchWiki(string query = "", string itemType = "all", int? limit = null,
        string scope = "discovered")
    {
        bool listing = string.IsNullOrWhiteSpace(query);
        int normalized;
        if (limit.HasValue)
            normalized = NormalizeWikiLimit(limit.Value, listing);
        else
            normalized = listing ? MaxWikiListLimit : DefaultWikiSearchLimit;
        return BuildWikiSearch(query, itemType, normalized, scope);
    }

    // "discovered" (the default) keeps the profile's own spoiler gate; "all"
    // searches ModelDb outright.  A tool building a static catalog wants "all":
    // a card the profile has not met still has a fully resolved description,
    // and without it that card reaches a deck with no text at all.
    private static Dictionary<string, object?> BuildWikiSearch(string query, string itemType, int limit,
        string scope = "discovered")
    {
        ProgressState? progress = SaveManager.Instance?.Progress;
        SaveManager? saveManager = SaveManager.Instance;
        if (progress == null || saveManager == null)
            return Error("No profile data available.");

        string? normalizedItemType = NormalizeWikiItemType(itemType);
        if (normalizedItemType == null)
            return Error("item_type must be one of: all, card, relic.");

        // Default stays spoiler-free: only what this profile has already seen.
        // scope=all searches the whole catalog, which is what you need when a card
        // reward generates a card you have never held and the alternative is passing
        // on a rare because its effect is unknowable.
        string? normalizedScope = NormalizeWikiScope(scope);
        if (normalizedScope == null)
            return Error("scope must be one of: discovered, all.");
        bool everything = string.Equals(normalizedScope, "all", StringComparison.Ordinal);

        HashSet<string> discoveredCards = ToDiscoveredWikiIds(progress.DiscoveredCards);
        HashSet<string> discoveredRelics = ToDiscoveredWikiIds(progress.DiscoveredRelics);

        List<WikiCandidate> candidates = BuildWikiCandidatePool(
            normalizedItemType,
            everything ? null : discoveredCards,
            everything ? null : discoveredRelics);

        // No query is a LISTING: every candidate in name order, unscored.  A
        // fuzzy score would only impose an arbitrary order on an enumeration
        // nobody searched, and dropping the `Score > 0` filter with it is the
        // point -- that filter is what a listing must not have.
        bool listing = string.IsNullOrWhiteSpace(query);

        // Counted before `Take`, so `truncated` says whether the limit actually
        // cut anything.  It has to be the ELIGIBLE count, not `candidates`: a
        // search's non-matching candidates are not results withheld.
        List<(WikiCandidate Candidate, double Score)> eligible = RankWikiCandidates(candidates, query, listing);

        List<Dictionary<string, object?>> matches = eligible
            .Take(limit)
            .Select(match => BuildWikiResult(match.Candidate, match.Score))
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["profile_id"] = saveManager.CurrentProfileId,
            ["query"] = query,
            ["mode"] = listing ? "list" : "search",
            ["item_type"] = normalizedItemType,
            ["limit"] = limit,
            ["truncated"] = eligible.Count > matches.Count,
            ["scope"] = everything ? "all_cards_and_relics" : "active_profile_discovered_cards_and_relics",
            ["selection_policy"] = DescribeWikiSelection(everything, listing),
            ["counts"] = BuildWikiCounts(discoveredCards, discoveredRelics, candidates, matches),
            ["results"] = matches
        };
    }

    private static int ParseWikiLimit(string? rawLimit, bool listing = false)
    {
        if (!int.TryParse(rawLimit, CultureInfo.InvariantCulture, out int limit))
            return listing ? MaxWikiListLimit : DefaultWikiSearchLimit;
        return NormalizeWikiLimit(limit, listing);
    }

    private static int NormalizeWikiLimit(int limit, bool listing = false)
        => Math.Clamp(limit, 1, listing ? MaxWikiListLimit : MaxWikiSearchLimit);

    private static string? NormalizeWikiScope(string? scope)
    {
        string value = (scope ?? "discovered").Trim().ToLowerInvariant();
        return value switch
        {
            "" or "discovered" or "active_profile" => "discovered",
            "all" or "catalog" or "everything" => "all",
            _ => null
        };
    }

    private static string? NormalizeWikiItemType(string? itemType)
    {
        string value = (itemType ?? "all").Trim().ToLowerInvariant();
        return value switch
        {
            "" or "all" or "any" => "all",
            "card" or "cards" => "card",
            "relic" or "relics" => "relic",
            _ => null
        };
    }

    private static Dictionary<string, object?> BuildWikiCounts(
        HashSet<string> discoveredCards,
        HashSet<string> discoveredRelics,
        List<WikiCandidate> candidates,
        List<Dictionary<string, object?>> matches)
        => new(StringComparer.Ordinal)
        {
            ["discovered_cards"] = discoveredCards.Count,
            ["discovered_relics"] = discoveredRelics.Count,
            ["searched"] = candidates.Count,
            ["returned"] = matches.Count
        };

    private static HashSet<string> ToDiscoveredWikiIds(IReadOnlySet<ModelId> ids)
        => ids.Select(id => id.Entry)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string DescribeWikiSelection(bool everything, bool listing)
        => (everything, listing) switch
        {
            (true, true) => "Lists every card and relic the game defines, in name order, ignoring the active profile's discovery progress.",
            (true, false) => "Searches every card and relic the game defines, ignoring the active profile's discovery progress, then returns the best fuzzy matches.",
            (false, true) => "Lists every card and relic discovered by the active profile, in name order.",
            (false, false) => "Searches only cards and relics discovered by the active profile, then returns the best fuzzy matches instead of exposing the full catalog."
        };

    private static List<WikiCandidate> BuildWikiCandidatePool(
        string itemType,
        HashSet<string>? discoveredCards,
        HashSet<string>? discoveredRelics)
    {
        // A card found mid-run reaches the profile in memory before the save is written, so the
        // stamp is the discovery sets themselves, not the save file. Digested rather than
        // spelled out because the sets run to hundreds of ids, and sorted first so the same set
        // always stamps the same.
        string stamp;
        if (discoveredCards == null)
        {
            stamp = "all";
        }
        else
        {
            stamp = string.Join('|',
                SaveManager.Instance?.CurrentProfileId.ToString(CultureInfo.InvariantCulture) ?? "",
                DiscoveryDigest(discoveredCards),
                discoveredRelics == null ? "" : DiscoveryDigest(discoveredRelics));
        }

        string key = string.Concat(stamp, "|", itemType);
        if (s_wikiCandidateCache.TryGet(key, out List<WikiCandidate>? cached))
        {
            return cached;
        }

        List<WikiCandidate> candidates = new();
        if (itemType is "all" or "card")
        {
            candidates.AddRange(BuildCardWikiCandidates(discoveredCards));
        }

        if (itemType is "all" or "relic")
        {
            candidates.AddRange(BuildRelicWikiCandidates(discoveredRelics));
        }

        s_wikiCandidateCache.Set(key, candidates);
        return candidates;
    }

    private static string DiscoveryDigest(HashSet<string> ids)
    {
        List<string> sorted = ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
        return StateJsonWriter.Digest(sorted).ToString("x16", CultureInfo.InvariantCulture);
    }

    private static List<(WikiCandidate Candidate, double Score)> RankWikiCandidates(
        List<WikiCandidate> candidates,
        string query,
        bool listing)
    {
        if (listing)
        {
            return candidates
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => (Candidate: candidate, Score: 0.0))
                .ToList();
        }

        return candidates
            .Select(candidate => (Candidate: candidate, Score: ScoreWikiCandidate(query, candidate)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Enumerate the game's canonical model registry rather than reflectively
    // constructing fresh instances per-type. Fresh instances never have
    // InitId() called on them (that only happens for instances stored in
    // ModelDb._contentById), so card.Id.Entry comes back empty and the
    // discoveredIds filter drops everything. ModelDb.AllCards also naturally
    // includes mod-injected cards.
    // A null `discoveredIds` means scope=all: every card ModelDb defines,
    // whether or not the profile has met it.
    private static List<WikiCandidate> BuildCardWikiCandidates(HashSet<string>? discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (CardModel card in ModelDb.AllCards)
        {
            string? id = SafeGetText(() => card.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || (discoveredIds != null && !discoveredIds.Contains(id)))
                continue;
            bool discovered = discoveredIds?.Contains(id) ?? true;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "card",
                Id: id,
                Name: SafeGetText(() => card.Title) ?? id,
                SearchText: BuildWikiSearchText("card", id, SafeGetText(() => card.Title), SafeGetText(() => card.Type), SafeGetText(() => card.Rarity)),
                Card: card,
                Relic: null,
                Discovered: discovered));
        }

        return byId.Values.ToList();
    }

    // Null `discoveredIds` means scope=all, as for cards above.
    private static List<WikiCandidate> BuildRelicWikiCandidates(HashSet<string>? discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (RelicModel relic in ModelDb.AllRelics)
        {
            string? id = SafeGetText(() => relic.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || (discoveredIds != null && !discoveredIds.Contains(id)))
                continue;
            bool discovered = discoveredIds?.Contains(id) ?? true;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "relic",
                Id: id,
                Name: SafeGetText(() => relic.Title) ?? id,
                SearchText: BuildWikiSearchText("relic", id, SafeGetText(() => relic.Title), SafeGetText(() => relic.Rarity)),
                Card: null,
                Relic: relic,
                Discovered: discovered));
        }

        return byId.Values.ToList();
    }

    private static Dictionary<string, object?> BuildWikiResult(WikiCandidate candidate, double score)
    {
        if (candidate.Card != null)
            return BuildWikiCardResult(candidate, score);
        if (candidate.Relic != null)
            return BuildWikiRelicResult(candidate, score);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["item_type"] = candidate.Kind,
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["score"] = Math.Round(score, 3),
            ["discovered"] = candidate.Discovered
        };
    }

    private static Dictionary<string, object?> BuildWikiCardResult(WikiCandidate candidate, double score)
    {
        CardModel card = candidate.Card!;
        CardModel? upgraded = SafeBuildUpgradedCardPreview(card);
        Dictionary<string, object?>? upgradedVariant = null;
        if (upgraded != null)
            upgradedVariant = BuildWikiCardVariant(upgraded, "upgraded");
        else if (card.IsUpgradable)
            upgradedVariant = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["variant"] = "upgraded",
                ["description"] = SafeGetCardUpgradePreviewDescription(card),
                ["preview_available"] = false
            };

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["item_type"] = "card",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["score"] = Math.Round(score, 3),
            ["discovered"] = candidate.Discovered,
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["type"] = SafeGetText(() => card.Type),
            ["is_upgradable"] = card.IsUpgradable,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["base"] = BuildWikiCardVariant(card, "base"),
            ["upgraded"] = upgradedVariant
        };

        return result;
    }

    private static Dictionary<string, object?> BuildWikiCardVariant(CardModel card, string variant)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["variant"] = variant,
            ["id"] = SafeGetText(() => card.Id.Entry),
            ["name"] = SafeGetText(() => card.Title),
            ["type"] = SafeGetText(() => card.Type),
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["cost"] = GetCostDisplay(card),
            ["star_cost"] = GetStarCostDisplay(card),
            ["description"] = SafeGetCardDescription(card),
            ["is_upgraded"] = card.IsUpgraded,
            ["is_upgradable"] = card.IsUpgradable,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["keywords"] = BuildHoverTips(card.HoverTips)
        };
    }

    private static Dictionary<string, object?> BuildWikiRelicResult(WikiCandidate candidate, double score)
    {
        RelicModel relic = candidate.Relic!;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["item_type"] = "relic",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["score"] = Math.Round(score, 3),
            ["discovered"] = candidate.Discovered,
            ["rarity"] = SafeGetText(() => relic.Rarity),
            ["description"] = SafeGetText(() => relic.DynamicDescription),
            ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
        };
    }

    private static string BuildWikiSearchText(params string?[] values)
        => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static double ScoreWikiCandidate(string query, WikiCandidate candidate)
    {
        string queryNormalized = NormalizeSearchText(query);
        string candidateNormalized = NormalizeSearchText(candidate.SearchText);
        if (string.IsNullOrWhiteSpace(queryNormalized) || string.IsNullOrWhiteSpace(candidateNormalized))
            return 0;

        string queryCompact = CompactSearchText(queryNormalized);
        string nameCompact = CompactSearchText(candidate.Name);
        string idCompact = CompactSearchText(candidate.Id);

        if (queryCompact.Equals(nameCompact, StringComparison.OrdinalIgnoreCase)
            || queryCompact.Equals(idCompact, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (nameCompact.Contains(queryCompact, StringComparison.OrdinalIgnoreCase)
            || idCompact.Contains(queryCompact, StringComparison.OrdinalIgnoreCase))
            return 850;

        List<string> queryTokens = TokenizeSearchText(queryNormalized);
        List<string> candidateTokens = TokenizeSearchText(candidateNormalized);
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            return 0;

        double total = 0;
        int strongMatches = 0;
        foreach (string queryToken in queryTokens)
        {
            double best = candidateTokens.Max(candidateToken => TokenSimilarity(queryToken, candidateToken));
            total += best;
            if (best >= 0.72)
                strongMatches++;
        }

        double average = total / queryTokens.Count;
        double score = average * 700;
        if (strongMatches > 0)
            score += strongMatches * 35;

        return score >= 300 ? score : 0;
    }

    private static string NormalizeSearchText(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool lastWasSpace = true;
        foreach (char ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string CompactSearchText(string value)
        => NormalizeSearchText(value).Replace(" ", "", StringComparison.Ordinal);

    private static List<string> TokenizeSearchText(string value)
        => NormalizeSearchText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static double TokenSimilarity(string queryToken, string candidateToken)
    {
        if (queryToken.Equals(candidateToken, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        // Require >=3 chars on the candidate token for the substring shortcut:
        // single-letter words like "a"/"I" otherwise trivially substring-match
        // arbitrary queries and produce spurious results for gibberish input.
        if (candidateToken.Length >= 3
            && (candidateToken.Contains(queryToken, StringComparison.OrdinalIgnoreCase)
                || queryToken.Contains(candidateToken, StringComparison.OrdinalIgnoreCase)))
            return 0.88;

        int distance = LevenshteinDistance(queryToken, candidateToken);
        int maxLength = Math.Max(queryToken.Length, candidateToken.Length);
        return maxLength == 0 ? 0 : 1.0 - ((double)distance / maxLength);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record WikiCandidate(
        string Kind,
        string Id,
        string Name,
        string SearchText,
        CardModel? Card,
        RelicModel? Relic,
        bool Discovered = true);
}
