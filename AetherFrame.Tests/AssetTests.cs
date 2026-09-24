using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

public class ImageSafetyTests
{
    [Fact]
    public void Sniff_UsesContent_NotExtension()
    {
        Assert.Equal(DetectedImageFormat.Png, ImageSafety.Sniff(TestImages.Png(1, 1)));
        Assert.Equal(DetectedImageFormat.Jpeg, ImageSafety.Sniff(TestImages.Jpeg(1, 1)));
        Assert.Equal(DetectedImageFormat.WebP, ImageSafety.Sniff(TestImages.WebPExtended(1, 1)));
        Assert.Equal(DetectedImageFormat.Unknown, ImageSafety.Sniff("GIF89a......"u8));
        Assert.Equal(DetectedImageFormat.Unknown, ImageSafety.Sniff([]));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void Inspect_ReadsDimensions(string kind)
    {
        using var dir = new TempDirectory();
        var bytes = kind switch
        {
            "png" => TestImages.Png(640, 360),
            "jpeg" => TestImages.Jpeg(640, 360),
            _ => TestImages.WebPExtended(640, 360),
        };
        var path = TestImages.Write(dir.Path, "image.bin", bytes);

        var inspection = ImageSafety.Inspect(path)!;

        Assert.Equal(640, inspection.Width);
        Assert.Equal(360, inspection.Height);
        Assert.Equal(1, inspection.FrameCount);
        Assert.Equal(bytes.Length, inspection.ByteLength);
        Assert.Null(ImageSafety.Validate(inspection));
    }

    [Fact]
    public void AnimatedImages_ReportTheirFrameCount()
    {
        using var dir = new TempDirectory();

        var apng = ImageSafety.Inspect(TestImages.Write(dir.Path, "a.png", TestImages.Png(10, 10, animationFrames: 12)))!;
        var webp = ImageSafety.Inspect(TestImages.Write(dir.Path, "a.webp", TestImages.WebPExtended(10, 10, animationFrames: 5)))!;

        Assert.Equal(12, apng.FrameCount);
        Assert.Equal(5, webp.FrameCount);
        Assert.Null(ImageSafety.Validate(apng));
    }

    [Fact]
    public void TooManyFrames_IsRejected()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "a.png", TestImages.Png(10, 10, animationFrames: ImageSafety.MaxFrameCount + 1)));

        Assert.NotNull(ImageSafety.Validate(inspection));
    }

    [Fact]
    public void OverDimensionLimit_IsRejected()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "wide.png", TestImages.Png(ImageSafety.MaxDimension + 1, 10)));

        Assert.Contains("pixels per side", ImageSafety.Validate(inspection));
    }

    [Fact]
    public void OverPixelCountLimit_IsRejected_EvenWithinDimensionLimit()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "big.png", TestImages.Png(ImageSafety.MaxDimension, ImageSafety.MaxDimension)))!;

        Assert.True(inspection.EstimatedDecodedBytes > ImageSafety.MaxDecodedBytes);
        Assert.Contains("megapixels", ImageSafety.Validate(inspection));
    }

    [Fact]
    public void OverFileSizeLimit_IsRejected()
    {
        using var dir = new TempDirectory();
        var path = TestImages.Write(dir.Path, "huge.png", TestImages.Png(100, 100));
        using (var stream = new FileStream(path, FileMode.Open))
        {
            stream.SetLength(ImageSafety.MaxFileBytes + 1);
        }

        Assert.Contains("too large", ImageSafety.Validate(ImageSafety.Inspect(path)));
    }

    [Fact]
    public void NotAnImage_IsRejected()
    {
        using var dir = new TempDirectory();
        var path = TestImages.Write(dir.Path, "fake.png", "this is text, not a picture"u8.ToArray());

        Assert.Contains("isn't a PNG, JPEG, or WebP", ImageSafety.Validate(ImageSafety.Inspect(path)));
    }

    [Fact]
    public void TruncatedHeader_IsRejected()
    {
        using var dir = new TempDirectory();
        var path = TestImages.Write(dir.Path, "cut.png", TestImages.Png(10, 10)[..12]);

        Assert.NotNull(ImageSafety.Validate(ImageSafety.Inspect(path)));
    }

    [Fact]
    public void UnsupportedByDecoder_IsRejected()
    {
        using var dir = new TempDirectory();
        var inspection = ImageSafety.Inspect(TestImages.Write(dir.Path, "a.webp", TestImages.WebPExtended(10, 10)));

        Assert.Contains("WEBP", ImageSafety.Validate(inspection, ext => ext != ".webp"));
    }
}

public class AssetImportTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();

        internal Fixture(Func<string, bool>? decoder = null)
        {
            Paths = new PlateStoragePaths(directory.Path);
            Metadata = new AssetMetadataStore(Paths.AssetMetadataDirectory);
            Storage = new AssetStorageService(Paths.AssetsDirectory, Paths.AssetStagingDirectory, Metadata, decoder);
            SourceDirectory = Path.Combine(directory.Path, "user-files", "My Pictures");
        }

        internal PlateStoragePaths Paths { get; }

        internal AssetMetadataStore Metadata { get; }

        internal AssetStorageService Storage { get; }

        internal string SourceDirectory { get; }

        public void Dispose() => directory.Dispose();
    }

    [Fact]
    public void Import_StoresUnderContentExtension_AndWritesMetadata()
    {
        using var fixture = new Fixture();
        var bytes = TestImages.Png(300, 200);
        var source = TestImages.Write(fixture.SourceDirectory, "portrait.JPG", bytes);

        var assetId = fixture.Storage.ImportImage(source);

        var stored = fixture.Storage.ResolveAssetPath(assetId)!;
        Assert.EndsWith(".png", stored, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(stored));

        var metadata = fixture.Metadata.TryLoad(assetId)!;
        Assert.Equal(assetId, metadata.AssetId);
        Assert.Equal("portrait.JPG", metadata.OriginalFileName);
        Assert.Equal("image/png", metadata.MediaType);
        Assert.Equal(bytes.Length, metadata.ByteLength);
        Assert.Equal(300, metadata.PixelWidth);
        Assert.Equal(200, metadata.PixelHeight);
        Assert.Equal(1, metadata.FrameCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), metadata.Sha256);
        Assert.Contains(assetId, fixture.Storage.ImportedThisSession);
    }

    [Fact]
    public void Import_NeverPersistsTheSourcePath()
    {
        using var fixture = new Fixture();
        var source = TestImages.Write(fixture.SourceDirectory, "secret-folder-name.png", TestImages.Png(4, 4));

        var assetId = fixture.Storage.ImportImage(source);

        var json = File.ReadAllText(fixture.Metadata.GetPath(assetId));
        Assert.DoesNotContain("My Pictures", json, StringComparison.Ordinal);
        Assert.DoesNotContain("user-files", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RejectsNonImages_WithoutLeavingFiles()
    {
        using var fixture = new Fixture();
        var source = TestImages.Write(fixture.SourceDirectory, "trojan.png", "MZ not an image"u8.ToArray());

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Storage.ImportImage(source));

        Assert.Contains("isn't a PNG", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Storage.ListAssets());
        Assert.False(Directory.Exists(fixture.Paths.AssetStagingDirectory) && Directory.GetFiles(fixture.Paths.AssetStagingDirectory).Length > 0);
    }

    [Fact]
    public void Import_RejectsOversizedImages()
    {
        using var fixture = new Fixture();
        var source = TestImages.Write(fixture.SourceDirectory, "bomb.png", TestImages.Png(20000, 20000));

        Assert.Throws<InvalidOperationException>(() => fixture.Storage.ImportImage(source));
        Assert.Empty(fixture.Storage.ListAssets());
    }

    [Fact]
    public void Import_RespectsTheDecoderSupportList()
    {
        using var fixture = new Fixture(decoder: ext => ext is ".png" or ".jpg");
        var webp = TestImages.Write(fixture.SourceDirectory, "a.webp", TestImages.WebPExtended(10, 10));
        var jpeg = TestImages.Write(fixture.SourceDirectory, "a.jpeg", TestImages.Jpeg(10, 10));

        Assert.Throws<InvalidOperationException>(() => fixture.Storage.ImportImage(webp));
        var jpegId = fixture.Storage.ImportImage(jpeg);
        Assert.EndsWith(".jpg", fixture.Storage.ResolveAssetPath(jpegId), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_MissingFile_Throws()
    {
        using var fixture = new Fixture();

        Assert.Throws<InvalidOperationException>(() => fixture.Storage.ImportImage(Path.Combine(fixture.SourceDirectory, "nope.png")));
    }

    [Fact]
    public void LegacyAssets_StillResolve_AndGetMetadataLazily()
    {
        using var fixture = new Fixture();
        var legacyId = Guid.NewGuid();
        var bytes = TestImages.Jpeg(64, 32);
        var legacyPath = TestImages.Write(fixture.Paths.AssetsDirectory, legacyId.ToString("N") + ".jpeg", bytes);

        Assert.Equal(legacyPath, fixture.Storage.ResolveAssetPath(legacyId));
        Assert.Null(fixture.Metadata.TryLoad(legacyId));

        var created = fixture.Metadata.GetOrCreate(legacyId, legacyPath)!;
        var loaded = fixture.Metadata.TryLoad(legacyId)!;

        Assert.Equal("image/jpeg", created.MediaType);
        Assert.Equal(64, created.PixelWidth);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), created.Sha256);
        Assert.Equal(created.Sha256, loaded.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void Metadata_RoundTripsThroughJson()
    {
        var metadata = new AssetMetadata
        {
            AssetId = Guid.NewGuid(),
            OriginalFileName = "art.png",
            MediaType = "image/png",
            ByteLength = 1234,
            PixelWidth = 10,
            PixelHeight = 20,
            FrameCount = 3,
            Sha256 = new string('a', 64),
            CreatedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };

        var result = VersionedJson.Parse<AssetMetadata>(VersionedJson.Serialize(metadata), PersistenceSchemas.AssetMetadata);

        Assert.True(result.IsUsable);
        Assert.Equal(JsonSerializer.Serialize(metadata, JsonOptions.Default), JsonSerializer.Serialize(result.Value, JsonOptions.Default));
    }

    [Fact]
    public void Hash_IsConsistent_ForIdenticalContent_AndDiffersOtherwise()
    {
        using var dir = new TempDirectory();
        var a = TestImages.Write(dir.Path, "a.png", TestImages.Png(5, 5));
        var b = TestImages.Write(dir.Path, "b.png", TestImages.Png(5, 5));
        var c = TestImages.Write(dir.Path, "c.png", TestImages.Png(6, 5));

        Assert.Equal(AssetMetadataStore.ComputeSha256(a), AssetMetadataStore.ComputeSha256(b));
        Assert.NotEqual(AssetMetadataStore.ComputeSha256(a), AssetMetadataStore.ComputeSha256(c));
        Assert.Equal(64, AssetMetadataStore.ComputeSha256(a).Length);
    }

    [Fact]
    public void MetadataForAnotherAsset_IsNotTrusted()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        fixture.Metadata.Save(new AssetMetadata { AssetId = Guid.NewGuid() });
        File.Move(Directory.GetFiles(fixture.Paths.AssetMetadataDirectory).Single(), fixture.Metadata.GetPath(id));

        Assert.Null(fixture.Metadata.TryLoad(id));
    }
}

public class AssetReferenceTests
{
    [Fact]
    public void Scanner_FindsElementBackgroundAndLegacyReferences()
    {
        var image = Guid.NewGuid();
        var background = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "x", DateTime.UtcNow);
        document.Elements.Add(new ImageProfileElement { AssetId = image });
        document.Elements.Add(new TextProfileElement { Text = "no asset" });
        document.Background = new ProfileBackground { Mode = ProfileBackgroundMode.SolidColor, ImageAssetId = background };
        document.LegacyBackgroundAssetId = legacy;

        var found = AssetReferenceScanner.Collect([document]);

        Assert.Equal(new[] { image, background, legacy }.OrderBy(g => g), found.OrderBy(g => g));
    }

    [Fact]
    public async Task LibraryScan_IncludesTrashedPlates()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now), JsonOptions.Default));
        var library = await fixture.LoadAsync();
        await library.DeletePlateAsync(plateId);

        var scan = await library.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
        Assert.Contains(SampleDocuments.BackgroundAsset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task LibraryScan_IsIncomplete_WhenAnyPlateIsUnreadable()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePlateJson(Guid.NewGuid(), "{ broken");
        var library = await fixture.LoadAsync();

        var scan = await library.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.NotEmpty(scan.Problems);
    }
}

public class TemplateAssetReferenceTests
{
    [Fact]
    public async Task TemplateLibraryScan_IncludesTemplateDocumentAssetReferences()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now);
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();
        await templates.SaveAsTemplateAsync(plateId, "Rich Template");

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
        Assert.Contains(SampleDocuments.BackgroundAsset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task TemplateLibraryScan_IsIncomplete_WhenAnyTemplateIsUnreadable()
    {
        using var fixture = new TemplateLibraryFixture();
        var id = Guid.NewGuid();
        fixture.WriteTemplateJson(id, "{ broken");
        var templates = await fixture.LoadAsync();

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.False(scan.IsComplete);
        Assert.NotEmpty(scan.Problems);
    }

    [Fact]
    public async Task TemplateLibraryScan_IncludesTrashedTemplates()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now);
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();
        var templateId = await templates.SaveAsTemplateAsync(plateId, "Rich Template");
        await templates.DeleteTemplateAsync(templateId);

        var scan = await templates.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(SampleDocuments.ImageAsset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task CombinedScan_AssetReferencedOnlyByATemplate_IsNotUnreferenced_WhenUnionedWithPlateScan()
    {
        using var fixture = new TemplateLibraryFixture();

        // Built directly (not via SaveAsTemplateAsync), so this asset is never referenced by any
        // Plate — the only way to isolate a genuinely Template-only reference, since a Template
        // saved from a Plate is always also visible to the Plate scan (including via its trash).
        var templateOnlyAsset = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var document = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, fixture.Clock.Now, null);
        document.ProfileId = templateId;
        document.Elements.Add(new ImageProfileElement { AssetId = templateOnlyAsset });
        var documentJson = PlateDocuments.ToJson(document).ToJsonString(JsonOptions.Default);
        fixture.WriteTemplateJson(templateId, TemplateSamples.Envelope(templateId, "Template Only", documentJson));

        var templates = await fixture.LoadAsync();

        var plateOnlyScan = await fixture.PlateLibrary.ScanAssetReferencesAsync();
        var combined = await LiveAssetReferences.ComputeAsync(fixture.PlateLibrary, templates);

        Assert.DoesNotContain(templateOnlyAsset, plateOnlyScan.ReferencedAssetIds);
        Assert.Contains(templateOnlyAsset, combined.ReferencedAssetIds);
    }

    [Fact]
    public async Task CombinedScan_AssetReferencedOnlyByAPlate_IsNotUnreferenced_WhenUnionedWithTemplateScan()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich", fixture.Clock.Now);
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();
        // No Template at all — the asset is referenced only by the Plate.

        var combined = await LiveAssetReferences.ComputeAsync(fixture.PlateLibrary, templates);

        Assert.Contains(SampleDocuments.ImageAsset, combined.ReferencedAssetIds);
    }

    [Fact]
    public async Task CombinedScan_IsIncomplete_WhenEitherLibraryScanIsIncomplete()
    {
        using var fixture = new TemplateLibraryFixture();
        fixture.WriteTemplateJson(Guid.NewGuid(), "{ broken");
        var templates = await fixture.LoadAsync();

        var combined = await LiveAssetReferences.ComputeAsync(fixture.PlateLibrary, templates);

        Assert.False(combined.IsComplete);
    }

    /// <summary>
    /// Proves the premise behind sharing AssetIds across a Template and its instantiated Plates:
    /// replacing an image always imports a fresh asset (see <see cref="EditorSession.ReplaceImage"/>)
    /// and never overwrites the bytes behind an existing AssetId, so a Template and every Plate
    /// that shares its asset references stay independent even after one of them is edited.
    /// </summary>
    [Fact]
    public async Task ReplacingImageOnInstantiatedPlate_NeverAffectsTemplateOrSourcePlate()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var assetStorage = new AssetStorageService(fixture.Paths.AssetsDirectory, fixture.Paths.AssetStagingDirectory, new AssetMetadataStore(fixture.Paths.AssetMetadataDirectory));

        var sourceId = Guid.NewGuid();
        var originalPath = TestImages.Write(Path.Combine(fixture.Root, "src"), "original.png", TestImages.Png(4, 4));
        var originalAssetId = assetStorage.ImportImage(originalPath);

        var sourceDocument = PlateFactory.Create(PlateStartingLayout.Blank, sourceId, "Source", fixture.Clock.Now);
        sourceDocument.Elements.Add(new ImageProfileElement { AssetId = originalAssetId, Position = new(0, 0), Size = new(100, 100) });
        fixture.WritePlateJson(sourceId, JsonSerializer.Serialize(sourceDocument, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();

        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Image Template");
        var instantiated = await templates.InstantiateAsync(templateId, null);

        var profiles = new ProfileService(fixture.PlateLibrary);
        profiles.OpenPlate(instantiated.PlateId);
        var session = new EditorSession(profiles, assetStorage, new FakeImages(), fixture.Log, () => 1);
        session.SyncWithCurrentProfile();

        var elementId = profiles.CurrentProfile!.Elements.Single(e => e is ImageProfileElement).Id;
        var replacementPath = TestImages.Write(Path.Combine(fixture.Root, "src"), "replacement.png", TestImages.Png(4, 4));
        session.ReplaceImage(elementId, replacementPath);
        Assert.Null(session.ErrorMessage);

        await fixture.PlateLibrary.SavePlateDocumentAsync(profiles.CurrentProfile!);

        var newPlateAssetId = ((ImageProfileElement)fixture.PlateLibrary.OpenDocumentForEditing(instantiated.PlateId).Elements.Single()).AssetId;
        Assert.NotEqual(originalAssetId, newPlateAssetId);

        var templateAssetId = ((ImageProfileElement)templates.GetSavedDocument(templateId)!.Elements.Single()).AssetId;
        Assert.Equal(originalAssetId, templateAssetId);

        var sourcePlateAssetId = ((ImageProfileElement)fixture.PlateLibrary.OpenDocumentForEditing(sourceId).Elements.Single()).AssetId;
        Assert.Equal(originalAssetId, sourcePlateAssetId);

        Assert.NotNull(assetStorage.ResolveAssetPath(originalAssetId));
        Assert.NotNull(assetStorage.ResolveAssetPath(newPlateAssetId));
    }
}

public class AssetGarbageCollectorTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();

        internal Fixture()
        {
            Paths = new PlateStoragePaths(directory.Path);
            Storage = new AssetStorageService(Paths.AssetsDirectory, Paths.AssetStagingDirectory, new AssetMetadataStore(Paths.AssetMetadataDirectory));
            Collector = new AssetGarbageCollector(Storage, Paths.AssetTrashDirectory, utcNow: () => Clock.Now);
        }

        internal FakeClock Clock { get; } = new() { Now = DateTime.UtcNow };

        internal PlateStoragePaths Paths { get; }

        internal AssetStorageService Storage { get; }

        internal AssetGarbageCollector Collector { get; }

        internal Guid AddLegacyAsset()
        {
            var id = Guid.NewGuid();
            TestImages.Write(Paths.AssetsDirectory, id.ToString("N") + ".png", TestImages.Png(2, 2));
            return id;
        }

        public void Dispose() => directory.Dispose();
    }

    private static AssetReferenceScan Complete(params Guid[] referenced) => new(true, referenced.ToHashSet(), []);

    [Fact]
    public void IncompleteScan_BlocksCleanup()
    {
        using var fixture = new Fixture();
        fixture.AddLegacyAsset();
        fixture.Clock.Now = DateTime.UtcNow.AddYears(1);

        var plan = fixture.Collector.Plan(new AssetReferenceScan(false, new System.Collections.Generic.HashSet<Guid>(), ["Plate X is Unreadable."]));

        Assert.False(plan.CanProceed);
        Assert.Throws<InvalidOperationException>(() => fixture.Collector.MoveToTrash(plan));
        Assert.Single(fixture.Storage.ListAssets());
    }

    [Fact]
    public void Plan_ClassifiesReferencedProtectedAndUnreferenced()
    {
        using var fixture = new Fixture();
        var referenced = fixture.AddLegacyAsset();
        var unreferenced = fixture.AddLegacyAsset();
        var inEditor = fixture.AddLegacyAsset();
        var imported = fixture.Storage.ImportImage(TestImages.Write(Path.Combine(fixture.Paths.Root, "src"), "new.png", TestImages.Png(3, 3)));
        fixture.Clock.Now = DateTime.UtcNow.AddDays(30);

        var plan = fixture.Collector.Plan(Complete(referenced), additionallyInUse: [inEditor]);
        var states = plan.Assets.ToDictionary(a => a.AssetId, a => a.State);

        Assert.Equal(AssetLifecycleState.Referenced, states[referenced]);
        Assert.Equal(AssetLifecycleState.Referenced, states[inEditor]);
        Assert.Equal(AssetLifecycleState.Protected, states[imported]);
        Assert.Equal(AssetLifecycleState.Unreferenced, states[unreferenced]);
    }

    [Fact]
    public void YoungUnreferencedAssets_AreProtected()
    {
        using var fixture = new Fixture();
        var fresh = fixture.AddLegacyAsset();

        var plan = fixture.Collector.Plan(Complete());

        Assert.Equal(AssetLifecycleState.Protected, plan.Assets.Single(a => a.AssetId == fresh).State);
    }

    [Fact]
    public void TrashIsReversible_AndPurgeIsOffByDefault()
    {
        using var fixture = new Fixture();
        var unused = fixture.AddLegacyAsset();
        fixture.Clock.Now = DateTime.UtcNow.AddDays(30);

        var moved = fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete()));
        Assert.Equal(1, moved);
        Assert.Null(fixture.Storage.ResolveAssetPath(unused));

        Assert.True(fixture.Collector.Restore(unused));
        Assert.NotNull(fixture.Storage.ResolveAssetPath(unused));

        fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete()));
        fixture.Clock.Now = fixture.Clock.Now.Add(AssetGarbageCollector.TrashGracePeriod).AddDays(1);

        var wouldPurge = fixture.Collector.PurgeExpired(purgeEnabled: false);
        Assert.Equal([unused], wouldPurge);
        Assert.True(fixture.Collector.Restore(unused));
    }

    [Fact]
    public void Purge_OnlyAfterGracePeriod_AndOnlyWhenEnabled()
    {
        using var fixture = new Fixture();
        var unused = fixture.AddLegacyAsset();
        fixture.Clock.Now = DateTime.UtcNow.AddDays(30);
        fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete()));

        Assert.Empty(fixture.Collector.PurgeExpired(purgeEnabled: true));
        Assert.Contains(Directory.GetFiles(fixture.Paths.AssetTrashDirectory), f => f.EndsWith(".png", StringComparison.Ordinal));

        fixture.Clock.Now = fixture.Clock.Now.Add(AssetGarbageCollector.TrashGracePeriod).AddMinutes(1);
        Assert.Equal([unused], fixture.Collector.PurgeExpired(purgeEnabled: true));
        Assert.Empty(Directory.GetFiles(fixture.Paths.AssetTrashDirectory));
        Assert.False(fixture.Collector.Restore(unused));
    }

    [Fact]
    public void ReferencedAssets_AreNeverTrashed()
    {
        using var fixture = new Fixture();
        var used = fixture.AddLegacyAsset();
        fixture.Clock.Now = DateTime.UtcNow.AddYears(5);

        Assert.Equal(0, fixture.Collector.MoveToTrash(fixture.Collector.Plan(Complete(used))));
        Assert.NotNull(fixture.Storage.ResolveAssetPath(used));
    }
}
