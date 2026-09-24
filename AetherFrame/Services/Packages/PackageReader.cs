using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Packages;

/// <summary>
/// Opens an .aetherframe file and validates ALL of it before anything is imported or shown —
/// every package is treated as hostile. Nothing is ever extracted to a path the archive names:
/// JSON is read into bounded memory, and images are streamed into this import's own staging
/// folder under names chosen here ("asset-00.png"...). Validation failures are reported as data
/// in <see cref="StagedPackage.Diagnostics"/>; this never throws for bad content.
///
/// <para>Order, cheapest and most fundamental first: file size → ZIP end record (entry count,
/// directory size) →
/// every entry's name, attributes, and declared size → manifest (format version, capabilities,
/// declarations) → version 1 layout (every entry expected, every declaration present) →
/// profile.json (size, hash, JSON, schema, content) → each image (size, hash, real format, header
/// limits, structure) → preview.png (only ever a warning).</para>
///
/// <para><b>Links and special entries.</b> Entries are never materialized from their own
/// metadata, so a symbolic link in an archive has nothing to act on. It is still refused: an
/// entry whose Unix mode (the high 16 bits of the external attributes) marks anything but a
/// regular file or directory — link, device, pipe, socket — or whose DOS attributes mark a
/// reparse point, device, or a folder where a file is expected, makes the package Invalid.</para>
/// </summary>
internal static class PackageReader
{
    private const int CopyBufferSize = 81920;

    // Unix st_mode file-type bits, as ZIP tools store them in the external attributes' high word.
    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;

    // DOS/Windows attribute bits in the low word.
    private const int DosDirectory = 0x10;
    private const int DosDevice = 0x40;
    private const int DosReparsePoint = 0x400;

    /// <summary>
    /// Validates <paramref name="packagePath"/> into a new folder under <paramref name="stagingRoot"/>.
    /// Always returns a result; the caller disposes it.
    /// </summary>
    /// <param name="isDecoderSupported">Whether the game can decode an extension such as ".webp"; null accepts every managed format.</param>
    internal static StagedPackage Open(string packagePath, string stagingRoot, Func<string, bool>? isDecoderSupported = null, IAetherFrameLog? log = null)
    {
        log ??= NullAetherFrameLog.Instance;
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var result = new StagedPackage(Path.GetFileName(packagePath), staging, log);

        try
        {
            Directory.CreateDirectory(staging);
            new Session(result, packagePath, isDecoderSupported).Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            result.Diagnostics.Error(PackageErrorCode.InvalidArchive, "The file couldn't be read. It may be damaged or in use.", ex.GetType().Name);
        }

        if (result.Compatibility is PackageCompatibility.Invalid or PackageCompatibility.Unsupported)
        {
            // Nothing staged is ever used from a refused package.
            result.PreparedProfile = null;
            result.PreviewDocument = null;
            result.Assets = [];
            result.PreviewImagePath = null;
            log.Warning("AetherFrame refused to import " + result.DescribeForLog());
        }
        else
        {
            log.Information("AetherFrame validated " + result.DescribeForLog());
        }

        return result;
    }

    /// <summary>One validation run; holds the open archive and the running byte count.</summary>
    private sealed class Session
    {
        private readonly StagedPackage result;
        private readonly string packagePath;
        private readonly Func<string, bool>? isDecoderSupported;
        private readonly PackageDiagnostics diagnostics;
        private long totalBytesRead;

        internal Session(StagedPackage result, string packagePath, Func<string, bool>? isDecoderSupported)
        {
            this.result = result;
            this.packagePath = packagePath;
            this.isDecoderSupported = isDecoderSupported;
            diagnostics = result.Diagnostics;
        }

        internal void Run()
        {
            var file = new FileInfo(packagePath);
            if (!file.Exists)
            {
                diagnostics.Error(PackageErrorCode.InvalidArchive, "The file couldn't be found.");
                return;
            }

            if (file.Length > PackagePolicy.MaxPackageBytes)
            {
                diagnostics.Error(PackageErrorCode.PackageTooLarge,
                    $"The file is too large ({PackageText.FormatBytes(file.Length)}). The limit is {PackageText.FormatBytes(PackagePolicy.MaxPackageBytes)}.");
                return;
            }

            using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize);

            if (ZipPreflight.Read(stream, out var preflightError) is not { } declared)
            {
                diagnostics.Error(PackageErrorCode.InvalidArchive, "This isn't a valid AetherFrame Plate file.", preflightError);
                return;
            }

            if (declared.EntryCount > PackagePolicy.MaxEntryCount || declared.DirectoryBytes > PackagePolicy.MaxCentralDirectoryBytes)
            {
                diagnostics.Error(PackageErrorCode.PackageTooLarge, "The file contains too many items to be a Plate.",
                    $"{declared.EntryCount} entries, {declared.DirectoryBytes}-byte directory");
                return;
            }

            stream.Position = 0;
            ZipArchive? archive = null;
            IReadOnlyList<ZipArchiveEntry> entries;
            try
            {
                archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                entries = archive.Entries;
            }
            catch (InvalidDataException ex)
            {
                archive?.Dispose();
                diagnostics.Error(PackageErrorCode.InvalidArchive, "This isn't a valid AetherFrame Plate file.", ex.GetType().Name);
                return;
            }

            using (archive)
            {
                Validate(entries);
            }
        }

        private void Validate(IReadOnlyList<ZipArchiveEntry> entries)
        {
            if (entries.Count > PackagePolicy.MaxEntryCount)
            {
                diagnostics.Error(PackageErrorCode.PackageTooLarge, "The file contains too many items to be a Plate.", $"{entries.Count} entries");
                return;
            }

            // ---- 1. every entry, whatever the package version: safe name, unique, plain, bounded.
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            long declaredTotal = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                // FullName is only ever compared, never used as a path.
                var name = entry.FullName;
                if (PackagePaths.CheckSafety(name) is { } unsafeReason)
                {
                    diagnostics.Error(PackageErrorCode.UnsafePath, "The file contains an item with an unsafe name.", $"entry {i}: {unsafeReason}");
                    continue;
                }

                if (!keys.Add(PackagePaths.CollisionKey(name)))
                {
                    diagnostics.Error(PackageErrorCode.DuplicateEntry, "The file contains the same item more than once.", $"entry {i}: {PackageManifest.SafeForLog(name)}");
                    continue;
                }

                if (CheckAttributes(entry, name.EndsWith('/')) is { } attributeProblem)
                {
                    diagnostics.Error(PackageErrorCode.UnsafePath, "The file contains a link or special item, which isn't allowed.", $"entry {i}: {attributeProblem}");
                    continue;
                }

                if (entry.IsEncrypted)
                {
                    diagnostics.Error(PackageErrorCode.InvalidArchive, "The file contains encrypted items, which aren't supported.", $"entry {i}");
                    continue;
                }

                if (entry.Length > PackagePolicy.MaxEntryBytes || entry.Length < 0)
                {
                    diagnostics.Error(PackageErrorCode.PackageTooLarge, "An item in the file is too large.", $"entry {i}: {entry.Length} bytes");
                    continue;
                }

                declaredTotal += entry.Length;
                byName[name] = entry;
            }

            if (declaredTotal > PackagePolicy.MaxTotalUncompressedBytes)
            {
                diagnostics.Error(PackageErrorCode.PackageTooLarge, "The file's contents are too large.", $"{declaredTotal} bytes declared");
            }

            if (diagnostics.HasErrors)
            {
                return;
            }

            // ---- 2. the manifest decides whether anything else is readable.
            if (!byName.TryGetValue(PackagePaths.ManifestPath, out var manifestEntry))
            {
                diagnostics.Error(PackageErrorCode.ManifestInvalid, "This isn't an AetherFrame Plate file.", "no manifest.json");
                return;
            }

            if (ReadBounded(manifestEntry, PackagePolicy.MaxManifestBytes, "manifest") is not { } manifestBytes)
            {
                return;
            }

            var manifest = PackageManifest.Parse(manifestBytes, diagnostics);
            if (manifest is null)
            {
                return;
            }

            result.Manifest = manifest;

            // ---- 3. version 1 layout: only expected entries, and exactly what the manifest declares.
            var assetEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            ZipArchiveEntry? profileEntry = null;
            ZipArchiveEntry? previewEntry = null;
            foreach (var (name, entry) in byName)
            {
                if (!PackagePaths.ClassifyVersion1(name, out var classified))
                {
                    diagnostics.Error(PackageErrorCode.UnexpectedEntry, "The file contains something that doesn't belong in a Plate.", $"unexpected entry {PackageManifest.SafeForLog(name)}");
                    continue;
                }

                switch (classified.Kind)
                {
                    case PackageEntryKind.Profile:
                        profileEntry = entry;
                        break;
                    case PackageEntryKind.Preview:
                        previewEntry = entry;
                        break;
                    case PackageEntryKind.Asset:
                        assetEntries[name] = entry;
                        break;
                    case PackageEntryKind.AssetsFolder when entry.Length != 0:
                        diagnostics.Error(PackageErrorCode.UnexpectedEntry, "The file contains something that doesn't belong in a Plate.", "assets/ folder entry has content");
                        break;
                }
            }

            if (profileEntry is null)
            {
                diagnostics.Error(PackageErrorCode.ProfileInvalid, "The Plate is missing from this file.", "no profile.json");
            }

            var declaredPaths = manifest.Assets.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var asset in manifest.Assets)
            {
                if (!assetEntries.ContainsKey(asset.Path))
                {
                    diagnostics.Error(PackageErrorCode.AssetMissing, "An image the Plate needs is missing from the file.", $"missing {asset.Path}");
                }
            }

            foreach (var path in assetEntries.Keys)
            {
                if (!declaredPaths.Contains(path))
                {
                    diagnostics.Error(PackageErrorCode.AssetUndeclared, "The file contains an image it doesn't list.", $"undeclared {PackageManifest.SafeForLog(path)}");
                }
            }

            if (previewEntry is not null && !manifest.PreviewDeclared)
            {
                diagnostics.Error(PackageErrorCode.AssetUndeclared, "The file contains a picture it doesn't list.", "undeclared preview.png");
            }

            if (diagnostics.HasErrors)
            {
                return;
            }

            // ---- 4. profile.json.
            if (ReadDeclared(profileEntry!, manifest.Profile, PackagePolicy.MaxProfileBytes, "profile") is not { } profileBytes)
            {
                return;
            }

            if (!PackageJson.TryParseObject(profileBytes, out var profileRaw, out var profileError))
            {
                diagnostics.Error(PackageErrorCode.ProfileInvalid, "The Plate in this file is damaged.", profileError);
                return;
            }

            var validated = PackageProfileValidator.Validate(profileRaw!, manifest, diagnostics);
            if (validated is null)
            {
                return;
            }

            // Every image the Plate uses must be in the package...
            var declaredIds = manifest.Assets.Select(a => a.AssetId).ToHashSet();
            foreach (var reference in validated.AssetReferences.OrderBy(id => id))
            {
                if (!declaredIds.Contains(reference))
                {
                    diagnostics.Error(PackageErrorCode.AssetMissing, "An image the Plate needs is missing from the file.", $"unpackaged reference {reference:N}");
                }
            }

            // ...and every image in the package must be used by the Plate — anywhere in it, including
            // data from newer builds this one can't interpret — so nothing rides along unused.
            var mentioned = PackageAssetIds.CollectGuidStrings(validated.Raw);
            foreach (var asset in manifest.Assets)
            {
                if (!mentioned.Contains(asset.AssetId))
                {
                    diagnostics.Error(PackageErrorCode.AssetUndeclared, "The file contains an image the Plate doesn't use.", $"unused {asset.Path}");
                }
            }

            if (diagnostics.HasErrors)
            {
                return;
            }

            // ---- 5. images, streamed into staging and checked from their own bytes.
            var staged = new List<StagedAsset>();
            var index = 0;
            foreach (var asset in manifest.Assets.OrderBy(a => a.AssetId.ToString("N"), StringComparer.Ordinal))
            {
                var stagedPath = Path.Combine(result.StagingDirectory, $"asset-{index++:D2}{asset.Extension}");
                if (StageAsset(assetEntries[asset.Path], asset, stagedPath) is not { } inspection)
                {
                    return;
                }

                staged.Add(new StagedAsset(asset, Guid.NewGuid(), stagedPath, inspection));
            }

            // ---- 6. preview.png: decoration only, so any problem just leaves it out.
            string? previewPath = null;
            if (manifest.PreviewDeclared)
            {
                previewPath = StagePreview(previewEntry, manifest.Preview);
                if (previewPath is null)
                {
                    diagnostics.Warning(PackageWarningCode.PreviewOmitted, "The file's preview picture couldn't be used, so it's left out. The Plate itself is fine.");
                }
            }

            // ---- 7. what would be imported: the validated JSON with every image re-pointed at a
            // brand-new local id (the package's ids are never used locally).
            var map = staged.ToDictionary(a => a.Declaration.AssetId, a => a.LocalAssetId);
            var prepared = (JsonObject)validated.Raw.DeepClone();
            PackageAssetIds.Remap(prepared, map);

            ProfileDocument previewDocument;
            try
            {
                previewDocument = PlateDocuments.Materialize((JsonObject)prepared.DeepClone());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
            {
                diagnostics.Error(PackageErrorCode.ProfileInvalid, "The Plate in this file is damaged.", "prepared document unreadable: " + ex.GetType().Name);
                return;
            }

            result.PreparedProfile = prepared;
            result.PreviewDocument = previewDocument;
            result.Assets = staged;
            result.PreviewImagePath = previewPath;
            result.Summary = new PackageSummary(
                manifest.FormatVersion,
                manifest.PlateName,
                manifest.PlateSchemaVersion,
                validated.Document.CanvasWidth,
                validated.Document.CanvasHeight,
                validated.Document.Elements.Count + validated.Document.UnsupportedElementCount,
                validated.Document.UnsupportedElementCount,
                staged.Count,
                staged.Sum(a => a.Declaration.ByteLength),
                previewPath is not null);
        }

        /// <summary>Null when the entry's attributes are a plain file (or, for a folder entry, a plain folder).</summary>
        private static string? CheckAttributes(ZipArchiveEntry entry, bool isFolderEntry)
        {
            var attributes = entry.ExternalAttributes;
            var unixMode = (attributes >> 16) & 0xFFFF;
            var unixType = unixMode & UnixTypeMask;
            if (unixType != 0 && unixType != (isFolderEntry ? UnixDirectory : UnixRegularFile))
            {
                return unixType == 0xA000 ? "symbolic link" : $"special Unix file type 0x{unixType:X}";
            }

            var dos = attributes & 0xFFFF;
            if ((dos & DosReparsePoint) != 0)
            {
                return "reparse point";
            }

            if ((dos & DosDevice) != 0)
            {
                return "device";
            }

            if (!isFolderEntry && (dos & DosDirectory) != 0)
            {
                return "folder attributes on a file";
            }

            return null;
        }

        /// <summary>Reads a small entry into memory, never more than <paramref name="limit"/> bytes.</summary>
        private byte[]? ReadBounded(ZipArchiveEntry entry, int limit, string what)
        {
            using var buffer = new MemoryStream();
            if (!CopyBounded(entry, buffer, limit, null, what))
            {
                return null;
            }

            return buffer.ToArray();
        }

        /// <summary>Reads a declared entry into memory and checks its size and hash against the declaration.</summary>
        private byte[]? ReadDeclared(ZipArchiveEntry entry, PackageFileDeclaration declaration, int limit, string what)
        {
            using var buffer = new MemoryStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (!CopyBounded(entry, buffer, Math.Min(limit, declaration.ByteLength), hash, what))
            {
                return null;
            }

            if (buffer.Length != declaration.ByteLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != declaration.Sha256)
            {
                diagnostics.Error(PackageErrorCode.HashMismatch, "The file's contents don't match its description. It may be damaged or altered.", $"{what} size or hash mismatch");
                return null;
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// Streams an entry into <paramref name="destination"/>, refusing it the moment it produces
        /// more than <paramref name="limit"/> bytes — the real limit on decompression, whatever the
        /// archive claims — or pushes the package past its total.
        /// </summary>
        private bool CopyBounded(ZipArchiveEntry entry, Stream destination, long limit, IncrementalHash? hash, string what, PackageDiagnostics? sink = null)
        {
            sink ??= diagnostics;
            try
            {
                using var source = entry.Open();
                var buffer = new byte[CopyBufferSize];
                long written = 0;
                int read;
                while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - written + 1))) > 0)
                {
                    written += read;
                    totalBytesRead += read;
                    if (written > limit)
                    {
                        sink.Error(PackageErrorCode.PackageTooLarge, "An item in the file is larger than it's allowed to be.", $"{what} exceeds {limit} bytes");
                        return false;
                    }

                    if (totalBytesRead > PackagePolicy.MaxTotalUncompressedBytes)
                    {
                        sink.Error(PackageErrorCode.PackageTooLarge, "The file's contents are too large.", "total uncompressed size exceeded");
                        return false;
                    }

                    hash?.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                }

                return true;
            }
            catch (InvalidDataException ex)
            {
                sink.Error(PackageErrorCode.InvalidArchive, "The file is damaged.", $"{what}: {ex.GetType().Name}");
                return false;
            }
            catch (NotSupportedException ex)
            {
                sink.Error(PackageErrorCode.InvalidArchive, "The file uses a compression method AetherFrame can't read.", $"{what}: {ex.GetType().Name}");
                return false;
            }
        }

        /// <summary>Streams one declared image into staging and validates it from its own bytes.</summary>
        private ImageInspection? StageAsset(ZipArchiveEntry entry, PackageAssetDeclaration declaration, string stagedPath)
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long length;
                using (var file = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
                {
                    if (!CopyBounded(entry, file, Math.Min(PackagePolicy.MaxImageBytes, declaration.ByteLength), hash, "asset"))
                    {
                        return null;
                    }

                    length = file.Length;
                }

                if (length != declaration.ByteLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != declaration.Sha256)
                {
                    diagnostics.Error(PackageErrorCode.HashMismatch, "An image in the file doesn't match its description. It may be damaged or altered.", $"{declaration.Path} size or hash mismatch");
                    return null;
                }
            }

            return CheckImage(stagedPath, declaration.MediaType, declaration.Width, declaration.Height, isDecoderSupported, out var problem) is { } inspection
                ? inspection
                : Fail(problem!);

            ImageInspection? Fail(string problem)
            {
                diagnostics.Error(PackageErrorCode.ImageInvalid, problem, declaration.Path);
                return null;
            }
        }

        private string? StagePreview(ZipArchiveEntry? entry, PackageFileDeclaration? declaration)
        {
            if (entry is null || declaration is null)
            {
                return null;
            }

            var stagedPath = Path.Combine(result.StagingDirectory, "preview.png");

            // Problems here only ever leave the preview out, so they go to a scratch sink rather
            // than the package's verdict. The bytes still count toward the package's total.
            var scratch = new PackageDiagnostics();
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var file = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
                {
                    if (!CopyBounded(entry, file, Math.Min(PackagePolicy.MaxPreviewBytes, declaration.ByteLength), hash, "preview", scratch))
                    {
                        return Discard(stagedPath);
                    }
                }

                if (new FileInfo(stagedPath).Length != declaration.ByteLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != declaration.Sha256)
                {
                    return Discard(stagedPath);
                }

                var inspection = CheckImage(stagedPath, declaration.MediaType, declaration.Width, declaration.Height, isDecoderSupported, out _);
                return inspection is { Format: DetectedImageFormat.Png } && inspection.Width <= PackagePolicy.MaxPreviewDimension && inspection.Height <= PackagePolicy.MaxPreviewDimension
                    ? stagedPath
                    : Discard(stagedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Discard(stagedPath);
            }
        }

        private static string? Discard(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the staging folder's own cleanup.
            }

            return null;
        }
    }

    /// <summary>
    /// The full image check, shared by import and export: the existing header inspection and limits
    /// (<see cref="ImageSafety"/>), the stricter structure check for untrusted files, and agreement
    /// with what the manifest declared. Null (with a player-facing <paramref name="problem"/>) when
    /// the image can't be used.
    /// </summary>
    internal static ImageInspection? CheckImage(string path, string? declaredMediaType, int declaredWidth, int declaredHeight, Func<string, bool>? isDecoderSupported, out string? problem)
    {
        var inspection = ImageSafety.Inspect(path);
        problem = ImageSafety.Validate(inspection, isDecoderSupported);
        if (problem is not null)
        {
            return null;
        }

        if (declaredMediaType is not null && PackageManifest.FormatOfMediaType(declaredMediaType) != inspection!.Format)
        {
            problem = "An image in the file isn't the kind of image it claims to be.";
            return null;
        }

        if (declaredWidth > 0 && (inspection!.Width != declaredWidth || inspection.Height != declaredHeight))
        {
            problem = "An image in the file isn't the size it claims to be.";
            return null;
        }

        problem = ImageSafety.CheckStructure(path, inspection!.Format);
        return problem is null ? inspection : null;
    }
}
