using System;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

public class PlateViewerTargetTests
{
    private static PlateViewerContent Resolve(PlateViewerTarget target, PlateLibraryService library, CurrentCharacterStub character, ProfileDocument? live = null) =>
        target.Resolve(character.ResolverFor(library), library.GetSavedDocument, live);

    [Fact]
    public void NewViewer_DefaultsToTheActivePlateRequest()
    {
        var target = new PlateViewerTarget();

        Assert.Equal(PlateViewerRequestKind.ActivePlate, target.Kind);
        Assert.Null(target.PlateId);
        Assert.Null(target.Document);
    }

    [Fact]
    public async Task NoExplicitPlate_ShowsTheActivePlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        var content = Resolve(new PlateViewerTarget(), library, new CurrentCharacterStub());

        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(library.GetSavedDocument(active.PlateId), content.Document);
    }

    [Fact]
    public async Task ExplicitPlate_OverridesTheActivePlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();

        target.RequestPlate(other.PlateId);
        var content = Resolve(target, library, new CurrentCharacterStub());

        Assert.Equal(PlateViewerRequestKind.Plate, target.Kind);
        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(library.GetSavedDocument(other.PlateId), content.Document);
    }

    [Fact]
    public async Task ExplicitPlate_IsShownEvenWithNoActivePlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var unbound = await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null);
        var target = new PlateViewerTarget();

        target.RequestPlate(unbound.PlateId);

        Assert.Same(library.GetSavedDocument(unbound.PlateId), Resolve(target, library, new CurrentCharacterStub()).Document);
    }

    [Fact]
    public async Task ExplicitDocument_OverridesTheActivePlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var templatePreview = new ProfileDocument();
        var target = new PlateViewerTarget();

        target.RequestDocument(templatePreview);
        var content = Resolve(target, library, new CurrentCharacterStub());

        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(templatePreview, content.Document);
    }

    [Fact]
    public async Task RequestingActiveAgain_AfterAnExplicitPlate_ReturnsToTheActivePlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();
        target.RequestPlate(other.PlateId);

        target.RequestActivePlate();

        Assert.Equal(PlateViewerRequestKind.ActivePlate, target.Kind);
        Assert.Null(target.PlateId);
        Assert.Same(library.GetSavedDocument(active.PlateId), Resolve(target, library, new CurrentCharacterStub()).Document);
    }

    [Fact]
    public async Task NoActivePlate_ShowsTheEmptyState_NeverAnotherPlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var bobs = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null);

        // Even the Plate open in the editors isn't substituted for a missing Active Plate.
        var live = library.OpenDocumentForEditing(bobs.PlateId);
        var content = Resolve(new PlateViewerTarget(), library, new CurrentCharacterStub(), live);

        Assert.Equal(PlateViewerState.NoActivePlate, content.State);
        Assert.Null(content.Document);
    }

    [Fact]
    public async Task ActivePlate_OpenInTheEditors_ShowsTheSavedDocument_NotTheLiveOne()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var live = library.OpenDocumentForEditing(active.PlateId);

        var content = Resolve(new PlateViewerTarget(), library, new CurrentCharacterStub(), live);

        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(library.GetSavedDocument(active.PlateId), content.Document);
        Assert.NotSame(live, content.Document);
    }

    [Fact]
    public async Task ActivePlate_WhileAnotherPlateIsOpenInTheEditors_ShowsTheSavedActivePlate()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var live = library.OpenDocumentForEditing(other.PlateId);

        var content = Resolve(new PlateViewerTarget(), library, new CurrentCharacterStub(), live);

        Assert.Same(library.GetSavedDocument(active.PlateId), content.Document);
    }

    [Fact]
    public async Task DefaultRequest_FollowsSetActive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var second = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();
        var character = new CurrentCharacterStub();

        await library.SetActivePlateAsync(Characters.Alice, second.PlateId);

        Assert.Same(library.GetSavedDocument(second.PlateId), Resolve(target, library, character).Document);
    }

    [Fact]
    public async Task DefaultRequest_AfterTheActivePlateIsDeleted_ShowsTheEmptyState()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();
        var character = new CurrentCharacterStub();
        Assert.Equal(PlateViewerState.Showing, Resolve(target, library, character).State);

        await library.DeletePlateAsync(active.PlateId);

        Assert.Equal(PlateViewerState.NoActivePlate, Resolve(target, library, character).State);
    }

    [Fact]
    public async Task DefaultRequest_FollowsTheLoggedInCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alices = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();
        var character = new CurrentCharacterStub();
        Assert.Same(library.GetSavedDocument(alices.PlateId), Resolve(target, library, character).Document);

        character.Current = Characters.Bob;
        Assert.Equal(PlateViewerState.NoActivePlate, Resolve(target, library, character).State);

        character.Current = null;
        Assert.Equal(PlateViewerState.NoCharacter, Resolve(target, library, character).State);
    }

    [Fact]
    public async Task ExplicitPlate_ThatNoLongerExists_IsUnavailable()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();

        target.RequestPlate(Guid.NewGuid());
        var content = Resolve(target, library, new CurrentCharacterStub());

        Assert.Equal(PlateViewerState.PlateUnavailable, content.State);
        Assert.Null(content.Document);
    }

    [Fact]
    public async Task ActivePlate_ThatCantBeRead_IsUnavailable()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        fixture.WritePlateJson(active.PlateId, "{ damaged");
        var reloaded = await fixture.LoadAsync();

        Assert.Equal(PlateViewerState.PlateUnavailable, Resolve(new PlateViewerTarget(), reloaded, new CurrentCharacterStub()).State);
    }

    [Fact]
    public void DefaultRequest_BeforeTheLibraryLoads_IsLibraryUnavailable()
    {
        using var fixture = new LibraryFixture();

        Assert.Equal(PlateViewerState.LibraryUnavailable, Resolve(new PlateViewerTarget(), fixture.CreateService(), new CurrentCharacterStub()).State);
    }
}
