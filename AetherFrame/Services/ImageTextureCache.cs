using System;
using AetherFrame.Services.Caching;
using AetherFrame.UI.Editor;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.Services;

/// <summary>
/// Runtime-only GPU texture cache keyed by asset id, so the canvas doesn't ask Dalamud to
/// resolve and load a file from disk on every single frame for every image element. Holds no
/// data that needs to persist; <see cref="Clear"/> drops every reference (e.g. on profile
/// switch or plugin unload) and lets Dalamud's own shared-texture lifetime take it from there —
/// <see cref="ISharedImmediateTexture"/> is not itself disposable, and disposing a rented
/// <see cref="IDalamudTextureWrap"/> here would fight Dalamud's own sharing/caching.
///
/// <para><b>Bounds.</b> The GPU memory itself is Dalamud's: a shared immediate texture is only
/// kept resident while something keeps drawing it, so a Plate that stops being shown releases
/// its images without anything here being disposed. What this class owns is its own lookup
/// state, which is LRU-bounded to <see cref="MaxCachedAssets"/> assets: far more than any one
/// Plate can show (a Plate holds at most 256 elements, and in practice a handful of images), yet
/// small enough that previewing many Plates in the Plate Viewer can't grow it without limit.</para>
/// </summary>
internal sealed class ImageTextureCache : IEditorImageInfo
{
    internal const int MaxCachedAssets = 256;

    private readonly AssetStorageService assetStorage;
    private readonly LruCache<Guid, ISharedImmediateTexture?> textures = new(MaxCachedAssets);
    private readonly LruCache<Guid, bool> loggedFailures = new(MaxCachedAssets);

    // Header-read native sizes for assets whose texture hasn't finished loading yet; read at
    // most once per asset (see GetNativeSize).
    private readonly LruCache<Guid, (int Width, int Height)?> headerSizes = new(MaxCachedAssets);

    internal ImageTextureCache(AssetStorageService assetStorage)
    {
        this.assetStorage = assetStorage;
    }

    /// <summary>
    /// Returns the loaded texture wrap for an asset, or null if the asset is missing, still
    /// loading, or failed to decode — callers should render a placeholder in that case.
    /// </summary>
    internal IDalamudTextureWrap? GetWrapOrNull(Guid assetId)
    {
        if (!textures.TryGetValue(assetId, out var shared))
        {
            var path = assetStorage.ResolveAssetPath(assetId);
            shared = path is null ? null : DalamudServices.TextureProvider.GetFromFile(path);
            textures.Set(assetId, shared);

            if (shared is null)
            {
                LogFailureOnce(assetId, "asset file is missing from managed storage");
            }
        }

        if (shared is null)
        {
            return null;
        }

        if (shared.TryGetWrap(out var wrap, out var exception))
        {
            return wrap;
        }

        // exception is null while the texture is still loading (not yet a failure); only log
        // once it's actually failed, and only once per asset so a broken image can't spam log.
        if (exception is not null)
        {
            LogFailureOnce(assetId, exception.Message);
        }

        return null;
    }

    /// <summary>
    /// The asset's native pixel size: from its loaded texture when available, otherwise from its
    /// file header (read once and cached, so this is cheap to call every frame), or null if the
    /// asset is missing or unreadable.
    /// </summary>
    internal (int Width, int Height)? GetNativeSize(Guid assetId)
    {
        if (GetWrapOrNull(assetId) is { } wrap)
        {
            return (wrap.Width, wrap.Height);
        }

        if (!headerSizes.TryGetValue(assetId, out var size))
        {
            var path = assetStorage.ResolveAssetPath(assetId);
            size = path is null ? null : ImageDimensionReader.TryReadDimensions(path);
            headerSizes.Set(assetId, size);
        }

        return size;
    }

    /// <summary>Forces a fresh load next time an asset is requested, e.g. after Replace Image.</summary>
    internal void Invalidate(Guid assetId)
    {
        textures.Remove(assetId);
        loggedFailures.Remove(assetId);
        headerSizes.Remove(assetId);
    }

    /// <summary>Drops every cached texture reference, e.g. on profile switch or plugin unload.</summary>
    internal void Clear()
    {
        textures.Clear();
        loggedFailures.Clear();
        headerSizes.Clear();
    }

    (int Width, int Height)? IEditorImageInfo.GetNativeSize(Guid assetId) => GetNativeSize(assetId);

    void IEditorImageInfo.Clear() => Clear();

    private void LogFailureOnce(Guid assetId, string reason)
    {
        if (!loggedFailures.ContainsKey(assetId))
        {
            loggedFailures.Set(assetId, true);
            DalamudServices.Log.Warning($"AetherFrame could not load image asset {assetId}: {reason}");
        }
    }
}
