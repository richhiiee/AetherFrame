using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AetherFrame.Domain.Profiles;
using Dalamud.Interface.ManagedFontAtlas;

namespace AetherFrame.Services.Fonts;

/// <summary>
/// Owns AetherFrame's own isolated <see cref="IFontAtlas"/> and hands out cached
/// <see cref="IFontHandle"/>s so <c>ProfileRenderer</c> can draw a <see cref="TextProfileElement"/>
/// crisply at its own requested size, without ever visibly stretching a smaller rasterized font
/// up to get there — and without the startup cost of building every size anyone might ever ask
/// for before anyone actually asks for it.
///
/// Every distinct (family, size, bold, italic) combination is only ever built once and reused
/// after — see <see cref="GetHandle"/> — but "size" is not the raw requested pixel size: it's
/// snapped to the nearest tier AT OR ABOVE the request in <see cref="SizeLadder"/>, a fixed,
/// fairly fine-grained set of sizes. This is what makes the whole strategy work:
///
/// <list type="bullet">
/// <item>The set of fonts a continuous zoom drag can ever cause to be built is bounded by the
/// ladder length, not by every pixel size the drag happens to pass through — nothing gets
/// rebuilt on every frame.</item>
/// <item>Because every draw uses a tier AT OR ABOVE what it actually needs, the glyph raster is
/// always shrunk slightly (or matched exactly) to reach the requested size, never stretched up —
/// downscaling a raster looks fine; upscaling one is the blur bug this exists to avoid.</item>
/// <item>If the ideal tier isn't built yet, <see cref="GetHandle"/> substitutes an already-
/// available LARGER tier of the same family/style instead — still just a bigger downscale, never
/// blurry — and, unlike before, ALSO kicks off building the ideal tier so it's ready next time,
/// rather than requiring it to have been prewarmed.</item>
/// </list>
///
/// Unlike the ladder itself, which stays full resolution so snapping always lands close, what
/// gets built EAGERLY is deliberately small:
///
/// <list type="bullet">
/// <item>At construction: only <see cref="CommonEditorSizes"/> (a handful of sizes, not the
/// whole ladder) for AetherFrame's own default family — the one new text actually uses. Dalamud
/// Default (which can carry a much larger CJK/game-symbol/icon glyph set) is never built until
/// something actually needs it.</item>
/// <item>When a profile loads: for each distinct (family, bold, italic) combo its text elements
/// actually use, only the ladder tier matching that element's OWN nominal size, plus
/// <see cref="CommonEditorSizes"/> for that combo — not all 26 tiers regardless of whether
/// they're ever requested.</item>
/// </list>
///
/// Everything else — an unusual zoom level, a size nobody anticipated — is built lazily, once,
/// the first time <see cref="GetHandle"/> actually needs it, and reused forever after via the
/// same cache. Every batch of more-than-one handle creation (both eager-warm paths above) is
/// wrapped in <see cref="IFontAtlas.SuppressAutoRebuild"/> so it costs one atlas rebuild instead
/// of one per handle — see that method's own doc, which recommends exactly this for "creating
/// multiple new handles."
/// </summary>
internal sealed class ProfileFontService : IDisposable
{
    /// <summary>
    /// Fixed set of pixel sizes every cached font handle is snapped to. Deliberately fine-grained
    /// (~20-25% steps) so any actual request — prewarmed or lazily built on first demand — is
    /// close to its ideal size; includes AetherFrame's documented test sizes (24/48/72/120/180)
    /// exactly. Being in this list does NOT mean a tier is built ahead of time — see
    /// <see cref="CommonEditorSizes"/> for the much smaller eager subset.
    /// </summary>
    private static readonly float[] SizeLadder =
    [
        10f, 12f, 14f, 16f, 20f, 24f, 28f, 32f, 40f, 48f, 56f, 64f, 72f, 84f, 96f,
        110f, 120f, 140f, 160f, 180f, 210f, 240f, 280f, 330f, 390f, 460f,
    ];

    /// <summary>
    /// The small subset of <see cref="SizeLadder"/> eagerly warmed for a family/style combo
    /// ("a sensible set of common editor sizes") — everything else in the ladder is only ever
    /// built lazily, on first actual demand, via <see cref="GetHandle"/>.
    /// </summary>
    private static readonly float[] CommonEditorSizes = [16f, 24f, 32f, 48f, 64f, 96f];

    // Bounds how many distinct (family, tier, bold, italic) font handles are kept alive at once.
    // Steady-state usage (CommonEditorSizes for a handful of combos, plus whatever a session
    // actually zooms/views) sits well below this. Eviction is LRU (see accessOrder) so it's the
    // least-recently-USED entry that goes, not simply the oldest-built one.
    private const int MaxCachedHandles = 160;

    private readonly IFontAtlas atlas;
    private readonly Dictionary<FontKey, IFontHandle> handles = new();
    private readonly LinkedList<FontKey> accessOrder = new();
    private readonly Dictionary<FontKey, LinkedListNode<FontKey>> accessNodes = new();
    private readonly Dictionary<string, byte[]> embeddedFontBytesCache = new();

    // Reference marker only (never dereferenced): lets EnsurePrewarmed short-circuit to a no-op
    // on every frame except the one where the profile instance actually changed.
    private ProfileDocument? lastPrewarmedProfile;

    internal ProfileFontService()
    {
        atlas = DalamudServices.PluginInterface.UiBuilder.CreateFontAtlas(
            FontAtlasAutoRebuildMode.Async, isGlobalScaled: false, "AetherFrame.ProfileFonts");

        // "A sensible set of common editor sizes", and only for the family new text actually
        // uses. Dalamud Default is deliberately NOT warmed here — it can carry a much larger
        // glyph set (CJK, game symbols, icons), and every legacy profile that actually uses it
        // gets it warmed on load instead (see EnsurePrewarmed); one that doesn't use it never
        // pays for it at all.
        using (atlas.SuppressAutoRebuild())
        {
            WarmSizes(ProfileFontFamilies.AetherFrameSans, bold: false, italic: false, CommonEditorSizes);
        }
    }

    /// <summary>
    /// Gets (building and caching on first use) a font handle for the given family at
    /// approximately <paramref name="requestedPixelSize"/> — see the type doc for the snap-to-
    /// ladder/prefer-larger-available strategy this implements. Must be called from the main/
    /// ImGui thread. Bold/Italic are silently dropped to false for a family that doesn't support
    /// them (see <see cref="ProfileFontFamilyDescriptor"/>).
    /// </summary>
    internal IFontHandle GetHandle(string? familyId, float requestedPixelSize, bool bold, bool italic) =>
        GetHandle(familyId, requestedPixelSize, bold, italic, out _);

    /// <summary>
    /// Same as <see cref="GetHandle(string?, float, bool, bool)"/>, also reporting whether the
    /// returned handle is the transient cold-start fallback (Dalamud's global default font, used
    /// only until the requested family's first tier finishes building) rather than the requested
    /// family itself — callers that cache anything measured with the font must not cache that.
    /// </summary>
    internal IFontHandle GetHandle(string? familyId, float requestedPixelSize, bool bold, bool italic, out bool isColdStartFallback)
    {
        var handle = GetHandleCore(familyId, requestedPixelSize, bold, italic);
        isColdStartFallback = ReferenceEquals(handle, DalamudServices.PluginInterface.UiBuilder.DefaultFontHandle);
        return handle;
    }

    private IFontHandle GetHandleCore(string? familyId, float requestedPixelSize, bool bold, bool italic)
    {
        var descriptor = ProfileFontCatalog.Resolve(familyId);
        var effectiveBold = bold && descriptor.SupportsBold;
        var effectiveItalic = italic && descriptor.SupportsItalic;

        var tierIndex = FindTierIndex(requestedPixelSize);

        var idealKey = new FontKey(descriptor.Id, SizeLadder[tierIndex], effectiveBold, effectiveItalic);
        var idealHandle = GetOrCreateHandle(idealKey, descriptor);
        if (idealHandle.Available)
        {
            return idealHandle;
        }

        // The ideal tier is still building (its atlas rebuild hasn't completed yet). Rather than
        // use it anyway — Push() would silently fall back to whatever font is currently active,
        // which AddText would then stretch to the requested size, exactly the blur bug this
        // service exists to avoid — look for an already-available LARGER tier of the same
        // family/style. Only ever searches upward (never a smaller tier): drawing at the
        // requested size from a bigger-than-needed raster is a downscale, not the upscale that
        // causes visible blur. Only considers tiers already built — never builds more just to
        // search, since that would defeat the point of building lazily.
        for (var i = tierIndex + 1; i < SizeLadder.Length; i++)
        {
            var fallbackKey = new FontKey(descriptor.Id, SizeLadder[i], effectiveBold, effectiveItalic);
            if (handles.TryGetValue(fallbackKey, out var fallback))
            {
                TouchAccess(fallbackKey);
                if (fallback.Available)
                {
                    return fallback;
                }
            }
        }

        // Nothing in this family/style is ready at or above the requested size yet — true only
        // for a cold-start frame before the ideal tier just kicked off above has finished
        // building. Dalamud's own default font handle is (for all practical purposes) always
        // already available, so this is the only path that can still show a transient upscale,
        // and only ever for a frame or two the first time a given size is ever requested.
        return DalamudServices.PluginInterface.UiBuilder.DefaultFontHandle;
    }

    /// <summary>
    /// Ensures every distinct (family, bold, italic) combination actually used by
    /// <paramref name="profile"/>'s text elements has at least its own nominal size (and the
    /// common-size baseline) warmed. Safe (and cheap — a single reference comparison) to call
    /// every frame; does real work only the first time it sees a given profile instance.
    /// </summary>
    internal void EnsurePrewarmed(ProfileDocument? profile)
    {
        if (ReferenceEquals(profile, lastPrewarmedProfile))
        {
            return;
        }

        lastPrewarmedProfile = profile;

        if (profile is null)
        {
            return;
        }

        // One rebuild for the whole profile's worth of newly-needed handles, not one per handle.
        using var suppression = atlas.SuppressAutoRebuild();

        var warmedCombos = new HashSet<(string FamilyId, bool Bold, bool Italic)>();

        foreach (var element in profile.Elements)
        {
            if (element is not TextProfileElement text)
            {
                continue;
            }

            var descriptor = ProfileFontCatalog.Resolve(text.FontFamily);
            var effectiveBold = text.Bold && descriptor.SupportsBold;
            var effectiveItalic = text.Italic && descriptor.SupportsItalic;

            if (warmedCombos.Add((descriptor.Id, effectiveBold, effectiveItalic)))
            {
                // The common baseline (covers most zoom/view-scale variations of this combo)...
                WarmSizes(descriptor.Id, effectiveBold, effectiveItalic, CommonEditorSizes);
            }

            // ...plus this specific element's own nominal size exactly, so what the profile
            // actually contains is never left to the lazy/on-demand path alone.
            var ownTier = SizeLadder[FindTierIndex(text.FontSize)];
            GetOrCreateHandle(new FontKey(descriptor.Id, ownTier, effectiveBold, effectiveItalic), descriptor);
        }
    }

    public void Dispose()
    {
        foreach (var handle in handles.Values)
        {
            handle.Dispose();
        }

        handles.Clear();
        accessOrder.Clear();
        accessNodes.Clear();
        embeddedFontBytesCache.Clear();

        atlas.Dispose();
    }

    /// <summary>Kicks off building the given sizes for one (family, bold, italic) combo. Safe to
    /// call repeatedly — <see cref="GetOrCreateHandle"/> is itself a no-op for an already-cached
    /// key.</summary>
    private void WarmSizes(string familyId, bool bold, bool italic, float[] sizes)
    {
        var descriptor = ProfileFontCatalog.Resolve(familyId);
        var effectiveBold = bold && descriptor.SupportsBold;
        var effectiveItalic = italic && descriptor.SupportsItalic;

        foreach (var size in sizes)
        {
            GetOrCreateHandle(new FontKey(descriptor.Id, size, effectiveBold, effectiveItalic), descriptor);
        }
    }

    private static int FindTierIndex(float requestedPixelSize)
    {
        for (var i = 0; i < SizeLadder.Length; i++)
        {
            if (SizeLadder[i] >= requestedPixelSize)
            {
                return i;
            }
        }

        // Requested size exceeds every tier: use the largest available (an upscale in this one
        // extreme-zoom case is unavoidable, but still far less severe than the old default-font-
        // stretched-5x scenario this service replaces).
        return SizeLadder.Length - 1;
    }

    private IFontHandle GetOrCreateHandle(FontKey key, ProfileFontFamilyDescriptor descriptor)
    {
        if (handles.TryGetValue(key, out var existing))
        {
            TouchAccess(key);
            return existing;
        }

        var handle = BuildHandle(descriptor, key.SizePx, key.Bold, key.Italic);
        handles[key] = handle;
        accessNodes[key] = accessOrder.AddLast(key);

        EvictExcess();

        return handle;
    }

    /// <summary>Moves a key to the most-recently-used end, for LRU eviction.</summary>
    private void TouchAccess(FontKey key)
    {
        if (!accessNodes.TryGetValue(key, out var node))
        {
            return;
        }

        accessOrder.Remove(node);
        accessNodes[key] = accessOrder.AddLast(key);
    }

    private IFontHandle BuildHandle(ProfileFontFamilyDescriptor descriptor, float sizePx, bool bold, bool italic) =>
        atlas.NewDelegateFontHandle(e => e.OnPreBuild(toolkit =>
        {
            var config = new SafeFontConfig { SizePx = sizePx };
            var resourceName = GetEmbeddedResourceName(descriptor.Id, bold, italic);

            toolkit.Font = resourceName is not null
                ? toolkit.AddFontFromMemory(GetEmbeddedFontBytes(resourceName), config, resourceName)
                : toolkit.AddDalamudDefaultFont(sizePx);
        }));

    /// <summary>Maps a curated family id + real style to its embedded TTF's logical resource
    /// name (see the AetherFrame.csproj Fonts glob), or null for <see cref="ProfileFontFamilies.DalamudDefault"/>
    /// (built via <see cref="IFontAtlasBuildToolkitPreBuild.AddDalamudDefaultFont"/> instead).</summary>
    private static string? GetEmbeddedResourceName(string familyId, bool bold, bool italic) => familyId switch
    {
        ProfileFontFamilies.AetherFrameSans => FaceResourceName("PTSans", bold, italic),
        ProfileFontFamilies.AetherFrameSerif => FaceResourceName("PTSerif", bold, italic),
        ProfileFontFamilies.AetherFrameMono => FaceResourceName("Cousine", bold, italic),
        _ => null,
    };

    private static string FaceResourceName(string filePrefix, bool bold, bool italic)
    {
        var suffix = (bold, italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            (false, false) => "Regular",
        };

        return $"AetherFrame.Fonts.{filePrefix}-{suffix}.ttf";
    }

    /// <summary>Reads an embedded font's bytes once and reuses them for every ladder tier built
    /// from it, rather than re-reading the manifest resource stream per tier.</summary>
    private byte[] GetEmbeddedFontBytes(string resourceName)
    {
        if (embeddedFontBytesCache.TryGetValue(resourceName, out var cached))
        {
            return cached;
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded font resource '{resourceName}' was not found.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();

        embeddedFontBytesCache[resourceName] = bytes;
        return bytes;
    }

    /// <summary>LRU eviction once the cache exceeds <see cref="MaxCachedHandles"/>: the least-
    /// recently-used entry goes, not simply the oldest-built one, so a heavily-reused common tier
    /// is never evicted just because it happened to be built early.</summary>
    private void EvictExcess()
    {
        while (accessOrder.Count > MaxCachedHandles && accessOrder.First is { } lruNode)
        {
            var lruKey = lruNode.Value;
            accessOrder.RemoveFirst();
            accessNodes.Remove(lruKey);

            if (handles.Remove(lruKey, out var evicted))
            {
                evicted.Dispose();
            }
        }
    }

    private readonly record struct FontKey(string FamilyId, float SizePx, bool Bold, bool Italic);
}
