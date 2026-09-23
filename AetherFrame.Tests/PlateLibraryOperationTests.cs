using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

public class PlateCreationTests
{
    [Fact]
    public async Task Create_WritesValidDocument_WithNewGuid_AndListsItFirst()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        var first = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        fixture.Clock.Tick();
        var second = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null);

        Assert.NotEqual(Guid.Empty, first.PlateId);
        Assert.NotEqual(first.PlateId, second.PlateId);
        Assert.True(File.Exists(fixture.Paths.GetPlatePath(first.PlateId)));
        Assert.Equal([second.PlateId, first.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([second.PlateId, first.PlateId], fixture.ReadLibraryOrder());

        var document = library.OpenDocumentForEditing(second.PlateId);
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, document.Version);
        Assert.Equal(second.PlateId, document.ProfileId);
        Assert.Equal(ProfileDocument.DefaultCanvasWidth, document.CanvasWidth);
        Assert.Equal(ProfileDocument.DefaultCanvasHeight, document.CanvasHeight);
        Assert.Equal(ProfileBackgroundMode.LinearGradient, document.Background!.Mode);
        Assert.Equal(0ul, document.OwnerContentId);
        Assert.Empty(document.Elements);
    }

    [Fact]
    public async Task Create_UsesLayoutName_AndMakesItUnique()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        var a = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null);
        var b = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null);
        var c = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        Assert.Equal("Adventure Plate", library.FindPlate(a.PlateId)!.DisplayName);
        Assert.Equal("Adventure Plate 2", library.FindPlate(b.PlateId)!.DisplayName);
        Assert.Equal("Blank Plate", library.FindPlate(c.PlateId)!.DisplayName);
    }

    [Fact]
    public async Task FirstPlate_ForCharacter_BecomesActive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        var result = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice);

        Assert.True(result.BecameActive);
        Assert.Equal(result.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));

        var binding = fixture.ReadBinding(Characters.Alice.ContentId);
        Assert.Equal(result.PlateId, binding.GetProperty("ActiveProfileId").GetGuid());
        Assert.Equal("Alice Example", binding.GetProperty("LastKnownCharacterName").GetString());
        Assert.Equal("Twintania", binding.GetProperty("LastKnownHomeWorld").GetString());
        Assert.Equal(2, binding.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task SecondPlate_DoesNotReplaceActive_ButIsAssociated()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        var first = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice);
        var second = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        Assert.False(second.BecameActive);
        Assert.Equal(first.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal([first.PlateId, second.PlateId], library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
    }

    [Fact]
    public async Task FirstPlate_RuleIsPerCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bobs = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);

        Assert.True(bobs.BecameActive);
        Assert.Equal(bobs.PlateId, library.GetActivePlateId(Characters.Bob.ContentId));
    }

    [Fact]
    public async Task LoggedOut_CreateWorks_AndStaysUnbound()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        var result = await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null);

        Assert.False(result.BecameActive);
        Assert.False(Directory.Exists(fixture.Paths.CharactersDirectory) && Directory.GetFiles(fixture.Paths.CharactersDirectory).Length > 0);
        Assert.Empty(library.FindPlate(result.PlateId)!.CharacterNames);
        Assert.Equal(0ul, library.OpenDocumentForEditing(result.PlateId).OwnerContentId);
    }

    [Fact]
    public async Task LoggedOut_OperationsNeverTouchExistingBindings()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alices = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var before = fixture.ReadBindingJson(Characters.Alice.ContentId);

        var unbound = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var copy = await library.DuplicatePlateAsync(alices.PlateId, null);
        await library.RenamePlateAsync(unbound.PlateId, "Renamed while logged out");
        await library.DeletePlateAsync(copy);

        Assert.Equal(before, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task CreatingFirstPlate_ReplacesAnActiveIdThatPointsNowhere()
    {
        using var fixture = new LibraryFixture();
        var ghost = Guid.NewGuid();
        fixture.WriteBindingJson(Characters.Alice.ContentId, LegacyData.VersionOneBinding(Characters.Alice.ContentId, ghost, ghost));
        var library = await fixture.LoadAsync();

        var result = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        Assert.True(result.BecameActive);
        Assert.Equal(result.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task InvalidName_IsRejected()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, null, "   "));
        Assert.Empty(library.GetOrderedPlates());
    }

    [Fact]
    public async Task OperationsBeforeLoad_AreRefused()
    {
        using var fixture = new LibraryFixture();
        var library = fixture.CreateService();

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, null));
        Assert.Empty(library.GetOrderedPlates());
    }
}

public class PlateDuplicateTests
{
    private static async Task<(LibraryFixture Fixture, PlateLibraryService Library, Guid SourceId)> SeedRichPlateAsync()
    {
        var fixture = new LibraryFixture();
        var sourceId = Guid.NewGuid();
        var document = SampleDocuments.Rich(sourceId, "Showcase", fixture.Clock.Now);
        fixture.WritePlateJson(sourceId, JsonSerializer.Serialize(document, Persistence.JsonOptions.Default));
        var library = await fixture.LoadAsync();
        return (fixture, library, sourceId);
    }

    [Fact]
    public async Task Duplicate_GetsNewGuid_AndCopyName()
    {
        var (fixture, library, sourceId) = await SeedRichPlateAsync();
        using var _ = fixture;

        var copyId = await library.DuplicatePlateAsync(sourceId, null);
        var secondCopyId = await library.DuplicatePlateAsync(sourceId, null);

        Assert.NotEqual(sourceId, copyId);
        Assert.Equal("Showcase Copy", library.FindPlate(copyId)!.DisplayName);
        Assert.Equal("Showcase Copy 2", library.FindPlate(secondCopyId)!.DisplayName);
        Assert.Equal(copyId, library.OpenDocumentForEditing(copyId).ProfileId);
    }

    [Fact]
    public async Task Duplicate_PreservesAllCreativeContent()
    {
        var (fixture, library, sourceId) = await SeedRichPlateAsync();
        using var _ = fixture;

        var copyId = await library.DuplicatePlateAsync(sourceId, null);

        JsonAssert.EqualExcept(
            fixture.ReadPlateJson(sourceId),
            fixture.ReadPlateJson(copyId),
            "ProfileId", "Name", "Revision", "CreatedAtUtc", "UpdatedAtUtc", "OwnerContentId");

        var copy = library.OpenDocumentForEditing(copyId);
        Assert.Equal(1600, copy.CanvasWidth);
        Assert.Equal(900, copy.CanvasHeight);
        Assert.Equal(ProfileBackgroundMode.Image, copy.Background!.Mode);
        Assert.Equal(42u, copy.BasicIdentity!.GameTitleId);
        Assert.Equal(IdentityTitleLayout.Classic, copy.BasicIdentity.Layout);
        Assert.Equal(2, copy.Elements.Count);
        Assert.Contains(copy.Elements, e => e is ImageProfileElement { RotationDegrees: 12.5f, FlipX: true });
        Assert.Equal(0, copy.Revision);
    }

    [Fact]
    public async Task Duplicate_ReusesAssetIds_WithoutCopyingImageBytes()
    {
        var (fixture, library, sourceId) = await SeedRichPlateAsync();
        using var _ = fixture;
        Directory.CreateDirectory(fixture.Paths.AssetsDirectory);
        File.WriteAllBytes(Path.Combine(fixture.Paths.AssetsDirectory, SampleDocuments.ImageAsset.ToString("N") + ".png"), TestImages.Png(4, 4));

        var copyId = await library.DuplicatePlateAsync(sourceId, null);

        var sourceAssets = AssetReferenceScanner.Collect([library.OpenDocumentForEditing(sourceId)]);
        var copyAssets = AssetReferenceScanner.Collect([library.OpenDocumentForEditing(copyId)]);
        Assert.Equal(sourceAssets.OrderBy(a => a), copyAssets.OrderBy(a => a));
        Assert.Contains(SampleDocuments.ImageAsset, copyAssets);
        Assert.Contains(SampleDocuments.BackgroundAsset, copyAssets);
        Assert.Single(Directory.GetFiles(fixture.Paths.AssetsDirectory));
    }

    [Fact]
    public async Task Duplicate_IsNotActive_EvenForTheSourcesCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice);

        var copyId = await library.DuplicatePlateAsync(source.PlateId, Characters.Alice);

        Assert.Equal(source.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(copyId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Empty(library.FindPlate(copyId)!.ActiveForContentIds);
    }

    [Fact]
    public async Task Duplicate_IsPlacedDirectlyAfterSource()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var a = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var b = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var c = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        // Order is c, b, a.
        var copyOfB = await library.DuplicatePlateAsync(b.PlateId, null);

        Assert.Equal([c.PlateId, b.PlateId, copyOfB, a.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([c.PlateId, b.PlateId, copyOfB, a.PlateId], fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task Duplicate_IsIndependent_AfterCreation()
    {
        var (fixture, library, sourceId) = await SeedRichPlateAsync();
        using var _ = fixture;
        var copyId = await library.DuplicatePlateAsync(sourceId, null);
        var sourceBefore = fixture.ReadPlateJson(sourceId);

        var copy = library.OpenDocumentForEditing(copyId);
        copy.Elements.Clear();
        copy.CanvasWidth = 800;
        await library.SavePlateDocumentAsync(copy);

        Assert.Equal(sourceBefore, fixture.ReadPlateJson(sourceId));
        Assert.Equal(2, library.OpenDocumentForEditing(sourceId).Elements.Count);
        Assert.Empty(library.OpenDocumentForEditing(copyId).Elements);
    }

    [Fact]
    public async Task Duplicate_CopiesSavedState_NotUnsavedEdits()
    {
        var (fixture, library, sourceId) = await SeedRichPlateAsync();
        using var _ = fixture;

        var editing = library.OpenDocumentForEditing(sourceId);
        editing.Elements.Clear();

        var copyId = await library.DuplicatePlateAsync(sourceId, null);

        Assert.Equal(2, library.OpenDocumentForEditing(copyId).Elements.Count);
    }

    [Fact]
    public async Task Duplicate_OfUnreadablePlate_IsRefused()
    {
        using var fixture = new LibraryFixture();
        var broken = Guid.NewGuid();
        fixture.WritePlateJson(broken, "{ not json");
        var library = await fixture.LoadAsync();

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.DuplicatePlateAsync(broken, null));
        Assert.Single(library.GetOrderedPlates());
    }
}

public class PlateRenameTests
{
    [Fact]
    public async Task Rename_ChangesOnlyTheName()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Before", fixture.Clock.Now), Persistence.JsonOptions.Default));
        var library = await fixture.LoadAsync();
        await library.SetActivePlateAsync(Characters.Alice, plateId);
        var jsonBefore = fixture.ReadPlateJson(plateId);
        var bindingBefore = fixture.ReadBindingJson(Characters.Alice.ContentId);

        fixture.Clock.Tick();
        await library.RenamePlateAsync(plateId, "  After  ");

        Assert.Equal("After", library.FindPlate(plateId)!.DisplayName);
        Assert.Equal("After", library.OpenDocumentForEditing(plateId).Name);
        Assert.Equal(plateId, library.OpenDocumentForEditing(plateId).ProfileId);
        Assert.Equal(plateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(bindingBefore, fixture.ReadBindingJson(Characters.Alice.ContentId));
        JsonAssert.EqualExcept(jsonBefore, fixture.ReadPlateJson(plateId), "Name", "UpdatedAtUtc");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("\n\t")]
    public async Task Rename_ToEmpty_IsRejected_AndNothingChanges(string? name)
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Keep Me");
        var before = fixture.ReadPlateJson(plate.PlateId);

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(plate.PlateId, name));

        Assert.Equal("Keep Me", library.FindPlate(plate.PlateId)!.DisplayName);
        Assert.Equal(before, fixture.ReadPlateJson(plate.PlateId));
    }

    [Fact]
    public async Task Rename_AllowsDuplicateNames()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var a = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Same");
        var b = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Other");

        await library.RenamePlateAsync(b.PlateId, "Same");

        Assert.All(library.GetOrderedPlates(), p => Assert.Equal("Same", p.DisplayName));
        Assert.NotEqual(a.PlateId, b.PlateId);
    }

    [Fact]
    public async Task Rename_RaisesEvent_AndSurvivesReload()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        (Guid Id, string Name)? raised = null;
        library.PlateRenamed += (id, name) => raised = (id, name);

        await library.RenamePlateAsync(plate.PlateId, "Renamed");

        Assert.Equal((plate.PlateId, "Renamed"), raised);
        var reloaded = await fixture.LoadAsync();
        Assert.Equal("Renamed", reloaded.FindPlate(plate.PlateId)!.DisplayName);
    }

    [Fact]
    public async Task Save_AfterRename_KeepsTheNewName()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Old");
        var openCopy = library.OpenDocumentForEditing(plate.PlateId);

        await library.RenamePlateAsync(plate.PlateId, "New");
        openCopy.CanvasWidth = 1000;
        await library.SavePlateDocumentAsync(openCopy);

        Assert.Equal("New", library.FindPlate(plate.PlateId)!.DisplayName);
        Assert.Equal("New", library.OpenDocumentForEditing(plate.PlateId).Name);
        Assert.Equal(1000, library.OpenDocumentForEditing(plate.PlateId).CanvasWidth);
    }
}

public class PlateActiveTests
{
    [Fact]
    public async Task SetActive_AffectsOnlyTheRequestedCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alicesFirst = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bobsFirst = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        var shared = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var bobBefore = fixture.ReadBindingJson(Characters.Bob.ContentId);

        await library.SetActivePlateAsync(Characters.Alice, shared.PlateId);

        Assert.Equal(shared.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(bobsFirst.PlateId, library.GetActivePlateId(Characters.Bob.ContentId));
        Assert.Equal(bobBefore, fixture.ReadBindingJson(Characters.Bob.ContentId));
        Assert.Contains(alicesFirst.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Contains(shared.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
    }

    [Fact]
    public async Task OpeningAndSaving_NeverActivates()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bindingBefore = fixture.ReadBindingJson(Characters.Alice.ContentId);

        var editing = library.OpenDocumentForEditing(other.PlateId);
        _ = library.GetSavedDocument(other.PlateId);
        editing.CanvasHeight = 500;
        await library.SavePlateDocumentAsync(editing);

        Assert.Equal(active.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(bindingBefore, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task SetActive_OnUnreadablePlate_IsRefused()
    {
        using var fixture = new LibraryFixture();
        var broken = Guid.NewGuid();
        fixture.WritePlateJson(broken, "garbage");
        var library = await fixture.LoadAsync();

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, broken));
        Assert.Null(library.GetBinding(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task SetActive_PersistsAcrossReload()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var second = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        await library.SetActivePlateAsync(Characters.Alice, second.PlateId);

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(second.PlateId, reloaded.GetActivePlateId(Characters.Alice.ContentId));
    }
}

public class PlateDeleteTests
{
    [Fact]
    public async Task DeleteInactive_RemovesDocumentIndexAndAssociation_KeepsActive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var inactive = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        var result = await library.DeletePlateAsync(inactive.PlateId);

        Assert.Empty(result.ClearedActiveForContentIds);
        Assert.False(File.Exists(fixture.Paths.GetPlatePath(inactive.PlateId)));
        Assert.Null(library.FindPlate(inactive.PlateId));
        Assert.DoesNotContain(inactive.PlateId, fixture.ReadLibraryOrder());
        Assert.DoesNotContain(inactive.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(active.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task DeleteActive_ClearsActive_AndNeverPicksAnother()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        var result = await library.DeletePlateAsync(active.PlateId);

        Assert.Equal([Characters.Alice.ContentId], result.ClearedActiveForContentIds);
        Assert.Null(library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Null(library.GetBinding(Characters.Alice.ContentId)!.ActivePlateId);
        Assert.Equal(JsonValueKind.Null, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("ActiveProfileId").ValueKind);

        var reloaded = await fixture.LoadAsync();
        Assert.Null(reloaded.GetActivePlateId(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task Delete_ClearsActiveForEveryCharacterUsingIt()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.SetActivePlateAsync(Characters.Bob, plate.PlateId);

        var result = await library.DeletePlateAsync(plate.PlateId);

        Assert.Equal(2, result.ClearedActiveForContentIds.Count);
        Assert.Null(library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Null(library.GetActivePlateId(Characters.Bob.ContentId));
    }

    [Fact]
    public async Task Delete_NeverDeletesAssets()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Has images", fixture.Clock.Now), Persistence.JsonOptions.Default));
        var assetPath = TestImages.Write(fixture.Paths.AssetsDirectory, SampleDocuments.ImageAsset.ToString("N") + ".png", TestImages.Png(8, 8));
        var library = await fixture.LoadAsync();

        await library.DeletePlateAsync(plateId);

        Assert.True(File.Exists(assetPath));
    }

    [Fact]
    public async Task Delete_MovesDocumentToTrash_Intact()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Trash me");
        var original = fixture.ReadPlateJson(plate.PlateId);

        await library.DeletePlateAsync(plate.PlateId);

        var trashed = Directory.GetFiles(fixture.Paths.PlateTrashDirectory, $"{plate.PlateId}*.json");
        Assert.Single(trashed);
        Assert.Equal(original, File.ReadAllText(trashed[0]));
    }

    [Fact]
    public async Task DeletedPlate_IsNotResurrected_ByReload()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        await library.DeletePlateAsync(plate.PlateId);

        var reloaded = await fixture.LoadAsync();

        Assert.Null(reloaded.FindPlate(plate.PlateId));
        Assert.Empty(reloaded.GetOrderedPlates());
    }

    [Fact]
    public async Task DeletedPlate_IsNotResurrected_FromReliableStorageBackups()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        await library.DeletePlateAsync(plate.PlateId);

        // The backup database still holds the deleted Plate, as Dalamud's would.
        Assert.True(store.Backups.ContainsKey(fixture.Paths.GetPlatePath(plate.PlateId)));

        var reloaded = new PlateLibraryService(fixture.Paths, store, fixture.Log, () => fixture.Clock.Now);
        await reloaded.InitializeAsync();
        Assert.Null(reloaded.FindPlate(plate.PlateId));
    }

    [Fact]
    public async Task SavingADeletedPlate_IsRefused_AndDoesNotRecreateIt()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var openCopy = library.OpenDocumentForEditing(plate.PlateId);
        await library.DeletePlateAsync(plate.PlateId);

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SavePlateDocumentAsync(openCopy));
        Assert.False(File.Exists(fixture.Paths.GetPlatePath(plate.PlateId)));
    }

    [Fact]
    public async Task Delete_RaisesEvent()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        Guid? raised = null;
        library.PlateDeleted += id => raised = id;

        await library.DeletePlateAsync(plate.PlateId);

        Assert.Equal(plate.PlateId, raised);
    }

    [Fact]
    public async Task Delete_UnreadablePlate_MovesItToTrashUntouched()
    {
        using var fixture = new LibraryFixture();
        var broken = Guid.NewGuid();
        fixture.WritePlateJson(broken, "{ damaged");
        var library = await fixture.LoadAsync();

        await library.DeletePlateAsync(broken);

        Assert.Empty(library.GetOrderedPlates());
        Assert.Equal("{ damaged", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory))));
    }
}

public class PlateOrderingAndSearchTests
{
    [Fact]
    public async Task Move_ReordersAndPersists()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var a = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var b = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var c = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        // c, b, a → move c after a → b, a, c
        await library.MovePlateAsync(c.PlateId, a.PlateId, placeAfter: true);

        Assert.Equal([b.PlateId, a.PlateId, c.PlateId], library.GetOrderedPlates().Select(p => p.PlateId));
        var reloaded = await fixture.LoadAsync();
        Assert.Equal([b.PlateId, a.PlateId, c.PlateId], reloaded.GetOrderedPlates().Select(p => p.PlateId));
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_OnNamesAndCharacterNames()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var gpose = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Gpose Showcase");
        var alices = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice, "Roleplay");
        await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Other");

        Assert.Equal([gpose.PlateId], library.Search("SHOWCASE").Select(p => p.PlateId));
        Assert.Equal([alices.PlateId], library.Search("alice").Select(p => p.PlateId));
        Assert.Equal(3, library.Search("  ").Count);
        Assert.Empty(library.Search("nothing matches"));
    }

    [Fact]
    public async Task MissingIdsInOrder_AreSkipped_NotFatal_AndKept()
    {
        using var fixture = new LibraryFixture();
        var real = Guid.NewGuid();
        var ghost = Guid.NewGuid();
        fixture.WritePlateJson(real, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, real, "Real", fixture.Clock.Now), Persistence.JsonOptions.Default));
        fixture.WriteLibraryJson($$"""{ "Version": 1, "OrderedPlateIds": ["{{ghost}}", "{{real}}", "{{real}}"] }""");

        var library = await fixture.LoadAsync();

        Assert.Equal([real], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([ghost, real], fixture.ReadLibraryOrder());
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("no longer exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlatesMissingFromOrder_AreAppended()
    {
        using var fixture = new LibraryFixture();
        var listed = Guid.NewGuid();
        var unlisted = Guid.NewGuid();
        fixture.WritePlateJson(listed, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, listed, "Listed", fixture.Clock.Now), Persistence.JsonOptions.Default));
        fixture.WritePlateJson(unlisted, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, unlisted, "Unlisted", fixture.Clock.Now), Persistence.JsonOptions.Default));
        fixture.WriteLibraryJson($$"""{ "Version": 1, "OrderedPlateIds": ["{{listed}}"] }""");

        var library = await fixture.LoadAsync();

        Assert.Equal([listed, unlisted], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([listed, unlisted], fixture.ReadLibraryOrder());
    }
}
