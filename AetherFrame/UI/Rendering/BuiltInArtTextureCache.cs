using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Owns the GPU textures of the artwork bundled inside the plugin assembly
/// (<see cref="BuiltInArtCatalog"/>). Each artwork is read from its manifest resource, decoded into
/// a short chain of halved levels (see <see cref="BundledArtImage"/>) and uploaded once, the first
/// time a Plate draws it — on the thread pool, never inside Draw (see <see cref="BuiltInArtLoader{TTexture}"/>):
/// until it is ready the artwork simply isn't drawn, and drawing then only picks a level for the
/// on-screen size — nothing is decoded, created or allocated per frame. Textures live until the
/// plugin unloads (a level chain costs about 4/3 of its top level: ~1.4 MB for a 512 x 512
/// artwork, ~8.4 MB for each full-resolution Celestial Sakura piece). A resource that is missing or
/// fails to decode is logged once and simply not drawn, like a missing managed image.
/// </summary>
internal sealed class BuiltInArtTextureCache : IDisposable
{
    private readonly BuiltInArtLoader<IDalamudTextureWrap> loader = new(LoadAsync);

    /// <summary>The level of <paramref name="art"/> to draw <paramref name="screenPixels"/> across, or null
    /// while it is loading or when it can't be loaded.</summary>
    internal IDalamudTextureWrap? GetWrapOrNull(BuiltInArtAsset art, float screenPixels) => loader.GetLevelOrNull(art, screenPixels);

    public void Dispose() => loader.Dispose();

    /// <summary>Runs on the thread pool: decode and prepare (CPU), then upload through Dalamud's async API.</summary>
    private static async Task<LoadedArt<IDalamudTextureWrap>> LoadAsync(BuiltInArtAsset art, CancellationToken cancellationToken)
    {
        var created = new List<IDalamudTextureWrap>();
        try
        {
            byte[] bytes;
            using (var stream = typeof(BuiltInArtTextureCache).Assembly.GetManifestResourceStream(art.ResourceName)
                ?? throw new FileNotFoundException("bundled resource is missing"))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            var levels = BundledArtImage.LoadLevels(bytes, art);
            var sizes = new int[levels.Count];
            for (var i = 0; i < levels.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sizes[i] = levels[i].LongSide;
                created.Add(await DalamudServices.TextureProvider.CreateFromRawAsync(
                    RawImageSpecification.Rgba32(levels[i].Width, levels[i].Height), levels[i].Rgba, $"AetherFrame.Art.{art.Id}.{levels[i].LongSide}", cancellationToken)
                    .ConfigureAwait(false));
            }

            return new LoadedArt<IDalamudTextureWrap>(created, sizes);
        }
        catch (Exception ex)
        {
            foreach (var wrap in created)
            {
                wrap.Dispose();
            }

            if (ex is not OperationCanceledException)
            {
                DalamudServices.Log.Warning(ex, $"AetherFrame could not load the built-in artwork {art.Id}.");
            }

            throw;
        }
    }
}
