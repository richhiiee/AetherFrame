using System;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Clean Preview's transparent presentation: a window exactly around the Plate's fitted visual bounds.</summary>
public class CleanPreviewLayoutTests
{
    private static readonly Vector2 HostPos = new(200, 100);
    private static readonly Vector2 HostSize = new(1400, 900);

    [Fact]
    public void WithoutOverflow_TheWindowIsTheFittedPlate_PlusTheSafeMargin()
    {
        var document = ComponentDocuments.WithAnchors();
        var bounds = ProfileVisualBounds.Compute(document);
        Assert.Equal(ProfileVisualBounds.Logical(document), bounds);

        var layout = CleanPreviewLayout.Compute(HostPos, HostSize, bounds)!.Value;
        var fit = PlateViewFit.Fit(HostSize, bounds);
        var canvasScreen = HostPos + fit.CanvasOffset;

        Assert.Equal(fit.Scale, layout.Scale);
        Assert.Equal(canvasScreen, layout.WindowPos + layout.CanvasOffset); // exactly where every other fit would put it
        AssertContains(layout, canvasScreen, canvasScreen + fit.Size);
        Assert.True(layout.WindowSize.X <= fit.Size.X + (2 * CleanPreviewLayout.SafeMargin) + 2);
        Assert.True(layout.WindowSize.Y <= fit.Size.Y + (2 * CleanPreviewLayout.SafeMargin) + 2);
    }

    [Theory]
    [InlineData(CornerMask.TopLeft)]
    [InlineData(CornerMask.BottomRight)]
    [InlineData(CornerMask.TopRight | CornerMask.BottomLeft)]
    [InlineData(CornerMask.All)]
    public void WithOverflow_TheWindowContainsEveryVisibleComponent(CornerMask corners)
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);
        component.Corners = CornerMasks.Normalize(corners);
        component.Offset = new Vector2(-90, -90);
        component.Scale = 3f;
        component.RotationDegrees = 30f;
        document.Components = [component];
        var bounds = ProfileVisualBounds.Compute(document);
        Assert.NotEqual(ProfileVisualBounds.Logical(document), bounds);

        var layout = CleanPreviewLayout.Compute(HostPos, HostSize, bounds)!.Value;
        var canvasScreen = layout.WindowPos + layout.CanvasOffset;

        // Every painted Component placement (rotated) lies inside the window: nothing is clipped.
        foreach (var step in ComponentDocuments.Plan(document))
        {
            if (step.Component is null)
            {
                continue;
            }

            var (min, max) = ComponentPaintPlan.GetVisualBounds([step], step.Component)!.Value;
            AssertContains(layout, canvasScreen + (min * layout.Scale), canvasScreen + (max * layout.Scale));
        }

        // And the whole Plate canvas too, undistorted.
        AssertContains(layout, canvasScreen, canvasScreen + (new Vector2(document.CanvasWidth, document.CanvasHeight) * layout.Scale));
    }

    [Fact]
    public void TheWindow_IsNoLargerThanWhatItShows_NotAHostSizedInputBlocker()
    {
        var document = ComponentDocuments.WithAnchors(); // 16:9 Plate in a 14:9 host
        var bounds = ProfileVisualBounds.Compute(document);

        var layout = CleanPreviewLayout.Compute(HostPos, HostSize, bounds)!.Value;
        var shown = bounds.Size * layout.Scale;

        Assert.True(layout.WindowSize.X < HostSize.X || layout.WindowSize.Y < HostSize.Y);
        Assert.True(layout.WindowSize.X - shown.X <= (2 * CleanPreviewLayout.SafeMargin) + 1f);
        Assert.True(layout.WindowSize.Y - shown.Y <= (2 * CleanPreviewLayout.SafeMargin) + 1f);

        // Stays within the rectangle the editor occupied (give or take the safe margin).
        Assert.True(layout.WindowPos.X >= HostPos.X - CleanPreviewLayout.SafeMargin - 1f);
        Assert.True(layout.WindowPos.Y >= HostPos.Y - CleanPreviewLayout.SafeMargin - 1f);
        Assert.True(layout.WindowPos.X + layout.WindowSize.X <= HostPos.X + HostSize.X + CleanPreviewLayout.SafeMargin + 1f);
        Assert.True(layout.WindowPos.Y + layout.WindowSize.Y <= HostPos.Y + HostSize.Y + CleanPreviewLayout.SafeMargin + 1f);
    }

    [Fact]
    public void TheWindow_IsPixelAligned_SoRoundingCantClipAnEdge()
    {
        var bounds = new CanvasBounds(new Vector2(-13.3f, -7.7f), new Vector2(1291.1f, 733.9f));

        var layout = CleanPreviewLayout.Compute(new Vector2(10.4f, 20.6f), new Vector2(977.7f, 611.3f), bounds)!.Value;

        Assert.Equal(layout.WindowPos, new Vector2(MathF.Round(layout.WindowPos.X), MathF.Round(layout.WindowPos.Y)));
        Assert.Equal(layout.WindowSize, new Vector2(MathF.Round(layout.WindowSize.X), MathF.Round(layout.WindowSize.Y)));
        var canvasScreen = layout.WindowPos + layout.CanvasOffset;
        AssertContains(layout, canvasScreen + (bounds.Min * layout.Scale), canvasScreen + (bounds.Max * layout.Scale));
    }

    [Fact]
    public void NothingToPresent_IsNull()
    {
        var bounds = new CanvasBounds(Vector2.Zero, new Vector2(1280, 720));
        Assert.Null(CleanPreviewLayout.Compute(HostPos, Vector2.Zero, bounds));
        Assert.Null(CleanPreviewLayout.Compute(HostPos, HostSize, new CanvasBounds(Vector2.Zero, Vector2.Zero)));
    }

    [Fact]
    public void Layout_NeverChangesThePlate()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentBracket);
        component.Offset = new Vector2(-50, 10);
        document.Components = [component];
        var json = Persistence.PlateDocuments.ToJson(document).ToJsonString();

        CleanPreviewLayout.Compute(HostPos, HostSize, ProfileVisualBounds.Compute(document));

        Assert.Equal(json, Persistence.PlateDocuments.ToJson(document).ToJsonString());
    }

    // ---- Close control -------------------------------------------------------------------------

    public static TheoryData<float, float, float, float> OverflowSides() => new()
    {
        { -200f, 0f, 0f, 0f },   // left
        { 0f, -200f, 0f, 0f },   // top
        { 0f, 0f, 200f, 0f },    // right
        { 0f, 0f, 0f, 200f },    // bottom
        { -150f, -150f, 150f, 150f }, // every side
    };

    [Theory]
    [MemberData(nameof(OverflowSides))]
    public void CloseControl_SitsInsideTheTopRightOfTheVisualBounds_WithOverflowOnAnySide(float left, float top, float right, float bottom)
    {
        var bounds = new CanvasBounds(new Vector2(left, top), new Vector2(1280 + right, 720 + bottom));

        var layout = CleanPreviewLayout.Compute(HostPos, HostSize, bounds, 26f)!.Value;
        var (boundsMin, boundsMax) = ScreenBounds(layout, bounds);
        var (buttonMin, buttonMax) = Button(layout);

        // Inside the visual bounds, near their top-right corner.
        Assert.True(buttonMin.X >= boundsMin.X && buttonMin.Y >= boundsMin.Y && buttonMax.X <= boundsMax.X + 0.5f && buttonMax.Y <= boundsMax.Y);
        Assert.True(boundsMax.X - buttonMax.X <= 26f && buttonMin.Y - boundsMin.Y <= 26f);

        // Inside the window, which it didn't enlarge beyond the bounds plus their safe margin.
        AssertContains(layout, buttonMin, buttonMax);
        Assert.True(layout.WindowSize.X <= (boundsMax.X - boundsMin.X) + (2 * CleanPreviewLayout.SafeMargin) + 2f);
        Assert.True(layout.WindowSize.Y <= (boundsMax.Y - boundsMin.Y) + (2 * CleanPreviewLayout.SafeMargin) + 2f);
    }

    [Fact]
    public void CloseControl_OnATinyPlate_ExtendsTheWindowOnlyByItsOwnSize()
    {
        var bounds = new CanvasBounds(Vector2.Zero, new Vector2(40, 20));

        var layout = CleanPreviewLayout.Compute(HostPos, new Vector2(30, 15), bounds, 26f)!.Value;
        var (boundsMin, boundsMax) = ScreenBounds(layout, bounds);
        var (buttonMin, buttonMax) = Button(layout);

        AssertContains(layout, buttonMin, buttonMax);
        AssertContains(layout, boundsMin, boundsMax);
        Assert.Equal(26f, buttonMax.X - buttonMin.X);

        // Adjacent to the composition: at or around its top-right corner.
        Assert.True(buttonMax.X >= boundsMax.X - 26f && buttonMin.Y <= boundsMin.Y + 26f);

        // The window is the union of the bounds (plus margin) and the control, nothing more.
        var unionMin = Vector2.Min(boundsMin - new Vector2(CleanPreviewLayout.SafeMargin + 1f), buttonMin);
        var unionMax = Vector2.Max(boundsMax + new Vector2(CleanPreviewLayout.SafeMargin + 1f), buttonMax);
        Assert.True(layout.WindowSize.X <= Math.Max(unionMax.X - unionMin.X, CleanPreviewLayout.MinWindowEdge) + 0.01f);
        Assert.True(layout.WindowSize.Y <= Math.Max(unionMax.Y - unionMin.Y, CleanPreviewLayout.MinWindowEdge) + 0.01f);
    }

    [Theory]
    [InlineData(10000f, 50f)]   // extremely wide
    [InlineData(50f, 10000f)]   // extremely tall
    [InlineData(1f, 1f)]        // extremely small
    public void CloseControl_WithExtremeVisualBounds_StaysInTheWindowAndNearTheComposition(float width, float height)
    {
        var bounds = new CanvasBounds(new Vector2(-width / 3f, -height / 3f), new Vector2(width, height));

        var layout = CleanPreviewLayout.Compute(HostPos, HostSize, bounds, 26f)!.Value;
        var (boundsMin, boundsMax) = ScreenBounds(layout, bounds);
        var (buttonMin, buttonMax) = Button(layout);

        AssertContains(layout, buttonMin, buttonMax);
        AssertContains(layout, boundsMin, boundsMax);
        Assert.True(Vector2.Distance(new Vector2(buttonMax.X, buttonMin.Y), new Vector2(boundsMax.X, boundsMin.Y)) <= 26f * 1.5f);
        Assert.True(layout.WindowSize.X <= Math.Max(boundsMax.X - boundsMin.X, 26f) + 26f + 4f);
        Assert.True(layout.WindowSize.Y <= Math.Max(boundsMax.Y - boundsMin.Y, 26f) + 26f + 4f);
    }

    [Fact]
    public void CloseControl_ScalesWithTheRequestedSize_AndIsPixelAligned()
    {
        var bounds = new CanvasBounds(new Vector2(-33.3f, -12.1f), new Vector2(1301.7f, 745.2f));
        foreach (var size in new[] { 26f, 39f, 52f })
        {
            var layout = CleanPreviewLayout.Compute(new Vector2(3.3f, 7.7f), HostSize, bounds, size)!.Value;
            Assert.Equal(size, layout.CloseButtonSize);
            var absolute = layout.WindowPos + layout.CloseButtonOffset;
            Assert.Equal(absolute, new Vector2(MathF.Round(absolute.X), MathF.Round(absolute.Y)));
        }
    }

    // ---- Presentation state ------------------------------------------------------------------

    [Fact]
    public void Presentation_RequestsATransparentRootAndChild_NoBorderPaddingTitleOrBlur()
    {
        Assert.Equal(Vector4.Zero, CleanPreviewPresentation.BackgroundColor);
        Assert.Equal(Vector2.Zero, CleanPreviewPresentation.WindowPadding);
        Assert.Equal(0f, CleanPreviewPresentation.WindowBorderSize);
        Assert.False(CleanPreviewPresentation.AllowBackgroundBlur);
        Assert.False(CleanPreviewPresentation.ShowTitleBar);
    }

    [Fact]
    public void Presentation_DrawsTheFinishedPlate_WithoutTheWorkspaceBackdrop()
    {
        var options = CleanPreviewPresentation.RenderOptions;

        Assert.True(options.HideCanvasBackdrop);
        Assert.Equal(ProfileRenderOptions.Finished with { HideCanvasBackdrop = true }, options);
        Assert.False(options.ShowElementBounds);
        Assert.False(options.ShowEmptySectionHeadings);
        Assert.Null(options.PlaceholderProvider);

        // Every other surface keeps the backdrop.
        Assert.False(ProfileRenderOptions.Finished.HideCanvasBackdrop);
        Assert.False(EditorPlaceholders.CanvasOptions.HideCanvasBackdrop);
        Assert.False(EditorPlaceholders.CanvasOptionsWithoutGuides.HideCanvasBackdrop);
    }

    [Fact]
    public void CloseControl_Colors_ReadOnBrightAndDarkPlates()
    {
        // A dark translucent backing under a light glyph and ring: the glyph contrasts with its own
        // backing, whatever the Plate behind it is.
        Assert.InRange(CleanPreviewPresentation.CloseBacking.W, 0.4f, 0.9f);
        Assert.True(Luminance(CleanPreviewPresentation.CloseBacking) < 0.1f);
        Assert.True(Luminance(CleanPreviewPresentation.CloseGlyph) > 0.9f && CleanPreviewPresentation.CloseGlyph.W > 0.9f);
        Assert.True(Luminance(CleanPreviewPresentation.CloseRing) > 0.9f && CleanPreviewPresentation.CloseRing.W > 0.7f);
    }

    private static float Luminance(Vector4 c) => (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);

    private static (Vector2 Min, Vector2 Max) ScreenBounds(CleanPreviewLayout layout, CanvasBounds bounds)
    {
        var canvas = layout.WindowPos + layout.CanvasOffset;
        return (canvas + (bounds.Min * layout.Scale), canvas + (bounds.Max * layout.Scale));
    }

    private static (Vector2 Min, Vector2 Max) Button(CleanPreviewLayout layout)
    {
        var min = layout.WindowPos + layout.CloseButtonOffset;
        return (min, min + new Vector2(layout.CloseButtonSize));
    }

    private static void AssertContains(CleanPreviewLayout layout, Vector2 min, Vector2 max)
    {
        const float epsilon = 0.01f;
        var windowMax = layout.WindowPos + layout.WindowSize;
        Assert.True(min.X >= layout.WindowPos.X - epsilon && min.Y >= layout.WindowPos.Y - epsilon, $"{min} outside window at {layout.WindowPos}");
        Assert.True(max.X <= windowMax.X + epsilon && max.Y <= windowMax.Y + epsilon, $"{max} outside window ending {windowMax}");
    }
}
