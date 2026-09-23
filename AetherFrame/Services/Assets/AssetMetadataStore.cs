using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AetherFrame.Domain.Assets;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Assets;

/// <summary>
/// Asset metadata sidecars ("asset-metadata/{guid:N}.json"), in their own directory so they can
/// never be mistaken for an asset file. Metadata is derived data: it's written at import, created
/// lazily for older assets, and an asset works exactly the same without it. Plain file IO — like
/// the assets themselves, it isn't irreplaceable document data.
/// </summary>
internal sealed class AssetMetadataStore
{
    private readonly string directory;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;

    internal AssetMetadataStore(string directory, IAetherFrameLog? log = null, Func<DateTime>? utcNow = null)
    {
        this.directory = directory;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal string GetPath(Guid assetId) => Path.Combine(directory, assetId.ToString("N") + ".json");

    /// <summary>The stored metadata, or null when missing, damaged, or from a newer version.</summary>
    internal AssetMetadata? TryLoad(Guid assetId)
    {
        var path = GetPath(assetId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var result = VersionedJson.Parse<AssetMetadata>(File.ReadAllText(path, Encoding.UTF8), PersistenceSchemas.AssetMetadata);
            return result.IsUsable && result.Value!.AssetId == assetId ? result.Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not read metadata for asset {assetId}: {ex.Message}");
            return null;
        }
    }

    internal void Save(AssetMetadata metadata)
    {
        metadata.Version = AssetMetadata.CurrentVersion;
        SystemFileStore.WriteAtomically(GetPath(metadata.AssetId), Encoding.UTF8.GetBytes(VersionedJson.Serialize(metadata)));
    }

    /// <summary>
    /// Stored metadata, or — for an asset imported before metadata existed, or whose metadata was
    /// lost — metadata computed from the asset file itself and then stored. Reads and hashes the
    /// whole file, so never call this from ImGui Draw. Null when the asset file is unreadable.
    /// </summary>
    internal AssetMetadata? GetOrCreate(Guid assetId, string assetPath)
    {
        if (TryLoad(assetId) is { } existing)
        {
            return existing;
        }

        var inspection = ImageSafety.Inspect(assetPath);
        if (inspection is null)
        {
            return null;
        }

        var metadata = new AssetMetadata
        {
            AssetId = assetId,

            // The original name is unknown for an older asset; the managed file name is all there is.
            OriginalFileName = Path.GetFileName(assetPath),
            MediaType = inspection.MediaType,
            ByteLength = inspection.ByteLength,
            PixelWidth = inspection.Width,
            PixelHeight = inspection.Height,
            FrameCount = Math.Max(1, inspection.FrameCount),
            Sha256 = ComputeSha256(assetPath),
            CreatedAtUtc = utcNow(),
        };

        try
        {
            Save(metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not store metadata for asset {assetId}: {ex.Message}");
        }

        return metadata;
    }

    internal void Delete(Guid assetId)
    {
        var path = GetPath(assetId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Lowercase hex SHA-256 of a file's bytes, streamed (never loaded whole).</summary>
    internal static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
