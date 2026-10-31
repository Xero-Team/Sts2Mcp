using System;
using System.Collections.Generic;

namespace Sts2Mcp;

/// <summary>
/// The last full state, kept beside the digest of the lean read that produced it.
///
/// An agent polls <c>GET /api/v1/singleplayer</c> far more often than the game changes: between
/// two actions, or while a human is reading the screen, every poll rebuilds the whole tree and
/// re-renders every card and relic description for an answer identical to the previous one. A
/// lean read (structure only, no rendered text) is cheap enough to take on every poll; when its
/// digest matches the one stored here, the full tree is reused instead of being rebuilt.
///
/// Correctness rests on one property, which <see cref="McpMod"/>'s lean read documents: a lean
/// build differs from a full build of the same game state only in its rendered-text values. Every
/// input those text values are rendered from - powers, relics, counters, enchantments, upgrade
/// levels, identifiers - is structural, so a digest over the lean tree cannot miss a change that
/// the full tree would show. The <c>values=powered|unpowered</c> mode is part of the key because it
/// changes the text without changing the structure.
///
/// Only the singleplayer state is cached. The multiplayer state carries per-player turn
/// readiness and is read by clients that are already polling in lockstep with the host, so it
/// gets no benefit worth the extra key.
/// </summary>
internal static class StatePollCache
{
    private sealed record Entry(ulong Digest, Dictionary<string, object?> State);

    // Keyed by the `values` mode and the `passive` flag, so neither can ever answer the other's
    // request. Four combinations, one entry each.
    private static readonly BoundedCache<string, Entry> s_entries = new(capacity: 4);

    internal static bool TryGet(string key, ulong digest, out Dictionary<string, object?>? state)
    {
        if (s_entries.TryGet(key, out Entry? entry) && entry.Digest == digest)
        {
            state = entry.State;
            return true;
        }

        state = null;
        return false;
    }

    internal static void Remember(string key, ulong digest, Dictionary<string, object?> state)
        => s_entries.Set(key, new Entry(digest, state));
}
