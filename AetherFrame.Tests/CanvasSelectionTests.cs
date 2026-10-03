using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Listing and selecting canvas Components (issue #115): hit testing over the whole paint plan, the
/// selection outlines, the editor session's Component selection, and Basic preview clicks on Components.
/// </summary>
public class CanvasSelectionTests
{
    /// <summary>The paint plan the renderer builds for the finished rendering (ProfileRenderer.BuildPaintPlan
    /// minus the font measurement): the same drawn elements, the same Component placements.</summary>
    private static List<PaintStep> Plan(ProfileDocument document)
    {
        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();
        ProfileVisualBounds.FillDrawnElements(document, ProfileRenderOptions.Finished, paintOrder, drawn);
        var plan = new List<PaintStep>();
        ComponentPaintPlan.Build(document, drawn, BuiltInComponentCatalog.Instance, plan);
        return plan;
    }

    private static List<PaintStep> StepsOf(List<PaintStep> plan, PlateComponent component) =>
        plan.Where(s => ReferenceEquals(s.Component, component)).ToList();

    private static Vector2 CenterOf(ElementRect rect) => rect.Position + (rect.Size / 2f);

    private static Vector2 CenterOf(ProfileElement element) => element.Position + (element.Size / 2f);

    /// <summary>A blank 1280 x 720 Plate (unit 1) holding only <paramref name="components"/>.</summary>
    private static ProfileDocument BlankWith(params PlateComponent[] components)
    {
        var document = BasicDocuments.Blank();
        document.Components = components.ToList();
        return document;
    }

    // ---------------------------------------------------------------- hit testing

    [Fact]
    public void CornerOrnament_IsHitInEachDrawnCorner_AndNotBetweenThem()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        var document = BlankWith(ornament);
        var plan = Plan(document);
        var corners = StepsOf(plan, ornament);

        Assert.Equal(4, corners.Count);
        foreach (var corner in corners)
        {
            var center = CenterOf(corner.Placement.Rect);
            Assert.True(CanvasHitTest.Hits(corner, center, 1f));
            Assert.Same(ornament, CanvasHitTest.Find(plan, center, 1f).Component);
        }

        // The middle of the top edge and of the left edge: inside the corners' union box, outside every corner.
        foreach (var between in new[] { new Vector2(640f, 42f), new Vector2(42f, 360f), new Vector2(640f, 360f) })
        {
            Assert.All(corners, corner => Assert.False(CanvasHitTest.Hits(corner, between, 1f)));
            Assert.True(CanvasHitTest.Find(plan, between, 1f).IsEmpty);
        }
    }

    [Fact]
    public void CornerOrnament_InOnlySomeCorners_IsHitOnlyWhereItIsDrawn()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        var plan = Plan(BlankWith(ornament));

        Assert.Same(ornament, CanvasHitTest.Find(plan, new Vector2(42f, 42f), 1f).Component);
        Assert.Same(ornament, CanvasHitTest.Find(plan, new Vector2(1238f, 678f), 1f).Component);
        Assert.True(CanvasHitTest.Find(plan, new Vector2(1238f, 42f), 1f).IsEmpty);
        Assert.True(CanvasHitTest.Find(plan, new Vector2(42f, 678f), 1f).IsEmpty);
    }

    [Fact]
    public void AnElementPaintedAboveAComponent_Wins_AndSkippingElementsFindsTheComponent()
    {
        var document = ComponentDocuments.WithAnchors();
        var backing = ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar);
        document.Components = [backing];
        var plan = Plan(document);
        var name = BasicSections.Find(document, ProfileElementRole.BasicName)!;
        var point = CenterOf(name);

        Assert.True(plan.FindIndex(s => ReferenceEquals(s.Component, backing)) < plan.FindIndex(s => ReferenceEquals(s.Element, name)));
        var hit = CanvasHitTest.Find(plan, point, 1f);
        Assert.Same(name, hit.Element);
        Assert.Null(hit.Component);

        var through = CanvasHitTest.Find(plan, point, 1f, skipElement: _ => true);
        Assert.Same(backing, through.Component);
        Assert.Null(through.Element);

        // Where only the backing is drawn (its padding above the name), it is found directly.
        Assert.Same(backing, CanvasHitTest.Find(plan, new Vector2(point.X, name.Position.Y - 3f), 1f).Component);
    }

    [Fact]
    public void AComponentPaintedAboveAnElement_Wins_AndSkippingComponentsFindsTheElement()
    {
        var document = ComponentDocuments.WithAnchors();
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        document.Components = [ornament];
        var plan = Plan(document);
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;
        var point = new Vector2(50f, 50f); // inside the top-left ornament and the portrait

        Assert.Same(ornament, CanvasHitTest.Find(plan, point, 1f).Component);
        Assert.Same(portrait, CanvasHitTest.Find(plan, point, 1f, skipComponent: c => c == ornament).Element);
        Assert.True(CanvasHitTest.Find(plan, point, 1f, skipElement: _ => true, skipComponent: _ => true).IsEmpty);
    }

    [Fact]
    public void PlateFrame_IsHitOnlyInItsEdgeBand()
    {
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        var plan = Plan(BlankWith(frame));
        var step = Assert.Single(StepsOf(plan, frame));
        var rect = step.Placement.Rect;
        var left = rect.Position.X;
        var y = CenterOf(rect).Y;

        // Inside: up to FrameBand in from the edge. Outside: up to half of it.
        Assert.True(CanvasHitTest.Hits(step, new Vector2(left + 1f, y), 1f));
        Assert.True(CanvasHitTest.Hits(step, new Vector2(left + CanvasHitTest.FrameBand - 1f, y), 1f));
        Assert.True(CanvasHitTest.Hits(step, new Vector2(left - (CanvasHitTest.FrameBand / 2f) + 1f, y), 1f));
        Assert.False(CanvasHitTest.Hits(step, new Vector2(left + CanvasHitTest.FrameBand + 1f, y), 1f));
        Assert.False(CanvasHitTest.Hits(step, new Vector2(left - (CanvasHitTest.FrameBand / 2f) - 1f, y), 1f));

        // The interior takes no clicks.
        Assert.False(CanvasHitTest.Hits(step, CenterOf(rect), 1f));
        Assert.True(CanvasHitTest.Find(plan, CenterOf(rect), 1f).IsEmpty);
        Assert.Same(frame, CanvasHitTest.Find(plan, new Vector2(CenterOf(rect).X, rect.Position.Y + 2f), 1f).Component);
    }

    [Fact]
    public void FrameBand_ScalesWithTheUnit()
    {
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        var step = Assert.Single(StepsOf(Plan(BlankWith(frame)), frame));
        var point = new Vector2(step.Placement.Rect.Position.X + (1.5f * CanvasHitTest.FrameBand), CenterOf(step.Placement.Rect).Y);

        Assert.False(CanvasHitTest.Hits(step, point, 1f));
        Assert.True(CanvasHitTest.Hits(step, point, 2f));

        // An unusable unit counts as 1.
        Assert.False(CanvasHitTest.Hits(step, point, 0f));
        Assert.False(CanvasHitTest.Hits(step, point, float.NaN));
    }

    [Fact]
    public void AClickInsideAPortraitFrame_ReachesThePortrait_AndItsEdgeSelectsTheFrame()
    {
        var document = ComponentDocuments.WithAnchors();
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine);
        document.Components = [frame];
        var plan = Plan(document);
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;
        var y = CenterOf(portrait).Y;

        Assert.Same(portrait, CanvasHitTest.Find(plan, CenterOf(portrait), 1f).Element);
        Assert.Same(frame, CanvasHitTest.Find(plan, new Vector2(portrait.Position.X + 5f, y), 1f).Component);
        Assert.Same(frame, CanvasHitTest.Find(plan, new Vector2(portrait.Position.X - 5f, y), 1f).Component);
    }

    [Fact]
    public void BackgroundAndPortraitOverlay_AreNeverClickable()
    {
        Assert.False(CanvasHitTest.IsClickable(PlateComponentKind.Background));
        Assert.False(CanvasHitTest.IsClickable(PlateComponentKind.PortraitOverlay));
        foreach (var kind in Enum.GetValues<PlateComponentKind>().Where(k => k is not (PlateComponentKind.Background or PlateComponentKind.PortraitOverlay)))
        {
            Assert.True(CanvasHitTest.IsClickable(kind));
        }

        var document = ComponentDocuments.WithAnchors();
        var background = ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura);
        var overlay = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayFade);
        document.Components = [background, overlay];
        var plan = Plan(document);
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;

        var backgroundStep = Assert.Single(StepsOf(plan, background));
        var overlayStep = Assert.Single(StepsOf(plan, overlay));
        Assert.False(CanvasHitTest.Hits(backgroundStep, new Vector2(640f, 360f), 1f));
        Assert.False(CanvasHitTest.Hits(overlayStep, CenterOf(portrait), 1f));

        // The overlay is drawn over the portrait, the background under everything: both are looked through.
        Assert.Same(portrait, CanvasHitTest.Find(plan, CenterOf(portrait), 1f).Element);
        Assert.True(CanvasHitTest.Find(plan, new Vector2(640f, 400f), 1f).IsEmpty);
    }

    [Fact]
    public void ARotatedPlacement_IsHitByItsRotatedShape()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Corners = CornerMask.TopLeft;
        ornament.RotationDegrees = 45f;
        var plan = Plan(BlankWith(ornament));
        var step = Assert.Single(StepsOf(plan, ornament));
        var rect = step.Placement.Rect;
        Assert.Equal(45f, step.Placement.RotationDegrees);

        // Near the unrotated box's top-left corner: inside the box, outside the diamond it turns into.
        var nearCorner = rect.Position + new Vector2(2f);
        Assert.False(CanvasHitTest.Hits(step, nearCorner, 1f));
        Assert.True(CanvasHitTest.Find(plan, nearCorner, 1f).IsEmpty);

        // Above the box's top edge, where the diamond's top point reaches.
        var aboveTop = new Vector2(CenterOf(rect).X, rect.Position.Y - 4f);
        Assert.True(CanvasHitTest.Hits(step, aboveTop, 1f));
        Assert.Same(ornament, CanvasHitTest.Find(plan, aboveTop, 1f).Component);
    }

    [Fact]
    public void ARotatedPlacement_IsHitWhereItsOutlineIsDrawn_NotTurnedTheOtherWay()
    {
        var divider = ComponentDocuments.Of(BuiltInComponentCatalog.DividerLine);
        var rect = new ElementRect(new Vector2(500f, 300f), new Vector2(300f, 20f));
        var step = new PaintStep(PlateLayer.Decorations, null, divider, BuiltInComponentCatalog.Find(divider.DefinitionId), new ComponentPlacement(rect, 30f, false, false));
        var plan = new List<PaintStep> { step };
        var center = CenterOf(rect);
        var alongBar = center + new Vector2(140f, 0f);

        var outlines = new List<Vector2[]>();
        CanvasHitTest.Outlines(plan, divider, outlines);
        var drawnEnd = (Assert.Single(outlines)[1] + Assert.Single(outlines)[2]) / 2f; // the right end, as outlined

        Assert.True(CanvasHitTest.Hits(step, RotationGeometry.RotatePoint(alongBar, center, 30f), 1f));
        Assert.True(CanvasHitTest.Hits(step, center + ((drawnEnd - center) * 0.95f), 1f));
        Assert.False(CanvasHitTest.Hits(step, RotationGeometry.RotatePoint(alongBar, center, -30f), 1f));
        Assert.False(CanvasHitTest.Hits(step, alongBar, 1f));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.01f, false)]
    [InlineData(0.5f, true)]
    [InlineData(1f, true)]
    public void AnInvisibleComponent_TakesNoClicks(float opacity, bool clickable)
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Opacity = opacity;
        var plan = Plan(BlankWith(ornament));
        var corners = StepsOf(plan, ornament);

        Assert.Equal(4, corners.Count); // still drawn (and listed), just not clickable
        foreach (var corner in corners)
        {
            var center = CenterOf(corner.Placement.Rect);
            Assert.Equal(clickable, CanvasHitTest.Hits(corner, center, 1f));
            Assert.Equal(clickable, CanvasHitTest.Find(plan, center, 1f).Component is not null);
        }
    }

    [Fact]
    public void AHiddenComponent_TakesNoClicks()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Visible = false;
        var plan = Plan(BlankWith(ornament));

        Assert.True(CanvasHitTest.Find(plan, new Vector2(42f, 42f), 1f).IsEmpty);
    }

    [Fact]
    public void ElementContains_FollowsTheElementsRotation()
    {
        var image = new ImageProfileElement { Position = new Vector2(100f, 100f), Size = new Vector2(200f, 20f), RotationDegrees = 90f };

        Assert.True(ProfilePaintOrder.Contains(image, new Vector2(200f, 110f)));
        Assert.True(ProfilePaintOrder.Contains(image, new Vector2(200f, 30f)));
        Assert.False(ProfilePaintOrder.Contains(image, new Vector2(110f, 110f)));
    }

    // ---------------------------------------------------------------- outlines

    [Fact]
    public void Outlines_GiveOneQuadPerPlacement()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        var plan = Plan(BlankWith(ornament, frame));
        var outlines = new List<Vector2[]> { new[] { Vector2.One } }; // cleared first

        CanvasHitTest.Outlines(plan, ornament, outlines);

        var corners = StepsOf(plan, ornament);
        Assert.Equal(4, outlines.Count);
        for (var i = 0; i < corners.Count; i++)
        {
            var rect = corners[i].Placement.Rect;
            Assert.Equal(RotationGeometry.GetRotatedCorners(rect.Position, rect.Size, corners[i].Placement.RotationDegrees), outlines[i]);
        }

        CanvasHitTest.Outlines(plan, frame, outlines);
        Assert.Single(outlines);

        ornament.Corners = CornerMask.TopRight | CornerMask.BottomLeft;
        CanvasHitTest.Outlines(Plan(BlankWith(ornament)), ornament, outlines);
        Assert.Equal(2, outlines.Count);
    }

    [Fact]
    public void Outlines_AreEmptyForAComponentThatIsNotDrawn()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        ornament.Visible = false;
        var outlines = new List<Vector2[]> { new[] { Vector2.One } };

        CanvasHitTest.Outlines(Plan(BlankWith(ornament)), ornament, outlines);

        Assert.Empty(outlines);
    }

    // ---------------------------------------------------------------- editor session

    /// <summary>A saved Classic Plate with a Corner Ornament (Components[0]) and a Plate Frame (Components[1]), opened: nothing to undo, nothing unsaved.</summary>
    private static Task<BasicHarness> SavedPlateWithOrnamentAsync()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket), ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine)];
        return BasicHarness.OpenDocumentAsync(document);
    }

    private static void AssertTarget(BasicEditorCategory? category, PlateComponent? component, (BasicEditorCategory? Category, PlateComponent? Component) target)
    {
        Assert.Equal(category, target.Category);
        Assert.Same(component, target.Component);
    }

    [Fact]
    public async Task SelectingAComponent_SelectsIt_AndNeverChangesThePlate()
    {
        using var harness = await SavedPlateWithOrnamentAsync();
        var ornament = harness.Document.Components![0];
        var session = harness.Session;
        var before = harness.Json();
        Assert.False(session.IsDirty);

        session.SelectComponent(ornament.Id);
        Assert.Equal(ornament.Id, session.SelectedComponentId);
        Assert.Null(session.SelectedElementId);

        session.SelectComponent(harness.Document.Components![1].Id);
        session.SelectComponent(null);
        Assert.Null(session.SelectedComponentId);

        Assert.Equal(before, harness.Json());
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public async Task SelectingAComponent_RecordsNoHistory()
    {
        using var harness = await SavedPlateWithOrnamentAsync();
        var ornament = harness.Document.Components![0];
        var session = harness.Session;
        session.EditComponent(ornament.Id, c => c.Offset = new Vector2(8f, 8f), continuous: false);
        var edited = harness.Json();

        session.SelectComponent(ornament.Id);
        session.SelectComponent(null);
        session.SelectComponent(ornament.Id);
        Assert.Equal(edited, harness.Json());

        // One undo takes back the edit itself: selecting added no entry of its own.
        session.Undo();
        Assert.Equal(Vector2.Zero, harness.Document.Components!.Single(c => c.Id == ornament.Id).Offset);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);

        // ...and the Component is still there, so it stays selected.
        Assert.Equal(ornament.Id, session.SelectedComponentId);
    }

    [Fact]
    public async Task AnIdThePlateDoesNotHold_ClearsTheSelection()
    {
        using var harness = await SavedPlateWithOrnamentAsync();
        var ornament = harness.Document.Components![0];
        var session = harness.Session;
        var name = BasicSections.Find(harness.Document, ProfileElementRole.BasicName)!;

        session.SelectComponent(Guid.NewGuid());
        Assert.Null(session.SelectedComponentId);

        session.SelectComponent(ornament.Id);
        session.SelectComponent(Guid.NewGuid());
        Assert.Null(session.SelectedComponentId);

        // An element's id is not a Component's.
        session.SelectComponent(name.Id);
        Assert.Null(session.SelectedComponentId);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task ElementAndComponentSelection_AreExclusive()
    {
        using var harness = await SavedPlateWithOrnamentAsync();
        var ornament = harness.Document.Components![0];
        var session = harness.Session;
        var name = BasicSections.Find(harness.Document, ProfileElementRole.BasicName)!;

        session.Select(name.Id);
        session.SelectComponent(ornament.Id);
        Assert.Null(session.SelectedElementId);
        Assert.Equal(ornament.Id, session.SelectedComponentId);

        session.Select(name.Id);
        Assert.Equal(name.Id, session.SelectedElementId);
        Assert.Null(session.SelectedComponentId);

        session.SelectComponent(ornament.Id);
        session.Select(null);
        Assert.Null(session.SelectedElementId);
        Assert.Null(session.SelectedComponentId);

        session.Select(name.Id);
        session.SelectComponent(null);
        Assert.Null(session.SelectedElementId);
        Assert.Null(session.SelectedComponentId);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task RemovingTheSelectedComponent_DropsTheSelection_AndRemovingAnotherKeepsIt()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var session = harness.Session;
        var ornament = session.AddComponent(BuiltInComponentCatalog.CornerOrnamentBracket)!.Value;
        var frame = session.AddComponent(BuiltInComponentCatalog.PlateFrameLine)!.Value;
        session.SelectComponent(ornament);

        session.RemoveComponent(frame);
        Assert.Equal(ornament, session.SelectedComponentId);

        session.RemoveComponent(ornament);
        Assert.Null(session.SelectedComponentId);

        // Undoing the removal brings the Component back, not the selection.
        session.Undo();
        Assert.Contains(harness.Document.Components!, c => c.Id == ornament);
        Assert.Null(session.SelectedComponentId);
    }

    [Fact]
    public async Task UndoingTheSelectedComponentsAddition_DropsTheSelection()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var session = harness.Session;
        var ornament = session.AddComponent(BuiltInComponentCatalog.CornerOrnamentBracket)!.Value;
        session.SelectComponent(ornament);

        session.Undo();

        Assert.True(harness.Document.Components is null or { Count: 0 });
        Assert.Null(session.SelectedComponentId);

        session.Redo();
        Assert.Null(session.SelectedComponentId);
        session.SelectComponent(ornament);
        Assert.Equal(ornament, session.SelectedComponentId);
    }

    [Fact]
    public async Task BasicSlots_ChangingTheStyleKeepsTheSelection_AndNoneDropsIt()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var session = harness.Session;
        session.SetComponentSlot(PlateComponentKind.CornerOrnament, BuiltInComponentCatalog.CornerOrnamentBracket);
        var id = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.CornerOrnament)!.Id;
        session.SelectComponent(id);

        session.SetComponentSlot(PlateComponentKind.CornerOrnament, BuiltInComponentCatalog.CornerOrnamentDiamond);
        Assert.Equal(id, session.SelectedComponentId);

        session.SetComponentSlot(PlateComponentKind.CornerOrnament, null);
        Assert.Null(session.SelectedComponentId);
    }

    [Fact]
    public async Task DiscardingTheChangesThatAddedTheSelectedComponent_DropsTheSelection()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var session = harness.Session;
        var ornament = session.AddComponent(BuiltInComponentCatalog.CornerOrnamentBracket)!.Value;
        session.SelectComponent(ornament);

        Assert.True(session.DiscardChanges());

        Assert.Null(session.SelectedComponentId);
    }

    // ---------------------------------------------------------------- Basic preview clicks

    [Fact]
    public void BasicPreview_AClickOnAnOrnament_OpensStyle_WithThatComponent()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        document.Components = [ornament];
        var plan = Plan(document);

        foreach (var corner in StepsOf(plan, ornament))
        {
            AssertTarget(BasicEditorCategory.Style, ornament, BasicEditorView.TargetAt(document, plan, CenterOf(corner.Placement.Rect)));
        }

        AssertTarget(BasicEditorCategory.Identity, null, BasicEditorView.TargetAt(document, plan, CenterOf(BasicSections.Find(document, ProfileElementRole.BasicName)!)));
        AssertTarget(null, null, BasicEditorView.TargetAt(document, plan, new Vector2(-200f, -200f)));
    }

    [Fact]
    public void BasicPreview_PortraitFrameEdge_OpensPortrait_WithTheFrame_AndFreeformElementsAreLookedThrough()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(document).CreatePortrait(Guid.NewGuid());
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine);
        document.Components = [frame];
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;
        var edge = new Vector2(portrait.Position.X + 4f, CenterOf(portrait).Y);

        var plan = Plan(document);
        AssertTarget(BasicEditorCategory.Portrait, null, BasicEditorView.TargetAt(document, plan, CenterOf(portrait)));
        AssertTarget(BasicEditorCategory.Portrait, frame, BasicEditorView.TargetAt(document, plan, edge));

        // A sticker over the frame's edge isn't a Basic section: the click reaches the frame beneath.
        document.Elements.Add(new TextProfileElement { Text = "Sticker", Position = edge - new Vector2(20f), Size = new Vector2(40f), ZIndex = 999 });
        plan = Plan(document);
        AssertTarget(BasicEditorCategory.Portrait, frame, BasicEditorView.TargetAt(document, plan, edge));
    }

    [Fact]
    public void ComponentKinds_OpenTheCategoryHoldingTheirSlot()
    {
        Assert.Equal(BasicEditorCategory.Portrait, BasicEditorView.CategoryOf(PlateComponentKind.PortraitFrame));
        Assert.Equal(BasicEditorCategory.Portrait, BasicEditorView.CategoryOf(PlateComponentKind.PortraitOverlay));
        Assert.Equal(BasicEditorCategory.Identity, BasicEditorView.CategoryOf(PlateComponentKind.NameBacking));

        foreach (var kind in new[]
        {
            PlateComponentKind.PlateFrame, PlateComponentKind.CornerOrnament, PlateComponentKind.Divider,
            PlateComponentKind.SectionHeader, PlateComponentKind.Background, PlateComponentKind.Unknown, (PlateComponentKind)99,
        })
        {
            Assert.Equal(BasicEditorCategory.Style, BasicEditorView.CategoryOf(kind));
        }
    }

    // ---------------------------------------------------------------- decorations over text

    [Fact]
    public void AHeadingUnderASectionHeader_StillOpensItsSection()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(document).SetPlaystyles(["Casual"]);
        var header = ComponentDocuments.Of(BuiltInComponentCatalog.SectionHeaderUnderline);
        document.Components = [header];
        var plan = Plan(document);
        var heading = BasicSections.Find(document, ProfileElementRole.BasicPlaystyleHeading)!;
        Assert.Contains(plan, step => ReferenceEquals(step.Component, header)); // drawn over the heading

        AssertTarget(BasicEditorCategory.Details, null, BasicEditorView.TargetAt(document, plan, CenterOf(heading)));
        Assert.Same(heading, CanvasHitTest.Find(plan, CenterOf(heading), 1f).Element);

        // With the heading looked through, the Section Header over it is what is hit.
        Assert.Same(header, CanvasHitTest.Find(plan, CenterOf(heading), 1f, skipElement: element => ReferenceEquals(element, heading)).Component);
    }

    [Theory]
    [InlineData(PlateComponentKind.SectionHeader)]
    [InlineData(PlateComponentKind.Divider)]
    public void ADecorationOverText_LetsTheTextTakeTheClick_AndIsHitWhereNothingIsUnderIt(PlateComponentKind kind)
    {
        var definition = BuiltInComponentCatalog.OfKind(kind).First(d => !d.RequiresAsset);
        var decoration = ComponentDocuments.Of(definition.Id);
        var text = new TextProfileElement { Text = "Heading", Position = new Vector2(100f, 100f), Size = new Vector2(200f, 40f) };
        var box = new ElementRect(new Vector2(100f, 100f), new Vector2(400f, 40f)); // reaches past the text
        var plan = new List<PaintStep>
        {
            new(PlateLayer.Identity, text, null, null, default),
            new(PlateLayer.Decorations, null, decoration, definition, new ComponentPlacement(box, 0f, false, false)),
        };

        Assert.True(CanvasHitTest.YieldsToText(kind));
        Assert.Same(text, CanvasHitTest.Find(plan, new Vector2(150f, 120f), 1f).Element);
        Assert.Same(decoration, CanvasHitTest.Find(plan, new Vector2(450f, 120f), 1f).Component);
    }

    [Fact]
    public void AnOrnamentOverText_StaysOnTop()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        var definition = BuiltInComponentCatalog.Find(ornament.DefinitionId);
        var text = new TextProfileElement { Text = "Corner", Position = new Vector2(10f, 10f), Size = new Vector2(80f, 80f) };
        var plan = new List<PaintStep>
        {
            new(PlateLayer.Identity, text, null, null, default),
            new(PlateLayer.Decorations, null, ornament, definition, new ComponentPlacement(new ElementRect(new Vector2(0f), new Vector2(100f)), 0f, false, false)),
        };

        Assert.False(CanvasHitTest.YieldsToText(PlateComponentKind.CornerOrnament));
        Assert.Same(ornament, CanvasHitTest.Find(plan, new Vector2(50f), 1f).Component);
    }

    // ---------------------------------------------------------------- artwork hit where it is drawn

    /// <summary>The logical canvas point drawn at (<paramref name="u"/>, <paramref name="v"/>) of a
    /// placement's image (0 to 1 from the image's top left as stored), mirrored and turned as the box is.</summary>
    private static Vector2 ImagePoint(in PaintStep step, float u, float v)
    {
        var rect = step.Placement.Rect;
        var local = new Vector2(step.Placement.MirrorX ? 1f - u : u, step.Placement.MirrorY ? 1f - v : v) * rect.Size;
        var point = rect.Position + local;
        var center = rect.Position + (rect.Size / 2f);
        return step.Placement.RotationDegrees == 0f ? point : RotationGeometry.RotatePoint(point, center, step.Placement.RotationDegrees);
    }

    [Fact]
    public void EveryCornerOrnamentArtwork_HasItsCoverageMeasured()
    {
        var artworks = BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament).Where(d => d.Art is not null).ToList();

        Assert.NotEmpty(artworks);
        Assert.All(artworks, definition =>
        {
            var coverage = ArtCoverage.For(definition.Art!);
            Assert.True(coverage is not null, $"{definition.Id} has no coverage: run tools/art/measure_coverage.py");
            Assert.Equal(ArtCoverage.GridSize, coverage!.Rows.Length);
            Assert.Contains(coverage.Rows, row => row != 0u); // something is drawn
            Assert.Contains(coverage.Rows, row => row != uint.MaxValue); // and not all of the box
        });
    }

    [Fact]
    public void ArtCoverage_ReadsItsGrid_AndNothingOutsideTheImage()
    {
        var rows = new uint[ArtCoverage.GridSize];
        rows[0] = 1u; // only the top-left cell
        var coverage = new ArtCoverage(rows);

        Assert.True(coverage.Covers(0f, 0f));
        Assert.True(coverage.Covers(0.02f, 0.02f));
        Assert.False(coverage.Covers(0.05f, 0.02f));
        Assert.False(coverage.Covers(0.02f, 0.05f));
        Assert.False(coverage.Covers(1f, 1f));
        Assert.False(coverage.Covers(-0.01f, 0f));
        Assert.False(coverage.Covers(0f, 1.01f));
        Assert.False(coverage.Covers(float.NaN, 0f));
        Assert.False(new ArtCoverage(new uint[3]).Covers(0f, 0f)); // a malformed grid covers nothing
    }

    [Fact]
    public void ACornerOrnamentsArtwork_IsHitWhereItIsDrawn_AndClicksOnItsClearPartReachWhatIsUnderIt()
    {
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentCelestialSakura);
        var definition = BuiltInComponentCatalog.Find(ornament.DefinitionId)!;
        var text = new TextProfileElement { Text = "Corner", Position = new Vector2(0f), Size = new Vector2(100f) };
        var box = new ElementRect(new Vector2(0f), new Vector2(100f));
        var plan = new List<PaintStep>
        {
            new(PlateLayer.Identity, text, null, null, default),
            new(PlateLayer.Decorations, null, ornament, definition, new ComponentPlacement(box, 0f, false, false)),
        };

        // Its blossoms run along the top; its inner quarter (toward the Plate) is clear.
        Assert.Same(ornament, CanvasHitTest.Find(plan, new Vector2(50f, 17f), 1f).Component);
        Assert.Same(text, CanvasHitTest.Find(plan, new Vector2(85f, 85f), 1f).Element);
        Assert.False(CanvasHitTest.Hits(plan[1], new Vector2(85f, 85f), 1f));

        // Its stem runs down the left: mirrored, the stem is on the right.
        Assert.Same(ornament, CanvasHitTest.Find(plan, new Vector2(10f, 64f), 1f).Component);
        Assert.Same(text, CanvasHitTest.Find(plan, new Vector2(90f, 64f), 1f).Element);
        plan[1] = new(PlateLayer.Decorations, null, ornament, definition, new ComponentPlacement(box, 0f, true, false));
        Assert.Same(text, CanvasHitTest.Find(plan, new Vector2(10f, 64f), 1f).Element);
        Assert.Same(ornament, CanvasHitTest.Find(plan, new Vector2(90f, 64f), 1f).Component);

        // A procedural ornament has no artwork to measure: its whole box takes the click, as before.
        var bracket = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        plan[1] = new(PlateLayer.Decorations, null, bracket, BuiltInComponentCatalog.Find(bracket.DefinitionId), new ComponentPlacement(box, 0f, false, false));
        Assert.Same(bracket, CanvasHitTest.Find(plan, new Vector2(85f, 85f), 1f).Component);
    }

    [Fact]
    public void BasicPreview_ArtCornerOrnaments_TakeClicksOnlyWhereTheyAreDrawn_InEveryCorner()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(document).CreatePortrait(Guid.NewGuid());
        var plain = Plan(document);

        foreach (var definition in BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament).Where(d => d.Art is not null))
        {
            var ornament = ComponentDocuments.Of(definition.Id);
            document.Components = [ornament];
            var plan = Plan(document);
            var coverage = ArtCoverage.For(definition.Art!)!;
            var corners = StepsOf(plan, ornament);
            Assert.Equal(4, corners.Count);

            foreach (var corner in corners)
            {
                // Each cell's center: the ornament where its artwork is drawn, else whatever the Plate has there without it.
                for (var y = 0; y < ArtCoverage.GridSize; y++)
                {
                    for (var x = 0; x < ArtCoverage.GridSize; x++)
                    {
                        var u = (x + 0.5f) / ArtCoverage.GridSize;
                        var v = (y + 0.5f) / ArtCoverage.GridSize;
                        var point = ImagePoint(corner, u, v);
                        var target = BasicEditorView.TargetAt(document, plan, point);
                        if (coverage.Covers(u, v))
                        {
                            AssertTarget(BasicEditorCategory.Style, ornament, target);
                        }
                        else
                        {
                            Assert.Equal(BasicEditorView.TargetAt(document, plain, point), target);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void BasicPreview_AClickOnTheClearInsideOfAnArtOrnament_OpensThePortraitUnderIt()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicDocuments.Editor(document).CreatePortrait(Guid.NewGuid());
        var portrait = BasicSections.Find(document, ProfileElementRole.BasicPortrait)!;
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentCelestialSakura);
        document.Components = [ornament];
        var plan = Plan(document);

        // A corner of the ornament whose clear inner quarter lies over the portrait.
        var overPortrait = StepsOf(plan, ornament)
            .Select(corner => ImagePoint(corner, 0.85f, 0.85f))
            .Where(point => ProfilePaintOrder.Contains(portrait, point))
            .ToList();
        Assert.NotEmpty(overPortrait);
        Assert.All(overPortrait, point => AssertTarget(BasicEditorCategory.Portrait, null, BasicEditorView.TargetAt(document, plan, point)));
    }
}
