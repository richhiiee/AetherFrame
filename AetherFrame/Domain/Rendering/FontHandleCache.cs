using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AetherFrame.Domain.Rendering;

/// <summary>One built font: a family (as the cache keys it), a ladder tier and a real style.</summary>
internal readonly record struct FontCacheKey(string FamilyId, float SizePx, bool Bold, bool Italic);

/// <summary>
/// What <see cref="FontHandleCache{TAtlas, THandle}"/> needs from the font system: Dalamud's font
/// atlases in the plugin, a fake in tests. Called on the framework thread only (the callback
/// <see cref="WhenAvailable"/> takes may run on any thread).
/// </summary>
internal interface IFontAtlasBackend<TAtlas, THandle>
{
    /// <summary>A new, empty atlas for one family's faces.</summary>
    TAtlas CreateAtlas(string familyId);

    /// <summary>A new handle for <paramref name="key"/> in <paramref name="atlas"/>; building it rebuilds that atlas.</summary>
    THandle CreateHandle(TAtlas atlas, FontCacheKey key);

    /// <summary>Whether the handle's face is built and can be drawn with.</summary>
    bool IsAvailable(THandle handle);

    /// <summary>Calls <paramref name="available"/> once, from any thread, when the handle's face is
    /// first built (at once if it already is); never for a handle disposed before then.</summary>
    void WhenAvailable(THandle handle, Action available);

    /// <summary>Holds back <paramref name="atlas"/>'s rebuilds until disposed, then rebuilds once if needed.</summary>
    IDisposable SuppressRebuild(TAtlas atlas);

    void DisposeHandle(THandle handle);

    void DisposeAtlas(TAtlas atlas);
}

/// <summary>What the font cache holds and how long new fonts took to load, for <c>/af fonts</c>.</summary>
internal readonly record struct FontCacheStats(
    int Families, int Handles, long SurfacePixels, int Loads, IReadOnlyList<long> RecentLoadMilliseconds, long LongestLoadMilliseconds)
{
    /// <summary>One line for chat.</summary>
    public string Describe()
    {
        var held = string.Create(CultureInfo.InvariantCulture, $"Fonts: {Families} families, {Handles} sizes, about {SurfacePixels / 1_000_000d:0.#} M glyph pixels. ");
        return held + (Loads == 0
            ? "No font loaded yet."
            : string.Create(CultureInfo.InvariantCulture, $"{Loads} loaded; the last {RecentLoadMilliseconds.Count} took {string.Join(", ", RecentLoadMilliseconds)} ms to appear (longest {LongestLoadMilliseconds} ms)."));
    }
}

/// <summary>
/// The font cache's rules, free of Dalamud (issue #117). Every built (family, tier, style) is kept
/// and reused; what changed for #117 is where each one lives and what is let go.
///
/// <list type="bullet">
/// <item><b>One atlas per family.</b> An atlas rebuild rasterizes every face in it, so with one
/// atlas for everything, each font tried made the next one slower to appear: trying a new family
/// rebuilt every family tried before it. Each family now has an atlas of its own, so a new font
/// costs only its own faces, however many came before.</item>
/// <item><b>What is in use stays.</b> A face drawn or measured within <see cref="InUseMilliseconds"/>
/// is never let go, so nothing on screen is rebuilt over and over. Beyond that, the
/// least-recently-used faces go once the cache holds more than <see cref="MaxCachedHandles"/> or
/// <see cref="FontTierPolicy.AtlasBudgetPixels"/> of glyph surface, and the least-recently-used
/// families once more than <see cref="MaxFamilyAtlases"/> are kept, so browsing every font stays
/// bounded while going back to one just tried costs nothing.</item>
/// <item><b>Batches.</b> Faces created inside <see cref="Batch"/> cost one rebuild per atlas.</item>
/// </list>
/// </summary>
internal sealed class FontHandleCache<TAtlas, THandle> : IDisposable
    where TAtlas : class
    where THandle : class
{
    /// <summary>The most faces kept at once, beyond those in use.</summary>
    internal const int MaxCachedHandles = 160;

    /// <summary>The most family atlases kept at once, beyond those in use.</summary>
    internal const int MaxFamilyAtlases = 12;

    /// <summary>A face or family used this recently is in use, and never let go.</summary>
    internal const long InUseMilliseconds = 2000;

    private const int RecentLoadCount = 8;

    private readonly IFontAtlasBackend<TAtlas, THandle> backend;
    private readonly Func<long> clock;
    private readonly long budgetPixels;
    private readonly Dictionary<FontCacheKey, Entry> entries = new();
    private readonly Dictionary<string, Family> families = new(StringComparer.Ordinal);

    // Least recently used first.
    private readonly LinkedList<Entry> accessOrder = new();
    private readonly Queue<long> recentLoads = new();

    // Faces built, with when: filled from the font system's threads, read on the framework thread.
    private readonly ConcurrentQueue<(Entry Entry, long At)> appeared = new();

    private long surfacePixels;
    private int batchDepth;
    private int loads;
    private long longestLoad;
    private bool disposed;

    /// <param name="clock">Milliseconds, ever increasing (Environment.TickCount64 in the plugin).</param>
    internal FontHandleCache(IFontAtlasBackend<TAtlas, THandle> backend, Func<long> clock, long budgetPixels = FontTierPolicy.AtlasBudgetPixels)
    {
        this.backend = backend;
        this.clock = clock;
        this.budgetPixels = budgetPixels;
    }

    internal int HandleCount => entries.Count;

    internal int FamilyCount => families.Count;

    /// <summary>
    /// The face to draw <paramref name="familyId"/> with at ladder tier <paramref name="tierIndex"/>:
    /// that tier once built, else an already-built LARGER tier of the same family and style (a
    /// downscale, never a blurry upscale), else null while it loads. Builds the tier when missing.
    /// </summary>
    internal THandle? Get(string familyId, int tierIndex, bool bold, bool italic)
    {
        var now = clock();
        var ideal = GetOrCreateEntry(new FontCacheKey(familyId, FontTierPolicy.SizeLadder[tierIndex], bold, italic), now);
        if (backend.IsAvailable(ideal.Handle))
        {
            return ideal.Handle;
        }

        var maxTierIndex = FontTierPolicy.MaxTierIndex(familyId);
        for (var i = tierIndex + 1; i <= maxTierIndex; i++)
        {
            if (entries.TryGetValue(new FontCacheKey(familyId, FontTierPolicy.SizeLadder[i], bold, italic), out var larger))
            {
                Touch(larger, now);
                if (backend.IsAvailable(larger.Handle))
                {
                    return larger.Handle;
                }
            }
        }

        return null;
    }

    /// <summary>The face for exactly <paramref name="key"/>, building it when missing (warming).</summary>
    internal THandle GetOrCreate(FontCacheKey key) => GetOrCreateEntry(key, clock()).Handle;

    /// <summary>Faces created until this is disposed cost one rebuild per atlas. Nests.</summary>
    internal IDisposable Batch()
    {
        batchDepth++;
        return new BatchScope(this);
    }

    internal FontCacheStats Stats
    {
        get
        {
            RecordLoads();
            return new(families.Count, entries.Count, surfacePixels, loads, recentLoads.ToArray(), longestLoad);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var entry in entries.Values)
        {
            backend.DisposeHandle(entry.Handle);
        }

        // Each atlas before the rebuild a batch held for it, so releasing it starts no build.
        foreach (var family in families.Values)
        {
            backend.DisposeAtlas(family.Atlas);
            family.Suppression?.Dispose();
            family.Suppression = null;
        }

        entries.Clear();
        families.Clear();
        accessOrder.Clear();
        surfacePixels = 0;
    }

    private Entry GetOrCreateEntry(FontCacheKey key, long now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RecordLoads();
        if (entries.TryGetValue(key, out var existing))
        {
            Touch(existing, now);
            return existing;
        }

        var family = GetOrCreateFamily(key.FamilyId, now);
        if (batchDepth > 0 && family.Suppression is null)
        {
            family.Suppression = backend.SuppressRebuild(family.Atlas);
        }

        var entry = new Entry(key, backend.CreateHandle(family.Atlas, key), family, FontTierPolicy.EstimatedSurfacePixels(key.FamilyId, key.SizePx), now);
        entries[key] = entry;
        entry.Node = accessOrder.AddLast(entry);
        WatchBuild(entry);
        family.Handles++;
        family.LastUsed = now;
        surfacePixels += entry.Surface;

        EvictHandles(entry, now);
        return entry;
    }

    // Its own method, so the callback's closure is allocated only for a new face, not on every lookup.
    private void WatchBuild(Entry entry) => backend.WhenAvailable(entry.Handle, () => appeared.Enqueue((entry, clock())));

    private Family GetOrCreateFamily(string familyId, long now)
    {
        if (families.TryGetValue(familyId, out var family))
        {
            return family;
        }

        EvictFamilies(now);
        family = new Family(familyId, backend.CreateAtlas(familyId), now);
        families[familyId] = family;
        return family;
    }

    private void Touch(Entry entry, long now)
    {
        entry.LastUsed = now;
        entry.Family.LastUsed = now;
        if (entry.Node is { } node && !ReferenceEquals(node, accessOrder.Last))
        {
            accessOrder.Remove(node);
            accessOrder.AddLast(node);
        }
    }

    /// <summary>
    /// Records how long each face built since the last call took to appear: from when it was asked
    /// for to when the font system finished it (<see cref="IFontAtlasBackend{TAtlas, THandle}.WhenAvailable"/>),
    /// whether or not anything has drawn it since, so a face built ahead of use and first drawn much
    /// later counts only its build. A face let go before it appeared isn't counted.
    /// </summary>
    private void RecordLoads()
    {
        while (appeared.TryDequeue(out var built))
        {
            var (entry, at) = built;
            if (entry.LoadRecorded || entry.Node is null)
            {
                continue;
            }

            entry.LoadRecorded = true;
            var took = Math.Max(0L, at - entry.Created);
            loads++;
            longestLoad = Math.Max(longestLoad, took);
            recentLoads.Enqueue(took);
            if (recentLoads.Count > RecentLoadCount)
            {
                recentLoads.Dequeue();
            }
        }
    }

    private bool InUse(long lastUsed, long now) => now - lastUsed < InUseMilliseconds;

    /// <summary>Lets go of the least-recently-used faces not in use while the cache is over its bounds.
    /// The access order is by last use, so the first face in use ends the search.</summary>
    private void EvictHandles(Entry justAdded, long now)
    {
        while ((entries.Count > MaxCachedHandles || surfacePixels > budgetPixels) && accessOrder.First is { } node)
        {
            var oldest = node.Value;
            if (ReferenceEquals(oldest, justAdded) || InUse(oldest.LastUsed, now))
            {
                return;
            }

            Remove(oldest);
        }
    }

    /// <summary>Makes room for one more family atlas by letting go of the least-recently-used families not in use.</summary>
    private void EvictFamilies(long now)
    {
        while (families.Count >= MaxFamilyAtlases)
        {
            Family? oldest = null;
            foreach (var family in families.Values)
            {
                if (!InUse(family.LastUsed, now) && (oldest is null || family.LastUsed < oldest.LastUsed))
                {
                    oldest = family;
                }
            }

            if (oldest is null)
            {
                return;
            }

            foreach (var entry in entries.Values.Where(e => ReferenceEquals(e.Family, oldest)).ToList())
            {
                Remove(entry);
            }

            DisposeFamilyIfEmpty(oldest);
            if (families.TryGetValue(oldest.Id, out var still) && ReferenceEquals(still, oldest))
            {
                return; // never loop on a family that could not be let go
            }
        }
    }

    private void Remove(Entry entry)
    {
        entries.Remove(entry.Key);
        if (entry.Node is { } node)
        {
            accessOrder.Remove(node);
            entry.Node = null;
        }

        surfacePixels -= entry.Surface;
        entry.Family.Handles--;
        backend.DisposeHandle(entry.Handle);
        DisposeFamilyIfEmpty(entry.Family);
    }

    /// <summary>An atlas with no faces left goes, with any rebuild a batch was holding for it.</summary>
    private void DisposeFamilyIfEmpty(Family family)
    {
        if (family.Handles > 0 || !families.TryGetValue(family.Id, out var current) || !ReferenceEquals(current, family))
        {
            return;
        }

        families.Remove(family.Id);
        backend.DisposeAtlas(family.Atlas);
        family.Suppression?.Dispose(); // after the atlas, so releasing the held rebuild starts no build
        family.Suppression = null;
    }

    private void EndBatch()
    {
        if (--batchDepth > 0 || disposed)
        {
            return;
        }

        foreach (var family in families.Values)
        {
            if (family.Suppression is { } suppression)
            {
                family.Suppression = null;
                suppression.Dispose();
            }
        }
    }

    private sealed class BatchScope(FontHandleCache<TAtlas, THandle> owner) : IDisposable
    {
        private bool ended;

        public void Dispose()
        {
            if (!ended)
            {
                ended = true;
                owner.EndBatch();
            }
        }
    }

    private sealed class Family(string id, TAtlas atlas, long lastUsed)
    {
        public string Id { get; } = id;

        public TAtlas Atlas { get; } = atlas;

        public int Handles { get; set; }

        public long LastUsed { get; set; } = lastUsed;

        public IDisposable? Suppression { get; set; }
    }

    private sealed class Entry(FontCacheKey key, THandle handle, Family family, long surface, long created)
    {
        public FontCacheKey Key { get; } = key;

        public THandle Handle { get; } = handle;

        public Family Family { get; } = family;

        public long Surface { get; } = surface;

        public long Created { get; } = created;

        public long LastUsed { get; set; } = created;

        public bool LoadRecorded { get; set; }

        public LinkedListNode<Entry>? Node { get; set; }
    }
}
