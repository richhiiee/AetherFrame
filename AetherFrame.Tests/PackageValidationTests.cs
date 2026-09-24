using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Pure validation pieces: limits, entry names, the ZIP end record, the manifest, asset id handling.</summary>
public class PackageValidationTests
{
    // ---------------------------------------------------------------- pinned limits

    [Fact]
    public void Limits_ArePinned()
    {
        Assert.Equal(".aetherframe", PackagePolicy.FileExtension);
        Assert.Equal("aetherframe.package", PackagePolicy.FormatIdentifier);
        Assert.Equal(1, PackagePolicy.CurrentFormatVersion);
        Assert.Equal(128L * 1024 * 1024, PackagePolicy.MaxPackageBytes);
        Assert.Equal(128L * 1024 * 1024, PackagePolicy.MaxTotalUncompressedBytes);
        Assert.Equal(36, PackagePolicy.MaxEntryCount);
        Assert.Equal(32L * 1024 * 1024, PackagePolicy.MaxEntryBytes);
        Assert.Equal(128, PackagePolicy.MaxEntryPathLength);
        Assert.Equal(64 * 1024, PackagePolicy.MaxManifestBytes);
        Assert.Equal(8 * 1024 * 1024, PackagePolicy.MaxProfileBytes);
        Assert.Equal(32, PackagePolicy.MaxJsonDepth);
        Assert.Equal(256, PackagePolicy.MaxElementCount);
        Assert.Equal(32, PackagePolicy.MaxAssetCount);
        Assert.Equal(16f, PackagePolicy.MinCanvasDimension);
        Assert.Equal(8192f, PackagePolicy.MaxCanvasDimension);
        Assert.Equal(100_000f, PackagePolicy.MaxCoordinateMagnitude);
        Assert.Equal(1024f, PackagePolicy.MaxFontSize);
        Assert.Equal(10_000f, PackagePolicy.MaxStyleMagnitude);
        Assert.Equal(8L * 1024 * 1024, PackagePolicy.MaxPreviewBytes);
        Assert.Equal(4096, PackagePolicy.MaxPreviewDimension);
        Assert.Equal(1e9, PackageProfileValidator.MaxJsonNumberMagnitude);
    }

    [Fact]
    public void ImageLimits_AreTheExistingImportLimits()
    {
        Assert.Equal(ImageSafety.MaxFileBytes, PackagePolicy.MaxImageBytes);
        Assert.Equal(ImageSafety.MaxDimension, PackagePolicy.MaxImageDimension);
        Assert.Equal(ImageSafety.MaxPixelCount, PackagePolicy.MaxImagePixelCount);
        Assert.Equal(ImageSafety.MaxDecodedBytes, PackagePolicy.MaxDecodedImageBytes);
        Assert.Equal(ProfileDocument.MaxElementCount, PackagePolicy.MaxElementCount);

        // Consistency: a full package's parts fit inside the totals.
        Assert.True(PackagePolicy.MaxProfileBytes <= PackagePolicy.MaxEntryBytes);
        Assert.True(PackagePolicy.MaxPreviewBytes <= PackagePolicy.MaxEntryBytes);
        Assert.True(PackagePolicy.MaxEntryCount >= 3 + PackagePolicy.MaxAssetCount);
        Assert.True("assets/".Length + 32 + ".webp".Length <= PackagePolicy.MaxEntryPathLength);
    }

    // ---------------------------------------------------------------- entry names

    [Theory]
    [InlineData("manifest.json", "Manifest")]
    [InlineData("profile.json", "Profile")]
    [InlineData("preview.png", "Preview")]
    [InlineData("assets/", "AssetsFolder")]
    [InlineData("assets/0123456789abcdef0123456789abcdef.png", "Asset")]
    [InlineData("assets/0123456789abcdef0123456789abcdef.jpg", "Asset")]
    [InlineData("assets/0123456789abcdef0123456789abcdef.webp", "Asset")]
    public void Version1Layout_IsAccepted(string name, string kind)
    {
        Assert.Null(PackagePaths.CheckSafety(name));
        Assert.True(PackagePaths.ClassifyVersion1(name, out var entry));
        Assert.Equal(Enum.Parse<PackageEntryKind>(kind), entry.Kind);
    }

    [Theory]
    [InlineData("assets/0123456789abcdef0123456789abcdef.jpeg")]
    [InlineData("assets/0123456789abcdef0123456789abcdef.PNG")]
    [InlineData("assets/0123456789ABCDEF0123456789abcdef.png")]
    [InlineData("assets/0123456789abcdef0123456789abcde.png")]
    [InlineData("assets/0123456789abcdef-0123456789abcdef.png")]
    [InlineData("assets/{0123456789abcdef0123456789abcdef}.png")]
    [InlineData("assets/0123456789abcdef0123456789abcdeg.png")]
    [InlineData("assets")]
    [InlineData("Manifest.json")]
    [InlineData("preview.jpg")]
    [InlineData("thumbnail.png")]
    public void Version1Layout_RejectsEverythingElse(string name) =>
        Assert.False(PackagePaths.CheckSafety(name) is null && PackagePaths.ClassifyVersion1(name, out _));

    [Fact]
    public void Fuzz_NoUnsafeNameIsEverAccepted()
    {
        // Deterministic: random names from an alphabet rich in the dangerous characters.
        var random = new Random(20260924);
        const string alphabet = "./\\:aA0 .\0\u00e9\u2215\uff0e~*?\"<>|$";
        for (var i = 0; i < 20_000; i++)
        {
            var length = random.Next(1, 24);
            var chars = new char[length];
            for (var j = 0; j < length; j++)
            {
                chars[j] = alphabet[random.Next(alphabet.Length)];
            }

            var name = new string(chars);
            if (PackagePaths.CheckSafety(name) is not null)
            {
                continue;
            }

            // Anything accepted is plain, relative, ASCII, with no traversal or alternate separators.
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain(':', name);
            Assert.False(name.StartsWith('/'));
            Assert.All(name.TrimEnd('/').Split('/'), segment =>
            {
                Assert.NotEqual(string.Empty, segment);
                Assert.NotEqual(".", segment);
                Assert.NotEqual("..", segment);
                Assert.False(segment.EndsWith('.'));
            });
            Assert.All(name, c => Assert.InRange(c, '!', '~'));

            // And resolving it under any root can never leave that root.
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "af-root")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, Path.GetFullPath(Path.Combine(root, name)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fuzz_CollisionKeysCatchEveryCaseVariant()
    {
        var random = new Random(7);
        var names = new[] { "manifest.json", "profile.json", "preview.png", PackagePaths.AssetPath(Guid.Parse("0123456789abcdef0123456789abcdef"), ".png") };
        foreach (var name in names)
        {
            for (var i = 0; i < 200; i++)
            {
                var variant = new string(name.Select(c => random.Next(2) == 0 ? char.ToUpperInvariant(c) : c).ToArray());
                Assert.Equal(PackagePaths.CollisionKey(name), PackagePaths.CollisionKey(variant));
            }
        }
    }

    [Fact]
    public void SuggestedFileNames_AreSafe()
    {
        Assert.Equal("My Plate", PackagePaths.SuggestFileName("My Plate"));
        Assert.Equal("a_b_c_d", PackagePaths.SuggestFileName("a/b\\c:d"));
        Assert.Equal("Plate", PackagePaths.SuggestFileName("..."));
        Assert.Equal("Plate", PackagePaths.SuggestFileName("   "));
        var traversal = PackagePaths.SuggestFileName("../../etc");
        Assert.DoesNotContain('/', traversal);
        Assert.DoesNotContain('\\', traversal);
        Assert.False(traversal.StartsWith('.'));
        Assert.Equal(Path.GetFileName(traversal), traversal);
    }

    // ---------------------------------------------------------------- ZIP end record

    private static byte[] EndRecord(ushort entriesOnDisk, ushort totalEntries, ushort disk = 0, ushort commentLength = 0)
    {
        var record = new byte[22 + commentLength];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), disk);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), entriesOnDisk);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), totalEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), commentLength);
        return record;
    }

    [Fact]
    public void Preflight_ReadsTheDeclaredEntryCount()
    {
        Assert.Equal(3, ZipPreflight.ReadEntryCount(new MemoryStream(EndRecord(3, 3)), out _));
        Assert.Equal(60000, ZipPreflight.ReadEntryCount(new MemoryStream([.. new byte[100], .. EndRecord(60000, 60000, commentLength: 10)]), out _));
    }

    [Fact]
    public void Preflight_RefusesZip64MultiDiskAndGarbage()
    {
        Assert.Null(ZipPreflight.ReadEntryCount(new MemoryStream(EndRecord(ushort.MaxValue, ushort.MaxValue)), out var zip64));
        Assert.Contains("ZIP64", zip64);
        Assert.Null(ZipPreflight.ReadEntryCount(new MemoryStream(EndRecord(1, 2)), out _));
        Assert.Null(ZipPreflight.ReadEntryCount(new MemoryStream(EndRecord(1, 1, disk: 1)), out _));
        Assert.Null(ZipPreflight.ReadEntryCount(new MemoryStream(new byte[10]), out _));
        Assert.Null(ZipPreflight.ReadEntryCount(new MemoryStream(new byte[4096]), out _));

        // A signature inside a comment that doesn't reach the end of the file isn't the record.
        var fake = EndRecord(1, 1);
        Assert.Null(ZipPreflight.ReadEntryCount(new MemoryStream([.. fake, .. new byte[50]]), out _));
    }

    [Fact]
    public void Preflight_ReportsTheCentralDirectorySize_AndRefusesOneOutsideTheFile()
    {
        var record = EndRecord(2, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), 300); // directory size
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), 100); // directory offset
        Assert.Equal((2, 300L), ZipPreflight.Read(new MemoryStream([.. new byte[400], .. record]), out _));
        Assert.Null(ZipPreflight.Read(new MemoryStream([.. new byte[399], .. record]), out var error));
        Assert.Contains("outside", error);
    }

    [Fact]
    public void PackageAssetIds_AreDerivedFromContent()
    {
        var a = PackageExporter.PackageAssetIdFor(PackageFiles.Sha256(TestImages.Png(3, 3)));
        var b = PackageExporter.PackageAssetIdFor(PackageFiles.Sha256(TestImages.Png(3, 3)));
        var c = PackageExporter.PackageAssetIdFor(PackageFiles.Sha256(TestImages.Png(4, 3)));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(Guid.Empty, PackageExporter.PackageAssetIdFor(new string('0', 64)));
    }

    // ---------------------------------------------------------------- manifest

    private static JsonObject ValidManifest()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");
        return JsonNode.Parse(new PackageManifest
        {
            PlateName = "Plate",
            PlateSchemaVersion = 2,
            Profile = new PackageFileDeclaration(PackagePaths.ProfilePath, 10, new string('a', 64)),
            Assets = [new PackageAssetDeclaration(id, PackagePaths.AssetPath(id, ".png"), "image/png", 100, new string('b', 64), 10, 10)],
        }.ToJsonBytes())!.AsObject();
    }

    [Fact]
    public void Manifest_RoundTrips()
    {
        var diagnostics = new PackageDiagnostics();
        var manifest = PackageManifest.Parse(Encoding.UTF8.GetBytes(ValidManifest().ToJsonString()), diagnostics);

        Assert.NotNull(manifest);
        Assert.False(diagnostics.HasErrors);
        Assert.Equal("Plate", manifest!.PlateName);
        Assert.Single(manifest.Assets);
        Assert.Equal(ValidManifest().ToJsonString(), JsonNode.Parse(manifest.ToJsonBytes())!.ToJsonString());
    }

    [Fact]
    public void Fuzz_MutatedManifestsNeverThrowAndNeverHalfParse()
    {
        // Deterministic: replace one field at a time with values of every wrong shape.
        var junk = new JsonNode?[] { null, 0, -1, 1.5, 1e300, "", "x", new string('z', 5000), true, new JsonArray(), new JsonObject(), "https://evil.example/", "../x", "C:\\x" };
        var random = new Random(99);
        var paths = new List<Func<JsonObject, (JsonObject Parent, string Key)>>
        {
            m => (m, "format"), m => (m, "formatVersion"), m => (m, "requires"), m => (m, "generator"), m => (m, "plate"), m => (m, "profile"), m => (m, "assets"),
            m => (m["plate"]!.AsObject(), "name"), m => (m["plate"]!.AsObject(), "schemaVersion"),
            m => (m["profile"]!.AsObject(), "path"), m => (m["profile"]!.AsObject(), "byteLength"), m => (m["profile"]!.AsObject(), "sha256"),
            m => (m["assets"]![0]!.AsObject(), "id"), m => (m["assets"]![0]!.AsObject(), "path"), m => (m["assets"]![0]!.AsObject(), "mediaType"),
            m => (m["assets"]![0]!.AsObject(), "byteLength"), m => (m["assets"]![0]!.AsObject(), "sha256"),
            m => (m["assets"]![0]!.AsObject(), "width"), m => (m["assets"]![0]!.AsObject(), "height"),
        };

        foreach (var select in paths)
        {
            foreach (var value in junk)
            {
                var manifest = ValidManifest();
                var (parent, key) = select(manifest);
                if (random.Next(4) == 0)
                {
                    parent.Remove(key);
                }
                else
                {
                    parent[key] = value?.DeepClone();
                }

                var diagnostics = new PackageDiagnostics();
                var parsed = PackageManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()), diagnostics);

                // Either a clean result or a clean refusal: never both, never an exception.
                Assert.Equal(parsed is null, diagnostics.HasErrors);
                if (parsed is not null)
                {
                    Assert.All(parsed.Assets, a => Assert.Equal(PackagePaths.AssetPath(a.AssetId, a.Extension), a.Path));
                    Assert.All(parsed.Assets, a => Assert.True(PackagePaths.IsSha256Hex(a.Sha256)));
                }
            }
        }
    }

    [Fact]
    public void Fuzz_AssetDeclarationsOnlyEverPointAtCanonicalAssetPaths()
    {
        var random = new Random(4242);
        var extensions = new[] { ".png", ".jpg", ".webp", ".gif", ".PNG", ".exe", "" };
        var mediaTypes = new[] { "image/png", "image/jpeg", "image/webp", "image/gif", "text/html", "" };
        for (var i = 0; i < 2_000; i++)
        {
            var manifest = ValidManifest();
            var asset = manifest["assets"]![0]!.AsObject();
            var id = Guid.NewGuid();
            var pathId = random.Next(5) == 0 ? Guid.NewGuid() : id;
            asset["id"] = random.Next(6) == 0 ? id.ToString("D") : id.ToString("N");
            asset["mediaType"] = mediaTypes[random.Next(mediaTypes.Length)];
            asset["path"] = PackagePaths.AssetsFolder + pathId.ToString("N") + extensions[random.Next(extensions.Length)];

            var parsed = PackageManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()), new PackageDiagnostics());
            if (parsed is null)
            {
                continue;
            }

            var declared = Assert.Single(parsed.Assets);
            Assert.True(PackagePaths.ClassifyVersion1(declared.Path, out var entry));
            Assert.Equal(declared.AssetId, entry.AssetId);
            Assert.Equal(ImageSafety.ExtensionOf(PackageManifest.FormatOfMediaType(declared.MediaType)!.Value), entry.Extension);
        }
    }

    [Fact]
    public void Manifest_UnknownFieldsAreIgnored()
    {
        var manifest = ValidManifest();
        manifest["somethingNew"] = new JsonObject { ["nested"] = new JsonArray(1, 2) };
        manifest["assets"]![0]!["colorProfile"] = "sRGB";

        var diagnostics = new PackageDiagnostics();
        Assert.NotNull(PackageManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()), diagnostics));
        Assert.Equal(PackageCompatibility.Supported, diagnostics.Compatibility);
    }

    // ---------------------------------------------------------------- numbers

    [Fact]
    public void Fuzz_NumberScreenAcceptsOnlyBoundedValues()
    {
        var random = new Random(31337);
        for (var i = 0; i < 5_000; i++)
        {
            var exponent = random.Next(-40, 40);
            var value = (random.NextDouble() * 2 - 1) * Math.Pow(10, exponent);
            var json = JsonNode.Parse($"{{\"v\": {value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}")!;
            var bad = PackageProfileValidator.FindBadNumber(json);
            var isInteger = Math.Abs(value) < 9e18 && value == Math.Floor(value);
            if (!isInteger)
            {
                Assert.Equal(Math.Abs(value) > PackageProfileValidator.MaxJsonNumberMagnitude, bad is not null);
            }

            if (bad is null && !isInteger)
            {
                Assert.True(float.IsFinite((float)value));
            }
        }
    }

    // ---------------------------------------------------------------- asset ids

    [Fact]
    public void Remap_ReplacesOnlyDeclaredIds_KeepingTheirFormat()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var json = JsonNode.Parse($$"""
            { "x": "{{a}}", "y": ["{{a:N}}", "{{unrelated}}", "{{a:B}}"], "z": { "{{a}}": "key stays", "n": 5, "t": "text {{a}}" } }
            """)!;

        var count = PackageAssetIds.Remap(json, new Dictionary<Guid, Guid> { [a] = b });

        Assert.Equal(3, count);
        Assert.Equal(b.ToString(), json["x"]!.GetValue<string>());
        Assert.Equal(b.ToString("N"), json["y"]![0]!.GetValue<string>());
        Assert.Equal(unrelated.ToString(), json["y"]![1]!.GetValue<string>());
        Assert.Equal(b.ToString("B"), json["y"]![2]!.GetValue<string>());
        Assert.Equal("key stays", json["z"]![a.ToString()]!.GetValue<string>());
        Assert.Equal($"text {a}", json["z"]!["t"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- image structure

    [Fact]
    public void Structure_AcceptsWholeImagesAndRejectsDamagedOnes()
    {
        using var dir = new TempDirectory();
        string Write(string name, byte[] bytes) => TestImages.Write(dir.Path, name, bytes);
        var png = TestImages.Png(10, 10);

        Assert.Null(ImageSafety.CheckStructure(Write("ok.png", png), DetectedImageFormat.Png));
        Assert.Null(ImageSafety.CheckStructure(Write("ok.jpg", TestImages.Jpeg(10, 10)), DetectedImageFormat.Jpeg));
        Assert.Null(ImageSafety.CheckStructure(Write("ok.webp", TestImages.WebPExtended(10, 10)), DetectedImageFormat.WebP));

        Assert.NotNull(ImageSafety.CheckStructure(Write("cut.png", png[..^12]), DetectedImageFormat.Png));
        Assert.NotNull(ImageSafety.CheckStructure(Write("tail.png", [.. png, 1, 2, 3]), DetectedImageFormat.Png));
        Assert.NotNull(ImageSafety.CheckStructure(Write("cut.jpg", TestImages.Jpeg(10, 10)[..^2]), DetectedImageFormat.Jpeg));
        Assert.NotNull(ImageSafety.CheckStructure(Write("cut.webp", TestImages.WebPExtended(10, 10)[..^4]), DetectedImageFormat.WebP));
        Assert.NotNull(ImageSafety.CheckStructure(Write("long.webp", [.. TestImages.WebPExtended(10, 10), 0, 0, 0, 0]), DetectedImageFormat.WebP));

        var badLength = (byte[])png.Clone();
        badLength[8] = 0x7F; // IHDR length far past the end of the file
        Assert.NotNull(ImageSafety.CheckStructure(Write("len.png", badLength), DetectedImageFormat.Png));
    }

    // ---------------------------------------------------------------- compatibility verdicts

    [Fact]
    public void Compatibility_DistinguishesTheFourStates()
    {
        var supported = new PackageDiagnostics();
        Assert.Equal(PackageCompatibility.Supported, supported.Compatibility);

        var warned = new PackageDiagnostics();
        warned.Warning(PackageWarningCode.PreviewOmitted, "x");
        Assert.Equal(PackageCompatibility.SupportedWithWarnings, warned.Compatibility);

        var newer = new PackageDiagnostics();
        newer.Error(PackageErrorCode.UnsupportedVersion, "x");
        Assert.Equal(PackageCompatibility.Unsupported, newer.Compatibility);

        var invalid = new PackageDiagnostics();
        invalid.Error(PackageErrorCode.UnsupportedVersion, "x");
        invalid.Error(PackageErrorCode.UnsafePath, "y");
        Assert.Equal(PackageCompatibility.Invalid, invalid.Compatibility);
    }
}
