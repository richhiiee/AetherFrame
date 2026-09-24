using System;
using System.Collections.Generic;
using System.IO;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Owns the GPU textures of the artwork bundled inside the plugin assembly
/// (<see cref="BuiltInArtCatalog"/>). Each artwork is read from its manifest resource and decoded
/// once, the first time a Plate draws it, into a short chain of halved levels (see
/// <see cref="BundledArtImage"/>); drawing then only picks a level for the on-screen size — nothing
/// is decoded, created or allocated per frame. Textures live until the plugin unloads (the whole
/// chain of one 512px artwork is about 1.4 MB). A resource that is missing or fails to decode is
/// logged once and simply not drawn, like a missing managed image.
/// </summary>
internal sealed class BuiltInArtTextureCache : IDisposable
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>The level of <paramref name="art"/> to draw <paramref name="screenPixels"/> across, or null when it can't be loaded.</summary>
    internal IDalamudTextureWrap? GetWrapOrNull(BuiltInArtAsset art, float screenPixels)
    {
        if (!entries.TryGetValue(art.Id, out var entry))
        {
            entry = Load(art);
            entries[art.Id] = entry;
        }

        return entry.Levels.Length == 0 ? null : entry.Levels[BundledArtImage.SelectLevel(entry.Sizes, screenPixels)];
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values)
        {
            foreach (var level in entry.Levels)
            {
                level.Dispose();
            }
        }

        entries.Clear();
    }

    private static Entry Load(BuiltInArtAsset art)
    {
        var created = new List<IDalamudTextureWrap>();
        try
        {
            using var stream = typeof(BuiltInArtTextureCache).Assembly.GetManifestResourceStream(art.ResourceName)
                ?? throw new FileNotFoundException("bundled resource is missing");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            var top = BundledArtImage.DecodePng(bytes);
            if (top.Size != art.PixelSize)
            {
                throw new InvalidDataException($"expected {art.PixelSize}px, found {top.Size}px");
            }

            var levels = BundledArtImage.BuildLevels(top);
            var sizes = new int[levels.Count];
            for (var i = 0; i < levels.Count; i++)
            {
                sizes[i] = levels[i].Size;
                created.Add(DalamudServices.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(levels[i].Size, levels[i].Size), levels[i].Rgba, $"AetherFrame.Art.{art.Id}.{levels[i].Size}"));
            }

            return new Entry([.. created], sizes);
        }
        catch (Exception ex)
        {
            foreach (var wrap in created)
            {
                wrap.Dispose();
            }

            DalamudServices.Log.Warning(ex, $"AetherFrame could not load the built-in artwork {art.Id}.");
            return new Entry([], []);
        }
    }

    private sealed record Entry(IDalamudTextureWrap[] Levels, int[] Sizes);
}
