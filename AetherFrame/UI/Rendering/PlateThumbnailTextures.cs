using System;
using AetherFrame.Services;
using AetherFrame.Services.Caching;
using AetherFrame.Services.Thumbnails;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Loads Ready thumbnail images for My Plates cards through Dalamud's shared texture pipeline
/// (never a GPU render target), bounded to <see cref="MaxCachedThumbnails"/> entries — a few
/// screens of cards; Dalamud itself releases any texture that stops being drawn. A thumbnail file
/// that fails to decode is reported back so its card falls back instead of retrying every frame.
/// Render thread only.
/// </summary>
internal sealed class PlateThumbnailTextures
{
    internal const int MaxCachedThumbnails = 64;

    private readonly PlateThumbnailService thumbnails;
    private readonly LruCache<string, ISharedImmediateTexture> textures = new(MaxCachedThumbnails);

    internal PlateThumbnailTextures(PlateThumbnailService thumbnails)
    {
        this.thumbnails = thumbnails;
    }

    /// <summary>The thumbnail's texture for this frame, or null (still loading, or failed).</summary>
    internal IDalamudTextureWrap? GetWrapOrNull(Guid plateId, PlateThumbnail thumbnail)
    {
        if (thumbnail.State != PlateThumbnailState.Ready || thumbnail.ImagePath is not { } path)
        {
            return null;
        }

        if (!textures.TryGetValue(path, out var shared))
        {
            shared = DalamudServices.TextureProvider.GetFromFile(path);
            textures.Set(path, shared);
        }

        if (shared.TryGetWrap(out var wrap, out var exception))
        {
            return wrap;
        }

        if (exception is not null)
        {
            textures.Remove(path);
            thumbnails.ReportDisplayFailure(plateId, exception.Message);
        }

        return null;
    }

    internal void Clear() => textures.Clear();
}
