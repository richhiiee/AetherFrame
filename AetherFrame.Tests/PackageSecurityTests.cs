using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Hostile packages. Every one must be refused before anything is written, leave the installation
/// byte-for-byte as it was (Plates, bindings, Active Plate, images), and clean up after itself.
/// </summary>
public class PackageSecurityTests
{
    /// <summary>A valid, minimal package: a Blank Plate with one image element and one text element.</summary>
    private sealed class Setup : IDisposable
    {
        private Setup(PackageFixture fixture, PlateLibraryService library, PlatePackageService packages, string path, Guid packageAssetId, Guid plateId)
        {
            Fixture = fixture;
            Library = library;
            Packages = packages;
            ValidPath = path;
            AssetId = packageAssetId;
            PlateId = plateId;
        }

        internal PackageFixture Fixture { get; }

        internal PlateLibraryService Library { get; }

        internal PlatePackageService Packages { get; }

        internal string ValidPath { get; }

        /// <summary>The image's id inside the package (derived from its content, not its local id).</summary>
        internal Guid AssetId { get; }

        internal Guid PlateId { get; }

        internal string AssetEntry => PackagePaths.AssetPath(AssetId, ".png");

        internal static async Task<Setup> CreateAsync()
        {
            var fixture = new PackageFixture();
            var (library, packages) = await fixture.LoadAsync();

            // An existing, bound, Active Plate — what a bad package must never disturb.
            var existing = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, PackageFixture.Owner, "Mine", new PlateStarterContent(null));
            Assert.True(existing.BecameActive);

            var assetId = fixture.AddImage(TestImages.Png(64, 48));
            var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small");
            var document = library.OpenDocumentForEditing(created.PlateId);
            document.Elements.Add(new ImageProfileElement { AssetId = assetId, Position = new Vector2(10, 10), Size = new Vector2(64, 48) });
            document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40), ZIndex = 1 });
            await library.SavePlateDocumentAsync(document);

            var path = fixture.Export(packages, created.PlateId, "valid.aetherframe");
            var manifest = PackageFiles.Json(PackageFiles.Entry(PackageFiles.Read(path), PackagePaths.ManifestPath));
            var packageAssetId = Guid.ParseExact(manifest["assets"]![0]!["id"]!.GetValue<string>(), "N");
            Assert.NotEqual(assetId, packageAssetId);
            return new Setup(fixture, library, packages, path, packageAssetId, existing.PlateId);
        }

        internal string Craft(Action<System.Collections.Generic.List<ZipSpec>> edit, bool reseal = true) => PackageFiles.Rewrite(ValidPath, edit, reseal);

        internal string CraftRaw(params ZipSpec[] entries) => PackageFiles.Write(Fixture.ExportDirectory, entries, $"raw-{Guid.NewGuid():N}.aetherframe");

        /// <summary>
        /// The package is refused with one of <paramref name="expected"/>, importing it does nothing,
        /// the installation is unchanged, and no staging is left behind.
        /// </summary>
        internal async Task AssertRefusedAsync(string path, params PackageErrorCode[] expected)
        {
            var before = Fixture.SnapshotInstallation();
            var plates = Library.GetOrderedPlates().Select(p => p.PlateId).ToList();

            var staged = Packages.Inspect(path);
            try
            {
                Assert.False(staged.CanImport, "a hostile package was importable: " + staged.DescribeForLog());
                Assert.Contains(staged.Compatibility, new[] { PackageCompatibility.Invalid, PackageCompatibility.Unsupported });
                Assert.Null(staged.PreparedProfile);
                Assert.Null(staged.PreviewDocument);
                Assert.Empty(staged.Assets);
                Assert.True(staged.Diagnostics.Errors.Any(e => expected.Contains(e.Code)),
                    $"expected one of [{string.Join(", ", expected)}], got: {staged.DescribeForLog()}");
                Assert.All(staged.Diagnostics.Errors, e => Assert.DoesNotContain(Fixture.Paths.Root, e.Message, StringComparison.OrdinalIgnoreCase));

                var attempt = await Packages.ImportAsync(staged);
                Assert.False(attempt.Succeeded);
            }
            finally
            {
                staged.Dispose();
            }

            Assert.Equal(before, Fixture.SnapshotInstallation());
            Assert.Equal(plates, Library.GetOrderedPlates().Select(p => p.PlateId).ToList());
            Assert.Equal(PlateId, Library.GetActivePlateId(PackageFixture.PrivateContentId));
            Assert.True(Fixture.StagingIsEmpty);
        }

        public void Dispose() => Fixture.Dispose();
    }

    private static ZipSpec Text(string name, string content = "x") => new(name, Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task TheBaselinePackageIsValid()
    {
        using var setup = await Setup.CreateAsync();
        using var staged = setup.Packages.Inspect(setup.ValidPath);
        Assert.Equal(PackageCompatibility.Supported, staged.Compatibility);
    }

    // ---------------------------------------------------------------- paths

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("../../../../Windows/System32/evil.dll")]
    [InlineData("assets/../../evil.png")]
    [InlineData("assets/../profile.json")]
    [InlineData("assets/./a.png")]
    [InlineData("./profile.json")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("C:evil.png")]
    [InlineData("/etc/passwd")]
    [InlineData("/profile.json")]
    [InlineData("\\\\server\\share\\evil.png")]
    [InlineData("//server/share/evil.png")]
    [InlineData("..\\evil.txt")]
    [InlineData("assets\\..\\..\\evil.png")]
    [InlineData("assets\\a.png")]
    [InlineData("profile.json.")]
    [InlineData("profile.json ")]
    [InlineData("manifest.json:hidden")]
    [InlineData("assets//a.png")]
    [InlineData("pro\0file.json")]
    [InlineData("pr\u00f6file.json")]
    [InlineData("profile\u2024json")]
    public async Task UnsafeEntryNames_AreRefused(string name)
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(Text(name))), PackageErrorCode.UnsafePath);
    }

    [Fact]
    public async Task DuplicateProfile_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(Text(PackagePaths.ProfilePath, "{}")), reseal: false), PackageErrorCode.DuplicateEntry);
    }

    [Fact]
    public async Task CaseCollision_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(Text("PROFILE.JSON", "{}")), reseal: false), PackageErrorCode.DuplicateEntry);
    }

    [Fact]
    public async Task CaseCollidingAsset_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(
            setup.Craft(entries => entries.Add(new ZipSpec(setup.AssetEntry.ToUpperInvariant().Replace("ASSETS/", "assets/"), TestImages.Png(1, 1))), reseal: false),
            PackageErrorCode.DuplicateEntry);
    }

    [Fact]
    public async Task SeparatorAndDotNormalizedDuplicates_AreRefused()
    {
        using var setup = await Setup.CreateAsync();
        var sneaky = setup.AssetEntry.Replace("assets/", "assets/x/../");
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(new ZipSpec(sneaky, TestImages.Png(1, 1))), reseal: false), PackageErrorCode.UnsafePath);
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(new ZipSpec(setup.AssetEntry.Replace('/', '\\'), TestImages.Png(1, 1))), reseal: false), PackageErrorCode.UnsafePath);
    }

    // ---------------------------------------------------------------- special entries

    [Theory]
    [InlineData(0xA1FF)] // symbolic link
    [InlineData(0x21B6)] // character device
    [InlineData(0x61B6)] // block device
    [InlineData(0x11B6)] // FIFO
    [InlineData(0xC1B6)] // socket
    [InlineData(0x41ED)] // directory mode on a file entry
    public async Task UnixSpecialFileModes_AreRefused(int mode)
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, PackagePaths.ProfilePath).ExternalAttributes = mode << 16), PackageErrorCode.UnsafePath);
    }

    [Theory]
    [InlineData(0x400)] // reparse point (Windows symbolic link / junction)
    [InlineData(0x40)] // device
    [InlineData(0x10)] // directory attribute on a file entry
    public async Task DosSpecialAttributes_AreRefused(int attributes)
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, setup.AssetEntry).ExternalAttributes = attributes), PackageErrorCode.UnsafePath);
    }

    [Fact]
    public async Task OrdinaryFileModes_AreAccepted()
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(entries =>
        {
            foreach (var entry in entries)
            {
                entry.ExternalAttributes = (0x81A4 << 16) | 0x20; // -rw-r--r--, archive bit
            }

            entries.Add(new ZipSpec(PackagePaths.AssetsFolder, [], externalAttributes: (0x41ED << 16) | 0x10));
        });

        using var staged = setup.Packages.Inspect(path);
        Assert.True(staged.CanImport, staged.DescribeForLog());
    }

    // ---------------------------------------------------------------- allowlist

    [Theory]
    [InlineData("evil.exe")]
    [InlineData("install.ps1")]
    [InlineData("run.bat")]
    [InlineData("plugin.dll")]
    [InlineData("assets/payload.dll")]
    [InlineData("assets/sub/a.png")]
    [InlineData("extras/readme.txt")]
    [InlineData("extras/")]
    [InlineData("assets/0123456789abcdef0123456789ABCDEF.png")]
    [InlineData("assets/0123456789abcdef0123456789abcdef.gif")]
    [InlineData("assets/0123456789abcdef0123456789abcdef.png.exe")]
    [InlineData("assets/00000000000000000000000000000000.png")]
    [InlineData("templates/future.json")]
    public async Task UnexpectedEntries_AreRefused(string name)
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(Text(name))), PackageErrorCode.UnexpectedEntry);
    }

    // ---------------------------------------------------------------- size limits

    [Fact]
    public async Task TooManyEntries_AreRefusedFromTheEndRecord()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            for (var i = 0; i < PackagePolicy.MaxEntryCount; i++)
            {
                entries.Add(Text($"filler{i}.txt"));
            }
        }), PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task OversizedPackageFile_IsRefusedBeforeOpening()
    {
        using var setup = await Setup.CreateAsync();
        var path = Path.Combine(setup.Fixture.ExportDirectory, "huge.aetherframe");
        using (var file = new FileStream(path, FileMode.CreateNew))
        {
            file.SetLength(PackagePolicy.MaxPackageBytes + 1);
        }

        await setup.AssertRefusedAsync(path, PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task ZipBomb_DeclaredExpansionBeyondTheEntryLimit_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var zeros = new byte[PackagePolicy.MaxEntryBytes + 1];
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, setup.AssetEntry).Bytes = zeros, reseal: false),
            PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task ZipBomb_TotalExpansionBeyondThePackageLimit_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var chunk = new byte[PackagePolicy.MaxEntryBytes - 1];
        var count = (int)(PackagePolicy.MaxTotalUncompressedBytes / chunk.Length) + 1;
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            for (var i = 0; i < count; i++)
            {
                entries.Add(new ZipSpec(PackagePaths.AssetPath(Guid.NewGuid(), ".png"), chunk));
            }
        }, reseal: false), PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task OversizedManifest_IsRefusedWhileStreaming()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var manifest = PackageFiles.Entry(entries, PackagePaths.ManifestPath);
            manifest.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest.Bytes) + new string(' ', PackagePolicy.MaxManifestBytes));
        }, reseal: false), PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task OversizedProfile_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
            profile.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(profile.Bytes) + new string(' ', PackagePolicy.MaxProfileBytes));
        }), PackageErrorCode.ManifestInvalid, PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task ProfileLongerThanDeclared_IsCutOffAtTheDeclaredSize()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
            profile.Bytes = [.. profile.Bytes, .. Encoding.UTF8.GetBytes(new string(' ', 4096))];
        }, reseal: false), PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task ExcessiveJsonDepth_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var deep = new string('[', 200) + new string(']', 200);
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
            profile.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(profile.Bytes).TrimEnd().TrimEnd('}') + ", \"Deep\": " + deep + "}");
        }), PackageErrorCode.ProfileInvalid);
    }

    [Fact]
    public async Task ExcessiveManifestDepth_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditManifest(entries, m =>
        {
            JsonNode node = new JsonArray();
            for (var i = 0; i < 60; i++)
            {
                node = new JsonArray(node);
            }

            m["deep"] = node;
        })), PackageErrorCode.ManifestInvalid);
    }

    [Fact]
    public async Task TooManyElements_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditProfile(entries, p =>
        {
            var elements = p["Elements"]!.AsArray();
            var template = elements.First(e => e!["elementType"]!.GetValue<string>() == "text")!;
            while (elements.Count <= PackagePolicy.MaxElementCount)
            {
                var copy = template.DeepClone();
                copy["Id"] = Guid.NewGuid().ToString();
                elements.Add(copy);
            }
        })), PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task TooManyAssets_AreRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditManifest(entries, m =>
        {
            var assets = m["assets"]!.AsArray();
            var template = assets[0]!;
            while (assets.Count <= PackagePolicy.MaxAssetCount)
            {
                var copy = template.DeepClone();
                var id = Guid.NewGuid();
                copy["id"] = id.ToString("N");
                copy["path"] = PackagePaths.AssetPath(id, ".png");
                assets.Add(copy);
            }
        }), reseal: false), PackageErrorCode.PackageTooLarge);
    }

    // ---------------------------------------------------------------- manifest

    [Fact]
    public async Task MissingManifest_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.RemoveAll(e => e.Name == PackagePaths.ManifestPath)), PackageErrorCode.ManifestInvalid);
    }

    [Theory]
    [InlineData("""{"format":"zip.package","formatVersion":1}""")]
    [InlineData("""{"formatVersion":1}""")]
    [InlineData("""{"format":"aetherframe.package","formatVersion":"1"}""")]
    [InlineData("""{"format":"aetherframe.package","formatVersion":0}""")]
    [InlineData("""{"format":"aetherframe.package","formatVersion":1.5}""")]
    [InlineData("""{"format":"aetherframe.package","formatVersion":1,"requires":[]}""")]
    [InlineData("""{"format":"aetherframe.package","formatVersion":1,"requires":"plate"}""")]
    [InlineData("""{"format":"aetherframe.package","format":"aetherframe.package","formatVersion":1}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"aetherframe.package\",")]
    [InlineData("{/*comment*/\"format\":\"aetherframe.package\"}")]
    public async Task MalformedManifests_AreRefused(string manifest)
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, PackagePaths.ManifestPath).Bytes = Encoding.UTF8.GetBytes(manifest), reseal: false),
            PackageErrorCode.ManifestInvalid);
    }

    [Fact]
    public async Task ManifestNameMismatch_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditManifest(entries, m => m["plate"]!["name"] = "Something Else")), PackageErrorCode.ProfileInvalid);
    }

    [Fact]
    public async Task ManifestSchemaMismatch_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditManifest(entries, m => m["plate"]!["schemaVersion"] = 1)), PackageErrorCode.ProfileInvalid);
    }

    [Fact]
    public async Task DuplicateAssetDeclaration_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditManifest(entries, m => m["assets"]!.AsArray().Add(m["assets"]![0]!.DeepClone()))),
            PackageErrorCode.DuplicateEntry);
    }

    // ---------------------------------------------------------------- declarations vs contents

    [Fact]
    public async Task MissingProfile_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.RemoveAll(e => e.Name == PackagePaths.ProfilePath)), PackageErrorCode.ProfileInvalid);
    }

    [Fact]
    public async Task MissingDeclaredAsset_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.RemoveAll(e => e.Name == setup.AssetEntry)), PackageErrorCode.AssetMissing);
    }

    [Fact]
    public async Task UndeclaredAsset_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(new ZipSpec(PackagePaths.AssetPath(Guid.NewGuid(), ".png"), TestImages.Png(2, 2)))),
            PackageErrorCode.AssetUndeclared);
    }

    [Fact]
    public async Task AssetDeclaredButNotUsedByThePlate_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var smuggled = Guid.NewGuid();
        var bytes = TestImages.Png(2, 2);
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            entries.Add(new ZipSpec(PackagePaths.AssetPath(smuggled, ".png"), bytes));
            PackageFiles.EditManifest(entries, m => m["assets"]!.AsArray().Add(new JsonObject
            {
                ["id"] = smuggled.ToString("N"),
                ["path"] = PackagePaths.AssetPath(smuggled, ".png"),
                ["mediaType"] = "image/png",
                ["byteLength"] = bytes.Length,
                ["sha256"] = PackageFiles.Sha256(bytes),
                ["width"] = 2,
                ["height"] = 2,
            }));
        }), PackageErrorCode.AssetUndeclared);
    }

    [Fact]
    public async Task ProfileReferencingAnImageNotInThePackage_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditProfile(entries, p => p["Background"]!["ImageAssetId"] = Guid.NewGuid().ToString())),
            PackageErrorCode.AssetMissing);
    }

    [Fact]
    public async Task UndeclaredPreview_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(new ZipSpec(PackagePaths.PreviewPath, TestImages.Png(16, 9)))), PackageErrorCode.AssetUndeclared);
    }

    [Fact]
    public async Task InvalidDeclaredPreview_IsOmittedWithAWarning_AndThePlateStillImports()
    {
        using var setup = await Setup.CreateAsync();
        var garbage = "definitely not a png"u8.ToArray();
        var path = setup.Craft(entries =>
        {
            entries.Add(new ZipSpec(PackagePaths.PreviewPath, garbage));
            PackageFiles.EditManifest(entries, m => m["preview"] = new JsonObject
            {
                ["path"] = PackagePaths.PreviewPath,
                ["mediaType"] = "image/png",
                ["byteLength"] = garbage.Length,
                ["sha256"] = PackageFiles.Sha256(garbage),
                ["width"] = 16,
                ["height"] = 9,
            });
        });

        using var staged = setup.Packages.Inspect(path);
        Assert.Equal(PackageCompatibility.SupportedWithWarnings, staged.Compatibility);
        Assert.Equal(PackageWarningCode.PreviewOmitted, Assert.Single(staged.Diagnostics.Warnings).Code);
        Assert.Null(staged.PreviewImagePath);
        Assert.False(File.Exists(Path.Combine(staged.StagingDirectory, "preview.png")));
        Assert.True(staged.CanImport);
    }

    // ---------------------------------------------------------------- integrity & images

    [Fact]
    public async Task AssetHashMismatch_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var asset = PackageFiles.Entry(entries, setup.AssetEntry);
            asset.Bytes = TestImages.Png(64, 48, animationFrames: 2); // a different, still valid image of the same size
        }, reseal: false), PackageErrorCode.HashMismatch, PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task SameSizeTamperedAsset_FailsTheHash()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var asset = PackageFiles.Entry(entries, setup.AssetEntry);
            var bytes = (byte[])asset.Bytes.Clone();
            bytes[^5] ^= 0xFF; // inside IEND's CRC: same length, different content
            asset.Bytes = bytes;
        }, reseal: false), PackageErrorCode.HashMismatch);
    }

    [Fact]
    public async Task ProfileHashMismatch_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
            profile.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(profile.Bytes).Replace("hello", "HELLO"));
        }, reseal: false), PackageErrorCode.HashMismatch);
    }

    [Theory]
    [InlineData("MZ")]
    [InlineData("script")]
    [InlineData("truncated")]
    [InlineData("jpeg-as-png")]
    [InlineData("padded")]
    public async Task InvalidImages_AreRefused(string kind)
    {
        using var setup = await Setup.CreateAsync();
        var png = TestImages.Png(64, 48);
        var bytes = kind switch
        {
            "MZ" => "MZ\u0090\0 this is a Windows executable"u8.ToArray(),
            "script" => "#!/bin/sh\nrm -rf /\n"u8.ToArray(),
            "truncated" => png[..^12],
            "jpeg-as-png" => TestImages.Jpeg(64, 48),
            _ => [.. png, .. "hidden payload after IEND"u8.ToArray()],
        };

        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, setup.AssetEntry).Bytes = bytes), PackageErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task OversizedImageDimensions_AreRefused()
    {
        using var setup = await Setup.CreateAsync();
        var huge = TestImages.Png(PackagePolicy.MaxImageDimension + 1, 10);
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            PackageFiles.Entry(entries, setup.AssetEntry).Bytes = huge;
            PackageFiles.EditManifest(entries, m => m["assets"]![0]!["width"] = PackagePolicy.MaxImageDimension + 1);
        }), PackageErrorCode.ManifestInvalid, PackageErrorCode.ImageInvalid);

        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, setup.AssetEntry).Bytes = huge), PackageErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task DecompressionBombImage_IsRefusedByPixelCount()
    {
        using var setup = await Setup.CreateAsync();
        var bomb = TestImages.Png(8000, 8000); // within per-side limit, 64 megapixels: 256 MiB decoded
        await setup.AssertRefusedAsync(setup.Craft(entries =>
        {
            PackageFiles.Entry(entries, setup.AssetEntry).Bytes = bomb;
            PackageFiles.EditManifest(entries, m =>
            {
                m["assets"]![0]!["width"] = 8000;
                m["assets"]![0]!["height"] = 8000;
            });
        }), PackageErrorCode.ManifestInvalid, PackageErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task ImageSizeDisagreeingWithTheManifest_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, setup.AssetEntry).Bytes = TestImages.Png(65, 48)), PackageErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task UnsupportedDecoder_IsRefused()
    {
        using var fixture = new PackageFixture(decoder: ext => ext == ".png");
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Webby");
        var document = library.OpenDocumentForEditing(created.PlateId);
        var webp = TestImages.WebPExtended(8, 8);
        document.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), Size = new Vector2(8, 8) });
        await library.SavePlateDocumentAsync(document);

        // Build a package by hand: this installation can't even export a WebP, so another did.
        var assetId = ((ImageProfileElement)document.Elements[0]).AssetId;
        var profile = JsonNode.Parse(library.GetSavedJsonForExport(created.PlateId).Json)!.AsObject();
        foreach (var field in PackageExporter.StrippedProfileFields)
        {
            profile.Remove(field);
        }

        var profileBytes = Encoding.UTF8.GetBytes(profile.ToJsonString());
        var manifest = new PackageManifest
        {
            PlateName = "Webby",
            PlateSchemaVersion = ProfileDocument.CurrentSchemaVersion,
            Profile = new PackageFileDeclaration(PackagePaths.ProfilePath, profileBytes.Length, PackageFiles.Sha256(profileBytes)),
            Assets = [new PackageAssetDeclaration(assetId, PackagePaths.AssetPath(assetId, ".webp"), "image/webp", webp.Length, PackageFiles.Sha256(webp), 8, 8)],
        };
        var path = PackageFiles.Write(fixture.ExportDirectory,
        [
            new ZipSpec(PackagePaths.ManifestPath, manifest.ToJsonBytes()),
            new ZipSpec(PackagePaths.ProfilePath, profileBytes),
            new ZipSpec(PackagePaths.AssetPath(assetId, ".webp"), webp),
        ]);

        using var staged = packages.Inspect(path);
        Assert.False(staged.CanImport);
        Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.ImageInvalid);

        using var permissive = new PackageFixture();
        var (_, permissivePackages) = await permissive.LoadAsync();
        using var accepted = permissivePackages.Inspect(path);
        Assert.True(accepted.CanImport, accepted.DescribeForLog());
    }

    // ---------------------------------------------------------------- malformed archives

    [Fact]
    public async Task NotAZip_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var path = Path.Combine(setup.Fixture.ExportDirectory, "random.aetherframe");
        var random = new byte[4096];
        new Random(1234).NextBytes(random);
        File.WriteAllBytes(path, random);
        await setup.AssertRefusedAsync(path, PackageErrorCode.InvalidArchive);

        File.WriteAllBytes(path, []);
        await setup.AssertRefusedAsync(path, PackageErrorCode.InvalidArchive);
    }

    [Fact]
    public async Task TruncatedZip_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var bytes = File.ReadAllBytes(setup.ValidPath);
        var path = Path.Combine(setup.Fixture.ExportDirectory, "cut.aetherframe");
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        await setup.AssertRefusedAsync(path, PackageErrorCode.InvalidArchive);
    }

    [Fact]
    public async Task CorruptedCompressedData_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        var bytes = File.ReadAllBytes(setup.ValidPath);

        // Scribble over the first entry's compressed data (profile.json; after its 30-byte local header and name).
        for (var i = 30 + PackagePaths.ProfilePath.Length + 4; i < 30 + PackagePaths.ProfilePath.Length + 64; i++)
        {
            bytes[i] ^= 0x5A;
        }

        var path = Path.Combine(setup.Fixture.ExportDirectory, "scribbled.aetherframe");
        File.WriteAllBytes(path, bytes);
        await setup.AssertRefusedAsync(path, PackageErrorCode.InvalidArchive, PackageErrorCode.HashMismatch, PackageErrorCode.PackageTooLarge);
    }

    // ---------------------------------------------------------------- hostile Plate content

    [Theory]
    [InlineData("image-url")]
    [InlineData("background-file-url")]
    [InlineData("background-unc")]
    public async Task RemoteOrFileReferences_AreRefused(string kind)
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditProfile(entries, p =>
        {
            switch (kind)
            {
                case "image-url":
                    p["Elements"]!.AsArray().First(e => e!["elementType"]!.GetValue<string>() == "image")!["AssetId"] = "https://evil.example/x.png";
                    break;
                case "background-file-url":
                    p["Background"]!["ImageAssetId"] = "file:///C:/Users/victim/secret.png";
                    break;
                default:
                    p["Background"]!["ImageAssetId"] = "\\\\evil-server\\share\\x.png";
                    break;
            }
        })), PackageErrorCode.ProfileInvalid);
    }

    [Fact]
    public async Task RemoteAssetPathInTheManifest_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.EditManifest(entries, m => m["assets"]![0]!["path"] = "https://evil.example/x.png")),
            PackageErrorCode.ManifestInvalid);
    }

    [Theory]
    [InlineData("canvas-huge")]
    [InlineData("canvas-negative")]
    [InlineData("position-far")]
    [InlineData("position-overflow")]
    [InlineData("size-negative")]
    [InlineData("font-huge")]
    [InlineData("rotation-overflow")]
    [InlineData("color-overflow")]
    [InlineData("text-too-long")]
    [InlineData("name-too-long")]
    [InlineData("duplicate-element-id")]
    [InlineData("empty-element-id")]
    [InlineData("element-not-object")]
    [InlineData("elements-not-array")]
    [InlineData("zindex-overflow")]
    [InlineData("unknown-type-name")]
    [InlineData("level-out-of-range")]
    [InlineData("too-many-playstyles")]
    [InlineData("active-hours")]
    [InlineData("duplicate-property")]
    public async Task HostileValues_AreRefused(string kind)
    {
        using var setup = await Setup.CreateAsync();
        var path = setup.Craft(entries =>
        {
            if (kind == "duplicate-property")
            {
                var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
                profile.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(profile.Bytes).TrimEnd().TrimEnd('}') + ", \"CanvasWidth\": 64 }");
                return;
            }

            PackageFiles.EditProfile(entries, p =>
            {
                var elements = p["Elements"] as JsonArray;
                var text = elements?.First(e => e!["elementType"]!.GetValue<string>() == "text");
                switch (kind)
                {
                    case "canvas-huge": p["CanvasWidth"] = 100_000; break;
                    case "canvas-negative": p["CanvasHeight"] = -720; break;
                    case "position-far": text!["Position"]!["X"] = 5_000_000; break;
                    case "position-overflow": text!["Position"]!["Y"] = JsonNode.Parse("1e39"); break;
                    case "size-negative": text!["Size"]!["X"] = -5; break;
                    case "font-huge": text!["FontSize"] = 5000; break;
                    case "rotation-overflow": elements!.First(e => e!["elementType"]!.GetValue<string>() == "image")!["RotationDegrees"] = JsonNode.Parse("3.5e38"); break;
                    case "color-overflow": text!["Color"]!["X"] = 1e20; break;
                    case "text-too-long": text!["Text"] = new string('a', TextProfileElement.MaxTextLength + 1); break;
                    case "name-too-long": text!["Name"] = new string('n', ProfileElement.MaxNameLength + 1); break;
                    case "duplicate-element-id": elements!.Add(text!.DeepClone()); break;
                    case "empty-element-id": text!["Id"] = Guid.Empty.ToString(); break;
                    case "element-not-object": elements!.Add(42); break;
                    case "elements-not-array": p["Elements"] = new JsonObject(); break;
                    case "zindex-overflow": text!["ZIndex"] = JsonNode.Parse("99999999999"); break;
                    case "unknown-type-name": text!["$type"] = "System.Diagnostics.Process, System"; text["elementType"] = "System.Diagnostics.Process"; text["Id"] = Guid.NewGuid().ToString(); text["Size"]!["X"] = -1; break;
                    case "level-out-of-range": p["BasicPlate"] = new JsonObject { ["Level"] = 100_000 }; break;
                    case "too-many-playstyles": p["BasicPlate"] = new JsonObject { ["Playstyles"] = new JsonArray("a", "b", "c", "d", "e", "f", "g") }; break;
                    case "active-hours": p["BasicPlate"] = new JsonObject { ["ActiveHours"] = new JsonObject { ["StartMinutes"] = -30 } }; break;
                }
            });
        });

        if (kind == "unknown-type-name")
        {
            // Not a known element type: kept as inert data (never instantiated), so the Plate is
            // otherwise importable — and nothing named by the data was ever created.
            using var staged = setup.Packages.Inspect(path);
            Assert.Equal(PackageCompatibility.SupportedWithWarnings, staged.Compatibility);
            Assert.Equal(1, staged.PreviewDocument!.UnsupportedElementCount);
            Assert.DoesNotContain(staged.PreviewDocument.Elements, e => e.GetType().Namespace != typeof(ProfileElement).Namespace);
            return;
        }

        await setup.AssertRefusedAsync(path, PackageErrorCode.ProfileInvalid, PackageErrorCode.PackageTooLarge);
    }

    [Fact]
    public async Task MalformedProfileJson_IsRefused()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => PackageFiles.Entry(entries, PackagePaths.ProfilePath).Bytes = "{\"Name\": \"Small\", "u8.ToArray()),
            PackageErrorCode.ProfileInvalid);
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public async Task AFailedPackage_DoesNotAffectLaterValidImports()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssertRefusedAsync(setup.Craft(entries => entries.Add(Text("../evil.txt"))), PackageErrorCode.UnsafePath);

        using var staged = setup.Packages.Inspect(setup.ValidPath);
        var result = await setup.Packages.ImportAsync(staged);
        Assert.True(result.Succeeded);
        Assert.Equal(setup.PlateId, setup.Library.GetActivePlateId(PackageFixture.PrivateContentId));
    }

    [Fact]
    public async Task NothingIsEverWrittenOutsideStaging_DuringValidation()
    {
        using var setup = await Setup.CreateAsync();
        var hostile = setup.Craft(entries =>
        {
            entries.Add(Text("../escaped.txt"));
            entries.Add(Text("..\\escaped2.txt"));
        });
        var parent = Path.GetDirectoryName(setup.Fixture.ExportDirectory)!;

        await setup.AssertRefusedAsync(hostile, PackageErrorCode.UnsafePath);

        Assert.False(File.Exists(Path.Combine(parent, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(parent, "escaped2.txt")));
        Assert.False(File.Exists(Path.Combine(setup.Fixture.Paths.Root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(setup.Fixture.Paths.PackageStagingDirectory, "escaped.txt")));
    }
}
