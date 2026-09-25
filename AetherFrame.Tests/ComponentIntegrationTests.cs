using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Components through the real editing stack: Basic slots, Advanced edits, undo and dirty state.</summary>
public class ComponentEditorTests
{
    [Fact]
    public async Task NewPlate_HasNoComponents_AndEverySlotIsEmpty()
    {
        using var harness = await BasicHarness.NewClassicAsync();

        Assert.Null(harness.Document.Components);
        foreach (var kind in PlateComponentEditor.BasicSlots.Concat(PlateComponentEditor.BasicDecorations))
        {
            Assert.Null(PlateComponentEditor.FindSlot(harness.Document, kind));
        }

        harness.SimulateBasicFrame();
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task ChoosingASlotStyle_AddsOneComponentWithDefaults_AsOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();

        harness.Session.SetComponentSlot(PlateComponentKind.PortraitFrame, BuiltInComponentCatalog.PortraitFrameDouble);

        var component = Assert.Single(harness.Document.Components!);
        Assert.Equal(PlateComponentKind.PortraitFrame, component.Kind);
        Assert.Equal(BuiltInComponentCatalog.PortraitFrameDouble, component.DefinitionId);
        Assert.Null(component.Color);
        Assert.Equal(1f, component.Opacity);
        Assert.Equal(1f, component.Scale);
        Assert.Equal(Vector2.Zero, component.Offset);
        Assert.Equal(0f, component.RotationDegrees);
        Assert.True(component.Visible);
        Assert.True(harness.Session.IsDirty);

        harness.Session.Undo();
        Assert.True(harness.Document.Components is null or { Count: 0 });
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);

        harness.Session.Redo();
        Assert.Equal(BuiltInComponentCatalog.PortraitFrameDouble, Assert.Single(harness.Document.Components!).DefinitionId);
    }

    [Fact]
    public async Task ChangingASlotStyle_KeepsTheInstanceAndItsAdvancedRefinements()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.NameBacking, BuiltInComponentCatalog.NameBackingBar);
        var id = harness.Document.Components![0].Id;
        harness.Session.EditComponent(id, c =>
        {
            c.Offset = new Vector2(10, -4);
            c.Scale = 1.2f;
            c.Color = new Vector4(1, 0, 0, 1);
        }, continuous: false);

        harness.Session.SetComponentSlot(PlateComponentKind.NameBacking, BuiltInComponentCatalog.NameBackingRibbon);

        var component = Assert.Single(harness.Document.Components);
        Assert.Equal(id, component.Id);
        Assert.Equal(BuiltInComponentCatalog.NameBackingRibbon, component.DefinitionId);
        Assert.Equal(new Vector2(10, -4), component.Offset);
        Assert.Equal(1.2f, component.Scale);
        Assert.Equal(new Vector4(1, 0, 0, 1), component.Color);
    }

    [Fact]
    public async Task ChoosingTheSameStyle_OrNoneOnAnEmptySlot_RecordsNothing()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.Divider, null);
        Assert.False(harness.Session.CanUndo);

        harness.Session.SetComponentSlot(PlateComponentKind.Divider, BuiltInComponentCatalog.DividerLine);
        harness.Session.SetComponentSlot(PlateComponentKind.Divider, BuiltInComponentCatalog.DividerLine);
        harness.Session.Undo();
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task None_RemovesTheSlotComponent_AsOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.PlateFrame, BuiltInComponentCatalog.PlateFrameLine);
        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, BuiltInComponentCatalog.CornerOrnamentBracket);

        harness.Session.SetComponentSlot(PlateComponentKind.PlateFrame, null);
        Assert.Equal(PlateComponentKind.CornerOrnament, Assert.Single(harness.Document.Components!).Kind);

        harness.Session.Undo();
        Assert.Equal(2, harness.Document.Components!.Count);
    }

    [Fact]
    public async Task BasicSlots_RejectImageStylesAndStylesOfOtherKinds()
    {
        using var harness = await BasicHarness.NewClassicAsync();

        harness.Session.SetComponentSlot(PlateComponentKind.PortraitOverlay, BuiltInComponentCatalog.PortraitOverlayImage);
        Assert.NotNull(harness.Session.ErrorMessage);
        harness.Session.SetComponentSlot(PlateComponentKind.PortraitOverlay, BuiltInComponentCatalog.PlateFrameLine);
        Assert.NotNull(harness.Session.ErrorMessage);
        harness.Session.SetComponentSlot(PlateComponentKind.PortraitOverlay, "af.not.real");
        Assert.NotNull(harness.Session.ErrorMessage);

        Assert.True(harness.Document.Components is null or { Count: 0 });
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task SlotsBindToTheFirstComponentOfTheirKind()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var first = harness.Session.AddComponent(BuiltInComponentCatalog.DividerLine)!.Value;
        var second = harness.Session.AddComponent(BuiltInComponentCatalog.DividerDiamond)!.Value;

        harness.Session.SetComponentSlot(PlateComponentKind.Divider, BuiltInComponentCatalog.DividerDiamond);

        Assert.Equal(BuiltInComponentCatalog.DividerDiamond, PlateComponentEditor.Find(harness.Document, first)!.DefinitionId);
        Assert.Equal(first, PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.Divider)!.Id);
        Assert.NotNull(PlateComponentEditor.Find(harness.Document, second));
    }

    [Fact]
    public async Task ContinuousAdvancedEdit_IsOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.PlateFrameNotched)!.Value;

        for (var i = 1; i <= 20; i++)
        {
            var value = i / 40f;
            harness.Session.EditComponent(id, c => c.Opacity = value, continuous: true);
        }

        Assert.True(harness.Session.IsDirty);
        harness.Session.CommitPendingDocumentEdit();
        Assert.Equal(0.5f, PlateComponentEditor.Find(harness.Document, id)!.Opacity);

        harness.Session.Undo();
        Assert.Equal(1f, PlateComponentEditor.Find(harness.Document, id)!.Opacity);
        harness.Session.Undo();
        Assert.Null(PlateComponentEditor.Find(harness.Document, id));
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task AdvancedState_SurvivesSaveAndReopen()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.CornerOrnamentDiamond)!.Value;
        harness.Session.EditComponent(id, c =>
        {
            c.Offset = new Vector2(6, 8);
            c.Scale = 1.5f;
            c.RotationDegrees = 45f;
            c.Opacity = 0.4f;
            c.Color = new Vector4(0.2f, 0.4f, 0.6f, 0.8f);
            c.LayerOrder = 4;
        }, continuous: false);

        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();
        Assert.False(harness.Session.IsDirty);

        var reopened = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.True(PlateComponent.ListsEqual(harness.Document.Components, reopened.Components));
    }

    [Fact]
    public async Task MoveInLayer_ReordersWithinTheLayerOnly_OneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var line = harness.Session.AddComponent(BuiltInComponentCatalog.DividerLine)!.Value;
        var corner = harness.Session.AddComponent(BuiltInComponentCatalog.CornerOrnamentBracket)!.Value;
        var frame = harness.Session.AddComponent(BuiltInComponentCatalog.PlateFrameLine)!.Value;

        harness.Session.MoveComponentInLayer(line, +1);

        var doc = harness.Document;
        Assert.True(PlateComponentEditor.Find(doc, line)!.LayerOrder > PlateComponentEditor.Find(doc, corner)!.LayerOrder);
        Assert.Equal(0, PlateComponentEditor.Find(doc, frame)!.LayerOrder);

        harness.Session.Undo();
        Assert.True(PlateComponentEditor.Find(doc, line)!.LayerOrder < PlateComponentEditor.Find(doc, corner)!.LayerOrder);

        // Top of its layer: nothing to do, nothing recorded — so the next undo removes the frame added before.
        harness.Session.MoveComponentInLayer(corner, +1);
        harness.Session.Undo();
        Assert.Null(PlateComponentEditor.Find(doc, frame));
        Assert.NotNull(PlateComponentEditor.Find(doc, corner));
    }

    [Fact]
    public async Task RemoveAndReset_AreEachOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.NameBackingFade)!.Value;
        harness.Session.EditComponent(id, c => c.Offset = new Vector2(30, 0), continuous: false);

        harness.Session.ResetComponentTransform(id);
        Assert.Equal(Vector2.Zero, PlateComponentEditor.Find(harness.Document, id)!.Offset);
        harness.Session.Undo();
        Assert.Equal(new Vector2(30, 0), PlateComponentEditor.Find(harness.Document, id)!.Offset);

        harness.Session.RemoveComponent(id);
        Assert.Null(PlateComponentEditor.Find(harness.Document, id));
        harness.Session.Undo();
        Assert.NotNull(PlateComponentEditor.Find(harness.Document, id));
    }

    [Fact]
    public async Task ComponentImage_IsImportedIntoManagedAssets_AndUndoable()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.PortraitOverlayImage)!.Value;

        harness.Session.SetComponentImage(id, harness.ImportablePng("overlay.png", 64, 64));

        var assetId = PlateComponentEditor.Find(harness.Document, id)!.AssetId;
        Assert.NotNull(assetId);
        Assert.NotNull(harness.Assets.ResolveAssetPath(assetId!.Value));
        Assert.Equal(ComponentStatus.Ready, ComponentPaintPlan.Resolve(PlateComponentEditor.Find(harness.Document, id)!, BuiltInComponentCatalog.Instance, out _));

        harness.Session.Undo();
        Assert.Null(PlateComponentEditor.Find(harness.Document, id)!.AssetId);
    }

    [Fact]
    public async Task EditorSave_KeepsUnreadableAndUnknownComponents()
    {
        var plateId = Guid.NewGuid();
        var raw = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        raw["ProfileId"] = plateId;
        var components = JsonNode.Parse("""
            [
              { "Kind": 1, "DefinitionId": "af.plate-frame.line" },
              { "Kind": 77, "DefinitionId": "af.future.halo", "Glow": 3 },
              { "Kind": 1, "Opacity": "unreadable" }
            ]
            """)!;
        raw["Components"] = components.DeepClone();
        using var harness = await BasicHarness.OpenJsonAsync(raw.ToJsonString(), plateId);

        harness.Session.SetComponentSlot(PlateComponentKind.PlateFrame, BuiltInComponentCatalog.PlateFrameDouble);
        Assert.True(await harness.Session.SaveProfileAsync());

        var saved = JsonNode.Parse(harness.Fixture.ReadPlateJson(plateId))!["Components"]!.AsArray();
        Assert.Equal(3, saved.Count);
        Assert.Equal("af.plate-frame.double", saved[0]!["DefinitionId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(components[1], saved[1]!.AsObject().Where(p => p.Key is "Kind" or "DefinitionId" or "Glow").Aggregate(new JsonObject(), (o, p) => { o[p.Key] = p.Value?.DeepClone(); return o; })));
        Assert.True(JsonNode.DeepEquals(components[2], saved[2]));
    }

    [Fact]
    public async Task RevertToSaved_RestoresComponents()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.PlateFrame, BuiltInComponentCatalog.PlateFrameLine);
        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();

        harness.Session.SetComponentSlot(PlateComponentKind.PlateFrame, BuiltInComponentCatalog.PlateFrameDouble);
        harness.Session.RevertToSaved(undoable: true);

        Assert.Equal(BuiltInComponentCatalog.PlateFrameLine, Assert.Single(harness.Document.Components!).DefinitionId);
        Assert.False(harness.Session.IsDirty);
    }
}

/// <summary>Templates, duplication, asset liveness, and packages carry Components as ordinary Plate state.</summary>
public class ComponentLibraryTests
{
    private static async Task<Guid> CreatePlateWithComponentsAsync(Services.Plates.PlateLibraryService library, Guid? imageAsset = null)
    {
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "With Components", new PlateStarterContent(null));
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Components = ComponentDocuments.OneOfEach();
        document.Components[0].Offset = new Vector2(3, 4);
        if (imageAsset is { } asset)
        {
            document.Components.Add(new PlateComponent { Kind = PlateComponentKind.PortraitOverlay, DefinitionId = BuiltInComponentCatalog.PortraitOverlayImage, AssetId = asset });
        }

        await library.SavePlateDocumentAsync(document);
        return created.PlateId;
    }

    [Fact]
    public async Task PlateFromTemplate_RetainsComponents()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithComponentsAsync(fixture.PlateLibrary);
        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);

        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Framed");
        Assert.True(PlateComponent.ListsEqual(source.Components, templates.GetSavedDocument(templateId)!.Components));

        var created = await templates.InstantiateAsync(templateId, null);
        var fromTemplate = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);

        Assert.NotEqual(sourceId, created.PlateId);
        Assert.True(PlateComponent.ListsEqual(source.Components, fromTemplate.Components));
    }

    [Fact]
    public async Task EditingAPlateFromATemplate_NeverChangesTheTemplate()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithComponentsAsync(fixture.PlateLibrary);
        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Framed");
        var templateJsonBefore = fixture.ReadTemplateJson(templateId);

        var created = await templates.InstantiateAsync(templateId, null);
        var plate = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
        PlateComponentEditor.Update(plate, plate.Components![0].Id, c => c.Scale = 3f);
        PlateComponentEditor.SetSlot(plate, PlateComponentKind.NameBacking, null, BuiltInComponentCatalog.Instance);
        PlateComponentEditor.Add(plate, BuiltInComponentCatalog.Find(BuiltInComponentCatalog.DividerLine)!);
        await fixture.PlateLibrary.SavePlateDocumentAsync(plate);

        Assert.Equal(templateJsonBefore, fixture.ReadTemplateJson(templateId));
        var template = templates.GetSavedDocument(templateId)!;
        Assert.Equal(1f, template.Components![0].Scale);
        Assert.Equal(ComponentDocuments.OneOfEach().Count, template.Components.Count);

        // And the other direction: the template's own document is independent of the source Plate.
        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);
        PlateComponentEditor.Remove(source, source.Components![0].Id);
        await fixture.PlateLibrary.SavePlateDocumentAsync(source);
        Assert.Equal(templateJsonBefore, fixture.ReadTemplateJson(templateId));
    }

    [Fact]
    public async Task TemplateComponentAssets_StayLive_AfterTheSourcePlateDropsThem()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var asset = Guid.NewGuid();
        var sourceId = await CreatePlateWithComponentsAsync(fixture.PlateLibrary, asset);
        await templates.SaveAsTemplateAsync(sourceId, "Framed");

        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);
        source.Components!.RemoveAll(c => c.AssetId == asset);
        await fixture.PlateLibrary.SavePlateDocumentAsync(source);

        var plateScan = await fixture.PlateLibrary.ScanAssetReferencesAsync();
        Assert.DoesNotContain(asset, plateScan.ReferencedAssetIds);

        var live = await LiveAssetReferences.ComputeAsync(fixture.PlateLibrary, templates);
        Assert.True(live.IsComplete);
        Assert.Contains(asset, live.ReferencedAssetIds);
    }

    [Fact]
    public async Task PlateComponentAssets_AreLive()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var asset = Guid.NewGuid();
        await CreatePlateWithComponentsAsync(library, asset);

        var scan = await library.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(asset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task DuplicatePlate_PreservesComponents_WithANewPlateIdentity()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithComponentsAsync(library, Guid.NewGuid());
        var source = library.OpenDocumentForEditing(sourceId);

        var copyId = await library.DuplicatePlateAsync(sourceId);
        var copy = library.OpenDocumentForEditing(copyId);

        Assert.NotEqual(sourceId, copyId);
        Assert.Equal(copyId, copy.ProfileId);
        Assert.True(PlateComponent.ListsEqual(source.Components, copy.Components));

        // Independent afterwards.
        PlateComponentEditor.Update(copy, copy.Components![0].Id, c => c.Opacity = 0.1f);
        await library.SavePlateDocumentAsync(copy);
        Assert.Equal(1f, library.OpenDocumentForEditing(sourceId).Components![0].Opacity);
    }

    [Fact]
    public async Task Duplicate_KeepsUnreadableComponentsVerbatim()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var raw = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        raw["ProfileId"] = plateId;
        raw["Components"] = JsonNode.Parse("""[ { "Kind": 1, "DefinitionId": "af.plate-frame.line" }, { "Kind": 1, "Opacity": "bad" } ]""");
        fixture.WritePlateJson(plateId, raw.ToJsonString());
        var library = await fixture.LoadAsync();

        var copyId = await library.DuplicatePlateAsync(plateId);
        var copyJson = JsonNode.Parse(fixture.ReadPlateJson(copyId))!;

        Assert.True(JsonNode.DeepEquals(raw["Components"], copyJson["Components"]));
    }

    [Fact]
    public async Task Package_RoundTrip_PreservesComponents_AndRepointsTheirImage()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var image = fixture.AddImage(TestImages.Png(64, 64), "overlay.png");
        var plateId = await CreatePlateWithComponentsAsync(library, image);

        var path = fixture.Export(packages, plateId);
        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport);
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        var original = library.OpenDocumentForEditing(plateId);
        var imported = library.OpenDocumentForEditing(result.PlateId);
        Assert.Equal(original.Components!.Count, imported.Components!.Count);

        var importedImage = imported.Components.Single(c => c.DefinitionId == BuiltInComponentCatalog.PortraitOverlayImage).AssetId!.Value;
        Assert.NotEqual(image, importedImage);
        Assert.NotNull(fixture.Assets.ResolveAssetPath(importedImage));

        for (var i = 0; i < original.Components.Count; i++)
        {
            var expected = original.Components[i].Clone();
            expected.AssetId = imported.Components[i].AssetId;
            Assert.True(expected.ContentEquals(imported.Components[i]));
        }
    }

    [Fact]
    public async Task Package_Export_RefusesAMissingComponentImage()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithComponentsAsync(library, Guid.NewGuid());

        var result = packages.Export(plateId, System.IO.Path.Combine(fixture.ExportDirectory, "x.aetherframe"), overwrite: false);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == PackageErrorCode.AssetMissing);
    }

    [Theory]
    [InlineData("""{ "not": "an array" }""", false)]
    [InlineData("TOO_MANY", true)]
    [InlineData("""[ { "Kind": 1, "DefinitionId": "af.plate-frame.line", "Scale": 50000.5 } ]""", false)]
    [InlineData("""[ { "Kind": 1, "DefinitionId": "af.plate-frame.line", "Opacity": -20000.5 } ]""", false)]
    [InlineData("""[ { "Kind": 1, "DefinitionId": "af.plate-frame.line", "RotationDegrees": 12345.5 } ]""", false)]
    [InlineData("""[ { "Kind": 1, "DefinitionId": "af.plate-frame.line", "Color": { "X": 1, "Y": 1, "Z": 1, "W": 20000.5 } } ]""", false)]
    [InlineData("""[ { "Kind": 1, "DefinitionId": "af.plate-frame.line", "Offset": { "X": 200000.5, "Y": 0 } } ]""", false)]
    public async Task Package_Import_RefusesDamagedComponents(string components, bool tooLarge)
    {
        var expected = tooLarge ? PackageErrorCode.PackageTooLarge : PackageErrorCode.ProfileInvalid;
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithComponentsAsync(library);
        var path = fixture.Export(packages, plateId);

        var node = components == "TOO_MANY"
            ? new JsonArray(Enumerable.Range(0, PlateComponentLimits.MaxComponentCount + 1).Select(_ => (JsonNode?)JsonNode.Parse("""{ "Kind": 6, "DefinitionId": "af.divider.line" }""")).ToArray())
            : JsonNode.Parse(components);
        var crafted = PackageFiles.Rewrite(path, entries => PackageFiles.EditProfile(entries, profile => profile["Components"] = node));

        using var staged = packages.Inspect(crafted);
        Assert.False(staged.CanImport);
        Assert.Contains(staged.Diagnostics.Errors, e => e.Code == expected);
    }

    [Fact]
    public async Task Package_Import_WarnsButKeeps_NewerAndUnreadableComponents()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithComponentsAsync(library);
        var path = fixture.Export(packages, plateId);
        var components = JsonNode.Parse("""
            [
              { "Kind": 1, "DefinitionId": "af.plate-frame.line" },
              { "Kind": 99, "DefinitionId": "af.future.thing" },
              { "Kind": 1, "DefinitionId": "af.plate-frame.from-the-future" },
              { "Kind": 1, "DefinitionId": "af.plate-frame.line", "Opacity": "oops" }
            ]
            """);
        var crafted = PackageFiles.Rewrite(path, entries => PackageFiles.EditProfile(entries, profile => profile["Components"] = components!.DeepClone()));

        using var staged = packages.Inspect(crafted);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Contains(staged.Diagnostics.Warnings, w => w.Code == PackageWarningCode.UnrecognizedSetting);
        Assert.Contains(staged.Diagnostics.Warnings, w => w.Code == PackageWarningCode.UnsupportedElements);

        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded);
        var saved = JsonNode.Parse(fixture.Library.ReadPlateJson(result.PlateId))!;
        Assert.Equal(4, saved["Components"]!.AsArray().Count);
    }
}
