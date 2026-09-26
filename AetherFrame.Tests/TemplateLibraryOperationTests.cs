using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

public class BuiltInTemplateCatalogTests
{
    [Fact]
    public void ExactlyAdventurePlateClassicAndBlankCanvas()
    {
        Assert.Equal(2, BuiltInTemplateCatalog.All.Count);
        Assert.Equal("Adventure Plate Classic", BuiltInTemplateCatalog.All[0].Name);
        Assert.Equal("Blank Canvas", BuiltInTemplateCatalog.All[1].Name);
        Assert.Equal(BuiltInTemplateCatalog.AdventurePlateClassicId, BuiltInTemplateCatalog.All[0].TemplateId);
        Assert.Equal(BuiltInTemplateCatalog.BlankCanvasId, BuiltInTemplateCatalog.All[1].TemplateId);
    }

    [Fact]
    public void NeverPersistedAsFiles()
    {
        using var fixture = new TemplateLibraryFixture();

        // Nothing was ever written; the built-ins still resolve.
        Assert.False(Directory.Exists(fixture.Paths.TemplatesDirectory));
        Assert.NotNull(BuiltInTemplateCatalog.Find(BuiltInTemplateCatalog.AdventurePlateClassicId));
    }

    [Fact]
    public void AdventurePlateClassic_MatchesPlateFactoryOutput()
    {
        // Two independent calls to AdventurePlateStarter mint fresh per-element Guids, so a byte
        // diff isn't meaningful here; what matters is that BuiltInTemplateCatalog delegates into
        // PlateFactory rather than re-implementing the starter — checked structurally instead.
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starter = new PlateStarterContent(null);

        var fromCatalog = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.AdventurePlateClassicId, now, starter);
        var fromFactory = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.Empty, "Adventure Plate Classic", now, starter);

        Assert.Equal(fromFactory.Elements.Count, fromCatalog.Elements.Count);
        Assert.Equal(fromFactory.Elements.Select(e => e.Role).OrderBy(r => r), fromCatalog.Elements.Select(e => e.Role).OrderBy(r => r));
        Assert.Equal(fromFactory.Background!.Mode, fromCatalog.Background!.Mode);
        Assert.Equal(fromFactory.BasicPlate!.ThemeId, fromCatalog.BasicPlate!.ThemeId);
        Assert.True(BasicEditorSession.CanResetLayout(fromCatalog));
    }

    [Fact]
    public void BlankCanvas_IsMinimalValid_WithNoBasicContent()
    {
        var document = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, DateTime.UtcNow, null);

        Assert.Empty(document.Elements);
        Assert.Null(document.BasicIdentity);
        Assert.Null(document.BasicPlate);
        Assert.True(document.CanvasWidth > 0);
        Assert.True(document.CanvasHeight > 0);
        Assert.NotNull(document.Background);
        Assert.False(BasicEditorSession.CanResetLayout(document));
    }

    [Fact]
    public void CreateDocument_IsFreshEveryCall()
    {
        var a = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.AdventurePlateClassicId, DateTime.UtcNow, null);
        var b = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.AdventurePlateClassicId, DateTime.UtcNow, null);

        Assert.NotSame(a, b);
    }

    [Fact]
    public async Task GetOrderedTemplates_ListsBuiltInsFirst_InCatalogOrder()
    {
        using var fixture = new TemplateLibraryFixture();
        var library = await fixture.LoadAsync();

        var ordered = library.GetOrderedTemplates();

        Assert.Equal(BuiltInTemplateCatalog.AdventurePlateClassicId, ordered[0].TemplateId);
        Assert.Equal(BuiltInTemplateCatalog.BlankCanvasId, ordered[1].TemplateId);
        Assert.All(ordered.Take(2), t => Assert.Equal(TemplateKind.BuiltIn, t.Kind));
    }

    [Fact]
    public void AdventurePlateClassic_SupportsPreview()
    {
        Assert.True(BuiltInTemplateCatalog.Find(BuiltInTemplateCatalog.AdventurePlateClassicId)!.SupportsPreview);
    }

    [Fact]
    public void BlankCanvas_DoesNotSupportPreview()
    {
        Assert.False(BuiltInTemplateCatalog.Find(BuiltInTemplateCatalog.BlankCanvasId)!.SupportsPreview);
    }

    [Fact]
    public async Task GetOrderedTemplates_SupportsPreview_MatchesCatalogCapability_NotDisplayName()
    {
        using var fixture = new TemplateLibraryFixture();
        var library = await fixture.LoadAsync();

        var ordered = library.GetOrderedTemplates();

        var classic = ordered.Single(t => t.TemplateId == BuiltInTemplateCatalog.AdventurePlateClassicId);
        var blank = ordered.Single(t => t.TemplateId == BuiltInTemplateCatalog.BlankCanvasId);
        Assert.True(classic.SupportsPreview);
        Assert.False(blank.SupportsPreview);
    }

    [Fact]
    public async Task GetOrderedTemplates_UserTemplate_AlwaysSupportsPreview()
    {
        using var fixture = new TemplateLibraryFixture();
        var library = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await library.SaveAsTemplateAsync(source.PlateId, "My Template");

        var summary = library.FindTemplate(templateId)!;

        Assert.True(summary.SupportsPreview);
    }
}

public class SaveAsTemplateTests
{
    private static async Task<(TemplateLibraryFixture Fixture, TemplateLibraryService Templates, Guid PlateId)> SeedAsync()
    {
        var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "My Plate");
        return (fixture, templates, plate.PlateId);
    }

    [Fact]
    public async Task CapturesLastSavedState_NotUnsavedEditorChanges()
    {
        var (fixture, templates, plateId) = await SeedAsync();
        using var _ = fixture;

        var editing = fixture.PlateLibrary.OpenDocumentForEditing(plateId);
        editing.Elements.Add(new TextProfileElement { Text = "Never saved" });
        // Deliberately never call SavePlateDocumentAsync.

        var templateId = await templates.SaveAsTemplateAsync(plateId, "My Template");

        var document = templates.GetSavedDocument(templateId)!;
        Assert.Empty(document.Elements);
    }

    [Fact]
    public async Task AlwaysCreatesNewTemplate_EvenWithSameName()
    {
        var (fixture, templates, plateId) = await SeedAsync();
        using var _ = fixture;

        var first = await templates.SaveAsTemplateAsync(plateId, "Same Name");
        var second = await templates.SaveAsTemplateAsync(plateId, "Same Name");

        Assert.NotEqual(first, second);
        Assert.Equal(2, templates.GetOrderedTemplates().Count(t => t.Kind == TemplateKind.UserSaved));
    }

    [Fact]
    public async Task ScrubsOwnerContentId()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        await fixture.LoadAsync();

        var plateId = Guid.NewGuid();
        var legacyJson = LegacyData.VersionOneDocument(plateId, owner: 555555);
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        await fixture.Store.WriteTextAsync(fixture.Paths.GetPlatePath(plateId), legacyJson);

        var reloadedPlateLibrary = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.PlateLog, () => fixture.Clock.Now);
        await reloadedPlateLibrary.InitializeAsync();
        var reloadedTemplates = new TemplateLibraryService(fixture.Paths, fixture.Store, reloadedPlateLibrary, fixture.Log, () => fixture.Clock.Now);
        await reloadedTemplates.InitializeAsync();

        var templateId = await reloadedTemplates.SaveAsTemplateAsync(plateId, "From Legacy");

        Assert.Equal(0ul, reloadedTemplates.GetSavedDocument(templateId)!.OwnerContentId);
    }

    [Fact]
    public async Task DoesNotCopyCharacterBindingOrActiveState()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var created = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Alice's Plate");
        Assert.True(created.BecameActive);

        var templateId = await templates.SaveAsTemplateAsync(created.PlateId, "Alice Template");

        var raw = fixture.ReadTemplateJson(templateId);
        Assert.DoesNotContain("1001", raw); // Alice's ContentId never appears anywhere in the Template
        Assert.Equal(0ul, templates.GetSavedDocument(templateId)!.OwnerContentId);
    }

    [Fact]
    public async Task PreservesFullCreativeState()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich Plate", fixture.Clock.Now);
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        await fixture.Store.WriteTextAsync(fixture.Paths.GetPlatePath(plateId), JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();

        var templateId = await templates.SaveAsTemplateAsync(plateId, "Rich Template");
        var document = templates.GetSavedDocument(templateId)!;

        Assert.Equal(rich.CanvasWidth, document.CanvasWidth);
        Assert.Equal(rich.CanvasHeight, document.CanvasHeight);
        Assert.Equal(rich.Background!.Mode, document.Background!.Mode);
        Assert.Equal(rich.Background.ImageAssetId, document.Background.ImageAssetId);
        Assert.Equal(rich.BasicIdentity!.GameTitleId, document.BasicIdentity!.GameTitleId);
        Assert.Equal(2, document.Elements.Count);
        Assert.Contains(document.Elements, e => e is ImageProfileElement { Role: ProfileElementRole.BasicPortrait } img && img.AssetId == SampleDocuments.ImageAsset);
        Assert.Contains(document.Elements, e => e is TextProfileElement { Role: ProfileElementRole.BasicName });
    }

    [Fact]
    public async Task OfUnreadablePlate_IsRefused()
    {
        using var fixture = new TemplateLibraryFixture();
        fixture.WritePlateJson(Guid.NewGuid(), "{ broken");
        var templates = await fixture.LoadAsync();
        var broken = fixture.PlateLibrary.GetOrderedPlates().Single().PlateId;

        await Assert.ThrowsAsync<PlateLibraryException>(() => templates.SaveAsTemplateAsync(broken, "x"));
    }

    [Fact]
    public async Task ReusesAssetIds_WithoutCopyingImageBytes()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich Plate", fixture.Clock.Now);
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        await fixture.Store.WriteTextAsync(fixture.Paths.GetPlatePath(plateId), JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();

        var templateId = await templates.SaveAsTemplateAsync(plateId, "Rich Template");
        var document = templates.GetSavedDocument(templateId)!;

        var imageElement = Assert.IsType<ImageProfileElement>(document.Elements.Single(e => e is ImageProfileElement));
        Assert.Equal(SampleDocuments.ImageAsset, imageElement.AssetId);
    }
}

public class TemplatePersistenceTests
{
    [Fact]
    public async Task PersistsAcrossReload_WithSameIdAndContent()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(plate.PlateId, "Reload Me");

        var reloadedPlateLibrary = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.PlateLog, () => fixture.Clock.Now);
        await reloadedPlateLibrary.InitializeAsync();
        var reloaded = new TemplateLibraryService(fixture.Paths, fixture.Store, reloadedPlateLibrary, fixture.Log, () => fixture.Clock.Now);
        await reloaded.InitializeAsync();

        var summary = reloaded.FindTemplate(templateId);
        Assert.NotNull(summary);
        Assert.Equal("Reload Me", summary!.DisplayName);
        Assert.True(summary.IsReady);
    }

    [Fact]
    public async Task MalformedFile_IsIsolated_OtherTemplatesStillLoad()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;

        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var document = PlateDocuments.ToJson(BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, fixture.Clock.Now, null)).ToJsonString(JsonOptions.Default);
        fixture.WriteTemplateJson(good, TemplateSamples.Envelope(good, "Good", document));
        fixture.WriteTemplateJson(bad, "{ this is not valid json");

        var templates = await fixture.LoadAsync();

        var goodSummary = templates.FindTemplate(good)!;
        var badSummary = templates.FindTemplate(bad)!;
        Assert.True(goodSummary.IsReady);
        Assert.Equal(TemplateStatus.Unreadable, badSummary.Status);
        Assert.NotNull(badSummary.Problem);
    }

    [Fact]
    public async Task EnvelopeNewerThanKnown_IsListedButNeverOpened()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;

        var id = Guid.NewGuid();
        fixture.WriteTemplateJson(id, TemplateSamples.Envelope(id, "From the future", "{}", version: 999));

        var templates = await fixture.LoadAsync();

        var summary = templates.FindTemplate(id)!;
        Assert.Equal(TemplateStatus.NewerVersion, summary.Status);
        Assert.Equal("From the future", summary.DisplayName);
        Assert.Null(templates.GetSavedDocument(id));
    }

    [Fact]
    public async Task EmbeddedDocumentNewerThanKnown_MarksTemplateNewerVersion_RegardlessOfEnvelopeVersion()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;

        var id = Guid.NewGuid();
        // Envelope itself is at a perfectly normal version 1; only the embedded document is "from the future".
        fixture.WriteTemplateJson(id, TemplateSamples.Envelope(id, "Future Content", """{ "Version": 999 }""", version: 1));

        var templates = await fixture.LoadAsync();

        var summary = templates.FindTemplate(id)!;
        Assert.Equal(TemplateStatus.NewerVersion, summary.Status);
    }

    [Fact]
    public async Task EmbeddedLegacyDocument_MigratesUsingProfileDocumentSchema_IndependentlyOfEnvelopeVersion()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;

        var id = Guid.NewGuid();
        var legacyDocument = LegacyData.VersionOneDocument(Guid.NewGuid(), owner: 0, name: "Legacy Content");
        fixture.WriteTemplateJson(id, TemplateSamples.Envelope(id, "Legacy Template", legacyDocument, version: 1));

        var templates = await fixture.LoadAsync();

        var summary = templates.FindTemplate(id)!;
        Assert.True(summary.IsReady);
        var document = templates.GetSavedDocument(id)!;
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, document.Version);
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, document.CanvasWidth);
        Assert.Contains(document.Elements, e => e is ImageProfileElement { AssetId: var assetId } && assetId == LegacyData.PortraitAsset);
    }

    [Fact]
    public async Task OpeningTemplateLibrary_NeverMutatesFiles()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(plate.PlateId, "Stable");
        var before = fixture.ReadTemplateJson(templateId);

        // "Opening" the library == querying and previewing it.
        _ = templates.GetOrderedTemplates();
        _ = templates.Search("Stable");
        _ = templates.GetSavedDocument(templateId);
        _ = templates.GetSavedDocument(BuiltInTemplateCatalog.AdventurePlateClassicId);

        Assert.Equal(before, fixture.ReadTemplateJson(templateId));
    }
}

public class TemplateRenameDuplicateDeleteTests
{
    private static async Task<(TemplateLibraryFixture Fixture, TemplateLibraryService Templates, Guid TemplateId)> SeedAsync(string name = "Original")
    {
        var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(plate.PlateId, name);
        return (fixture, templates, templateId);
    }

    [Fact]
    public async Task Rename_ChangesOnlyNameAndUpdatedTimestamp()
    {
        var (fixture, templates, templateId) = await SeedAsync();
        using var _ = fixture;
        var createdBefore = templates.FindTemplate(templateId)!.CreatedUtc;
        fixture.Clock.Tick();

        await templates.RenameTemplateAsync(templateId, "Renamed");

        var summary = templates.FindTemplate(templateId)!;
        Assert.Equal("Renamed", summary.DisplayName);
        Assert.Equal(createdBefore, summary.CreatedUtc);
        Assert.Equal(fixture.Clock.Now, summary.ModifiedUtc);
    }

    [Fact]
    public async Task Rename_ToEmpty_IsRejected()
    {
        var (fixture, templates, templateId) = await SeedAsync();
        using var _ = fixture;

        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(templateId, "   "));
    }

    [Fact]
    public async Task Rename_OnBuiltIn_Throws()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var ex = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.RenameTemplateAsync(BuiltInTemplateCatalog.BlankCanvasId, "Nope"));
        Assert.Contains("Built-in", ex.Message);
    }

    [Fact]
    public async Task Duplicate_GetsFreshGuid_NeverTheSources()
    {
        var (fixture, templates, templateId) = await SeedAsync();
        using var _ = fixture;

        var copyId = await templates.DuplicateTemplateAsync(templateId);

        Assert.NotEqual(templateId, copyId);
    }

    [Fact]
    public async Task Duplicate_NeverCreatesAPlate()
    {
        var (fixture, templates, templateId) = await SeedAsync();
        using var _ = fixture;
        var plateCountBefore = fixture.PlateLibrary.GetOrderedPlates().Count;

        await templates.DuplicateTemplateAsync(templateId);

        Assert.Equal(plateCountBefore, fixture.PlateLibrary.GetOrderedPlates().Count);
    }

    [Fact]
    public async Task Duplicate_NeverMutatesTheOriginal()
    {
        var (fixture, templates, templateId) = await SeedAsync();
        using var _ = fixture;
        var before = fixture.ReadTemplateJson(templateId);

        await templates.DuplicateTemplateAsync(templateId);

        Assert.Equal(before, fixture.ReadTemplateJson(templateId));
    }

    [Fact]
    public async Task Duplicate_OnBuiltIn_Throws()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var ex = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.DuplicateTemplateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId));
        Assert.Contains("Built-in", ex.Message);
    }

    [Fact]
    public async Task Delete_MovesToTemplateTrash_NeverDestroys()
    {
        var (fixture, templates, templateId) = await SeedAsync();
        using var _ = fixture;

        await templates.DeleteTemplateAsync(templateId);

        Assert.Null(templates.FindTemplate(templateId));
        Assert.False(File.Exists(fixture.Paths.GetTemplatePath(templateId)));
        Assert.Single(Directory.GetFiles(fixture.Paths.TemplateTrashDirectory));
    }

    [Fact]
    public async Task Delete_OnBuiltIn_Throws()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var ex = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.DeleteTemplateAsync(BuiltInTemplateCatalog.BlankCanvasId));
        Assert.Contains("Built-in", ex.Message);
    }

    [Fact]
    public async Task Delete_DoesNotTouchAssetFiles()
    {
        var fixture = new TemplateLibraryFixture();
        using var _ = fixture;
        var templates = await fixture.LoadAsync();

        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich Plate", fixture.Clock.Now);
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        await fixture.Store.WriteTextAsync(fixture.Paths.GetPlatePath(plateId), JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();
        var templateId = await templates.SaveAsTemplateAsync(plateId, "Rich Template");

        Directory.CreateDirectory(fixture.Paths.AssetsDirectory);
        var assetFile = Path.Combine(fixture.Paths.AssetsDirectory, SampleDocuments.ImageAsset.ToString("N") + ".png");
        File.WriteAllBytes(assetFile, [1, 2, 3]);

        await templates.DeleteTemplateAsync(templateId);

        Assert.True(File.Exists(assetFile));
    }
}

/// <summary>
/// The saved Templates failing to load (their folder unreadable, say) never blocks Create Plate:
/// built-in Templates are generated, not read, so they keep working; the saved ones refuse every
/// change rather than acting on a Library that isn't really loaded.
/// </summary>
public class TemplateLoadFailureTests
{
    [Fact]
    public async Task SavedTemplatesUnreadable_BuiltInTemplatesStillCreatePlates()
    {
        var store = new FaultInjectingStore();
        using var fixture = new TemplateLibraryFixture(store);
        await fixture.PlateLibrary.InitializeAsync();
        var templates = fixture.CreateService();
        store.FailList = directory => directory == fixture.Paths.TemplatesDirectory;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => templates.InitializeAsync());

        Assert.True(templates.LoadFailed);
        Assert.False(templates.IsLoaded);
        Assert.Equal(BuiltInTemplateCatalog.All.Select(d => d.TemplateId), templates.GetOrderedTemplates().Select(t => t.TemplateId));

        var classic = await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, Characters.Alice, new PlateStarterContent(null));
        var blank = await templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, null);

        Assert.True(BasicEditorSession.CanResetLayout(fixture.PlateLibrary.OpenDocumentForEditing(classic.PlateId)));
        Assert.Empty(fixture.PlateLibrary.OpenDocumentForEditing(blank.PlateId).Elements);
    }

    [Fact]
    public async Task SavedTemplatesUnreadable_EveryChangeIsRefused_AndNothingIsWritten()
    {
        var store = new FaultInjectingStore();
        using var fixture = new TemplateLibraryFixture(store);
        await fixture.PlateLibrary.InitializeAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templates = fixture.CreateService();
        store.FailList = directory => directory == fixture.Paths.TemplatesDirectory;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => templates.InitializeAsync());

        var refused = await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.SaveAsTemplateAsync(source.PlateId, "New"));
        Assert.Contains("couldn't be loaded", refused.Message);
        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.InstantiateAsync(Guid.NewGuid(), null));

        // An incomplete scan must never feed image cleanup.
        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.ScanAssetReferencesAsync());
        Assert.False(Directory.Exists(fixture.Paths.TemplatesDirectory));
    }

    [Fact]
    public async Task BuiltInTemplate_NeverWaitsForSavedTemplatesToLoad()
    {
        using var fixture = new TemplateLibraryFixture();
        await fixture.PlateLibrary.InitializeAsync();
        var templates = fixture.CreateService();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, null);

        Assert.False(templates.IsLoaded);
        Assert.NotEqual(Guid.Empty, result.PlateId);
    }
}

public class TemplateInstantiateTests
{
    [Fact]
    public async Task BuiltInAdventurePlateClassic_MatchesTodaysCreateFlow()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, null, new PlateStarterContent(null));

        var document = fixture.PlateLibrary.OpenDocumentForEditing(result.PlateId);
        Assert.True(BasicEditorSession.CanResetLayout(document));
        Assert.Equal(ProfileBackgroundMode.LinearGradient, document.Background!.Mode);
    }

    [Fact]
    public async Task BuiltInBlankCanvas_MatchesTodaysBlankFlow()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, null);

        var document = fixture.PlateLibrary.OpenDocumentForEditing(result.PlateId);
        Assert.Empty(document.Elements);
        Assert.False(BasicEditorSession.CanResetLayout(document));
    }

    [Fact]
    public async Task UserTemplate_CreatesPlate_WithFreshPlateGuid()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");

        var result = await templates.InstantiateAsync(templateId, null);

        Assert.NotEqual(Guid.Empty, result.PlateId);
        Assert.NotEqual(source.PlateId, result.PlateId);
    }

    [Fact]
    public async Task NeverReusesTemplateGuidAsPlateGuid()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");

        var result = await templates.InstantiateAsync(templateId, null);

        Assert.NotEqual(templateId, result.PlateId);
    }

    [Fact]
    public async Task PreservesFullCreativeState()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var plateId = Guid.NewGuid();
        var rich = SampleDocuments.Rich(plateId, "Rich Plate", fixture.Clock.Now);
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        await fixture.Store.WriteTextAsync(fixture.Paths.GetPlatePath(plateId), JsonSerializer.Serialize(rich, JsonOptions.Default));
        await fixture.PlateLibrary.InitializeAsync();
        var templateId = await templates.SaveAsTemplateAsync(plateId, "Rich Template");

        var result = await templates.InstantiateAsync(templateId, null);
        var document = fixture.PlateLibrary.OpenDocumentForEditing(result.PlateId);

        Assert.Equal(rich.CanvasWidth, document.CanvasWidth);
        Assert.Equal(rich.Background!.ImageAssetId, document.Background!.ImageAssetId);
        Assert.Equal(2, document.Elements.Count);
    }

    [Fact]
    public async Task NeverMutatesTheTemplate()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");
        var before = fixture.ReadTemplateJson(templateId);

        await templates.InstantiateAsync(templateId, null);
        await templates.InstantiateAsync(templateId, null);

        Assert.Equal(before, fixture.ReadTemplateJson(templateId));
    }

    [Fact]
    public async Task DoesNotCopyOwnerContentId()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, Characters.Alice, new PlateStarterContent(null));

        Assert.Equal(0ul, fixture.PlateLibrary.OpenDocumentForEditing(result.PlateId).OwnerContentId);
    }

    [Fact]
    public async Task DoesNotCopyCharacterBindingOrActiveState()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var alicesFirst = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Alice 1");
        Assert.True(alicesFirst.BecameActive);
        var templateId = await templates.SaveAsTemplateAsync(alicesFirst.PlateId, "Alice Template");

        var result = await templates.InstantiateAsync(templateId, Characters.Alice);

        Assert.False(result.BecameActive);
        Assert.Equal(alicesFirst.PlateId, fixture.PlateLibrary.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task FirstPlateForCharacter_BecomesActive()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, Characters.Alice, new PlateStarterContent(null));

        Assert.True(result.BecameActive);
        Assert.Equal(result.PlateId, fixture.PlateLibrary.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task SecondPlateForCharacter_NeverAutoActivates()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, Characters.Alice, new PlateStarterContent(null));

        var second = await templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, Characters.Alice);

        Assert.False(second.BecameActive);
    }

    [Fact]
    public async Task LoggedOut_CreatesUnboundPlate()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.BlankCanvasId, null);

        Assert.False(result.BecameActive);
        Assert.Empty(fixture.PlateLibrary.FindPlate(result.PlateId)!.CharacterNames);
    }

    [Fact]
    public async Task NamesThePlateAfterTheTemplate_MadeUnique()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Existing");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "Existing");

        var result = await templates.InstantiateAsync(templateId, null);

        Assert.Equal("Existing 2", fixture.PlateLibrary.FindPlate(result.PlateId)!.DisplayName);
    }

    [Fact]
    public async Task SharesAssetIds_WithoutCopyingImageBytes()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        var result = await templates.InstantiateAsync(BuiltInTemplateCatalog.AdventurePlateClassicId, null, new PlateStarterContent(null));

        // Adventure Plate Classic starts with no images; this documents that instantiation never
        // imports/copies bytes on its own (no asset directory is created).
        Assert.False(Directory.Exists(fixture.Paths.AssetsDirectory) && Directory.GetFiles(fixture.Paths.AssetsDirectory).Length > 0);
        Assert.NotNull(fixture.PlateLibrary.FindPlate(result.PlateId));
    }

    [Fact]
    public async Task OfNewerVersionTemplate_IsRefused()
    {
        using var fixture = new TemplateLibraryFixture();
        var id = Guid.NewGuid();
        fixture.WriteTemplateJson(id, TemplateSamples.Envelope(id, "Future", "{}", version: 999));
        var templates = await fixture.LoadAsync();

        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.InstantiateAsync(id, null));
    }

    [Fact]
    public async Task OfUnreadableTemplate_IsRefused()
    {
        using var fixture = new TemplateLibraryFixture();
        var id = Guid.NewGuid();
        fixture.WriteTemplateJson(id, "{ not json");
        var templates = await fixture.LoadAsync();

        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.InstantiateAsync(id, null));
    }
}

public class TemplateDefaultEditorDerivationTests
{
    [Fact]
    public void AdventurePlateClassic_HasBasicStructure()
    {
        var document = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.AdventurePlateClassicId, DateTime.UtcNow, new PlateStarterContent(null));
        Assert.True(BasicEditorSession.CanResetLayout(document));
    }

    [Fact]
    public void BlankCanvas_HasNoBasicStructure()
    {
        var document = BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, DateTime.UtcNow, null);
        Assert.False(BasicEditorSession.CanResetLayout(document));
    }

    [Fact]
    public async Task UserTemplate_WithBasicContent_IsDetected()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Basic Source", new PlateStarterContent(null));
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "Basic Template");

        Assert.True(BasicEditorSession.CanResetLayout(templates.GetSavedDocument(templateId)!));
    }

    [Fact]
    public async Task UserTemplate_WithoutBasicContent_IsDetected()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Advanced Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "Advanced Template");

        Assert.False(BasicEditorSession.CanResetLayout(templates.GetSavedDocument(templateId)!));
    }
}

public class TemplateOriginTests
{
    [Fact]
    public async Task SaveAsTemplate_OriginIsSavedFromPlate_AndStoresNoLocalIdentifiers()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");

        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");

        var raw = fixture.ReadTemplateJson(templateId);
        Assert.DoesNotContain("SourcePlateId", raw);
        Assert.DoesNotContain("SourceTemplateId", raw);
        Assert.DoesNotContain(source.PlateId.ToString(), raw);
    }

    [Fact]
    public async Task DuplicateTemplate_OriginIsDuplicated_AndStoresNoLocalIdentifiers()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");

        var copyId = await templates.DuplicateTemplateAsync(templateId);

        var raw = fixture.ReadTemplateJson(copyId);
        Assert.DoesNotContain("SourceTemplateId", raw);
        Assert.DoesNotContain("SourcePlateId", raw);
        Assert.DoesNotContain(templateId.ToString(), raw);
    }

    [Fact]
    public void NumericValues_ArePinned()
    {
        Assert.Equal(0, (int)TemplateOriginKind.Unknown);
        Assert.Equal(1, (int)TemplateOriginKind.SavedFromPlate);
        Assert.Equal(2, (int)TemplateOriginKind.Duplicated);
    }

    [Fact]
    public async Task SaveAsTemplate_WritesKindOne_NotZero()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");

        var template = TemplateDocuments.Materialize(ParseObject(fixture.ReadTemplateJson(templateId)));

        Assert.Equal(TemplateOriginKind.SavedFromPlate, template.Origin.Kind);
        Assert.Equal(1, (int)template.Origin.Kind);
    }

    [Fact]
    public async Task DuplicateTemplate_WritesKindTwo()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var source = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");
        var templateId = await templates.SaveAsTemplateAsync(source.PlateId, "My Template");
        var copyId = await templates.DuplicateTemplateAsync(templateId);

        var template = TemplateDocuments.Materialize(ParseObject(fixture.ReadTemplateJson(copyId)));

        Assert.Equal(TemplateOriginKind.Duplicated, template.Origin.Kind);
        Assert.Equal(2, (int)template.Origin.Kind);
    }

    private static string MinimalDocumentJson() =>
        JsonSerializer.Serialize(BuiltInTemplateCatalog.CreateDocument(BuiltInTemplateCatalog.BlankCanvasId, DateTime.UtcNow, null), JsonOptions.Default);

    [Fact]
    public void MissingOriginKindField_DeserializesAsUnknown_NeverSavedFromPlate()
    {
        var id = Guid.NewGuid();
        var raw = ParseObject(TemplateSamples.Envelope(id, "No Kind Field", MinimalDocumentJson()).Replace("\"Kind\": 0", ""));

        var template = TemplateDocuments.Materialize(raw);

        Assert.Equal(TemplateOriginKind.Unknown, template.Origin.Kind);
        Assert.NotEqual(TemplateOriginKind.SavedFromPlate, template.Origin.Kind);
    }

    [Fact]
    public void MissingOriginObject_DeserializesAsUnknown()
    {
        var id = Guid.NewGuid();
        var raw = ParseObject(TemplateSamples.Envelope(id, "No Origin", MinimalDocumentJson()));
        raw.Remove(nameof(PlateTemplate.Origin));

        var template = TemplateDocuments.Materialize(raw);

        Assert.Equal(TemplateOriginKind.Unknown, template.Origin.Kind);
    }

    [Fact]
    public void ExplicitNullOrigin_DeserializesAsUnknown_NotNull()
    {
        var id = Guid.NewGuid();
        var raw = ParseObject(TemplateSamples.Envelope(id, "Null Origin", MinimalDocumentJson()));
        raw[nameof(PlateTemplate.Origin)] = null;

        var template = TemplateDocuments.Materialize(raw);

        Assert.NotNull(template.Origin);
        Assert.Equal(TemplateOriginKind.Unknown, template.Origin.Kind);
    }

    [Fact]
    public void UnrecognizedNumericKind_FromANewerBuild_DeserializesAsUnknown()
    {
        var id = Guid.NewGuid();
        var raw = ParseObject(TemplateSamples.Envelope(id, "Future Kind", MinimalDocumentJson(), originKind: 99));

        var template = TemplateDocuments.Materialize(raw);

        Assert.Equal(TemplateOriginKind.Unknown, template.Origin.Kind);
    }

    private static JsonObject ParseObject(string json) => (JsonObject)JsonNode.Parse(json)!;
}
