using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Export → validate → import as a new Plate, and everything that must hold across it.</summary>
public class PackageRoundTripTests
{
    [Fact]
    public async Task RoundTrip_ImportsAnIndependentPlateWithTheSameCreativeState()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, portrait, background, extra) = await fixture.CreateRichPlateAsync(library);
        var bindingBefore = fixture.Library.ReadBindingJson(PackageFixture.PrivateContentId);

        var path = fixture.Export(packages, plateId);

        using var staged = packages.Inspect(path);
        Assert.Equal(PackageCompatibility.Supported, staged.Compatibility);
        Assert.True(staged.CanImport);
        Assert.Equal("Traveler's Plate", staged.Summary!.PlateName);
        Assert.Equal(3, staged.Summary.AssetCount);
        Assert.Equal(PackagePolicy.CurrentFormatVersion, staged.Summary.FormatVersion);

        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        // A new identity, never the source's.
        Assert.NotEqual(plateId, result.PlateId);
        Assert.NotEqual(Guid.Empty, result.PlateId);
        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.Equal(result.PlateId, library.GetOrderedPlates()[0].PlateId);

        // The same creative state: identical JSON once identity and image ids are set aside.
        var original = PlateDocuments.Materialize(JsonNode.Parse(library.GetSavedJsonForExport(plateId).Json)!.AsObject());
        var imported = library.OpenDocumentForEditing(result.PlateId);
        var map = new Dictionary<Guid, Guid>
        {
            [ImageOf(imported, original, portrait)] = portrait,
            [imported.Background!.ImageAssetId!.Value] = background,
            [ImageOf(imported, original, extra)] = extra,
        };
        Assert.DoesNotContain(portrait, map.Keys);
        Assert.DoesNotContain(background, map.Keys);
        Assert.DoesNotContain(extra, map.Keys);

        var importedJson = PlateDocuments.ToJson(imported);
        PackageAssetIds.Remap(importedJson, map);
        JsonAssert.EqualExcept(PlateDocuments.ToJson(original).ToJsonString(), importedJson.ToJsonString(),
            "ProfileId", "Revision", "CreatedAtUtc", "UpdatedAtUtc", "OwnerContentId");

        // Every image the new Plate uses exists, byte-for-byte what the original uses.
        foreach (var (newId, oldId) in map)
        {
            var newPath = fixture.Assets.ResolveAssetPath(newId);
            Assert.NotNull(newPath);
            Assert.Equal(File.ReadAllBytes(fixture.Assets.ResolveAssetPath(oldId)!), File.ReadAllBytes(newPath));
        }

        // Content checks a player would see.
        Assert.Equal(ProfileThemePresets.All[3].Id, imported.BasicPlate!.ThemeId);
        Assert.Equal(ProfileBackgroundTexture.Honeycomb, imported.Background.Texture);
        Assert.Equal(["Casual", "Roleplay"], imported.BasicPlate.Playstyles);
        Assert.Contains(imported.Elements.OfType<TextProfileElement>(), t => t.Text.StartsWith("Advanced-only caption", StringComparison.Ordinal) && t.Role == ProfileElementRole.None);

        // Bindings and the Active Plate untouched; the import belongs to no one.
        Assert.Equal(bindingBefore, fixture.Library.ReadBindingJson(PackageFixture.PrivateContentId));
        Assert.Equal(plateId, library.GetActivePlateId(PackageFixture.PrivateContentId));
        var summary = library.FindPlate(result.PlateId)!;
        Assert.Empty(summary.ActiveForContentIds);
        Assert.Empty(summary.CharacterNames);
        Assert.Equal(0UL, imported.OwnerContentId);
        Assert.Equal(0, imported.Revision);
        Assert.Equal(fixture.Clock.Now, imported.CreatedAtUtc);

        // It survives a restart like any other Plate.
        var reloaded = await fixture.Library.LoadAsync();
        Assert.True(reloaded.FindPlate(result.PlateId)!.IsReady);
        staged.Dispose();
        Assert.True(fixture.StagingIsEmpty);
    }

    /// <summary>The asset the imported element with the same element id as the original's <paramref name="oldAsset"/> element uses.</summary>
    private static Guid ImageOf(ProfileDocument imported, ProfileDocument original, Guid oldAsset)
    {
        var elementId = original.Elements.OfType<ImageProfileElement>().Single(e => e.AssetId == oldAsset).Id;
        return imported.Elements.OfType<ImageProfileElement>().Single(e => e.Id == elementId).AssetId;
    }

    [Fact]
    public async Task Staging_IsRemovedAfterImportAndAfterCancel()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);

        var cancelled = packages.Inspect(path);
        Assert.True(Directory.Exists(cancelled.StagingDirectory));
        cancelled.Dispose();
        Assert.False(Directory.Exists(cancelled.StagingDirectory));

        var imported = packages.Inspect(path);
        Assert.True((await packages.ImportAsync(imported)).Succeeded);
        imported.Dispose();
        Assert.True(fixture.StagingIsEmpty);
        Assert.False(imported.CanImport);
    }

    [Fact]
    public async Task Export_ContainsNoPrivateLocalIdentifiers()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, portrait, background, extra) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);

        var everything =Encoding.UTF8.GetString(PackageFiles.Read(path).SelectMany(e => Encoding.UTF8.GetBytes(e.Name + "\n").Concat(e.Bytes)).ToArray());
        var forbidden = new[]
        {
            PackageFixture.PrivateContentId.ToString(),
            PackageFixture.PrivateCharacterName,
            PackageFixture.PrivateWorld,
            plateId.ToString(),
            plateId.ToString("N"),
            portrait.ToString(),
            portrait.ToString("N"),
            background.ToString(),
            background.ToString("N"),
            extra.ToString(),
            extra.ToString("N"),
            fixture.Library.Root,
            fixture.Library.Root.Replace('\\', '/'),
            fixture.SourceDirectory,
            "portrait.png",
            "background.jpg",
            "sticker.webp",
            "\"OwnerContentId\"",
            "\"Revision\"",
            "\"ProfileId\"",
            "\"CreatedAtUtc\"",
            "\"UpdatedAtUtc\"",
            "ActivePlateId",
            "ContentId",
            "OrderedPlateIds",
            "Trash",
            "thumbnails",
        };

        foreach (var text in forbidden)
        {
            Assert.DoesNotContain(text, everything, StringComparison.OrdinalIgnoreCase);
        }

        // Authored visible content stays.
        Assert.Contains("Visible Hero", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_PackagesIdenticalImagesOnce_AndStillImportsBothReferences()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var bytes = TestImages.Png(30, 30);
        var first = fixture.AddImage(bytes, "one.png");
        var second = fixture.AddImage(bytes, "two.png");
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Twins");
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Elements.Add(new ImageProfileElement { AssetId = first, Size = new System.Numerics.Vector2(30, 30) });
        document.Elements.Add(new ImageProfileElement { AssetId = second, Size = new System.Numerics.Vector2(30, 30), ZIndex = 1 });
        await library.SavePlateDocumentAsync(document);

        var path = fixture.Export(packages, created.PlateId);
        Assert.Single(PackageFiles.Read(path), e => e.Name.StartsWith(PackagePaths.AssetsFolder, StringComparison.Ordinal));

        using var staged = packages.Inspect(path);
        var result = await packages.ImportAsync(staged);
        var images = library.OpenDocumentForEditing(result.PlateId).Elements.OfType<ImageProfileElement>().ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal(images[0].AssetId, images[1].AssetId);
        Assert.NotNull(fixture.Assets.ResolveAssetPath(images[0].AssetId));
    }

    [Fact]
    public async Task Export_StripsLegacyOwnerContentId()
    {
        using var fixture = new PackageFixture();
        var legacyId = Guid.NewGuid();
        fixture.Library.WritePlateJson(legacyId, LegacyData.VersionOneDocument(legacyId, PackageFixture.PrivateContentId));
        var portrait = fixture.AddImage(TestImages.Png(40, 80));
        var background = fixture.AddImage(TestImages.Png(160, 90));
        var json = fixture.Library.ReadPlateJson(legacyId)
            .Replace(LegacyData.PortraitAsset.ToString(), portrait.ToString())
            .Replace(LegacyData.BackgroundAsset.ToString(), background.ToString());
        fixture.Library.WritePlateJson(legacyId, json);

        var (_, packages) = await fixture.LoadAsync();
        var path = fixture.Export(packages, legacyId);

        var profile = Encoding.UTF8.GetString(PackageFiles.Entry(PackageFiles.Read(path), PackagePaths.ProfilePath).Bytes);
        Assert.DoesNotContain(PackageFixture.PrivateContentId.ToString(), profile, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerContentId", profile, StringComparison.Ordinal);

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, staged.DescribeForLog());
    }

    [Fact]
    public async Task Export_IsStructurallyDeterministic()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);

        var first = PackageFiles.Read(fixture.Export(packages, plateId, "a.aetherframe"));
        fixture.Clock.Tick(3600);
        var second = PackageFiles.Read(fixture.Export(packages, plateId, "b.aetherframe"));

        Assert.Equal(first.Select(e => e.Name), second.Select(e => e.Name));
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Bytes, second[i].Bytes);
        }

        // Fixed order: profile, then assets by id, then the manifest; the manifest lists assets by id.
        Assert.Equal(PackagePaths.ProfilePath, first[0].Name);
        Assert.Equal(PackagePaths.ManifestPath, first[^1].Name);
        var assetNames = first.Where(e => e.Name.StartsWith(PackagePaths.AssetsFolder, StringComparison.Ordinal)).Select(e => e.Name).ToList();
        Assert.Equal(assetNames.OrderBy(n => n, StringComparer.Ordinal), assetNames);
        var declared = PackageFiles.Json(PackageFiles.Entry(first, PackagePaths.ManifestPath))["assets"]!.AsArray().Select(a => a!["path"]!.GetValue<string>()).ToList();
        Assert.Equal(assetNames, declared);
    }

    [Fact]
    public async Task Manifest_DescribesThePackageWithHashesOfTheExactBytes()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var entries = PackageFiles.Read(fixture.Export(packages, plateId));
        var manifest = PackageFiles.Json(PackageFiles.Entry(entries, PackagePaths.ManifestPath));

        Assert.Equal("aetherframe.package", manifest["format"]!.GetValue<string>());
        Assert.Equal(1, manifest["formatVersion"]!.GetValue<int>());
        Assert.Equal(["plate"], manifest["requires"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Traveler's Plate", manifest["plate"]!["name"]!.GetValue<string>());
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, manifest["plate"]!["schemaVersion"]!.GetValue<int>());
        Assert.Null(manifest["preview"]);

        var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
        Assert.Equal(PackageFiles.Sha256(profile.Bytes), manifest["profile"]!["sha256"]!.GetValue<string>());
        foreach (var asset in manifest["assets"]!.AsArray())
        {
            var entry = PackageFiles.Entry(entries, asset!["path"]!.GetValue<string>());
            Assert.Equal(PackageFiles.Sha256(entry.Bytes), asset["sha256"]!.GetValue<string>());
            Assert.Equal(entry.Bytes.Length, asset["byteLength"]!.GetValue<long>());
        }

        // Nothing resembling a URL, path, or instruction anywhere in it.
        var text = Encoding.UTF8.GetString(PackageFiles.Entry(entries, PackagePaths.ManifestPath).Bytes);
        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_MissingImage_FailsWithoutWritingAnything()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, portrait, _, _) = await fixture.CreateRichPlateAsync(library);
        File.Delete(fixture.Assets.ResolveAssetPath(portrait)!);

        var destination = Path.Combine(fixture.ExportDirectory, "broken.aetherframe");
        var result = packages.Export(plateId, destination, overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.AssetMissing, result.Errors[0].Code);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(fixture.ExportDirectory));
    }

    [Fact]
    public async Task Export_DamagedImage_Fails()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, portrait, _, _) = await fixture.CreateRichPlateAsync(library);
        var assetPath = fixture.Assets.ResolveAssetPath(portrait)!;
        File.WriteAllBytes(assetPath, File.ReadAllBytes(assetPath)[..^12]); // IEND cut off

        var result = packages.Export(plateId, Path.Combine(fixture.ExportDirectory, "x.aetherframe"), overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.ImageInvalid, result.Errors[0].Code);
    }

    [Fact]
    public async Task Export_NeverReplacesAnExistingFileUnlessConfirmed()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var destination = Path.Combine(fixture.ExportDirectory, "taken.aetherframe");
        File.WriteAllText(destination, "someone else's file");

        var refused = packages.Export(plateId, destination, overwrite: false);
        Assert.False(refused.Succeeded);
        Assert.Equal("someone else's file", File.ReadAllText(destination));

        var replaced = packages.Export(plateId, destination, overwrite: true);
        Assert.True(replaced.Succeeded);
        using var staged = packages.Inspect(destination);
        Assert.True(staged.CanImport);
    }

    [Fact]
    public async Task Export_RequiresTheAetherframeExtension()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);

        var result = packages.Export(plateId, Path.Combine(fixture.ExportDirectory, "plate.zip"), overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.GetFiles(fixture.ExportDirectory));
    }

    [Fact]
    public async Task Import_Twice_GivesTwoIndependentPlatesWithTheSameName()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);

        using var first = packages.Inspect(path);
        using var second = packages.Inspect(path);
        var a = await packages.ImportAsync(first);
        var b = await packages.ImportAsync(second);

        Assert.NotEqual(a.PlateId, b.PlateId);
        Assert.Equal(3, library.GetOrderedPlates().Count);
        Assert.All(library.GetOrderedPlates(), p => Assert.Equal("Traveler's Plate", p.DisplayName));

        var aAssets = library.OpenDocumentForEditing(a.PlateId).Elements.OfType<ImageProfileElement>().Select(e => e.AssetId).ToHashSet();
        var bAssets = library.OpenDocumentForEditing(b.PlateId).Elements.OfType<ImageProfileElement>().Select(e => e.AssetId).ToHashSet();
        Assert.Empty(aAssets.Intersect(bAssets));
    }

    [Fact]
    public async Task Import_ANewPlateIsNeverActiveNorBound_EvenAsTheOnlyPlate()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);

        // A second installation with no Plates and a character that has no Active Plate yet.
        using var other = new PackageFixture();
        var (otherLibrary, otherPackages) = await other.LoadAsync();
        using var staged = otherPackages.Inspect(path);
        var result = await otherPackages.ImportAsync(staged);

        Assert.True(result.Succeeded);
        Assert.Null(otherLibrary.GetActivePlateId(PackageFixture.PrivateContentId));
        Assert.False(Directory.Exists(other.Paths.CharactersDirectory) && Directory.GetFiles(other.Paths.CharactersDirectory).Length > 0);
    }

    [Fact]
    public async Task Import_AssetIdsCollidingWithLocalImages_NeverTouchTheLocalImages()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, portrait, background, extra) = await fixture.CreateRichPlateAsync(library);

        // An unrelated local image, and a package that claims its id for different bytes.
        var victim = fixture.AddImage(TestImages.Png(11, 11), "victim.png");
        var victimPath = fixture.Assets.ResolveAssetPath(victim)!;
        var victimBytes = File.ReadAllBytes(victimPath);

        var hostile = PackageFiles.Rewrite(fixture.Export(packages, plateId), entries =>
        {
            // The package's PNG (the portrait) is re-labelled with the victim's local id throughout.
            var manifest = PackageFiles.Json(PackageFiles.Entry(entries, PackagePaths.ManifestPath));
            var packageId = Guid.ParseExact(manifest["assets"]!.AsArray().Single(a => a!["mediaType"]!.GetValue<string>() == "image/png")!["id"]!.GetValue<string>(), "N");
            PackageFiles.Entry(entries, PackagePaths.AssetPath(packageId, ".png")).Name = PackagePaths.AssetPath(victim, ".png");
            PackageFiles.EditManifest(entries, m =>
            {
                var asset = m["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == packageId.ToString("N"))!;
                asset["id"] = victim.ToString("N");
                asset["path"] = PackagePaths.AssetPath(victim, ".png");
            });
            var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
            profile.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(profile.Bytes).Replace(packageId.ToString(), victim.ToString()));
        });

        using var staged = packages.Inspect(hostile);
        Assert.True(staged.CanImport, staged.DescribeForLog());
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded);

        // The local image is untouched and still where it was; the import got its own new ids.
        Assert.Equal(victimBytes, File.ReadAllBytes(victimPath));
        Assert.Equal(victimPath, fixture.Assets.ResolveAssetPath(victim));
        var importedAssets = library.OpenDocumentForEditing(result.PlateId).Elements.OfType<ImageProfileElement>().Select(e => e.AssetId).ToList();
        Assert.DoesNotContain(victim, importedAssets);
        Assert.DoesNotContain(portrait, importedAssets);
        Assert.DoesNotContain(extra, importedAssets);
        Assert.NotEqual(background, library.OpenDocumentForEditing(result.PlateId).Background!.ImageAssetId);

        // And the original Plate's images are untouched too.
        foreach (var id in new[] { portrait, background, extra })
        {
            Assert.NotNull(fixture.Assets.ResolveAssetPath(id));
        }
    }

    [Fact]
    public async Task ForwardCompatibleData_SurvivesExportAndImport()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var hidden = fixture.AddImage(TestImages.Png(20, 20), "hidden.png");
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Future Plate");
        var saved = JsonNode.Parse(library.GetSavedJsonForExport(created.PlateId).Json)!.AsObject();

        saved["FutureTopLevel"] = new JsonObject { ["mood"] = "sparkly", ["weights"] = new JsonArray(1, 2, 3) };
        saved["Elements"]!.AsArray().Add(JsonNode.Parse("""
            { "elementType": "text", "Id": "5b0b0b0b-0000-0000-0000-000000000001", "Text": "known", "FontSize": 20,
              "Position": { "X": 10, "Y": 10 }, "Size": { "X": 200, "Y": 40 }, "FutureElementField": { "glow": 0.5 } }
            """));
        saved["Elements"]!.AsArray().Add(JsonNode.Parse($$"""
            { "elementType": "hologram", "Id": "5b0b0b0b-0000-0000-0000-000000000002", "Source": "{{hidden:N}}", "Shimmer": [1, 2] }
            """));
        fixture.Library.WritePlateJson(created.PlateId, saved.ToJsonString());

        var reloaded = await fixture.Library.LoadAsync();
        var reloadedPackages = fixture.CreatePackages(reloaded);
        var path = fixture.Export(reloadedPackages, created.PlateId);

        using var staged = reloadedPackages.Inspect(path);
        Assert.Equal(PackageCompatibility.SupportedWithWarnings, staged.Compatibility);
        Assert.Contains(staged.Diagnostics.Warnings, w => w.Code == PackageWarningCode.UnsupportedElements);
        Assert.Equal(1, staged.Summary!.AssetCount);

        var result = await reloadedPackages.ImportAsync(staged);
        Assert.True(result.Succeeded);

        var imported = JsonNode.Parse(reloaded.GetSavedJsonForExport(result.PlateId).Json)!.AsObject();
        Assert.Equal("sparkly", imported["FutureTopLevel"]!["mood"]!.GetValue<string>());
        var elements = imported["Elements"]!.AsArray();
        Assert.Equal(0.5, elements.Single(e => e!["Text"]?.GetValue<string>() == "known")!["FutureElementField"]!["glow"]!.GetValue<double>());

        // The unknown element is kept, and the image only it references came along under a new id.
        var hologram = elements.Single(e => e!["elementType"]!.GetValue<string>() == "hologram")!;
        Assert.Equal(new JsonArray(1, 2).ToJsonString(), hologram["Shimmer"]!.ToJsonString());
        var newSource = Guid.ParseExact(hologram["Source"]!.GetValue<string>(), "N");
        Assert.NotEqual(hidden, newSource);
        Assert.NotNull(fixture.Assets.ResolveAssetPath(newSource));
        Assert.True(reloaded.FindPlate(result.PlateId)!.HasUnsupportedElements);
    }

    [Fact]
    public async Task OlderPlateSchema_InACurrentPackage_IsMigratedByTheExistingSchemaChain()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Old Style");
        var path = fixture.Export(packages, created.PlateId);

        // A version-1 (pre-canvas-size) document inside a format-1 container.
        var legacy = JsonNode.Parse(LegacyData.VersionOneDocument(Guid.NewGuid(), 0, "Old Style"))!.AsObject();
        legacy.Remove("BackgroundAssetId");
        legacy["Elements"]!.AsArray().RemoveAt(1);
        var older = PackageFiles.Rewrite(path, entries =>
        {
            PackageFiles.SetJson(PackageFiles.Entry(entries, PackagePaths.ProfilePath), legacy);
            PackageFiles.EditManifest(entries, m => m["plate"]!["schemaVersion"] = 1);
        });

        using var staged = packages.Inspect(older);
        Assert.True(staged.CanImport, staged.DescribeForLog());
        Assert.Equal(1, staged.Summary!.PlateSchemaVersion);
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, staged.Summary.CanvasWidth);

        var result = await packages.ImportAsync(staged);
        var imported = JsonNode.Parse(library.GetSavedJsonForExport(result.PlateId).Json)!.AsObject();
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, imported["Version"]!.GetValue<int>());
        Assert.Equal("Hello from the past", library.OpenDocumentForEditing(result.PlateId).Elements.OfType<TextProfileElement>().Single().Text);
    }

    [Fact]
    public async Task NewerPackageFormat_IsUnsupported_EvenWithAnOlderPlateInside()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var newer = PackageFiles.Rewrite(fixture.Export(packages, created.PlateId), entries =>
        {
            PackageFiles.EditManifest(entries, m => m["formatVersion"] = 2);
            entries.Add(new ZipSpec("templates/future.json", "{}"u8.ToArray()));
        });

        using var staged = packages.Inspect(newer);

        Assert.Equal(PackageCompatibility.Unsupported, staged.Compatibility);
        Assert.Equal(PackageErrorCode.UnsupportedVersion, Assert.Single(staged.Diagnostics.Errors).Code);
        Assert.False(staged.CanImport);
    }

    [Fact]
    public async Task NewerPlateSchema_InACurrentPackage_IsUnsupported()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var newer = PackageFiles.Rewrite(fixture.Export(packages, created.PlateId), entries =>
        {
            PackageFiles.EditProfile(entries, p => p["Version"] = ProfileDocument.CurrentSchemaVersion + 1);
            PackageFiles.EditManifest(entries, m => m["plate"]!["schemaVersion"] = ProfileDocument.CurrentSchemaVersion + 1);
        });

        using var staged = packages.Inspect(newer);

        Assert.Equal(PackageCompatibility.Unsupported, staged.Compatibility);
        Assert.False(staged.CanImport);
    }

    [Fact]
    public async Task UnknownRequiredCapability_IsUnsupported_ButUnknownOptionalFieldsAreHarmless()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var path = fixture.Export(packages, created.PlateId);

        var needsMore = PackageFiles.Rewrite(path, entries => PackageFiles.EditManifest(entries, m => m["requires"]!.AsArray().Add("theme-kit")));
        using (var staged = packages.Inspect(needsMore))
        {
            Assert.Equal(PackageCompatibility.Unsupported, staged.Compatibility);
            Assert.Equal(PackageErrorCode.UnsupportedCapability, Assert.Single(staged.Diagnostics.Errors).Code);
        }

        var extraMetadata = PackageFiles.Rewrite(path, entries => PackageFiles.EditManifest(entries, m =>
        {
            m["authorNote"] = "made with love";
            m["plate"]!["futureHint"] = new JsonObject { ["x"] = 1 };
        }));
        using (var staged = packages.Inspect(extraMetadata))
        {
            Assert.Equal(PackageCompatibility.Supported, staged.Compatibility);
        }
    }

    [Fact]
    public async Task UnknownThemeAndPattern_AreWarnings_AndKeptExactly()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Themed", new PlateStarterContent(null));
        var edited = PackageFiles.Rewrite(fixture.Export(packages, created.PlateId), entries => PackageFiles.EditProfile(entries, p =>
        {
            p["BasicPlate"]!["ThemeName"] = "future-theme-99"; // ThemeId's JSON name
            p["Background"]!["Texture"] = 999;
        }));

        using var staged = packages.Inspect(edited);
        Assert.Equal(PackageCompatibility.SupportedWithWarnings, staged.Compatibility);
        Assert.Contains(staged.Diagnostics.Warnings, w => w.Code == PackageWarningCode.UnrecognizedSetting);

        var result = await packages.ImportAsync(staged);
        var imported = library.OpenDocumentForEditing(result.PlateId);
        Assert.Equal("future-theme-99", imported.BasicPlate!.ThemeId);
        Assert.Equal((ProfileBackgroundTexture)999, imported.Background!.Texture);
    }

    [Fact]
    public async Task CommitFailure_RollsBackOnlyTheImportsOwnImages()
    {
        var store = new FaultInjectingStore();
        using var fixture = new PackageFixture(store: store);
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var path = fixture.Export(packages, plateId);
        var before = fixture.SnapshotInstallation();
        var plateCount = library.GetOrderedPlates().Count;

        store.FailWrite = p => p.StartsWith(fixture.Paths.PlatesDirectory, StringComparison.OrdinalIgnoreCase);
        using var staged = packages.Inspect(path);
        var result = await packages.ImportAsync(staged);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageErrorCode.CommitFailed, result.Error!.Code);
        Assert.Equal(plateCount, library.GetOrderedPlates().Count);

        // Every file that existed is unchanged, and nothing new remains (images rolled back).
        Assert.Equal(before, fixture.SnapshotInstallation());
    }

    [Fact]
    public async Task ImportedImages_AreProtectedFromCleanupForTheSession()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        using var staged = packages.Inspect(fixture.Export(packages, plateId));

        await packages.ImportAsync(staged);

        Assert.All(staged.Assets, a => Assert.Contains(a.LocalAssetId, fixture.Assets.ImportedThisSession));
    }

    [Fact]
    public async Task PreviewDocument_IsExactlyWhatGetsImported()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        using var staged = packages.Inspect(fixture.Export(packages, plateId));
        var previewJson = PlateDocuments.ToJson(staged.PreviewDocument!);

        var result = await packages.ImportAsync(staged);

        JsonAssert.EqualExcept(previewJson.ToJsonString(), PlateDocuments.ToJson(library.OpenDocumentForEditing(result.PlateId)).ToJsonString(),
            "ProfileId", "Revision", "CreatedAtUtc", "UpdatedAtUtc", "OwnerContentId");
        Assert.All(staged.Assets, a => Assert.StartsWith(staged.StagingDirectory, a.StagedPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task VisibleUrlText_IsJustText()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        using var staged = packages.Inspect(fixture.Export(packages, plateId));

        // A link typed on the Plate is shown as text; nothing about it is fetched or resolved.
        Assert.Equal(PackageCompatibility.Supported, staged.Compatibility);
        Assert.Contains(staged.PreviewDocument!.Elements.OfType<TextProfileElement>(), t => t.Text.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_WhenValid_IsIncludedAndDeclared()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var thumbnail = TestImages.Write(fixture.SourceDirectory, "thumb.png", TestImages.Png(320, 180));
        var destination = Path.Combine(fixture.ExportDirectory, "with-preview.aetherframe");

        var result = packages.Export(plateId, destination, overwrite: false, previewPngPath: thumbnail);

        Assert.True(result.Succeeded);
        Assert.True(result.IncludedPreview);
        using var staged = packages.Inspect(destination);
        Assert.Equal(PackageCompatibility.Supported, staged.Compatibility);
        Assert.True(staged.Summary!.HasPreview);
        Assert.NotNull(staged.PreviewImagePath);
    }

    [Fact]
    public async Task Preview_WhenUnavailable_IsSimplyLeftOut()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var notAnImage = TestImages.Write(fixture.SourceDirectory, "thumb.png", "not a png"u8.ToArray());
        var destination = Path.Combine(fixture.ExportDirectory, "no-preview.aetherframe");

        var result = packages.Export(plateId, destination, overwrite: false, previewPngPath: notAnImage);

        Assert.True(result.Succeeded);
        Assert.False(result.IncludedPreview);
        Assert.DoesNotContain(PackageFiles.Read(destination), e => e.Name == PackagePaths.PreviewPath);
    }
}
