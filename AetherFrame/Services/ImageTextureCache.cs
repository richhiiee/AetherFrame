using System;
using System.Collections.Generic;
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
/// </summary>
internal sealed class ImageTextureCache
{
    private readonly AssetStorageService assetStorage;
    private readonly Dictionary<Guid, ISharedImmediateTexture?> textures = new();
    private readonly HashSet<Guid> loggedFailures = new();

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
            textures[assetId] = shared;

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

    /// <summary>Forces a fresh load next time an asset is requested, e.g. after Replace Image.</summary>
    internal void Invalidate(Guid assetId)
    {
        textures.Remove(assetId);
        loggedFailures.Remove(assetId);
    }

    /// <summary>Drops every cached texture reference, e.g. on profile switch or plugin unload.</summary>
    internal void Clear()
    {
        textures.Clear();
        loggedFailures.Clear();
    }

    private void LogFailureOnce(Guid assetId, string reason)
    {
        if (loggedFailures.Add(assetId))
        {
            DalamudServices.Log.Warning($"AetherFrame could not load image asset {assetId}: {reason}");
        }
    }
}
