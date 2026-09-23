using System;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The editors' side: <see cref="ProfileService"/> holding the open Plate.</summary>
public class OpenPlateTests
{
    [Fact]
    public async Task OpenPlate_GivesAnIndependentDocument_AndNeedsNoCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);

        editor.OpenPlate(plate.PlateId);
        editor.AddTextElement("Unsaved");

        Assert.Equal(plate.PlateId, editor.OpenPlateId);
        Assert.Single(editor.CurrentProfile!.Elements);
        Assert.Empty(library.GetSavedDocument(plate.PlateId)!.Elements);
        Assert.NotSame(library.GetSavedDocument(plate.PlateId), editor.CurrentProfile);
    }

    [Fact]
    public async Task ReopeningTheOpenPlate_KeepsItsEdits()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);
        var document = editor.CurrentProfile;
        editor.AddTextElement("Keep me");

        editor.OpenPlate(plate.PlateId);

        Assert.Same(document, editor.CurrentProfile);
        Assert.Single(editor.CurrentProfile!.Elements);
    }

    [Fact]
    public async Task OpeningAnotherPlate_ReplacesTheDocumentInstance()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var a = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var b = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(a.PlateId);
        var first = editor.CurrentProfile;

        editor.OpenPlate(b.PlateId);

        Assert.NotSame(first, editor.CurrentProfile);
        Assert.Equal(b.PlateId, editor.OpenPlateId);
    }

    [Fact]
    public async Task Save_WritesThePlate_BumpsRevision_AndNeverActivates()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var editor = new ProfileService(library);
        editor.OpenPlate(other.PlateId);
        editor.AddTextElement("Saved text");

        await editor.SaveCurrentProfileAsync();

        Assert.Equal(1, editor.CurrentProfile!.Revision);
        var saved = library.OpenDocumentForEditing(other.PlateId);
        Assert.Equal(1, saved.Revision);
        Assert.Equal("Saved text", Assert.IsType<TextProfileElement>(Assert.Single(saved.Elements)).Text);
        Assert.Equal(active.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task RenameInLibrary_RelabelsTheOpenPlate_WithoutMakingItDirty()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Old");
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);
        var before = ProfileService.DocumentState.Capture(editor.CurrentProfile!);

        await library.RenamePlateAsync(plate.PlateId, "New");

        Assert.Equal("New", editor.CurrentProfile!.Name);
        var after = ProfileService.DocumentState.Capture(editor.CurrentProfile);
        Assert.Equal(before.Elements.Count, after.Elements.Count);
        Assert.Equal(before.CanvasWidth, after.CanvasWidth);

        await editor.SaveCurrentProfileAsync();
        Assert.Equal("New", library.FindPlate(plate.PlateId)!.DisplayName);
    }

    [Fact]
    public async Task DeletingTheOpenPlate_ClosesIt_AndItCanNeverBeSavedBack()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);

        await library.DeletePlateAsync(plate.PlateId);

        Assert.Null(editor.CurrentProfile);
        Assert.Null(editor.OpenPlateId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.SaveCurrentProfileAsync());
        Assert.Throws<InvalidOperationException>(() => editor.AddTextElement("x"));
    }

    [Fact]
    public async Task DeletingAnotherPlate_LeavesTheOpenOneAlone()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var open = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(open.PlateId);
        var document = editor.CurrentProfile;

        await library.DeletePlateAsync(other.PlateId);

        Assert.Same(document, editor.CurrentProfile);
    }

    [Fact]
    public async Task OpeningAnUnreadablePlate_Throws_AndKeepsWhatWasOpen()
    {
        using var fixture = new LibraryFixture();
        var broken = Guid.NewGuid();
        fixture.WritePlateJson(broken, "{ nope");
        var library = await fixture.LoadAsync();
        var good = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(good.PlateId);

        Assert.Throws<PlateLibraryException>(() => editor.OpenPlate(broken));
        Assert.Equal(good.PlateId, editor.OpenPlateId);
    }

    [Fact]
    public async Task CloseDocument_LeavesTheSavedPlateUntouched()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var before = fixture.ReadPlateJson(plate.PlateId);
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);
        editor.AddTextElement("Discarded");

        editor.CloseDocument();

        Assert.Null(editor.CurrentProfile);
        Assert.Equal(before, fixture.ReadPlateJson(plate.PlateId));
    }
}
