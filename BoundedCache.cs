using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sts2Mcp;

/// <summary>
/// A small thread-safe cache that drops everything when it fills.
///
/// The caches in this mod hold a handful of large, short-lived objects (one state tree, one
/// rendered catalog); a real LRU's bookkeeping would cost more than the miss it avoids, and
/// clearing cannot thrash because the working set is bounded by what a caller asks for in one
/// burst. HTTP threads share it, so every member takes the lock.
/// </summary>
internal sealed class BoundedCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _entries;
    private readonly object _lock = new();
    private readonly int _capacity;

    internal BoundedCache(int capacity = 64, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Dictionary<TKey, TValue>(capacity, comparer);
    }

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    internal bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(key, out value);
        }
    }

    internal void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_entries.Count >= _capacity && !_entries.ContainsKey(key))
            {
                _entries.Clear();
            }

            _entries[key] = value;
        }
    }

    internal void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
