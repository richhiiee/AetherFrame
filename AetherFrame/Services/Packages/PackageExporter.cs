using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Packages;

/// <summary>What to export, and where.</summary>
/// <param name="SavedJson">The Plate's saved JSON exactly as stored (never unsaved editor state).</param>
/// <param name="PlateName">The Library's name for the Plate.</param>
/// <param name="PreviewPngPath">A picture of this saved Plate (from the thumbnail cache), or null.</param>
/// <param name="Overwrite">Replace an existing file at <paramref name="DestinationPath"/> — only after the player confirmed it.</param>
internal sealed record PackageExportRequest(string SavedJson, string PlateName, string DestinationPath, string? PreviewPngPath = null, bool Overwrite = false);

/// <summary>
/// Writes one Plate as an .aetherframe file.
///
/// <para><b>Privacy.</b> A package holds the Plate's creative content and nothing about the
/// installation it came from. Removed from profile.json: the Plate's local id, its legacy
/// character owner (a Content ID), its local revision counter, and its local created/modified
/// times. Character bindings, Active state, Library order, trash, thumbnails, asset metadata
/// (including original file names), and file paths are never read at all. Text the player put
/// ON the Plate — a character name, a World — is creative content and stays.</para>
///
/// <para><b>Images.</b> Exactly the images the Plate references, resolved only through managed
/// asset storage by id (never a path from anywhere else), each checked by the same image checks
/// an import runs, and declared with the SHA-256 of the exact bytes written. A missing or unusable
/// image fails the export: a package never claims to be complete when it isn't. Inside the
/// package, each image is identified by an id derived from its content (see
/// <see cref="PackageAssetIdFor"/>), not its local id — so two exports from one installation
/// can't be linked through local ids, and the same image always gets the same package id.</para>
///
/// <para><b>Safety net.</b> The package is written to a temporary file beside the destination,
/// then opened and validated by <see cref="PackageReader"/> exactly as an import would, and only
/// then moved into place. An export that its own importer would refuse is never produced.</para>
///
/// <para><b>Determinism.</b> Unchanged Plates give identical manifest and profile bytes: fixed
/// entry order (manifest, profile, assets by id, preview), fixed entry timestamps, fixed field
/// order, and no export time or other per-run data.</para>
/// </summary>
internal static class PackageExporter
{
    /// <summary>Profile fields that are local history or identity, not Plate content.</summary>
    internal static readonly IReadOnlyList<string> StrippedProfileFields =
    [
        nameof(ProfileDocument.ProfileId),
        nameof(ProfileDocument.OwnerContentId),
        nameof(ProfileDocument.Revision),
        nameof(ProfileDocument.CreatedAtUtc),
        nameof(ProfileDocument.UpdatedAtUtc),
    ];

    /// <summary>The earliest timestamp a ZIP entry can hold; every entry gets it.</summary>
    private static readonly DateTimeOffset FixedEntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static PackageExportResult Export(
        PackageExportRequest request, AssetStorageService assets, string stagingRoot, string generator,
        Func<string, bool>? isDecoderSupported = null, IAetherFrameLog? log = null)
    {
        log ??= NullAetherFrameLog.Instance;

        if (!request.DestinationPath.EndsWith(PackagePolicy.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return PackageExportResult.Failed(PackageErrorCode.ExportFailed, $"Plate files must end in {PackagePolicy.FileExtension}.");
        }

        if (File.Exists(request.DestinationPath) && !request.Overwrite)
        {
            return PackageExportResult.Failed(PackageErrorCode.ExportFailed, "A file with that name already exists.");
        }

        // ---- the Plate, sanitized.
        JsonObject profile;
        ProfileDocument document;
        try
        {
            profile = JsonNode.Parse(request.SavedJson) as JsonObject ?? throw new System.Text.Json.JsonException("not an object");
            document = PlateDocuments.Materialize((JsonObject)profile.DeepClone());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            return PackageExportResult.Failed(PackageErrorCode.ExportFailed, "This Plate couldn't be read, so it can't be exported.", ex.GetType().Name);
        }

        if (!PlateNaming.TryNormalizeName(request.PlateName, out var plateName, out _))
        {
            plateName = PlateNaming.DefaultName;
        }

        foreach (var field in StrippedProfileFields)
        {
            profile.Remove(field);
        }

        profile[nameof(ProfileDocument.Name)] = plateName;
        var schemaVersion = PersistenceSchemas.ProfileDocument.TryReadVersion(profile, out var version, out _) ? version : ProfileDocument.CurrentSchemaVersion;

        // ---- its images, re-pointed at their package ids.
        var planned = PlanAssets(document, assets, isDecoderSupported, out var localToPackage, out var assetErrors);
        if (assetErrors.Count > 0)
        {
            return PackageExportResult.Failed(assetErrors);
        }

        PackageAssetIds.Remap(profile, localToPackage);
        var profileBytes = Encoding.UTF8.GetBytes(VersionedJson.Serialize(profile));

        var previewPath = request.PreviewPngPath is { } candidate && File.Exists(candidate)
            && PackageReader.CheckImage(candidate, "image/png", 0, 0, isDecoderSupported, out _) is { Format: DetectedImageFormat.Png } preview
            && preview.Width <= PackagePolicy.MaxPreviewDimension && preview.Height <= PackagePolicy.MaxPreviewDimension
            && preview.ByteLength <= PackagePolicy.MaxPreviewBytes
                ? candidate
                : null;

        // ---- write beside the destination, self-check, then move into place.
        var directory = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))!;
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}{PackagePolicy.FileExtension}.tmp");
        try
        {
            WritePackage(temporaryPath, profileBytes, plateName, schemaVersion, planned, previewPath, generator);

            using (var check = PackageReader.Open(temporaryPath, stagingRoot, isDecoderSupported, log))
            {
                if (check.Compatibility is not (PackageCompatibility.Supported or PackageCompatibility.SupportedWithWarnings))
                {
                    log.Warning("AetherFrame's export self-check refused its own package: " + check.DescribeForLog());
                    return PackageExportResult.Failed(check.Diagnostics.Errors.Count > 0
                        ? check.Diagnostics.Errors
                        : [new PackageError(PackageErrorCode.ExportFailed, "The Plate couldn't be exported.")]);
                }
            }

            File.Move(temporaryPath, request.DestinationPath, overwrite: request.Overwrite);
            log.Information($"AetherFrame exported a Plate ({planned.Count} images, preview: {previewPath is not null}).");
            return new PackageExportResult(true, request.DestinationPath, planned.Count, previewPath is not null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Warning($"AetherFrame could not write an exported Plate ({ex.GetType().Name}).");
            return PackageExportResult.Failed(PackageErrorCode.ExportFailed,
                ex is IOException && File.Exists(request.DestinationPath) && !request.Overwrite
                    ? "A file with that name already exists."
                    : "The file couldn't be saved there. Check the folder and try again.",
                ex.GetType().Name);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warning($"AetherFrame could not remove a temporary export file ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>
    /// An image's id inside a package: the first 16 bytes of its SHA-256. Deterministic, the same
    /// for identical images, and unrelated to anything in the installation it came from. (Import
    /// never trusts it as an identity: every imported image gets a brand-new local id.)
    /// </summary>
    internal static Guid PackageAssetIdFor(string sha256Hex)
    {
        var bytes = Convert.FromHexString(sha256Hex.AsSpan(0, 32));
        var id = new Guid(bytes);
        return id == Guid.Empty ? new Guid([.. bytes[..15], 1]) : id;
    }

    /// <summary>An image to package: its managed file, its package id, and what its bytes really are.</summary>
    private sealed record PlannedAsset(Guid AssetId, string SourcePath, ImageInspection Inspection, string Sha256)
    {
        internal string EntryPath => PackagePaths.AssetPath(AssetId, Inspection.Extension);
    }

    /// <summary>
    /// The images to package, ordered by id. Ids from fields known to hold images must resolve;
    /// other GUIDs (from data this build doesn't understand) are included only if they are managed
    /// images — the same conservative rule asset cleanup uses.
    /// </summary>
    private static List<PlannedAsset> PlanAssets(
        ProfileDocument document, AssetStorageService assets, Func<string, bool>? isDecoderSupported, out Dictionary<Guid, Guid> localToPackage, out List<PackageError> errors)
    {
        errors = new List<PackageError>();
        localToPackage = new Dictionary<Guid, Guid>();
        var known = new HashSet<Guid>();
        foreach (var element in document.Elements)
        {
            if (element is ImageProfileElement { AssetId: var id } && id != Guid.Empty)
            {
                known.Add(id);
            }
        }

        if (document.Background?.ImageAssetId is { } backgroundId && backgroundId != Guid.Empty)
        {
            known.Add(backgroundId);
        }

        var candidates = new HashSet<Guid>(known);
        AssetReferenceScanner.Collect(document, candidates);

        var planned = new Dictionary<Guid, PlannedAsset>();
        foreach (var id in candidates.OrderBy(id => id.ToString("N"), StringComparer.Ordinal))
        {
            var path = assets.ResolveAssetPath(id);
            if (path is null)
            {
                if (known.Contains(id))
                {
                    errors.Add(new PackageError(PackageErrorCode.AssetMissing,
                        "An image this Plate uses is missing from AetherFrame's image storage, so the Plate can't be exported. Replace or remove that image and save, then try again.",
                        $"asset {id:N} missing"));
                }

                continue;
            }

            if (PackageReader.CheckImage(path, null, 0, 0, isDecoderSupported, out var problem) is not { } inspection)
            {
                errors.Add(new PackageError(PackageErrorCode.ImageInvalid, $"An image in this Plate can't be exported: {problem}", $"asset {id:N}"));
                continue;
            }

            // Identical images (even under different local ids) are packaged once.
            var sha256 = AssetMetadataStore.ComputeSha256(path);
            var packageId = PackageAssetIdFor(sha256);
            localToPackage[id] = packageId;
            planned.TryAdd(packageId, new PlannedAsset(packageId, path, inspection, sha256));
        }

        if (planned.Count > PackagePolicy.MaxAssetCount)
        {
            errors.Add(new PackageError(PackageErrorCode.PackageTooLarge, $"This Plate has too many images to export (the limit is {PackagePolicy.MaxAssetCount}).",
                $"{planned.Count} assets"));
        }

        var total = planned.Values.Sum(a => a.Inspection.ByteLength);
        if (total > PackagePolicy.MaxTotalUncompressedBytes - PackagePolicy.MaxProfileBytes - PackagePolicy.MaxManifestBytes - PackagePolicy.MaxPreviewBytes)
        {
            errors.Add(new PackageError(PackageErrorCode.PackageTooLarge, "This Plate's images are too large to export together.", $"{total} bytes"));
        }

        return planned.Values.OrderBy(a => a.AssetId.ToString("N"), StringComparer.Ordinal).ToList();
    }

    private static void WritePackage(
        string path, byte[] profileBytes, string plateName, int schemaVersion, List<PlannedAsset> assets, string? previewPath, string generator)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Hashes come from the exact bytes written, so the manifest (which needs them) goes
            // last in the file; readers find entries through the archive's directory, not order.
            WriteBytes(archive, PackagePaths.ProfilePath, profileBytes, CompressionLevel.Optimal);
            var profile = new PackageFileDeclaration(PackagePaths.ProfilePath, profileBytes.Length, Convert.ToHexStringLower(SHA256.HashData(profileBytes)));

            var declarations = new List<PackageAssetDeclaration>();
            foreach (var asset in assets)
            {
                var (length, sha256) = WriteFile(archive, asset.EntryPath, asset.SourcePath, PackagePolicy.MaxImageBytes);
                if (sha256 != asset.Sha256)
                {
                    throw new InvalidDataException("An image changed while it was being exported.");
                }

                declarations.Add(new PackageAssetDeclaration(asset.AssetId, asset.EntryPath, asset.Inspection.MediaType, length, sha256, asset.Inspection.Width, asset.Inspection.Height));
            }

            PackageFileDeclaration? preview = null;
            if (previewPath is not null)
            {
                var (length, sha256) = WriteFile(archive, PackagePaths.PreviewPath, previewPath, PackagePolicy.MaxPreviewBytes);
                var inspection = ImageSafety.Inspect(previewPath)!;
                preview = new PackageFileDeclaration(PackagePaths.PreviewPath, length, sha256, inspection.MediaType, inspection.Width, inspection.Height);
            }

            var manifest = new PackageManifest
            {
                Generator = generator.Length > 64 ? generator[..64] : generator,
                PlateName = plateName,
                PlateSchemaVersion = schemaVersion,
                Profile = profile,
                Assets = declarations,
                Preview = preview,
                PreviewDeclared = preview is not null,
            };

            WriteBytes(archive, PackagePaths.ManifestPath, manifest.ToJsonBytes(), CompressionLevel.Optimal);
        }

        file.Flush(flushToDisk: true);
    }

    private static void WriteBytes(ZipArchive archive, string name, byte[] bytes, CompressionLevel level)
    {
        var entry = archive.CreateEntry(name, level);
        entry.LastWriteTime = FixedEntryTime;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Copies a managed file into the archive (images are already compressed: stored as-is), hashing what's written.</summary>
    private static (long Length, string Sha256) WriteFile(ZipArchive archive, string name, string sourcePath, long limit)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = FixedEntryTime;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var destination = entry.Open();

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("An image grew past the size limit while it was being exported.");
            }

            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
