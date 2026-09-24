using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Plates;
using AetherFrame.Services.Assets;

namespace AetherFrame.Services.Packages;

/// <summary>One image the package declares. Only declared images may be in a package.</summary>
internal sealed record PackageAssetDeclaration(Guid AssetId, string Path, string MediaType, long ByteLength, string Sha256, int Width, int Height)
{
    internal string Extension => System.IO.Path.GetExtension(Path);
}

/// <summary>A declared file with its integrity data (profile.json, preview.png).</summary>
internal sealed record PackageFileDeclaration(string Path, long ByteLength, string Sha256, string? MediaType = null, int Width = 0, int Height = 0);

/// <summary>
/// manifest.json: what the package is and everything it contains, readable (and fully checked)
/// before profile.json is even opened. Version 1 fields, in the order they're written:
///
/// <code>
/// format          "aetherframe.package" — what kind of file this is
/// formatVersion   1 — the package container version (NOT the Plate's schema version)
/// requires        ["plate"] — capabilities a reader must understand to use the package
/// generator       "AetherFrame x.y.z.w" — informational only
/// plate           { name, schemaVersion } — the Plate's name and its document schema version
/// profile         { path, byteLength, sha256 } — profile.json's integrity data
/// assets          [ { id, path, mediaType, byteLength, sha256, width, height } ], ordered by id
/// preview         { path, mediaType, byteLength, sha256, width, height } — optional
/// </code>
///
/// <para><b>Deliberately absent:</b> character Content IDs or names, the source Plate's id, local
/// file paths, export time, or anything else about the installation it came from; URLs of any
/// kind; and anything executable. A package is data, never instructions.</para>
///
/// <para><b>Forward compatibility.</b> A newer <c>formatVersion</c> means the container itself
/// changed: Unsupported. An unknown entry in <c>requires</c> means the package needs a feature this
/// build doesn't have (a future Template or Theme Kit, say): also Unsupported. Any other field this
/// build doesn't know is harmless optional metadata and is ignored — never an error, never
/// acted on.</para>
/// </summary>
internal sealed class PackageManifest
{
    internal const string FormatKey = "format";
    internal const string FormatVersionKey = "formatVersion";
    internal const string RequiresKey = "requires";
    internal const string GeneratorKey = "generator";
    internal const string PlateKey = "plate";
    internal const string ProfileKey = "profile";
    internal const string AssetsKey = "assets";
    internal const string PreviewKey = "preview";

    private const int MaxGeneratorLength = 64;

    /// <summary>The capabilities this build understands.</summary>
    internal static readonly IReadOnlySet<string> KnownCapabilities = new HashSet<string>(StringComparer.Ordinal) { PackagePolicy.PlateCapability };

    internal int FormatVersion { get; init; } = PackagePolicy.CurrentFormatVersion;

    internal IReadOnlyList<string> Requires { get; init; } = [PackagePolicy.PlateCapability];

    internal string? Generator { get; init; }

    internal string PlateName { get; init; } = string.Empty;

    internal int PlateSchemaVersion { get; init; }

    internal PackageFileDeclaration Profile { get; init; } = new(PackagePaths.ProfilePath, 0, string.Empty);

    internal IReadOnlyList<PackageAssetDeclaration> Assets { get; init; } = [];

    /// <summary>Null when the package has no preview, or its declaration was unusable (see <see cref="PreviewDeclared"/>).</summary>
    internal PackageFileDeclaration? Preview { get; init; }

    /// <summary>True when the manifest mentions a preview at all, usable or not.</summary>
    internal bool PreviewDeclared { get; init; }

    // ---------------------------------------------------------------- writing

    /// <summary>The manifest as UTF-8 JSON, byte-for-byte deterministic for the same content.</summary>
    internal byte[] ToJsonBytes()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(FormatKey, PackagePolicy.FormatIdentifier);
            writer.WriteNumber(FormatVersionKey, FormatVersion);

            writer.WriteStartArray(RequiresKey);
            foreach (var capability in Requires)
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();

            if (Generator is not null)
            {
                writer.WriteString(GeneratorKey, Generator);
            }

            writer.WriteStartObject(PlateKey);
            writer.WriteString("name", PlateName);
            writer.WriteNumber("schemaVersion", PlateSchemaVersion);
            writer.WriteEndObject();

            writer.WriteStartObject(ProfileKey);
            WriteFileFields(writer, Profile, includeImageFields: false);
            writer.WriteEndObject();

            writer.WriteStartArray(AssetsKey);
            foreach (var asset in Assets.OrderBy(a => a.AssetId.ToString("N"), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", asset.AssetId.ToString("N"));
                writer.WriteString("path", asset.Path);
                writer.WriteString("mediaType", asset.MediaType);
                writer.WriteNumber("byteLength", asset.ByteLength);
                writer.WriteString("sha256", asset.Sha256);
                writer.WriteNumber("width", asset.Width);
                writer.WriteNumber("height", asset.Height);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (Preview is { } preview)
            {
                writer.WriteStartObject(PreviewKey);
                WriteFileFields(writer, preview, includeImageFields: true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteFileFields(Utf8JsonWriter writer, PackageFileDeclaration file, bool includeImageFields)
    {
        writer.WriteString("path", file.Path);
        if (includeImageFields)
        {
            writer.WriteString("mediaType", file.MediaType);
        }

        writer.WriteNumber("byteLength", file.ByteLength);
        writer.WriteString("sha256", file.Sha256);
        if (includeImageFields)
        {
            writer.WriteNumber("width", file.Width);
            writer.WriteNumber("height", file.Height);
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Parses manifest.json (already size-checked) into a manifest, reporting every problem to
    /// <paramref name="diagnostics"/>. Null when the package can't be used: invalid, or
    /// Unsupported (a newer format or an unknown required capability — in which case nothing past
    /// the header is interpreted, since a newer package may lay out everything else differently).
    /// </summary>
    internal static PackageManifest? Parse(ReadOnlySpan<byte> utf8Json, PackageDiagnostics diagnostics)
    {
        if (PackageJson.TryParseObject(utf8Json, out var root, out var parseError) is false)
        {
            diagnostics.Error(PackageErrorCode.ManifestInvalid, "The package's description is damaged.", parseError);
            return null;
        }

        return Parse(root!, diagnostics);
    }

    internal static PackageManifest? Parse(JsonObject root, PackageDiagnostics diagnostics)
    {
        void Invalid(string detail) => diagnostics.Error(PackageErrorCode.ManifestInvalid, "The package's description is damaged.", detail);

        // ---- header: identifies the file and decides whether the rest is readable at all.
        if (ReadString(root, FormatKey) != PackagePolicy.FormatIdentifier)
        {
            diagnostics.Error(PackageErrorCode.ManifestInvalid, "This isn't an AetherFrame Plate file.", "format identifier missing or wrong");
            return null;
        }

        if (ReadInt(root, FormatVersionKey) is not { } formatVersion || formatVersion < 1)
        {
            Invalid("formatVersion missing or not a positive integer");
            return null;
        }

        if (formatVersion > PackagePolicy.CurrentFormatVersion)
        {
            diagnostics.Error(PackageErrorCode.UnsupportedVersion, "This file was made with a newer version of AetherFrame. Update AetherFrame to import it.",
                $"package format {formatVersion}, supported {PackagePolicy.CurrentFormatVersion}");
            return null;
        }

        if (root[RequiresKey] is not JsonArray requiresArray || requiresArray.Count > PackagePolicy.MaxCapabilityCount)
        {
            Invalid("requires missing, not an array, or too long");
            return null;
        }

        var requires = new List<string>();
        foreach (var node in requiresArray)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var capability) || capability.Length is 0 or > PackagePolicy.MaxIdentifierLength)
            {
                Invalid("requires holds a non-string or oversized value");
                return null;
            }

            requires.Add(capability);
        }

        var unknown = requires.Where(c => !KnownCapabilities.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            diagnostics.Error(PackageErrorCode.UnsupportedCapability, "This file needs features from a newer version of AetherFrame. Update AetherFrame to import it.",
                "unknown required capabilities: " + string.Join(", ", unknown.Select(SafeForLog)));
            return null;
        }

        if (!requires.Contains(PackagePolicy.PlateCapability, StringComparer.Ordinal))
        {
            Invalid("a version 1 package must require \"plate\"");
            return null;
        }

        // ---- body (version 1).
        var generator = root[GeneratorKey] is null ? null : ReadString(root, GeneratorKey);
        if (root[GeneratorKey] is not null && (generator is null || generator.Length > MaxGeneratorLength))
        {
            Invalid("generator is not a short string");
            return null;
        }

        if (root[PlateKey] is not JsonObject plate)
        {
            Invalid("plate section missing");
            return null;
        }

        if (!PlateNaming.TryNormalizeName(ReadString(plate, "name"), out var plateName, out _) || plateName != ReadString(plate, "name"))
        {
            Invalid("plate name missing, blank, too long, or untrimmed");
            return null;
        }

        if (ReadInt(plate, "schemaVersion") is not { } schemaVersion || schemaVersion < 0)
        {
            Invalid("plate schemaVersion missing or negative");
            return null;
        }

        if (root[ProfileKey] is not JsonObject profileNode
            || ReadFile(profileNode, PackagePaths.ProfilePath, PackagePolicy.MaxProfileBytes, image: false) is not { } profile)
        {
            Invalid("profile declaration missing or malformed");
            return null;
        }

        if (root[AssetsKey] is not JsonArray assetArray)
        {
            Invalid("assets missing or not an array");
            return null;
        }

        if (assetArray.Count > PackagePolicy.MaxAssetCount)
        {
            diagnostics.Error(PackageErrorCode.PackageTooLarge, $"The Plate has too many images (the limit is {PackagePolicy.MaxAssetCount}).",
                $"{assetArray.Count} assets declared");
            return null;
        }

        var assets = new List<PackageAssetDeclaration>();
        var seen = new HashSet<Guid>();
        foreach (var node in assetArray)
        {
            if (node is not JsonObject assetNode || ReadAsset(assetNode) is not { } asset)
            {
                Invalid("an asset declaration is malformed");
                return null;
            }

            if (!seen.Add(asset.AssetId))
            {
                diagnostics.Error(PackageErrorCode.DuplicateEntry, "The package lists the same image twice.", "duplicate asset declaration");
                return null;
            }

            assets.Add(asset);
        }

        // The preview is decoration: a bad declaration only loses the picture (see PackageReader).
        var previewDeclared = root[PreviewKey] is not null;
        var preview = root[PreviewKey] is JsonObject previewNode
            ? ReadFile(previewNode, PackagePaths.PreviewPath, PackagePolicy.MaxPreviewBytes, image: true)
            : null;
        if (preview is not null && (preview.MediaType != ImageSafety.MediaTypeOf(DetectedImageFormat.Png)
            || preview.Width > PackagePolicy.MaxPreviewDimension || preview.Height > PackagePolicy.MaxPreviewDimension))
        {
            preview = null;
        }

        return new PackageManifest
        {
            FormatVersion = formatVersion,
            Requires = requires,
            Generator = generator,
            PlateName = plateName,
            PlateSchemaVersion = schemaVersion,
            Profile = profile,
            Assets = assets,
            Preview = preview,
            PreviewDeclared = previewDeclared,
        };
    }

    private static PackageAssetDeclaration? ReadAsset(JsonObject node)
    {
        var idText = ReadString(node, "id");
        if (idText is not { Length: 32 } || !Guid.TryParseExact(idText, "N", out var assetId) || assetId == Guid.Empty || idText != assetId.ToString("N"))
        {
            return null;
        }

        var file = ReadFile(node, expectedPath: null, PackagePolicy.MaxImageBytes, image: true);
        if (file is null || FormatOfMediaType(file.MediaType) is not { } format)
        {
            return null;
        }

        // The path is fully determined by the id and the declared format: no other name is valid.
        if (file.Path != PackagePaths.AssetPath(assetId, ImageSafety.ExtensionOf(format)))
        {
            return null;
        }

        if ((long)file.Width * file.Height > PackagePolicy.MaxImagePixelCount)
        {
            return null;
        }

        return new PackageAssetDeclaration(assetId, file.Path, file.MediaType!, file.ByteLength, file.Sha256, file.Width, file.Height);
    }

    private static PackageFileDeclaration? ReadFile(JsonObject node, string? expectedPath, long maxBytes, bool image)
    {
        var path = ReadString(node, "path");
        if (path is null || (expectedPath is not null && path != expectedPath))
        {
            return null;
        }

        if (ReadLong(node, "byteLength") is not { } byteLength || byteLength < 1 || byteLength > maxBytes)
        {
            return null;
        }

        var sha256 = ReadString(node, "sha256");
        if (!PackagePaths.IsSha256Hex(sha256))
        {
            return null;
        }

        if (!image)
        {
            return new PackageFileDeclaration(path, byteLength, sha256!);
        }

        var mediaType = ReadString(node, "mediaType");
        if (mediaType is null || FormatOfMediaType(mediaType) is null)
        {
            return null;
        }

        if (ReadInt(node, "width") is not { } width || ReadInt(node, "height") is not { } height
            || width < 1 || height < 1 || width > PackagePolicy.MaxImageDimension || height > PackagePolicy.MaxImageDimension)
        {
            return null;
        }

        return new PackageFileDeclaration(path, byteLength, sha256!, mediaType, width, height);
    }

    /// <summary>The image format a declared media type stands for; only the managed asset formats.</summary>
    internal static DetectedImageFormat? FormatOfMediaType(string? mediaType) => mediaType switch
    {
        "image/png" => DetectedImageFormat.Png,
        "image/jpeg" => DetectedImageFormat.Jpeg,
        "image/webp" => DetectedImageFormat.WebP,
        _ => null,
    };

    private static string? ReadString(JsonObject node, string key) =>
        node[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static int? ReadInt(JsonObject node, string key) =>
        node[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : null;

    private static long? ReadLong(JsonObject node, string key) =>
        node[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var number) ? number : null;

    /// <summary>Package-supplied text for a log line: bounded, printable ASCII only.</summary>
    internal static string SafeForLog(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 32));
        foreach (var c in text.Take(32))
        {
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return builder.ToString();
    }
}
