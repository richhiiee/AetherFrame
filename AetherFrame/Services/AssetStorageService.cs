using System;
using System.IO;

namespace AetherFrame.Services;

/// <summary>
/// Copies imported image files into AetherFrame-managed local storage and resolves a stable
/// <see cref="Guid"/> asset id back to the file on disk. Deliberately plain file IO: images are
/// not profile data (never stored in <c>IReliableFileStorage</c> or embedded/base64'd into
/// profile JSON) — profile documents only ever reference an asset id.
/// </summary>
internal sealed class AssetStorageService
{
    private const string AssetsDirectoryName = "assets";

    private readonly string directory;

    internal AssetStorageService()
    {
        directory = Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, AssetsDirectoryName);
    }

    /// <summary>
    /// Copies an image file into managed storage under a freshly generated asset id and
    /// returns that id. The source file is never depended on again afterwards.
    /// </summary>
    internal Guid ImportImage(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new InvalidOperationException($"Image file not found: {sourceFilePath}");
        }

        var extension = Path.GetExtension(sourceFilePath);
        if (!ImageFormatSupport.IsSupported(extension))
        {
            throw new InvalidOperationException($"Unsupported image format: {extension}");
        }

        Directory.CreateDirectory(directory);

        var assetId = Guid.NewGuid();
        var destinationPath = GetAssetPath(assetId, extension);
        File.Copy(sourceFilePath, destinationPath, overwrite: false);

        return assetId;
    }

    /// <summary>Resolves an asset id to its file on disk, or null if it's missing/unreadable.</summary>
    internal string? ResolveAssetPath(Guid assetId)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var matches = Directory.GetFiles(directory, assetId.ToString("N") + ".*");
        return matches.Length > 0 ? matches[0] : null;
    }

    private string GetAssetPath(Guid assetId, string extension) =>
        Path.Combine(directory, assetId.ToString("N") + extension.ToLowerInvariant());
}
