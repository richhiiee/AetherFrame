using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The compatibility warning state for Plates holding element types this build can't display.</summary>
public class UnsupportedElementWarningTests
{
    private static int CountUnknownElements(string savedJson) =>
        JsonNode.Parse(savedJson)!["Elements"]!.AsArray().Count(e => e!["elementType"]?.GetValue<string>() == "hologram");

    private static async Task<(LibraryFixture Fixture, PlateLibraryService Library, Guid PlateId)> LoadFuturePlateAsync()
    {
        var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId));
        return (fixture, await fixture.LoadAsync(), plateId);
    }

    [Fact]
    public async Task SupportedElementsOnly_HasNoWarningState()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Normal", fixture.Clock.Now), JsonOptions.Default));
        var library = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        foreach (var id in new[] { plateId, created.PlateId })
        {
            Assert.False(library.FindPlate(id)!.HasUnsupportedElements);
            var document = library.OpenDocumentForEditing(id);
            Assert.False(document.HasUnsupportedElements);
            Assert.Equal(0, document.UnsupportedElementCount);
        }
    }

    [Fact]
    public async Task UnknownElement_ReportsUnsupportedContent_WithoutMakingThePlateUnreadable()
    {
        var (fixture, library, plateId) = await LoadFuturePlateAsync();
        using var _ = fixture;

        var summary = library.FindPlate(plateId)!;
        var document = library.OpenDocumentForEditing(plateId);

        Assert.Equal(PlateStatus.Ready, summary.Status);
        Assert.True(summary.HasUnsupportedElements);
        Assert.True(library.GetSavedDocument(plateId)!.HasUnsupportedElements);
        Assert.True(document.HasUnsupportedElements);
        Assert.Equal(1, document.UnsupportedElementCount);
        Assert.Equal(2, document.Elements.Count);
    }

    [Fact]
    public async Task Saving_PreservesTheUnknownElement_AndTheWarning()
    {
        var (fixture, library, plateId) = await LoadFuturePlateAsync();
        using var _ = fixture;
        var editor = new ProfileService(library);
        editor.OpenPlate(plateId);

        await editor.SaveCurrentProfileAsync();

        Assert.Equal(1, CountUnknownElements(fixture.ReadPlateJson(plateId)));
        Assert.True(library.FindPlate(plateId)!.HasUnsupportedElements);
        Assert.True(editor.CurrentProfile!.HasUnsupportedElements);
    }

    [Fact]
    public async Task EditingKnownElements_NeverRemovesTheUnknownElement()
    {
        var (fixture, library, plateId) = await LoadFuturePlateAsync();
        using var _ = fixture;
        var editor = new ProfileService(library);
        editor.OpenPlate(plateId);

        editor.UpdateElement(FutureData.TextId, e => ((TextProfileElement)e).Text = "Edited");
        editor.RemoveElement(FutureData.ImageId);
        var added = editor.AddTextElement("Added");
        editor.BringToFront(added);
        editor.ResizeCanvas(1600, 900, scaleContentsProportionally: true);
        editor.RestoreDocumentState(editor.CaptureDocumentState());
        await editor.SaveCurrentProfileAsync();

        var saved = fixture.ReadPlateJson(plateId);
        Assert.Equal(1, CountUnknownElements(saved));
        Assert.True(JsonNode.DeepEquals(FutureData.UnknownElement(),
            JsonNode.Parse(saved)!["Elements"]!.AsArray().Single(e => e!["elementType"]!.GetValue<string>() == "hologram")));
        Assert.True(editor.CurrentProfile!.HasUnsupportedElements);
    }

    [Fact]
    public async Task Duplicate_PreservesTheUnknownElement_AndTheWarning()
    {
        var (fixture, library, plateId) = await LoadFuturePlateAsync();
        using var _ = fixture;

        var copyId = await library.DuplicatePlateAsync(plateId);

        Assert.True(library.FindPlate(copyId)!.HasUnsupportedElements);
        Assert.True(library.OpenDocumentForEditing(copyId).HasUnsupportedElements);
        Assert.Equal(1, CountUnknownElements(fixture.ReadPlateJson(copyId)));
        Assert.True(library.FindPlate(plateId)!.HasUnsupportedElements);
    }

    [Fact]
    public async Task Reload_PreservesTheWarningState()
    {
        var (fixture, library, plateId) = await LoadFuturePlateAsync();
        using var _ = fixture;
        await library.RenamePlateAsync(plateId, "Renamed");
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        var reloaded = await fixture.LoadAsync();

        Assert.True(reloaded.FindPlate(plateId)!.HasUnsupportedElements);
        Assert.True(reloaded.OpenDocumentForEditing(plateId).HasUnsupportedElements);
        Assert.Equal(1, CountUnknownElements(fixture.ReadPlateJson(plateId)));
    }

    [Fact]
    public async Task WarningPlate_IsFullyEditable_NotReadOnly()
    {
        var (fixture, library, plateId) = await LoadFuturePlateAsync();
        using var _ = fixture;
        var editor = new ProfileService(library);

        editor.OpenPlate(plateId);
        editor.AddTextElement("Still editable");
        await editor.SaveCurrentProfileAsync();
        await library.SetActivePlateAsync(Characters.Alice, plateId);

        Assert.Equal(3, library.OpenDocumentForEditing(plateId).Elements.Count);
        Assert.Equal(plateId, library.GetActivePlateId(Characters.Alice.ContentId));
    }
}
