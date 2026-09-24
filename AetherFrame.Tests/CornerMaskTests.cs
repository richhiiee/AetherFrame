using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Per-instance corner selection for Corner Ornaments (<see cref="PlateComponent.Corners"/>).</summary>
public class CornerMaskTests
{
    private const string Astrolabe = BuiltInComponentCatalog.CornerOrnamentAstrolabePivot;
    private const string Bracket = BuiltInComponentCatalog.CornerOrnamentBracket;

    private static readonly CornerMask[] Order = [CornerMask.TopLeft, CornerMask.TopRight, CornerMask.BottomLeft, CornerMask.BottomRight];

    [Fact]
    public void BitValues_AreFrozen()
    {
        Assert.Equal(0, (int)CornerMask.None);
        Assert.Equal(1, (int)CornerMask.TopLeft);
        Assert.Equal(2, (int)CornerMask.TopRight);
        Assert.Equal(4, (int)CornerMask.BottomLeft);
        Assert.Equal(8, (int)CornerMask.BottomRight);
        Assert.Equal(15, (int)CornerMask.All);
    }

    [Fact]
    public void LegacyComponentWithoutMask_DrawsAllFour_AndSavesWithoutAMask()
    {
        var raw = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        raw["Components"] = JsonNode.Parse("""[ { "Kind": 5, "DefinitionId": "af.corner-ornament.bracket" } ]""");

        var document = PlateDocuments.Deserialize((JsonObject)raw.DeepClone())!;
        var component = Assert.Single(document.Components!);

        Assert.Null(component.Corners);
        Assert.Equal(CornerMask.All, CornerMasks.Effective(component));
        Assert.Equal(4, CornerSteps(document).Count);
        Assert.False(PlateDocuments.ToJson(document)["Components"]![0]!.AsObject().ContainsKey("Corners"));
    }

    public static IEnumerable<object[]> Selections() =>
    [
        [CornerMask.TopLeft],
        [CornerMask.TopRight],
        [CornerMask.BottomLeft],
        [CornerMask.BottomRight],
        [CornerMask.TopLeft | CornerMask.TopRight],
        [CornerMask.BottomLeft | CornerMask.BottomRight],
        [CornerMask.TopLeft | CornerMask.BottomRight],
        [CornerMask.TopRight | CornerMask.BottomLeft],
        [CornerMask.All],
    ];

    [Theory]
    [MemberData(nameof(Selections))]
    public void OnlySelectedCorners_AreDrawn_ProceduralAndArt(CornerMask mask)
    {
        foreach (var definitionId in new[] { Bracket, Astrolabe })
        {
            var document = ComponentDocuments.WithAnchors();
            var component = ComponentDocuments.Of(definitionId);
            component.Corners = CornerMasks.Normalize(mask);
            document.Components = [component];

            var steps = CornerSteps(document);
            var expected = Order.Where(c => (mask & c) != 0).ToList();
            Assert.Equal(expected.Count, steps.Count);
            Assert.Equal(expected, steps.Select(s => CornerOf(document, s)));
        }
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public void ArtRotation_IsCorrectPerSelectedCorner_AndProceduralMirroringToo(CornerMask mask)
    {
        var document = ComponentDocuments.WithAnchors();
        var art = ComponentDocuments.Of(Astrolabe);
        art.Corners = CornerMasks.Normalize(mask);
        art.RotationDegrees = 5f;
        var bracket = ComponentDocuments.Of(Bracket);
        bracket.Corners = CornerMasks.Normalize(mask);

        document.Components = [art];
        foreach (var step in CornerSteps(document))
        {
            var corner = CornerOf(document, step);
            Assert.Equal(ArtRotation(corner) + 5f, step.Placement.RotationDegrees);
            Assert.False(step.Placement.MirrorX || step.Placement.MirrorY);

            // The artwork's top-left texel (the pivot) points into that canvas corner.
            var output = new List<ComponentPrimitive>();
            art.RotationDegrees = 0f;
            ComponentGeometry.Build(document, art, step.Definition!, step.Placement with { RotationDegrees = ArtRotation(corner) }, output);
            art.RotationDegrees = 5f;
            Assert.Equal(corner, CornerAt(document, Assert.Single(output).A));
        }

        document.Components = [bracket];
        foreach (var step in CornerSteps(document))
        {
            var corner = CornerOf(document, step);
            Assert.Equal(0f, step.Placement.RotationDegrees);
            Assert.Equal(corner is CornerMask.TopRight or CornerMask.BottomRight, step.Placement.MirrorX);
            Assert.Equal(corner is CornerMask.BottomLeft or CornerMask.BottomRight, step.Placement.MirrorY);
        }
    }

    [Fact]
    public void SelectedCorners_ShareTheInstancesTransformsAndColor()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(Astrolabe);
        component.Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        component.Offset = new Vector2(12, 7);
        component.Scale = 1.5f;
        component.RotationDegrees = 20f;
        component.Color = new Vector4(0.3f, 0.6f, 0.9f, 1f);
        component.Opacity = 0.5f;
        document.Components = [component];

        var unit = ComponentPaintPlan.Unit(document);
        var size = ComponentPaintPlan.CornerSize * unit * BuiltInArtCatalog.AstrolabePivot.SizeFactor;
        var inset = ComponentPaintPlan.CornerInset * unit;
        var steps = CornerSteps(document);

        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal(new Vector2(size * 1.5f), s.Placement.Rect.Size));
        Assert.Equal(new Vector2(inset + (size / 2f)) + new Vector2(12, 7), Center(steps[0]));
        Assert.Equal(new Vector2(document.CanvasWidth - inset - (size / 2f), document.CanvasHeight - inset - (size / 2f)) - new Vector2(12, 7), Center(steps[1]));
        foreach (var step in steps)
        {
            var output = new List<ComponentPrimitive>();
            ComponentGeometry.Build(document, component, step.Definition!, step.Placement, output);
            Assert.Equal(new Vector4(0.3f, 0.6f, 0.9f, 0.5f), Assert.Single(output).Color);
        }
    }

    [Fact]
    public void MultipleInstances_DifferentStylesInDifferentCorners()
    {
        var document = ComponentDocuments.WithAnchors();
        var a = ComponentDocuments.Of(Astrolabe);
        a.Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        var b = ComponentDocuments.Of(Bracket, layerOrder: 1);
        b.Corners = CornerMask.TopRight | CornerMask.BottomLeft;
        document.Components = [a, b];

        var steps = CornerSteps(document);

        Assert.Equal([CornerMask.TopLeft, CornerMask.BottomRight], steps.Where(s => ReferenceEquals(s.Component, a)).Select(s => CornerOf(document, s)));
        Assert.Equal([CornerMask.TopRight, CornerMask.BottomLeft], steps.Where(s => ReferenceEquals(s.Component, b)).Select(s => CornerOf(document, s)));
        Assert.All(steps.Where(s => ReferenceEquals(s.Component, a)), s => Assert.Equal(Astrolabe, s.Definition!.Id));
        Assert.All(steps.Where(s => ReferenceEquals(s.Component, b)), s => Assert.Equal(Bracket, s.Definition!.Id));
    }

    [Fact]
    public void SameCorner_SharedByInstances_PaintsInComponentOrder()
    {
        var document = ComponentDocuments.WithAnchors();
        var top = ComponentDocuments.Of(Bracket, layerOrder: 5);
        top.Corners = CornerMask.TopLeft;
        var bottom = ComponentDocuments.Of(Astrolabe, layerOrder: 1);
        bottom.Corners = CornerMask.TopLeft | CornerMask.TopRight;
        document.Components = [top, bottom];

        var topLeft = CornerSteps(document).Where(s => CornerOf(document, s) == CornerMask.TopLeft).ToList();

        Assert.Equal(2, topLeft.Count);
        Assert.Same(bottom, topLeft[0].Component); // lower LayerOrder paints first
        Assert.Same(top, topLeft[1].Component);

        PlateComponentEditor.MoveInLayer(document, top.Id, -1);
        Assert.Same(top, CornerSteps(document).First(s => CornerOf(document, s) == CornerMask.TopLeft).Component);
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public void VisualBounds_CoverOnlyTheSelectedCorners(CornerMask mask)
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(Astrolabe);
        component.Corners = CornerMasks.Normalize(mask);
        document.Components = [component];

        var plan = ComponentDocuments.Plan(document);
        var (min, max) = ComponentPaintPlan.GetVisualBounds(plan, component)!.Value;

        var expectedMin = new Vector2(float.MaxValue);
        var expectedMax = new Vector2(float.MinValue);
        foreach (var step in plan.Where(s => ReferenceEquals(s.Component, component)))
        {
            expectedMin = Vector2.Min(expectedMin, step.Placement.Rect.Position);
            expectedMax = Vector2.Max(expectedMax, step.Placement.Rect.Position + step.Placement.Rect.Size);
        }

        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
        var mid = new Vector2(document.CanvasWidth, document.CanvasHeight) / 2f;
        Assert.Equal((mask & (CornerMask.TopRight | CornerMask.BottomRight)) != 0, max.X > mid.X);
        Assert.Equal((mask & (CornerMask.TopLeft | CornerMask.BottomLeft)) != 0, min.X < mid.X);
        Assert.Equal((mask & (CornerMask.BottomLeft | CornerMask.BottomRight)) != 0, max.Y > mid.Y);
        Assert.Equal((mask & (CornerMask.TopLeft | CornerMask.TopRight)) != 0, min.Y < mid.Y);
    }

    [Fact]
    public void VisualBounds_FollowOverflowPastThePlate_ForSelectedCornersOnly()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(Astrolabe);
        component.Corners = CornerMask.TopLeft;
        component.Offset = new Vector2(-60, -60); // pushed outward, past the canvas edge
        component.RotationDegrees = 45f;
        document.Components = [component];

        var plan = ComponentDocuments.Plan(document);
        var (min, max) = ComponentPaintPlan.GetVisualBounds(plan, component)!.Value;
        var step = Assert.Single(plan, s => ReferenceEquals(s.Component, component));
        var diagonal = step.Placement.Rect.Size.X * MathF.Sqrt(2f);

        Assert.True(min.X < 0f && min.Y < 0f);
        Assert.Equal(diagonal, max.X - min.X, 3);
        Assert.True(max.X < document.CanvasWidth / 2f && max.Y < document.CanvasHeight / 2f);
    }

    [Fact]
    public void VisualBounds_AreNull_WhenNothingIsDrawn()
    {
        var document = ComponentDocuments.WithAnchors();
        var hidden = ComponentDocuments.Of(Astrolabe);
        hidden.Visible = false;
        var none = ComponentDocuments.Of(Bracket);
        none.Corners = CornerMask.None; // only reachable through crafted data: treated as hidden
        document.Components = [hidden, none];

        var plan = ComponentDocuments.Plan(document);

        Assert.Null(ComponentPaintPlan.GetVisualBounds(plan, hidden));
        Assert.Null(ComponentPaintPlan.GetVisualBounds(plan, none));
        Assert.Empty(CornerSteps(document));
    }

    [Fact]
    public void UnknownFutureBits_ArePreserved_AndIgnoredWhenDrawing()
    {
        var raw = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        raw["Components"] = JsonNode.Parse("""[ { "Kind": 5, "DefinitionId": "af.corner-ornament.bracket", "Corners": 49 } ]""");

        var document = PlateDocuments.Deserialize((JsonObject)raw.DeepClone())!;
        var component = Assert.Single(document.Components!);
        Assert.Equal(49, (int)component.Corners!.Value); // 32 + 16 unknown, + TopLeft
        Assert.Equal(CornerMask.TopLeft, CornerMasks.Effective(component));
        Assert.Equal([CornerMask.TopLeft], CornerSteps(document).Select(s => CornerOf(document, s)));

        Assert.True(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.BottomRight, true));
        Assert.Equal(49 | 8, (int)component.Corners!.Value);
        Assert.Equal(49 | 8, (int)PlateDocuments.ToJson(document)["Components"]![0]!["Corners"]!);

        // Unknown bits alone are "no known corner": the last known corner can't be turned off.
        Assert.True(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.TopLeft, false));
        Assert.False(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.BottomRight, false));
        Assert.Equal(48 | 8, (int)component.Corners!.Value);
    }

    [Fact]
    public void Editor_KeepsAtLeastOneCorner_NormalizesAllFour_AndOnlyAppliesToCornerOrnaments()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(Bracket);
        var divider = ComponentDocuments.Of(BuiltInComponentCatalog.DividerLine);
        document.Components = [component, divider];

        Assert.False(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.TopLeft, true)); // already on
        Assert.True(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.TopLeft, false));
        Assert.Equal(CornerMask.TopRight | CornerMask.BottomLeft | CornerMask.BottomRight, component.Corners);
        Assert.True(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.BottomLeft, false));
        Assert.True(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.BottomRight, false));
        Assert.Equal(CornerMask.TopRight, component.Corners);
        Assert.False(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.TopRight, false)); // the last one stays
        Assert.Equal(CornerMask.TopRight, component.Corners);

        foreach (var corner in new[] { CornerMask.TopLeft, CornerMask.BottomLeft, CornerMask.BottomRight })
        {
            PlateComponentEditor.SetCorner(document, component.Id, corner, true);
        }

        Assert.Null(component.Corners); // all four is the default, stored as null

        Assert.False(PlateComponentEditor.SetCorner(document, divider.Id, CornerMask.TopLeft, false));
        Assert.Null(divider.Corners);
        Assert.False(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.TopLeft | CornerMask.TopRight, false)); // one corner at a time
        Assert.False(PlateComponentEditor.SetCorner(document, component.Id, CornerMask.All, false));
    }

    [Fact]
    public void ChangingStyle_KeepsTheCornerSelection()
    {
        var document = ComponentDocuments.WithAnchors();
        PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, Bracket, BuiltInComponentCatalog.Instance);
        var slot = PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament)!;
        PlateComponentEditor.SetCorner(document, slot.Id, CornerMask.TopRight, false);

        PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, Astrolabe, BuiltInComponentCatalog.Instance);
        PlateComponentEditor.ResetTransform(document, slot.Id);

        Assert.Equal(CornerMask.All & ~CornerMask.TopRight, slot.Corners);
    }

    [Fact]
    public void Serialization_RoundTrips_AndCountsForContentEquality()
    {
        var document = ComponentDocuments.WithAnchors();
        var a = ComponentDocuments.Of(Astrolabe);
        a.Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        var b = ComponentDocuments.Of(Bracket);
        document.Components = [a, b];

        var json = PlateDocuments.ToJson(document);
        Assert.Equal(9, (int)json["Components"]![0]!["Corners"]!);
        Assert.False(json["Components"]![1]!.AsObject().ContainsKey("Corners"));

        var reloaded = PlateDocuments.Deserialize((JsonObject)json.DeepClone())!;
        Assert.True(PlateComponent.ListsEqual(document.Components, reloaded.Components));
        Assert.True(JsonNode.DeepEquals(json, PlateDocuments.ToJson(reloaded)));

        var clone = a.Clone();
        Assert.Equal(a.Corners, clone.Corners);
        clone.Corners = CornerMask.TopLeft;
        Assert.False(a.ContentEquals(clone));
    }

    [Fact]
    public async Task BasicSlot_CornerToggle_IsOneUndoStep_AndDrivesDirtyState()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, Astrolabe);
        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();
        Assert.False(harness.Session.IsDirty);
        var id = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.CornerOrnament)!.Id;

        harness.Session.SetComponentCorner(id, CornerMask.TopRight, false);
        Assert.Equal(CornerMask.All & ~CornerMask.TopRight, PlateComponentEditor.Find(harness.Document, id)!.Corners);
        Assert.True(harness.Session.IsDirty);

        harness.Session.Undo();
        Assert.Null(PlateComponentEditor.Find(harness.Document, id)!.Corners);
        Assert.False(harness.Session.IsDirty);

        harness.Session.Redo();
        Assert.True(harness.Session.IsDirty);

        // Turning it back on returns to the saved state: not dirty, even though it took two edits.
        harness.Session.SetComponentCorner(id, CornerMask.TopRight, true);
        Assert.Null(PlateComponentEditor.Find(harness.Document, id)!.Corners);
        Assert.False(harness.Session.IsDirty);

        // A refused change (the last corner) records nothing.
        foreach (var corner in new[] { CornerMask.TopLeft, CornerMask.TopRight, CornerMask.BottomLeft })
        {
            harness.Session.SetComponentCorner(id, corner, false);
        }

        harness.Session.SetComponentCorner(id, CornerMask.BottomRight, false);
        Assert.Equal(CornerMask.BottomRight, PlateComponentEditor.Find(harness.Document, id)!.Corners);

        // The next undo is the last real change (Bottom Left off), not the refused one.
        harness.Session.Undo();
        Assert.Equal(CornerMask.BottomLeft | CornerMask.BottomRight, PlateComponentEditor.Find(harness.Document, id)!.Corners);
        harness.Session.Redo();

        Assert.True(await harness.Session.SaveProfileAsync());
        var reopened = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.Equal(CornerMask.BottomRight, PlateComponentEditor.FindSlot(reopened, PlateComponentKind.CornerOrnament)!.Corners);
    }

    [Fact]
    public async Task Advanced_EachInstanceKeepsItsOwnCorners_ThroughUndo()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        var a = harness.Session.AddComponent(Astrolabe)!.Value;
        var b = harness.Session.AddComponent(Bracket)!.Value;

        harness.Session.SetComponentCorner(a, CornerMask.TopRight, false);
        harness.Session.SetComponentCorner(a, CornerMask.BottomLeft, false);
        harness.Session.SetComponentCorner(b, CornerMask.TopLeft, false);
        harness.Session.SetComponentCorner(b, CornerMask.BottomRight, false);

        Assert.Equal(CornerMask.TopLeft | CornerMask.BottomRight, PlateComponentEditor.Find(harness.Document, a)!.Corners);
        Assert.Equal(CornerMask.TopRight | CornerMask.BottomLeft, PlateComponentEditor.Find(harness.Document, b)!.Corners);

        harness.Session.Undo();
        Assert.Equal(CornerMask.TopRight | CornerMask.BottomLeft | CornerMask.BottomRight, PlateComponentEditor.Find(harness.Document, b)!.Corners);
        Assert.Equal(CornerMask.TopLeft | CornerMask.BottomRight, PlateComponentEditor.Find(harness.Document, a)!.Corners);
    }

    [Fact]
    public async Task Template_PreservesCornerMasks()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithMasksAsync(fixture.PlateLibrary);
        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);

        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Corners");
        Assert.True(PlateComponent.ListsEqual(source.Components, templates.GetSavedDocument(templateId)!.Components));

        var created = await templates.InstantiateAsync(templateId, null);
        var fromTemplate = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
        Assert.True(PlateComponent.ListsEqual(source.Components, fromTemplate.Components));
        Assert.Equal(CornerMask.TopLeft | CornerMask.BottomRight, fromTemplate.Components![0].Corners);
    }

    [Fact]
    public async Task Duplicate_PreservesCornerMasks()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithMasksAsync(library);

        var copyId = await library.DuplicatePlateAsync(sourceId);

        Assert.True(PlateComponent.ListsEqual(library.OpenDocumentForEditing(sourceId).Components, library.OpenDocumentForEditing(copyId).Components));
        Assert.Equal(CornerMask.TopRight | CornerMask.BottomLeft, library.OpenDocumentForEditing(copyId).Components![1].Corners);
    }

    [Fact]
    public async Task Package_RoundTrip_PreservesCornerMasks()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithMasksAsync(library);

        var path = fixture.Export(packages, plateId);
        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Empty(staged.Diagnostics.Warnings);
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        Assert.True(PlateComponent.ListsEqual(library.OpenDocumentForEditing(plateId).Components, library.OpenDocumentForEditing(result.PlateId).Components));
    }

    [Fact]
    public async Task Package_WithFutureCornerBits_ImportsAndKeepsThem()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithMasksAsync(library);
        var path = fixture.Export(packages, plateId);
        var crafted = PackageFiles.Rewrite(path, e => PackageFiles.EditProfile(e, profile => profile["Components"]![0]!["Corners"] = 64 | 1));

        using var staged = packages.Inspect(crafted);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded);

        Assert.Equal(65, (int)library.OpenDocumentForEditing(result.PlateId).Components![0].Corners!.Value);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static async Task<Guid> CreatePlateWithMasksAsync(Services.Plates.PlateLibraryService library)
    {
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Corners", new PlateStarterContent(null));
        var document = library.OpenDocumentForEditing(created.PlateId);
        var a = ComponentDocuments.Of(Astrolabe);
        a.Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        var b = ComponentDocuments.Of(Bracket, layerOrder: 1);
        b.Corners = CornerMask.TopRight | CornerMask.BottomLeft;
        document.Components = [a, b, ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentDiamond, layerOrder: 2)];
        await library.SavePlateDocumentAsync(document);
        return created.PlateId;
    }

    private static List<PaintStep> CornerSteps(ProfileDocument document) =>
        ComponentDocuments.Plan(document).Where(s => !s.IsElement && s.Component!.Kind == PlateComponentKind.CornerOrnament).ToList();

    private static Vector2 Center(PaintStep step) => step.Placement.Rect.Position + (step.Placement.Rect.Size / 2f);

    /// <summary>The canvas quadrant a placement sits in.</summary>
    private static CornerMask CornerOf(ProfileDocument document, PaintStep step) => CornerAt(document, Center(step));

    private static CornerMask CornerAt(ProfileDocument document, Vector2 point)
    {
        var right = point.X > document.CanvasWidth / 2f;
        var bottom = point.Y > document.CanvasHeight / 2f;
        return (right, bottom) switch
        {
            (false, false) => CornerMask.TopLeft,
            (true, false) => CornerMask.TopRight,
            (false, true) => CornerMask.BottomLeft,
            _ => CornerMask.BottomRight,
        };
    }

    private static float ArtRotation(CornerMask corner) => corner switch
    {
        CornerMask.TopRight => 90f,
        CornerMask.BottomRight => 180f,
        CornerMask.BottomLeft => 270f,
        _ => 0f,
    };
}
