using System;
using System.Collections.Generic;

namespace AetherFrame.Services.Caching;

/// <summary>
/// A small bounded least-recently-used map. Reads and writes both count as use; once more than
/// <see cref="Capacity"/> entries exist, the least recently used are evicted (and passed to the
/// optional eviction callback, e.g. to dispose them). Not thread-safe: owners use it from one
/// thread (the render thread) or guard it themselves.
/// </summary>
internal sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> order = new();
    private readonly Action<TKey, TValue>? onEvicted;

    internal LruCache(int capacity, Action<TKey, TValue>? onEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        this.onEvicted = onEvicted;
    }

    internal int Capacity { get; }

    internal int Count => map.Count;

    internal bool TryGetValue(TKey key, out TValue value)
    {
        if (map.TryGetValue(key, out var node))
        {
            order.Remove(node);
            order.AddLast(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    internal bool ContainsKey(TKey key) => map.ContainsKey(key);

    internal void Set(TKey key, TValue value)
    {
        if (map.TryGetValue(key, out var existing))
        {
            order.Remove(existing);
            map.Remove(key);
        }

        map[key] = order.AddLast((key, value));

        while (map.Count > Capacity && order.First is { } oldest)
        {
            order.RemoveFirst();
            map.Remove(oldest.Value.Key);
            onEvicted?.Invoke(oldest.Value.Key, oldest.Value.Value);
        }
    }

    internal bool Remove(TKey key)
    {
        if (!map.TryGetValue(key, out var node))
        {
            return false;
        }

        order.Remove(node);
        map.Remove(key);
        onEvicted?.Invoke(node.Value.Key, node.Value.Value);
        return true;
    }

    internal void Clear()
    {
        var entries = new List<(TKey Key, TValue Value)>(order);
        order.Clear();
        map.Clear();

        if (onEvicted is not null)
        {
            foreach (var (key, value) in entries)
            {
                onEvicted(key, value);
            }
        }
    }
}
