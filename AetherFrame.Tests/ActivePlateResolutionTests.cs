using System;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>A resolver over a fixture's library, for whichever character is "logged in".</summary>
internal sealed class CurrentCharacterStub
{
    internal CharacterContext? Current { get; set; } = Characters.Alice;

    internal ActivePlateResolver ResolverFor(PlateLibraryService library) => new(library, () => Current);
}

public class ActivePlateResolutionTests
{
    [Fact]
    public async Task CurrentCharacter_WithActivePlate_ResolvesThatPlatesSavedDocument()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice);

        var resolution = new CurrentCharacterStub().ResolverFor(library).ResolveForCurrentCharacter();

        Assert.Equal(ActivePlateStatus.Resolved, resolution.Status);
        Assert.True(resolution.IsResolved);
        Assert.Equal(plate.PlateId, resolution.PlateId);
        Assert.Same(library.GetSavedDocument(plate.PlateId), resolution.Document);
        Assert.Equal(plate.PlateId, resolution.Document!.ProfileId);
    }

    [Fact]
    public async Task CurrentCharacter_WithNoActivePlate_ResolvesNone_EvenWhenOtherPlatesExist()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);

        var resolution = new CurrentCharacterStub().ResolverFor(library).ResolveForCurrentCharacter();

        Assert.Equal(ActivePlateStatus.NoActivePlate, resolution.Status);
        Assert.Null(resolution.PlateId);
        Assert.Null(resolution.Document);
    }

    [Fact]
    public async Task ActivePlate_PointingAtMissingPlate_ResolvesNone_AndLeavesTheBindingAlone()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        // The Active Plate's document vanishes outside AetherFrame.
        File.Delete(fixture.Paths.GetPlatePath(active.PlateId));
        var reloaded = await fixture.LoadAsync();
        var bindingBefore = fixture.ReadBindingJson(Characters.Alice.ContentId);

        var resolution = new CurrentCharacterStub().ResolverFor(reloaded).ResolveForCurrentCharacter();

        Assert.Equal(ActivePlateStatus.NoActivePlate, resolution.Status);
        Assert.Null(resolution.Document);

        // Resolution is read-only: no replacement Active Plate, no rewrite of the stale id.
        Assert.Equal(active.PlateId, reloaded.GetBinding(Characters.Alice.ContentId)!.ActivePlateId);
        Assert.Equal(bindingBefore, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task ActivePlate_ThatCantBeRead_IsReportedUnavailable_NotReplaced()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        fixture.WritePlateJson(active.PlateId, "{ damaged");
        var reloaded = await fixture.LoadAsync();

        var resolution = new CurrentCharacterStub().ResolverFor(reloaded).ResolveForCurrentCharacter();

        Assert.Equal(ActivePlateStatus.PlateUnavailable, resolution.Status);
        Assert.Equal(active.PlateId, resolution.PlateId);
        Assert.Null(resolution.Document);
    }

    [Fact]
    public async Task ChangingActive_ChangesSubsequentResolution()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var first = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var second = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice);
        var resolver = new CurrentCharacterStub().ResolverFor(library);
        Assert.Equal(first.PlateId, resolver.ResolveForCurrentCharacter().PlateId);

        await library.SetActivePlateAsync(Characters.Alice, second.PlateId);

        var resolution = resolver.ResolveForCurrentCharacter();
        Assert.Equal(ActivePlateStatus.Resolved, resolution.Status);
        Assert.Equal(second.PlateId, resolution.PlateId);
        Assert.Equal(second.PlateId, resolution.Document!.ProfileId);
    }

    [Fact]
    public async Task DeletingActive_LeavesNoActivePlate_WithoutChoosingAnother()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var resolver = new CurrentCharacterStub().ResolverFor(library);

        await library.DeletePlateAsync(active.PlateId);

        Assert.Equal(ActivePlateStatus.NoActivePlate, resolver.ResolveForCurrentCharacter().Status);
        Assert.Null(library.GetBinding(Characters.Alice.ContentId)!.ActivePlateId);
        Assert.Contains(other.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);

        // Still none after a reload: nothing picks a replacement at startup either.
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(ActivePlateStatus.NoActivePlate, new CurrentCharacterStub().ResolverFor(reloaded).ResolveForCurrentCharacter().Status);
    }

    [Fact]
    public async Task DifferentCharacters_ResolveTheirOwnActivePlates()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alices = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bobs = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        var character = new CurrentCharacterStub();
        var resolver = character.ResolverFor(library);

        Assert.Equal(alices.PlateId, resolver.ResolveForCurrentCharacter().PlateId);
        character.Current = Characters.Bob;
        Assert.Equal(bobs.PlateId, resolver.ResolveForCurrentCharacter().PlateId);

        // Bob making Alice's Plate Active changes only Bob's resolution.
        await library.SetActivePlateAsync(Characters.Bob, alices.PlateId);
        Assert.Equal(alices.PlateId, resolver.Resolve(Characters.Bob).PlateId);
        Assert.Equal(alices.PlateId, resolver.Resolve(Characters.Alice).PlateId);
        await library.SetActivePlateAsync(Characters.Alice, bobs.PlateId);
        Assert.Equal(bobs.PlateId, resolver.Resolve(Characters.Alice).PlateId);
        Assert.Equal(alices.PlateId, resolver.Resolve(Characters.Bob).PlateId);
    }

    [Fact]
    public async Task NoCharacterLoggedIn_ResolvesNoCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        var resolution = new CurrentCharacterStub { Current = null }.ResolverFor(library).ResolveForCurrentCharacter();

        Assert.Equal(ActivePlateStatus.NoCharacter, resolution.Status);
        Assert.Null(resolution.PlateId);
    }

    [Fact]
    public void LibraryNotLoaded_ResolvesLibraryUnavailable()
    {
        using var fixture = new LibraryFixture();
        var library = fixture.CreateService();

        Assert.Equal(ActivePlateStatus.LibraryUnavailable, new CurrentCharacterStub().ResolverFor(library).ResolveForCurrentCharacter().Status);
    }
}
