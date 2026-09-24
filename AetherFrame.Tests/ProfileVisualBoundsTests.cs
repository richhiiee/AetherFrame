using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Logical vs visual Plate bounds (<see cref="ProfileVisualBounds"/>) and fitting them into a viewport (<see cref="PlateViewFit"/>).</summary>
public class ProfileVisualBoundsTests
{
    private const string Astrolabe = BuiltInComponentCatalog.CornerOrnamentAstrolabePivot;
    private const string Bracket = BuiltInComponentCatalog.CornerOrnamentBracket;

    [Fact]
    public void Logical_IsTheSavedCanvas_AndNeverChanges()
    {
        var document = ComponentDocuments.WithAnchors();
        var before = (document.CanvasWidth, document.CanvasHeight);
        var component = Overflowing(Astrolabe, CornerMask.All);
        document.Components = [component];

        var visual = ProfileVisualBounds.Compute(document);

        Assert.Equal(new CanvasBounds(Vector2.Zero, new Vector2(document.CanvasWidth, document.CanvasHeight)), ProfileVisualBounds.Logical(document));
        Assert.NotEqual(ProfileVisualBounds.Logical(document), visual);
        Assert.Equal(before, (document.CanvasWidth, document.CanvasHeight));
        Assert.Equal(new Vector2(-80, -80), component.Offset); // nothing about the Component is touched
        Assert.Equal(3f, component.Scale);
    }

    [Fact]
    public void NoComponentsOrNoOverflow_VisualEqualsLogical()
    {
        var document = ComponentDocuments.WithAnchors();
        Assert.Equal(ProfileVisualBounds.Logical(document), ProfileVisualBounds.Compute(document));

        document.Components = ComponentDocuments.OneOfEach().Append(ComponentDocuments.Of(Astrolabe)).ToList();
        Assert.Equal(ProfileVisualBounds.Logical(document), ProfileVisualBounds.Compute(document));
    }

    [Fact]
    public void OneCornerOutside_ExtendsOnlyThatSide()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(Astrolabe);
        component.Corners = CornerMask.TopLeft;
        component.Offset = new Vector2(-100, -50);
        document.Components = [component];

        var visual = ProfileVisualBounds.Compute(document);
        var step = Assert.Single(ComponentDocuments.Plan(document), s => ReferenceEquals(s.Component, component));

        Assert.Equal(step.Placement.Rect.Position, visual.Min);
        Assert.True(visual.Min.X < 0f && visual.Min.Y < 0f);
        Assert.Equal(new Vector2(document.CanvasWidth, document.CanvasHeight), visual.Max);
    }

    public static IEnumerable<object[]> Masks() =>
    [
        [CornerMask.TopLeft], [CornerMask.TopRight], [CornerMask.BottomLeft], [CornerMask.BottomRight],
        [CornerMask.TopLeft | CornerMask.TopRight], [CornerMask.BottomLeft | CornerMask.BottomRight],
        [CornerMask.TopLeft | CornerMask.BottomRight], [CornerMask.TopRight | CornerMask.BottomLeft],
        [CornerMask.All],
    ];

    [Theory]
    [MemberData(nameof(Masks))]
    public void CornerMasks_OnlySelectedCornersOverflow(CornerMask mask)
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [Overflowing(Astrolabe, mask)];
        var logical = ProfileVisualBounds.Logical(document);

        var visual = ProfileVisualBounds.Compute(document);

        Assert.Equal((mask & (CornerMask.TopLeft | CornerMask.BottomLeft)) != 0, visual.Min.X < logical.Min.X);
        Assert.Equal((mask & (CornerMask.TopRight | CornerMask.BottomRight)) != 0, visual.Max.X > logical.Max.X);
        Assert.Equal((mask & (CornerMask.TopLeft | CornerMask.TopRight)) != 0, visual.Min.Y < logical.Min.Y);
        Assert.Equal((mask & (CornerMask.BottomLeft | CornerMask.BottomRight)) != 0, visual.Max.Y > logical.Max.Y);
    }

    [Fact]
    public void AllFourOverflowing_IsSymmetric()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [Overflowing(Bracket, CornerMask.All)];
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);

        var visual = ProfileVisualBounds.Compute(document);

        Assert.True(visual.Min.X < 0f && visual.Min.Y < 0f);
        Assert.Equal(-visual.Min.X, visual.Max.X - canvas.X, 3);
        Assert.Equal(-visual.Min.Y, visual.Max.Y - canvas.Y, 3);
        Assert.Equal(canvas / 2f, visual.Center);
    }

    [Fact]
    public void MultipleComponents_OverflowingDifferentSides_AreAllIncluded()
    {
        var document = ComponentDocuments.WithAnchors();
        var left = ComponentDocuments.Of(Astrolabe);
        left.Corners = CornerMask.TopLeft;
        left.Offset = new Vector2(-120, 0);
        var bottom = ComponentDocuments.Of(Bracket);
        bottom.Corners = CornerMask.BottomRight;
        bottom.Offset = new Vector2(0, -90); // mirrored for the bottom corners: outward
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        frame.Scale = 1.2f; // the plate frame grows past every edge
        document.Components = [left, bottom];

        var visual = ProfileVisualBounds.Compute(document);
        Assert.True(visual.Min.X < 0f);
        Assert.True(visual.Max.Y > document.CanvasHeight);
        Assert.Equal(0f, visual.Min.Y);
        Assert.Equal(document.CanvasWidth, visual.Max.X);

        document.Components.Add(frame);
        var withFrame = ProfileVisualBounds.Compute(document);
        Assert.True(withFrame.Min.Y < 0f && withFrame.Max.X > document.CanvasWidth);
        Assert.True(withFrame.Min.X <= visual.Min.X && withFrame.Max.Y >= visual.Max.Y);
    }

    [Fact]
    public void Rotation_Scale_AndOffset_AreIncluded()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(Astrolabe);
        component.Corners = CornerMask.TopLeft;
        document.Components = [component];
        Assert.Equal(ProfileVisualBounds.Logical(document), ProfileVisualBounds.Compute(document));

        component.Scale = 2.5f; // grows around the corner box's center, past the edge
        var scaled = ProfileVisualBounds.Compute(document);
        Assert.True(scaled.Min.X < 0f);

        component.RotationDegrees = 45f; // a rotated square reaches further
        var rotated = ProfileVisualBounds.Compute(document);
        Assert.True(rotated.Min.X < scaled.Min.X);

        component.Offset = new Vector2(-30, -30);
        var offset = ProfileVisualBounds.Compute(document);
        Assert.Equal(rotated.Min - new Vector2(30), offset.Min);
    }

    [Fact]
    public void HiddenMissingAndUnrenderableComponents_AreExcluded()
    {
        var document = ComponentDocuments.WithAnchors();
        var hidden = Overflowing(Astrolabe, CornerMask.All);
        hidden.Visible = false;
        var missing = Overflowing(Bracket, CornerMask.All);
        missing.DefinitionId = "af.corner-ornament.from-the-future";
        var unknownKind = Overflowing(Bracket, CornerMask.All);
        unknownKind.Kind = (PlateComponentKind)99;
        var mismatch = Overflowing(Astrolabe, CornerMask.All);
        mismatch.Kind = PlateComponentKind.PlateFrame;
        var noImage = new PlateComponent { Kind = PlateComponentKind.PortraitOverlay, DefinitionId = BuiltInComponentCatalog.PortraitOverlayImage, Scale = 4f };
        var noCorners = Overflowing(Bracket, CornerMask.None);
        document.Components = [hidden, missing, unknownKind, mismatch, noImage, noCorners];

        Assert.Equal(ProfileVisualBounds.Logical(document), ProfileVisualBounds.Compute(document));
    }

    [Fact]
    public void Bounds_AreDeterministic_AndMatchThePlanHelper()
    {
        var document = ComponentDocuments.WithAnchors();
        var a = Overflowing(Astrolabe, CornerMask.TopLeft | CornerMask.BottomRight);
        a.RotationDegrees = 17f;
        var b = Overflowing(Bracket, CornerMask.TopRight);
        document.Components = [a, b];

        var first = ProfileVisualBounds.Compute(document);
        Assert.Equal(first, ProfileVisualBounds.Compute(document));
        Assert.Equal(first, ProfileVisualBounds.Compute(PlateDocuments_RoundTrip(document)));

        var plan = ComponentDocuments.Plan(document);
        var expected = ProfileVisualBounds.Logical(document);
        foreach (var component in document.Components)
        {
            if (ComponentPaintPlan.GetVisualBounds(plan, component) is var (min, max))
            {
                expected = expected.Union(min, max);
            }
        }

        Assert.Equal(expected, ProfileVisualBounds.Compute(document, plan));
        Assert.Equal(expected, first);
    }

    [Fact]
    public void FillDrawnElements_MatchesTheRendererFilter()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Elements.First(e => e.Role == ProfileElementRole.BasicWorld).Visible = false; // leaves an empty heading
        var paintOrder = new List<ProfileElement>();
        var drawn = new List<ProfileElement>();

        ProfileVisualBounds.FillDrawnElements(document, ProfileRenderOptions.Finished, paintOrder, drawn);
        Assert.DoesNotContain(drawn, e => !e.Visible);
        Assert.DoesNotContain(drawn, e => e.Role == ProfileElementRole.BasicWorldHeading);

        ProfileVisualBounds.FillDrawnElements(document, new ProfileRenderOptions { ShowEmptySectionHeadings = true }, paintOrder, drawn);
        Assert.Contains(drawn, e => e.Role == ProfileElementRole.BasicWorldHeading);
    }

    // ---- Fitting --------------------------------------------------------------------------------

    [Fact]
    public void Fit_WithoutOverflow_IsTheClassicCanvasFit()
    {
        var fit = PlateViewFit.Fit(new Vector2(640, 600), new CanvasBounds(Vector2.Zero, new Vector2(1280, 720)));

        Assert.Equal(0.5f, fit.Scale);
        Assert.Equal(new Vector2(640, 360), fit.Size);
        Assert.Equal(new Vector2(0, 120), fit.CanvasOffset);

        var (scale, size) = BasicEditorView.ComputePreview(new Vector2(640, 600), 1280, 720, PreviewZoom.Fit);
        Assert.Equal(fit.Scale, scale);
        Assert.Equal(fit.Size, size);
    }

    [Theory]
    [InlineData(800f, 600f)]
    [InlineData(300f, 900f)]
    [InlineData(1920f, 400f)]
    public void Fit_ContainsTheFullVisualBounds_WithoutDistortion(float width, float height)
    {
        var document = ComponentDocuments.WithAnchors();
        var a = Overflowing(Astrolabe, CornerMask.TopLeft);
        var b = Overflowing(Bracket, CornerMask.BottomRight);
        document.Components = [a, b];
        var visual = ProfileVisualBounds.Compute(document);
        var available = new Vector2(width, height);

        var fit = PlateViewFit.Fit(available, visual);

        // Every visual corner lands inside the viewport.
        var min = fit.CanvasOffset + (visual.Min * fit.Scale);
        var max = fit.CanvasOffset + (visual.Max * fit.Scale);
        Assert.True(min.X >= -0.01f && min.Y >= -0.01f, $"{min}");
        Assert.True(max.X <= width + 0.01f && max.Y <= height + 0.01f, $"{max}");

        // Touches the viewport on the limiting axis (it's a fit, not a shrink), and is centered.
        Assert.True(Math.Abs(max.X - min.X - width) < 0.01f || Math.Abs(max.Y - min.Y - height) < 0.01f);
        Assert.Equal(available / 2f, (min + max) / 2f);

        // The canvas keeps its proportions and sits inside the visual bounds at its true place.
        var canvasMin = fit.CanvasOffset;
        var canvasMax = fit.CanvasOffset + (new Vector2(document.CanvasWidth, document.CanvasHeight) * fit.Scale);
        Assert.Equal(document.CanvasWidth / document.CanvasHeight, (canvasMax.X - canvasMin.X) / (canvasMax.Y - canvasMin.Y), 4);
        Assert.True(canvasMin.X > min.X && canvasMin.Y > min.Y);
        Assert.True(canvasMax.X < max.X && canvasMax.Y < max.Y);
    }

    [Fact]
    public void Fit_Zoomed_StartsAtTheScrollOrigin_WithTheBoundsTopLeftVisible()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [Overflowing(Astrolabe, CornerMask.All)];
        var visual = ProfileVisualBounds.Compute(document);

        var fit = BasicEditorView.ComputePreview(new Vector2(640, 600), visual, PreviewZoom.Larger);

        Assert.Equal(Vector2.Zero, fit.CanvasOffset + (visual.Min * fit.Scale));
        Assert.Equal(visual.Size * fit.Scale, fit.Size);
    }

    [Fact]
    public void Fit_NothingToDraw_IsNone()
    {
        Assert.Equal(PlateViewFit.None, PlateViewFit.Fit(Vector2.Zero, new CanvasBounds(Vector2.Zero, new Vector2(100))));
        Assert.Equal(PlateViewFit.None, PlateViewFit.Fit(new Vector2(100), new CanvasBounds(Vector2.Zero, new Vector2(0, 100))));
        Assert.Equal(PlateViewFit.None, PlateViewFit.Fit(new Vector2(100), new CanvasBounds(Vector2.Zero, new Vector2(float.NaN, 100))));
    }

    [Fact]
    public async Task AdvancedFit_FramesTheVisualBounds_AndIsUnchangedWithoutOverflow()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        var panel = new Vector2(1000, 700);
        var document = harness.Document;
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);

        harness.Session.ApplyFitZoom(panel);
        var plainZoom = harness.Session.Zoom;
        Assert.Equal(Vector2.Zero, harness.Session.PanOffset);

        var id = harness.Session.AddComponent(Bracket)!.Value;
        harness.Session.EditComponent(id, c =>
        {
            c.Corners = CornerMask.TopLeft;
            c.Offset = new Vector2(-150, -150);
        }, continuous: false);
        var visual = ProfileVisualBounds.Compute(document);

        harness.Session.ApplyFitZoom(panel);
        var zoom = harness.Session.Zoom;
        Assert.True(zoom < plainZoom);

        // Canvas placement as the Advanced canvas lays it out; the visual bounds are centered and inside the panel.
        var panelCenter = panel / 2f;
        var canvasOrigin = panelCenter - (canvas * zoom / 2f) + harness.Session.PanOffset;
        var min = canvasOrigin + (visual.Min * zoom);
        var max = canvasOrigin + (visual.Max * zoom);
        Assert.True(Vector2.Distance(panelCenter, (min + max) / 2f) < 0.01f);
        Assert.True(min.X >= 0f && min.Y >= 0f && max.X <= panel.X && max.Y <= panel.Y);

        // Fitting never edits the Plate.
        Assert.Equal(canvas, new Vector2(document.CanvasWidth, document.CanvasHeight));
        Assert.Equal(new Vector2(-150, -150), PlateComponentEditor.Find(document, id)!.Offset);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>A Corner Ornament pushed outward and enlarged so every selected corner leaves the canvas.</summary>
    private static PlateComponent Overflowing(string definitionId, CornerMask corners)
    {
        var component = ComponentDocuments.Of(definitionId);
        component.Corners = corners == CornerMask.All ? null : corners;
        component.Offset = new Vector2(-80, -80);
        component.Scale = 3f;
        return component;
    }

    private static ProfileDocument PlateDocuments_RoundTrip(ProfileDocument document) =>
        Persistence.PlateDocuments.Deserialize(Persistence.PlateDocuments.ToJson(document))!;
}
