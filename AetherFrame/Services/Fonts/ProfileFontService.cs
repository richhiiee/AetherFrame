using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using Dalamud.Interface.ManagedFontAtlas;

namespace AetherFrame.Services.Fonts;

/// <summary>
/// Owns AetherFrame's own isolated <see cref="IFontAtlas"/>es (one per family) and hands out cached
/// <see cref="IFontHandle"/>s so <c>ProfileRenderer</c> can draw a <see cref="TextProfileElement"/>
/// crisply at its own requested size, without ever visibly stretching a smaller rasterized font
/// up to get there — and without the startup cost of building every size anyone might ever ask
/// for before anyone actually asks for it.
///
/// Every distinct (family, size, bold, italic) combination is only ever built once and reused
/// after — see <see cref="GetHandle"/> — but "size" is not the raw requested pixel size: it's
/// snapped to the nearest tier AT OR ABOVE the request in <see cref="FontTierPolicy.SizeLadder"/>,
/// a fixed, fairly fine-grained set of sizes. This is what makes the whole strategy work:
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
/// <item>At construction: only <see cref="FontTierPolicy.CommonEditorSizes"/> (a handful of
/// sizes, not the whole ladder) for AetherFrame's own default family — the one new text actually
/// uses. Dalamud Default (which can carry a much larger CJK/game-symbol/icon glyph set) is never
/// built until something actually needs it.</item>
/// <item>When a profile loads: for each distinct (family, bold, italic) combo its text elements
/// actually use, only the ladder tier matching that element's OWN nominal size, plus
/// <see cref="FontTierPolicy.CommonEditorSizes"/> for that combo — not all 26 tiers regardless
/// of whether they're ever requested.</item>
/// </list>
///
/// Everything else — an unusual zoom level, a size nobody anticipated — is built lazily, once,
/// the first time <see cref="GetHandle"/> actually needs it, and reused after via the same cache.
/// Every batch of more-than-one handle creation (both eager-warm paths above) holds back each
/// atlas's rebuilds (<see cref="IFontAtlas.SuppressAutoRebuild"/>), so it costs one rebuild per
/// atlas instead of one per handle.
///
/// Each family's faces live in an atlas of their own, and what is kept and let go is
/// <see cref="FontHandleCache{TAtlas, THandle}"/>'s rule (issue #117): an atlas rebuild
/// re-rasterizes every face in that atlas, so with one shared atlas every font tried made the next
/// one slower to appear. Each family only builds tiers up to the size its glyph set can afford
/// (<see cref="FontTierPolicy"/>; a request above that uses the largest allowed tier, as a request
/// above the ladder's top always has). The bundled families keep every glyph their TTFs map.
/// </summary>
internal sealed class ProfileFontService : IDisposable
{
    private readonly FontHandleCache<IFontAtlas, IFontHandle> cache;

    // Read on the atlases' build threads, several of which can run at once (one per family).
    private readonly Dictionary<string, byte[]> embeddedFontBytesCache = new();
    private readonly object embeddedFontBytesLock = new();

    private static readonly object PrewarmedMarker = new();

    // Which document instances have been prewarmed (weakly held: never keeps a document alive).
    // Per instance rather than "the last one seen", so two documents drawn in the same frame —
    // the open Plate in an editor and a different Plate in the Plate Viewer — are each warmed
    // once instead of alternating (and re-warming) every frame.
    private readonly ConditionalWeakTable<ProfileDocument, object> prewarmedProfiles = new();

    internal ProfileFontService()
    {
        cache = new FontHandleCache<IFontAtlas, IFontHandle>(new DalamudFontBackend(this), static () => Environment.TickCount64);

        // "A sensible set of common editor sizes", and only for the family new text actually
        // uses. Dalamud Default is deliberately NOT warmed here — it can carry a much larger
        // glyph set (CJK, game symbols, icons), and every legacy profile that actually uses it
        // gets it warmed on load instead (see EnsurePrewarmed); one that doesn't use it never
        // pays for it at all.
        using (cache.Batch())
        {
            WarmSizes(ProfileFontFamilies.AetherFrameSans, bold: false, italic: false, FontTierPolicy.CommonEditorSizes);
        }
    }

    /// <summary>What the cache holds and how long new fonts took to appear (<c>/af fonts</c>).</summary>
    internal FontCacheStats Stats => cache.Stats;

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
        var (effectiveBold, effectiveItalic) = FontLibrary.EffectiveStyle(descriptor.Id, bold, italic, descriptor.SupportsBold, descriptor.SupportsItalic);
        var tierIndex = FontTierPolicy.FindTierIndex(descriptor.Id, requestedPixelSize);

        // The ideal tier, or an already-built larger one of the same family and style (a
        // downscale, never the blurry upscale this service exists to avoid). Nothing ready yet is
        // true only for the frames before this family's first tier finishes building; then
        // Dalamud's own default font handle is returned, which callers treat as "not yet"
        // (see the isColdStartFallback overload).
        return cache.Get(descriptor.Id, tierIndex, effectiveBold, effectiveItalic)
            ?? DalamudServices.PluginInterface.UiBuilder.DefaultFontHandle;
    }

    /// <summary>
    /// Ensures every distinct (family, bold, italic) combination actually used by
    /// <paramref name="profile"/>'s text elements has at least its own nominal size (and the
    /// common-size baseline) warmed. Safe (and cheap — a single weak-table lookup) to call
    /// every frame; does real work only the first time it sees a given profile instance.
    /// </summary>
    internal void EnsurePrewarmed(ProfileDocument? profile)
    {
        if (profile is null || prewarmedProfiles.TryGetValue(profile, out _))
        {
            return;
        }

        prewarmedProfiles.AddOrUpdate(profile, PrewarmedMarker);

        // One rebuild per atlas for the whole profile's worth of newly-needed handles, not one per handle.
        using var batch = cache.Batch();

        var warmedCombos = new HashSet<(string FamilyId, bool Bold, bool Italic)>();

        foreach (var element in profile.Elements)
        {
            if (element is not TextProfileElement text)
            {
                continue;
            }

            var descriptor = ProfileFontCatalog.Resolve(text.FontFamily);
            var (effectiveBold, effectiveItalic) = FontLibrary.EffectiveStyle(descriptor.Id, text.Bold, text.Italic, descriptor.SupportsBold, descriptor.SupportsItalic);

            // A library family warms only the tier its text uses: a Plate (a shared one above all)
            // may name dozens of library families, and six sizes of each would crowd the atlas.
            if (!FontTierPolicy.UsesFallback(descriptor.Id) && warmedCombos.Add((descriptor.Id, effectiveBold, effectiveItalic)))
            {
                // The common baseline (covers most zoom/view-scale variations of this combo)...
                WarmSizes(descriptor.Id, effectiveBold, effectiveItalic, FontTierPolicy.CommonEditorSizes);
            }

            // ...plus this specific element's own nominal size exactly (or the family's largest
            // allowed tier, for a size beyond it), so what the profile actually contains is never
            // left to the lazy/on-demand path alone.
            var ownTier = FontTierPolicy.SizeLadder[FontTierPolicy.FindTierIndex(descriptor.Id, text.FontSize)];
            cache.GetOrCreate(new FontCacheKey(descriptor.Id, ownTier, effectiveBold, effectiveItalic));
        }
    }

    public void Dispose()
    {
        cache.Dispose();
        lock (embeddedFontBytesLock)
        {
            embeddedFontBytesCache.Clear();
        }
    }

    /// <summary>Kicks off building the given sizes for one (family, bold, italic) combo. Safe to
    /// call repeatedly — <see cref="GetOrCreateHandle"/> is itself a no-op for an already-cached
    /// key.</summary>
    private void WarmSizes(string familyId, bool bold, bool italic, IReadOnlyList<float> sizes)
    {
        var descriptor = ProfileFontCatalog.Resolve(familyId);
        var effectiveBold = bold && descriptor.SupportsBold;
        var effectiveItalic = italic && descriptor.SupportsItalic;

        foreach (var size in sizes)
        {
            cache.GetOrCreate(new FontCacheKey(descriptor.Id, size, effectiveBold, effectiveItalic));
        }
    }

    private IFontHandle BuildHandle(IFontAtlas atlas, ProfileFontFamilyDescriptor descriptor, float sizePx, bool bold, bool italic) =>
        atlas.NewDelegateFontHandle(e => e.OnPreBuild(toolkit =>
        {
            // The bundled faces keep every glyph their TTF maps, as 0.1.5 built them (see
            // FontTierPolicy.GlyphRanges); what bounds a tier is its size cap, not its glyphs.
            var config = new SafeFontConfig { SizePx = sizePx, GlyphRanges = FontTierPolicy.GlyphRanges(descriptor.Id) };
            var resourceName = GetEmbeddedResourceName(descriptor.Id, bold, italic);

            toolkit.Font = resourceName is not null
                ? toolkit.AddFontFromMemory(GetEmbeddedFontBytes(resourceName), config, resourceName)
                : toolkit.AddDalamudDefaultFont(sizePx);

            // A library family draws a character it lacks (an accented letter in a display face,
            // say) in AetherFrame Sans of the same style, rather than as "?": ImGui merges only the
            // codepoints the family doesn't map.
            if (FontTierPolicy.UsesFallback(descriptor.Id))
            {
                var fallbackName = FaceResourceName("PTSans", bold, italic);
                var fallback = new SafeFontConfig { SizePx = sizePx, GlyphRanges = FontTierPolicy.FallbackGlyphRanges, MergeFont = toolkit.Font };
                toolkit.AddFontFromMemory(GetEmbeddedFontBytes(fallbackName), fallback, fallbackName);
            }
        }));

    /// <summary>Maps a curated family id + real style to its embedded TTF's logical resource
    /// name (see the AetherFrame.csproj Fonts glob), or null for <see cref="ProfileFontFamilies.DalamudDefault"/>
    /// (built via <see cref="IFontAtlasBuildToolkitPreBuild.AddDalamudDefaultFont"/> instead).</summary>
    private static string? GetEmbeddedResourceName(string familyId, bool bold, bool italic) => familyId switch
    {
        ProfileFontFamilies.AetherFrameSans => FaceResourceName("PTSans", bold, italic),
        ProfileFontFamilies.AetherFrameSerif => FaceResourceName("PTSerif", bold, italic),
        ProfileFontFamilies.AetherFrameMono => FaceResourceName("Cousine", bold, italic),
        _ when FontLibrary.Find(familyId) is { } library => FontLibrary.ResourceName(library, FontLibrary.FaceStyle(library, bold, italic)),
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
    /// from it, rather than re-reading the manifest resource stream per tier. Called on the
    /// atlases' build threads.</summary>
    private byte[] GetEmbeddedFontBytes(string resourceName)
    {
        lock (embeddedFontBytesLock)
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
    }

    /// <summary>Dalamud's side of the cache: one async-rebuilding atlas per family, outside the
    /// global scale (Plate text is sized by the Plate, not the UI).</summary>
    private sealed class DalamudFontBackend(ProfileFontService owner) : IFontAtlasBackend<IFontAtlas, IFontHandle>
    {
        public IFontAtlas CreateAtlas(string familyId) =>
            DalamudServices.PluginInterface.UiBuilder.CreateFontAtlas(FontAtlasAutoRebuildMode.Async, isGlobalScaled: false, $"AetherFrame.ProfileFonts.{familyId}");

        public IFontHandle CreateHandle(IFontAtlas atlas, FontCacheKey key) =>
            owner.BuildHandle(atlas, ProfileFontCatalog.Resolve(key.FamilyId), key.SizePx, key.Bold, key.Italic);

        public bool IsAvailable(IFontHandle handle) => handle.Available;

        public void WhenAvailable(IFontHandle handle, Action available) =>
            _ = handle.WaitAsync().ContinueWith(
                task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        available();
                    }
                    else
                    {
                        _ = task.Exception; // disposed before it was built: observed, so it isn't logged as an error
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        public IDisposable SuppressRebuild(IFontAtlas atlas) => atlas.SuppressAutoRebuild();

        public void DisposeHandle(IFontHandle handle) => handle.Dispose();

        public void DisposeAtlas(IFontAtlas atlas) => atlas.Dispose();
    }
}
