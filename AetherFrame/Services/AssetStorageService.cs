using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Domain.Assets;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services;

/// <summary>
/// Copies imported image files into AetherFrame-managed local storage and resolves a stable
/// <see cref="Guid"/> asset id back to the file on disk. Deliberately plain file IO: images are
/// not profile data (never stored in <c>IReliableFileStorage</c> or embedded/base64'd into
/// profile JSON) — profile documents only ever reference an asset id.
///
/// Imports are checked against <see cref="ImageSafety"/>'s hard limits using the file's actual
/// content (never its extension), stored under the extension matching that content, hashed, and
/// described by an <see cref="AssetMetadata"/> sidecar. Existing assets keep working unchanged:
/// nothing here requires metadata or re-validates files already in storage.
/// </summary>
internal sealed class AssetStorageService
{
    private const int CopyBufferSize = 81920;

    private readonly string directory;
    private readonly string stagingDirectory;
    private readonly AssetMetadataStore metadataStore;
    private readonly Func<string, bool>? isDecoderSupported;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;

    private readonly object gate = new();
    private readonly HashSet<Guid> importedThisSession = new();

    /// <param name="isDecoderSupported">Whether the texture pipeline can decode an extension such
    /// as ".webp"; null accepts every format <see cref="ImageSafety"/> recognizes.</param>
    internal AssetStorageService(
        string assetsDirectory,
        string stagingDirectory,
        AssetMetadataStore metadataStore,
        Func<string, bool>? isDecoderSupported = null,
        IAetherFrameLog? log = null,
        Func<DateTime>? utcNow = null)
    {
        directory = assetsDirectory;
        this.stagingDirectory = stagingDirectory;
        this.metadataStore = metadataStore;
        this.isDecoderSupported = isDecoderSupported;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal string AssetsDirectory => directory;

    internal AssetMetadataStore Metadata => metadataStore;

    /// <summary>
    /// Assets imported since the plugin loaded. An unsaved editor session may reference any of
    /// them, so asset cleanup always treats them as in use.
    /// </summary>
    internal IReadOnlySet<Guid> ImportedThisSession
    {
        get
        {
            lock (gate)
            {
                return importedThisSession.ToHashSet();
            }
        }
    }

    /// <summary>
    /// Validates an image file and copies it into managed storage under a freshly generated asset
    /// id, returning that id. The source file is never depended on again afterwards. Throws
    /// <see cref="InvalidOperationException"/> with a player-facing message when the file is
    /// missing, not a supported image, or over a safety limit.
    ///
    /// Runs synchronously, as imports always have: header inspection is cheap, and the copy
    /// (which the import always did) now also hashes in the same single pass. Pixels are never
    /// decoded here.
    /// </summary>
    internal Guid ImportImage(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new InvalidOperationException("The image file couldn't be found.");
        }

        var inspection = ImageSafety.Inspect(sourceFilePath);
        if (ImageSafety.Validate(inspection, isDecoderSupported) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(stagingDirectory);

        var assetId = Guid.NewGuid();
        var stagingPath = Path.Combine(stagingDirectory, assetId.ToString("N") + ".importing");
        var destinationPath = Path.Combine(directory, assetId.ToString("N") + inspection!.Extension);

        try
        {
            var (byteLength, sha256) = CopyAndHash(sourceFilePath, stagingPath);

            // The source could have changed since it was inspected: what's validated is what's stored.
            var staged = ImageSafety.Inspect(stagingPath);
            if (ImageSafety.Validate(staged, isDecoderSupported) is { } stagedError)
            {
                throw new InvalidOperationException(stagedError);
            }

            File.Move(stagingPath, destinationPath, overwrite: false);
            RecordImport(assetId, sourceFilePath, staged!, byteLength, sha256);
        }
        finally
        {
            TryDelete(stagingPath);
        }

        lock (gate)
        {
            importedThisSession.Add(assetId);
        }

        return assetId;
    }

    /// <summary>
    /// Adds an image that an .aetherframe import already validated into managed storage, under
    /// <paramref name="assetId"/> — a brand-new id the import minted, never one from the package.
    /// The bytes are copied (staging is left alone), re-hashed and re-inspected from the copy, and
    /// must still match <paramref name="expectedSha256"/>, so what was validated is exactly what
    /// is stored. Never overwrites: refuses if any file for that id already exists. Returns the
    /// created file's path, which is all <see cref="RemoveImportedAsset"/> may later remove.
    /// Throws <see cref="InvalidOperationException"/> (player-facing) or an IO exception.
    /// </summary>
    internal string AddValidatedPackageImage(string stagedFilePath, Guid assetId, string expectedSha256, string originalFileName)
    {
        if (assetId == Guid.Empty || ResolveAssetPath(assetId) is not null)
        {
            throw new InvalidOperationException("An image id for the import was already in use.");
        }

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(stagingDirectory);

        var stagingPath = Path.Combine(stagingDirectory, assetId.ToString("N") + ".importing");
        try
        {
            var (byteLength, sha256) = CopyAndHash(stagedFilePath, stagingPath);
            if (sha256 != expectedSha256)
            {
                throw new InvalidOperationException("An image changed while it was being imported.");
            }

            var staged = ImageSafety.Inspect(stagingPath);
            if (ImageSafety.Validate(staged, isDecoderSupported) is { } error)
            {
                throw new InvalidOperationException(error);
            }

            var destinationPath = Path.Combine(directory, assetId.ToString("N") + staged!.Extension);
            File.Move(stagingPath, destinationPath, overwrite: false);

            lock (gate)
            {
                importedThisSession.Add(assetId);
            }

            RecordImport(assetId, originalFileName, staged, byteLength, sha256);
            return destinationPath;
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    /// <summary>
    /// Rolls back one <see cref="AddValidatedPackageImage"/>: deletes exactly the file it created
    /// (and that asset's metadata), and nothing else. Only ever called for an id minted by the
    /// same import, so it can never touch an image that existed before.
    /// </summary>
    internal void RemoveImportedAsset(Guid assetId, string createdPath)
    {
        if (Path.GetFileNameWithoutExtension(createdPath) != assetId.ToString("N")
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(createdPath)), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a file that isn't this import's managed image.");
        }

        TryDelete(createdPath);
        try
        {
            metadataStore.Delete(assetId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not remove metadata for a rolled-back image ({ex.GetType().Name}).");
        }
    }

    /// <summary>Resolves an asset id to its file on disk, or null if it's missing/unreadable.</summary>
    internal string? ResolveAssetPath(Guid assetId)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var matches = Directory.GetFiles(directory, assetId.ToString("N") + ".*");
        return matches.Length > 0 ? matches.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First() : null;
    }

    /// <summary>Every managed asset file whose name is an asset id.</summary>
    internal IReadOnlyList<(Guid AssetId, string Path)> ListAssets()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<(Guid, string)>();
        foreach (var path in Directory.GetFiles(directory))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var assetId))
            {
                result.Add((assetId, path));
            }
        }

        return result;
    }

    private void RecordImport(Guid assetId, string sourceFilePath, ImageInspection inspection, long byteLength, string sha256)
    {
        try
        {
            metadataStore.Save(new AssetMetadata
            {
                AssetId = assetId,

                // The bare file name only: a source path is never persisted.
                OriginalFileName = Path.GetFileName(sourceFilePath),
                MediaType = inspection.MediaType,
                ByteLength = byteLength,
                PixelWidth = inspection.Width,
                PixelHeight = inspection.Height,
                FrameCount = Math.Max(1, inspection.FrameCount),
                Sha256 = sha256,
                CreatedAtUtc = utcNow(),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Metadata is derived and can be recreated later; the import itself succeeded.
            log.Warning($"AetherFrame imported asset {assetId} but could not store its metadata: {ex.Message}");
        }
    }

    /// <summary>One read of the source: copies it and hashes the copied bytes, refusing to copy
    /// more than the size limit (in case the file grew after it was inspected).</summary>
    private static (long ByteLength, string Sha256) CopyAndHash(string sourcePath, string destinationPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize);

        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > ImageSafety.MaxFileBytes)
            {
                throw new InvalidOperationException($"The image is too large. The limit is {ImageSafety.MaxFileBytes / (1024 * 1024)} MB.");
            }

            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }

        destination.Flush(flushToDisk: true);
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not remove a temporary import file: {ex.Message}");
        }
    }
}
