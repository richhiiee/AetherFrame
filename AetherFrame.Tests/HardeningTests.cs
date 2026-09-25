using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>A current document with unknown data at every level, as a newer build might write it.</summary>
internal static class FutureData
{
    internal static readonly Guid TextId = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    internal static readonly Guid ImageId = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    internal static readonly Guid HologramAsset = Guid.Parse("99999999-8888-7777-6666-555555555555");

    internal static JsonObject UnknownElement() => new()
    {
        ["elementType"] = "hologram",
        ["Id"] = "00000000-0000-0000-0000-00000000a003",
        ["HoloAsset"] = HologramAsset.ToString(),
        ["Shimmer"] = new JsonArray(1, 2, 3),
        ["ZIndex"] = 7,
    };

    internal static string Document(Guid plateId, int version = 2)
    {
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, plateId, "Future", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        document.Version = version;
        document.BasicIdentity = new BasicIdentityHeader { TitleSource = IdentityTitleSource.Custom, CustomTitle = "Hero" };
        document.Elements.Add(new TextProfileElement { Id = TextId, Text = "Hi" });
        document.Elements.Add(new ImageProfileElement { Id = ImageId, AssetId = Guid.NewGuid() });

        var json = JsonSerializer.SerializeToNode(document, JsonOptions.Default)!.AsObject();
        var elements = json["Elements"]!.AsArray();
        elements[0]!["FutureGlow"] = new JsonObject { ["Radius"] = 4, ["Color"] = "gold" };
        elements[0]!["Tags"] = new JsonArray("common", "base");
        elements[1]!["FutureMask"] = "soft-circle";
        elements[1]!["Tags"] = new JsonArray("portrait");
        json["Background"]!["FutureParallax"] = 3;
        json["BasicIdentity"]!["FutureBadgeStyle"] = "gilded";
        json["FutureTopLevel"] = true;
        elements.Add(UnknownElement());
        return json.ToJsonString(JsonOptions.Default);
    }

    /// <summary>Asserts every piece of unknown data is present, unchanged, in a saved file.</summary>
    internal static void AssertAllPreserved(string savedJson)
    {
        var json = JsonNode.Parse(savedJson)!.AsObject();
        var elements = json["Elements"]!.AsArray();
        var text = elements.Single(e => e!["Id"]!.GetValue<string>() == TextId.ToString())!;
        var image = elements.Single(e => e!["Id"]!.GetValue<string>() == ImageId.ToString())!;
        var unknown = elements.Single(e => e!["elementType"]!.GetValue<string>() == "hologram")!;

        Assert.Equal(4, text["FutureGlow"]!["Radius"]!.GetValue<int>());
        Assert.Equal("gold", text["FutureGlow"]!["Color"]!.GetValue<string>());
        Assert.Equal(["common", "base"], text["Tags"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("soft-circle", image["FutureMask"]!.GetValue<string>());
        Assert.Equal(["portrait"], image["Tags"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(3, json["Background"]!["FutureParallax"]!.GetValue<int>());
        Assert.Equal("gilded", json["BasicIdentity"]!["FutureBadgeStyle"]!.GetValue<string>());
        Assert.True(json["FutureTopLevel"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(UnknownElement(), unknown));

        // The type discriminator is written exactly once per element (never duplicated from extension data).
        Assert.Equal(elements.Count, System.Text.RegularExpressions.Regex.Matches(savedJson, "\"elementType\"").Count);
    }
}

public class ElementForwardCompatibilityTests
{
    [Fact]
    public async Task UnknownData_SurvivesLoadAndLibrarySave()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId));
        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        var document = library.OpenDocumentForEditing(plateId);
        Assert.Equal(2, document.Elements.Count);
        Assert.Single(document.UnrecognizedElements!);

        document.CanvasWidth = 1000;
        await library.SavePlateDocumentAsync(document);

        FutureData.AssertAllPreserved(fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task UnknownData_SurvivesEditorEditsUndoAndSave()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId));
        var library = await fixture.LoadAsync();
        var editor = new ProfileService(library);
        editor.OpenPlate(plateId);

        // An undo snapshot of each element, an edit, then restoring the snapshot in place — the
        // exact path the editor's undo/redo takes (Clone + CopyFrom).
        var textBefore = editor.CloneElement(FutureData.TextId);
        var imageBefore = editor.CloneElement(FutureData.ImageId);
        editor.UpdateElement(FutureData.TextId, e => ((TextProfileElement)e).Text = "Changed");
        editor.UpdateElement(FutureData.ImageId, e => e.Visible = false);
        editor.UpdateElement(FutureData.TextId, e => e.CopyFrom(textBefore));
        editor.UpdateElement(FutureData.ImageId, e => e.CopyFrom(imageBefore));
        editor.AddTextElement("Newly added");
        editor.UpdateBackground(b => b.Opacity = 0.5f);
        var documentState = editor.CaptureDocumentState();
        editor.RestoreDocumentState(documentState);

        await editor.SaveCurrentProfileAsync();

        FutureData.AssertAllPreserved(fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task UnknownData_SurvivesRenameAndDuplicate()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId));
        var library = await fixture.LoadAsync();

        await library.RenamePlateAsync(plateId, "Renamed");
        var copyId = await library.DuplicatePlateAsync(plateId);

        FutureData.AssertAllPreserved(fixture.ReadPlateJson(plateId));
        FutureData.AssertAllPreserved(fixture.ReadPlateJson(copyId));

        // And a save of the duplicate keeps them too.
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(copyId));
        FutureData.AssertAllPreserved(fixture.ReadPlateJson(copyId));
    }

    [Fact]
    public async Task UnknownData_SurvivesSchemaMigrationAndSave()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId, version: 1));
        var library = await fixture.LoadAsync();

        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        var saved = fixture.ReadPlateJson(plateId);
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, JsonNode.Parse(saved)!["Version"]!.GetValue<int>());
        FutureData.AssertAllPreserved(saved);
    }

    [Fact]
    public void Clone_CopiesUnknownData_Independently()
    {
        var json = JsonNode.Parse(FutureData.Document(Guid.NewGuid()))!.AsObject();
        var document = PlateDocuments.Materialize(json);
        var text = document.Elements.OfType<TextProfileElement>().Single();

        var clone = (TextProfileElement)text.Clone();
        clone.ExtensionData!.Remove("FutureGlow");

        Assert.True(text.ExtensionData!.ContainsKey("FutureGlow"));
        Assert.False(text.ExtensionData.ContainsKey(ProfileElement.TypeDiscriminatorPropertyName));
        Assert.True(document.Background!.Clone().ExtensionData!.ContainsKey("FutureParallax"));
        Assert.True(document.BasicIdentity!.Clone().ExtensionData!.ContainsKey("FutureBadgeStyle"));
    }

    [Fact]
    public void UnknownData_DoesNotMakeTheEditorDirty()
    {
        var document = PlateDocuments.Materialize(JsonNode.Parse(FutureData.Document(Guid.NewGuid()))!.AsObject());
        var text = document.Elements.OfType<TextProfileElement>().Single();
        var clone = text.Clone();
        clone.ExtensionData = null;

        Assert.True(text.ContentEquals(clone));
    }

    [Fact]
    public async Task UnknownElementType_KeepsThePlateReadable_AndIsNeverGuessedIntoAKnownType()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId));
        var library = await fixture.LoadAsync();

        var document = library.OpenDocumentForEditing(plateId);

        Assert.All(document.Elements, e => Assert.True(e is TextProfileElement or ImageProfileElement));
        Assert.DoesNotContain(document.Elements, e => e.Id == Guid.Parse("00000000-0000-0000-0000-00000000a003"));
        Assert.Equal("hologram", document.UnrecognizedElements!.Single().GetProperty("elementType").GetString());
    }

    [Fact]
    public async Task ElementWithoutAType_IsPreservedRatherThanFailingThePlate()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var json = JsonNode.Parse(JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "x", fixture.Clock.Now), JsonOptions.Default))!.AsObject();
        json["Elements"]!.AsArray().Add(new JsonObject { ["Mystery"] = 1 });
        fixture.WritePlateJson(plateId, json.ToJsonString());
        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        await library.SavePlateDocumentAsync(library.OpenDocumentForEditing(plateId));

        Assert.Equal(1, JsonNode.Parse(fixture.ReadPlateJson(plateId))!["Elements"]!.AsArray().Single()!["Mystery"]!.GetValue<int>());
    }

    [Fact]
    public async Task AssetScan_TreatsGuidsInUnknownData_AsReferences()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, FutureData.Document(plateId));
        var library = await fixture.LoadAsync();

        var scan = await library.ScanAssetReferencesAsync();

        Assert.Contains(FutureData.HologramAsset, scan.ReferencedAssetIds);
    }
}

public class DuplicateAssociationTests
{
    [Fact]
    public async Task BoundSource_CopyIsAssociatedWithTheSameCharacter_NotActive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        var copyId = await library.DuplicatePlateAsync(source.PlateId);

        var binding = library.GetBinding(Characters.Alice.ContentId)!;
        Assert.Contains(copyId, binding.PlateIds);
        Assert.Equal(source.PlateId, binding.ActivePlateId);
        Assert.Contains(copyId, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public async Task UnboundSource_GivesAnUnboundCopy()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        var unbound = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var bobBefore = fixture.ReadBindingJson(Characters.Bob.ContentId);

        var copyId = await library.DuplicatePlateAsync(unbound.PlateId);

        Assert.Empty(library.FindPlate(copyId)!.CharacterNames);
        Assert.DoesNotContain(copyId, library.GetBinding(Characters.Bob.ContentId)!.PlateIds);
        Assert.Equal(bobBefore, fixture.ReadBindingJson(Characters.Bob.ContentId));
        Assert.Single(Directory.GetFiles(fixture.Paths.CharactersDirectory));
    }

    [Fact]
    public async Task SourceWithSeveralCharacters_CopyIsAssociatedWithAllOfThem()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.SetActivePlateAsync(Characters.Bob, source.PlateId);

        var copyId = await library.DuplicatePlateAsync(source.PlateId);

        Assert.Contains(copyId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Contains(copyId, library.GetBinding(Characters.Bob.ContentId)!.PlateIds);
        Assert.Equal(2, library.FindPlate(copyId)!.CharacterNames.Count);
    }

    [Fact]
    public async Task ActiveSource_CopyNeverInheritsActive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var source = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.SetActivePlateAsync(Characters.Bob, source.PlateId);

        var copyId = await library.DuplicatePlateAsync(source.PlateId);

        Assert.Equal(source.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(source.PlateId, library.GetActivePlateId(Characters.Bob.ContentId));
        Assert.Empty(library.FindPlate(copyId)!.ActiveForContentIds);

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(source.PlateId, reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Empty(reloaded.FindPlate(copyId)!.ActiveForContentIds);
    }

    [Fact]
    public async Task UnrelatedCharacter_IsNeverAssociated()
    {
        // Duplicate takes no character at all: whoever is logged in can't change its result.
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alices = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        var bobBefore = fixture.ReadBindingJson(Characters.Bob.ContentId);

        var copyId = await library.DuplicatePlateAsync(alices.PlateId);

        Assert.DoesNotContain(copyId, library.GetBinding(Characters.Bob.ContentId)!.PlateIds);
        Assert.Equal(bobBefore, fixture.ReadBindingJson(Characters.Bob.ContentId));
        Assert.Equal(["Alice Example"], library.FindPlate(copyId)!.CharacterNames);
    }

    [Fact]
    public async Task Copy_IsANewPlate_WithItsOwnTimestamps_AndMatchingCreativeState()
    {
        using var fixture = new LibraryFixture();
        var sourceId = Guid.NewGuid();
        var source = SampleDocuments.Rich(sourceId, "Old", new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        source.UpdatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        source.Revision = 12;
        fixture.WritePlateJson(sourceId, JsonSerializer.Serialize(source, JsonOptions.Default));
        var library = await fixture.LoadAsync();
        fixture.Clock.Now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        var copyId = await library.DuplicatePlateAsync(sourceId);

        var summary = library.FindPlate(copyId)!;
        var copy = library.OpenDocumentForEditing(copyId);
        Assert.NotEqual(sourceId, copyId);
        Assert.Equal(copyId, copy.ProfileId);
        Assert.Equal(fixture.Clock.Now, summary.CreatedUtc);
        Assert.Equal(fixture.Clock.Now, summary.ModifiedUtc);
        Assert.Equal(fixture.Clock.Now, copy.CreatedAtUtc);
        Assert.Equal(fixture.Clock.Now, copy.UpdatedAtUtc);
        Assert.Equal(0, copy.Revision);

        var original = library.OpenDocumentForEditing(sourceId);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), original.CreatedAtUtc);
        JsonAssert.EqualExcept(fixture.ReadPlateJson(sourceId), fixture.ReadPlateJson(copyId),
            "ProfileId", "Name", "Revision", "CreatedAtUtc", "UpdatedAtUtc", "OwnerContentId");

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(fixture.Clock.Now, reloaded.FindPlate(copyId)!.CreatedUtc);
    }
}

public class SetActiveOnUnboundPlateTests
{
    [Fact]
    public async Task PlateCreatedLoggedOut_SetActive_AssociatesAndActivates_InOneWrite()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var unbound = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        await library.SetActivePlateAsync(Characters.Alice, unbound.PlateId);

        var binding = fixture.ReadBinding(Characters.Alice.ContentId);
        Assert.Equal(unbound.PlateId, binding.GetProperty("ActiveProfileId").GetGuid());
        Assert.Equal([unbound.PlateId], binding.GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(["Alice Example"], library.FindPlate(unbound.PlateId)!.CharacterNames);

        var reloaded = await fixture.LoadAsync();
        Assert.Equal(unbound.PlateId, reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(unbound.PlateId, reloaded.GetBinding(Characters.Alice.ContentId)!.PlateIds);
    }

    [Fact]
    public async Task SetActive_OnUnassociatedPlate_KeepsTheCharactersOtherPlates()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var previous = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var unbound = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        await library.SetActivePlateAsync(Characters.Alice, unbound.PlateId);

        var binding = library.GetBinding(Characters.Alice.ContentId)!;
        Assert.Equal(unbound.PlateId, binding.ActivePlateId);
        Assert.Equal([previous.PlateId, unbound.PlateId], binding.PlateIds);
    }

    [Fact]
    public async Task SetActive_ForOneCharacter_LeavesThePlateUnboundForOthers()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var unbound = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        await library.SetActivePlateAsync(Characters.Alice, unbound.PlateId);

        Assert.Null(library.GetBinding(Characters.Bob.ContentId));
    }
}

public class RenameWithUnsavedEditsTests
{
    private static async Task<(LibraryFixture Fixture, PlateLibraryService Library, ProfileService Editor, Guid PlateId, ProfileService.DocumentState Baseline)> OpenWithUnsavedEditAsync()
    {
        var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Before");
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);

        // What the editor baselines on open (its "last saved" state for dirty checks and Discard).
        var baseline = editor.CaptureDocumentState();
        editor.AddTextElement("Unsaved creative change");
        return (fixture, library, editor, plate.PlateId, baseline);
    }

    [Fact]
    public async Task Rename_Persists_WithoutSavingOrDiscardingUnsavedWork()
    {
        var (fixture, library, editor, plateId, baseline) = await OpenWithUnsavedEditAsync();
        using var _ = fixture;

        await library.RenamePlateAsync(plateId, "After");

        // The rename is on disk; the unsaved change is not.
        var saved = JsonNode.Parse(fixture.ReadPlateJson(plateId))!;
        Assert.Equal("After", saved["Name"]!.GetValue<string>());
        Assert.Empty(saved["Elements"]!.AsArray());

        // The unsaved change is still in the editor, which shows the new name at once.
        Assert.Equal("After", editor.CurrentProfile!.Name);
        Assert.Single(editor.CurrentProfile.Elements);

        // Still dirty: the document differs from its saved baseline (the name isn't part of it).
        Assert.NotEqual(baseline.Elements.Count, editor.CaptureDocumentState().Elements.Count);
    }

    [Fact]
    public async Task SaveAfterRename_KeepsTheNewName_AndTheCreativeEdits()
    {
        var (fixture, library, editor, plateId, _) = await OpenWithUnsavedEditAsync();
        using var __ = fixture;
        await library.RenamePlateAsync(plateId, "After");

        await editor.SaveCurrentProfileAsync();

        var saved = JsonNode.Parse(fixture.ReadPlateJson(plateId))!;
        Assert.Equal("After", saved["Name"]!.GetValue<string>());
        Assert.Equal("Unsaved creative change", saved["Elements"]!.AsArray().Single()!["Text"]!.GetValue<string>());
        Assert.Equal("After", library.FindPlate(plateId)!.DisplayName);
    }

    [Fact]
    public async Task DiscardAfterRename_DropsTheCreativeEdits_ButKeepsTheRename()
    {
        var (fixture, library, editor, plateId, baseline) = await OpenWithUnsavedEditAsync();
        using var _ = fixture;
        await library.RenamePlateAsync(plateId, "After");

        // Discard = restore the saved baseline (EditorSession.DiscardChanges does exactly this).
        editor.RestoreDocumentState(baseline);

        Assert.Empty(editor.CurrentProfile!.Elements);
        Assert.Equal("After", editor.CurrentProfile.Name);
        Assert.Equal("After", library.FindPlate(plateId)!.DisplayName);
        Assert.Equal("After", JsonNode.Parse(fixture.ReadPlateJson(plateId))!["Name"]!.GetValue<string>());

        var reloaded = await fixture.LoadAsync();
        Assert.Equal("After", reloaded.FindPlate(plateId)!.DisplayName);
        Assert.Empty(reloaded.OpenDocumentForEditing(plateId).Elements);
    }
}

public class FailureInjectionTests
{
    private static bool IsPlate(string path) => path.Contains(Path.DirectorySeparatorChar + "Profiles" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsBinding(string path) => path.Contains(Path.DirectorySeparatorChar + "Characters" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsLibrary(string path) => path.EndsWith("library.json", StringComparison.Ordinal);

    [Fact]
    public async Task RenameWriteFailure_ChangesNothing()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Original");
        var before = fixture.ReadPlateJson(plate.PlateId);
        var renamed = false;
        library.PlateRenamed += (_, _) => renamed = true;
        store.FailWrite = IsPlate;

        await Assert.ThrowsAsync<IOException>(() => library.RenamePlateAsync(plate.PlateId, "New"));

        Assert.Equal("Original", library.FindPlate(plate.PlateId)!.DisplayName);
        Assert.Equal(before, fixture.ReadPlateJson(plate.PlateId));
        Assert.False(renamed);

        store.FailWrite = null;
        await library.RenamePlateAsync(plate.PlateId, "New");
        Assert.Equal("New", library.FindPlate(plate.PlateId)!.DisplayName);
    }

    [Fact]
    public async Task DeleteMoveFailure_LeavesThePlateFullyIntact()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var document = fixture.ReadPlateJson(plate.PlateId);
        var binding = fixture.ReadBindingJson(Characters.Alice.ContentId);
        var order = fixture.ReadLibraryOrder();
        store.FailMove = IsPlate;

        await Assert.ThrowsAsync<IOException>(() => library.DeletePlateAsync(plate.PlateId));

        Assert.NotNull(library.FindPlate(plate.PlateId));
        Assert.Equal(plate.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(document, fixture.ReadPlateJson(plate.PlateId));
        Assert.Equal(binding, fixture.ReadBindingJson(Characters.Alice.ContentId));
        Assert.Equal(order, fixture.ReadLibraryOrder());
        Assert.False(Directory.Exists(fixture.Paths.PlateTrashDirectory) && Directory.GetFiles(fixture.Paths.PlateTrashDirectory).Length > 0);
    }

    [Fact]
    public async Task DeleteBindingUpdateFailure_StillDeletes_AndThePlateStaysRecoverable()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var original = fixture.ReadPlateJson(plate.PlateId);
        store.FailWrite = IsBinding;

        await library.DeletePlateAsync(plate.PlateId);

        Assert.Null(library.FindPlate(plate.PlateId));
        Assert.Null(library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory))));

        // The binding on disk still names the deleted Plate; that stale reference is harmless.
        store.FailWrite = null;
        Assert.Equal(plate.PlateId, fixture.ReadBinding(Characters.Alice.ContentId).GetProperty("ActiveProfileId").GetGuid());
        var reloaded = await fixture.LoadAsync();
        Assert.Null(reloaded.FindPlate(plate.PlateId));
        Assert.Null(reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("binding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteOrderUpdateFailure_StillDeletes_WithoutResurrection()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var kept = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var deleted = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        store.FailWrite = IsLibrary;

        await library.DeletePlateAsync(deleted.PlateId);

        store.FailWrite = null;
        Assert.Contains(deleted.PlateId, fixture.ReadLibraryOrder());
        var reloaded = await fixture.LoadAsync();
        Assert.Equal([kept.PlateId], reloaded.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Single(Directory.GetFiles(fixture.Paths.PlateTrashDirectory));
    }

    [Fact]
    public async Task SetActiveWriteFailure_KeepsThePreviousActivePlate()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var other = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var before = fixture.ReadBindingJson(Characters.Alice.ContentId);
        store.FailWrite = IsBinding;

        await Assert.ThrowsAsync<IOException>(() => library.SetActivePlateAsync(Characters.Alice, other.PlateId));

        Assert.Equal(active.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.DoesNotContain(other.PlateId, library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
        Assert.Equal(before, fixture.ReadBindingJson(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task CreateWriteFailure_AddsNothing()
    {
        var store = new FaultInjectingStore { FailWrite = IsPlate };
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();

        await Assert.ThrowsAsync<IOException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice));

        Assert.Empty(library.GetOrderedPlates());
        Assert.Null(library.GetBinding(Characters.Alice.ContentId));
    }

    [Fact]
    public async Task DuplicateAssociationFailure_KeepsTheCopy_AndTheSource()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var source = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var sourceJson = fixture.ReadPlateJson(source.PlateId);
        store.FailWrite = IsBinding;

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.DuplicatePlateAsync(source.PlateId));

        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.Equal(sourceJson, fixture.ReadPlateJson(source.PlateId));
        Assert.Equal(source.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        var copy = library.GetOrderedPlates().Single(p => p.PlateId != source.PlateId);
        Assert.Equal(copy.PlateId, library.OpenDocumentForEditing(copy.PlateId).ProfileId);
    }

    [Fact]
    public async Task SaveFailure_KeepsTheUnsavedEditsInTheEditor()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var editor = new ProfileService(library);
        editor.OpenPlate(plate.PlateId);
        editor.AddTextElement("Precious");
        store.FailWrite = IsPlate;

        await Assert.ThrowsAsync<IOException>(() => editor.SaveCurrentProfileAsync());

        Assert.False(editor.IsBusy);
        Assert.Equal(0, editor.CurrentProfile!.Revision);
        Assert.Equal("Precious", Assert.IsType<TextProfileElement>(Assert.Single(editor.CurrentProfile.Elements)).Text);

        store.FailWrite = null;
        await editor.SaveCurrentProfileAsync();
        Assert.Single(library.OpenDocumentForEditing(plate.PlateId).Elements);
    }
}

public class EditorSurfaceCoordinatorTests
{
    private static (EditorSurfaceCoordinator Coordinator, FakeSurface Basic, FakeSurface Advanced, List<string> Log) Create()
    {
        var log = new List<string>();
        var coordinator = new EditorSurfaceCoordinator(() => log.Add("commit"));
        var basic = new FakeSurface();
        var advanced = new FakeSurface();
        coordinator.Attach(basic, advanced);
        return (coordinator, basic, advanced, log);
    }

    [Fact]
    public void SwitchingSurfaces_HandsOver_AndCommitsInProgressEditsFirst()
    {
        var (coordinator, basic, advanced, log) = Create();
        coordinator.Show(EditorSurfaceKind.Basic);

        coordinator.Show(EditorSurfaceKind.Advanced);

        Assert.False(basic.IsOpen);
        Assert.True(advanced.IsOpen);
        Assert.Equal(["show", "handoff"], basic.Calls);
        Assert.Equal(["commit"], log);
        Assert.Equal(EditorSurfaceKind.Advanced, coordinator.ActiveSurface);
    }

    [Fact]
    public void ShowingTheOpenSurfaceAgain_DoesNotCommitOrClose()
    {
        var (coordinator, basic, advanced, log) = Create();
        coordinator.Show(EditorSurfaceKind.Basic);

        coordinator.Show(EditorSurfaceKind.Basic);

        Assert.True(basic.IsOpen);
        Assert.False(advanced.IsOpen);
        Assert.Empty(log);
    }

    [Fact]
    public void OpeningBySomeOtherPath_StillEnforcesOneSurface()
    {
        var (coordinator, basic, advanced, log) = Create();
        advanced.IsOpen = true;
        basic.IsOpen = true;

        coordinator.NotifyOpened(EditorSurfaceKind.Basic);

        Assert.False(advanced.IsOpen);
        Assert.True(basic.IsOpen);
        Assert.Equal(["commit"], log);
    }

    [Fact]
    public void NoSurfaceOpen_MeansNoOwner()
    {
        var (coordinator, _, _, _) = Create();

        Assert.Null(coordinator.ActiveSurface);
    }
}
