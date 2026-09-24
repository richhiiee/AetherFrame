using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Plate Viewer's transparent presentation (<see cref="PlateViewerLayout"/>), its session
/// placement (<see cref="PlateViewerPlacement"/>), usage hint (<see cref="PlateViewerHint"/>), and
/// title bar ordering (<see cref="TitleBarOrder"/>).
/// </summary>
public class PlateViewerLayoutTests
{
    private static readonly Vector2 ViewportPos = new(0, 0);
    private static readonly Vector2 Viewport = new(2560, 1440);
    private static readonly Vector2 Canvas = new(1280, 720);
    private const float Control = PlateViewerLayout.DefaultControlSize;

    private static readonly CanvasBounds Plain = new(Vector2.Zero, Canvas);
    private static readonly CanvasBounds Overflowing = new(new Vector2(-120, -60), Canvas + new Vector2(90, 150));

    // ---- Layout ------------------------------------------------------------------------------

    [Fact]
    public void Default_Is100Percent_Centered()
    {
        var scale = PlateViewerLayout.DefaultScale(Plain, Viewport);
        var pos = PlateViewerLayout.CenteredPosition(Plain, scale, ViewportPos, Viewport);
        var size = PlateViewerLayout.CompositionSize(Plain, scale);

        Assert.Equal(PlateViewerLayout.BaseScale, scale);
        Assert.Equal(100f, PlateViewerLayout.PercentForScale(scale), 4);
        Assert.Equal(Viewport / 2f, pos + (size / 2f));
    }

    // ---- Scale baseline (rebased: new 100% = old 75%) ------------------------------------------

    [Fact]
    public void New100Percent_IsTheOld75PercentVisualSize()
    {
        // Before the rebase a percentage was of the Plate's own logical size (old 75% = 0.75 px per unit).
        const float old75 = 0.75f;

        Assert.Equal(old75, PlateViewerLayout.ScaleForPercent(100), 6);
        Assert.Equal(PlateViewerLayout.CompositionSize(Plain, old75), PlateViewerLayout.CompositionSize(Plain, PlateViewerLayout.ScaleForPercent(100)));
        Assert.Equal(new Vector2(960, 540), PlateViewerLayout.CompositionSize(Plain, PlateViewerLayout.ScaleForPercent(100)));

        // An existing session scale keeps its exact on-screen size; only its label changes (old 100% now reads 133%).
        var placement = new PlateViewerPlacement();
        placement.Update(Plain, Canvas, ViewportPos, Viewport);
        placement.SetPercent(100, Plain, Canvas, Viewport);
        Assert.Equal(100, placement.Percent);
        Assert.Equal(133f, PlateViewerLayout.PercentForScale(1f), 0);
    }

    [Theory]
    [InlineData(50, 0.5f)]
    [InlineData(75, 0.75f)]
    [InlineData(125, 1.25f)]
    [InlineData(150, 1.5f)]
    [InlineData(200, 2f)]
    public void Presets_AreRelativeToTheNew100Percent(int percent, float timesTheDefault)
    {
        var (placement, _) = Placed(Plain);
        var hundred = PlateViewerLayout.CompositionSize(Plain, PlateViewerLayout.ScaleForPercent(100));

        placement.SetPercent(percent, Plain, Canvas, Viewport);
        var size = PlateViewerLayout.CompositionSize(Plain, placement.Scale);

        Assert.True(Vector2.Distance(hundred * timesTheDefault, size) < 0.01f, $"{percent}%: {size} vs {hundred * timesTheDefault}");
        Assert.Equal(percent, placement.Percent);
    }

    [Fact]
    public void FirstOpen_AndResetSize_AreNew100Percent()
    {
        var (placement, _) = Placed(Plain);
        Assert.Equal(100, placement.Percent);
        Assert.Equal(PlateViewerLayout.BaseScale, placement.Scale);

        placement.SetPercent(200, Plain, Canvas, Viewport);
        placement.ZoomByWheel(-3f, Plain, Canvas, Viewport);
        placement.ResetSize(Plain, Canvas, Viewport);

        Assert.Equal(100, placement.Percent);
        Assert.Equal(PlateViewerLayout.BaseScale, placement.Scale, 6);
    }

    [Fact]
    public void Default_IsOnlyReducedWhenThePlateWouldNotFitTheScreen()
    {
        var huge = new CanvasBounds(Vector2.Zero, new Vector2(4000, 3000));

        Assert.Equal(PlateViewerLayout.MaxScale(huge, Viewport), PlateViewerLayout.DefaultScale(huge, Viewport), 6);
        Assert.True(PlateViewerLayout.DefaultScale(huge, Viewport) < PlateViewerLayout.BaseScale);
    }

    [Fact]
    public void CtrlWheel_StepsAreProportional_FromTheNewBaseline()
    {
        var (placement, _) = Placed(Plain);

        placement.ZoomByWheel(1f, Plain, Canvas, Viewport);
        Assert.Equal(110, placement.Percent);
        placement.ZoomByWheel(1f, Plain, Canvas, Viewport);
        Assert.Equal(121, placement.Percent);

        placement.SetPercent(200, Plain, Canvas, Viewport);
        placement.ZoomByWheel(-1f, Plain, Canvas, Viewport);
        Assert.Equal(182, placement.Percent); // 200 / 1.1: the same ratio at any size
    }

    [Fact]
    public void WindowIsExactlyTheComposition_OnlyCloseIsDrawnAsAControl()
    {
        var pos = new Vector2(300.4f, 200.6f);
        var layout = PlateViewerLayout.Compute(pos, 1f, Plain)!.Value;
        var composition = PlateViewerLayout.CompositionSize(Plain, 1f);

        Assert.True(layout.WindowSize.X <= composition.X + (2 * PlateViewerLayout.SafeMargin) + 1f);
        Assert.True(layout.WindowSize.Y <= composition.Y + (2 * PlateViewerLayout.SafeMargin) + 1f);

        // Close: inside the composition, near its top-right corner.
        var close = layout.WindowPos + layout.CloseOffset;
        Assert.True(close.X >= pos.X && close.Y >= pos.Y && close.X + Control <= pos.X + composition.X + 0.5f);
        Assert.True(pos.X + composition.X - (close.X + Control) <= Control && close.Y - pos.Y <= Control);
        Assert.Equal(new[] { "WindowPos", "WindowSize", "CanvasOffset", "Scale", "CloseOffset", "ControlSize" },
            typeof(PlateViewerLayout).GetProperties().Select(p => p.Name).ToArray()); // no move or resize grips
    }

    public static TheoryData<float, float, float, float> OverflowSides() => new()
    {
        { -200f, 0f, 0f, 0f },
        { 0f, -200f, 0f, 0f },
        { 0f, 0f, 200f, 0f },
        { 0f, 0f, 0f, 200f },
        { -150f, -150f, 150f, 150f },
    };

    [Theory]
    [MemberData(nameof(OverflowSides))]
    public void Overflow_OnAnySide_IsInsideTheWindow_AndTheCanvasSitsAtItsTruePlace(float left, float top, float right, float bottom)
    {
        var bounds = new CanvasBounds(new Vector2(left, top), Canvas + new Vector2(right, bottom));
        var scale = PlateViewerLayout.DefaultScale(bounds, Viewport);
        var pos = PlateViewerLayout.CenteredPosition(bounds, scale, ViewportPos, Viewport);

        var layout = PlateViewerLayout.Compute(pos, scale, bounds)!.Value;
        var canvasScreen = layout.WindowPos + layout.CanvasOffset;

        Assert.True(Vector2.Distance(pos, canvasScreen + (bounds.Min * scale)) < 0.001f);
        AssertInsideWindow(layout, canvasScreen + (bounds.Min * scale), canvasScreen + (bounds.Max * scale));
    }

    [Fact]
    public void RealPlate_WithOverflowingOrnaments_IsFullyVisible()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);
        component.Offset = new Vector2(-80, -80);
        component.Scale = 3f;
        component.RotationDegrees = 20f;
        document.Components = [component];
        var bounds = ProfileVisualBounds.Compute(document);
        var scale = PlateViewerLayout.DefaultScale(bounds, Viewport);

        var layout = PlateViewerLayout.Compute(PlateViewerLayout.CenteredPosition(bounds, scale, ViewportPos, Viewport), scale, bounds)!.Value;
        var canvasScreen = layout.WindowPos + layout.CanvasOffset;

        foreach (var step in ComponentDocuments.Plan(document).Where(s => s.Component is not null))
        {
            var (min, max) = ComponentPaintPlan.GetVisualBounds([step], step.Component!)!.Value;
            AssertInsideWindow(layout, canvasScreen + (min * scale), canvasScreen + (max * scale));
        }
    }

    [Fact]
    public void NothingToDraw_IsNull()
    {
        Assert.Null(PlateViewerLayout.Compute(Vector2.Zero, 0f, Plain));
        Assert.Null(PlateViewerLayout.Compute(Vector2.Zero, 1f, new CanvasBounds(Vector2.Zero, Vector2.Zero)));
        Assert.Null(PlateViewerLayout.Compute(new Vector2(float.NaN), 1f, Plain));
        Assert.Equal(0f, PlateViewerLayout.ClampScale(1f, new CanvasBounds(Vector2.Zero, Vector2.Zero), Viewport));
    }

    // ---- Hit testing and dragging -----------------------------------------------------------

    private static (PlateViewerPlacement Placement, PlateViewerLayout Layout) Placed(CanvasBounds bounds)
    {
        var placement = new PlateViewerPlacement();
        var layout = placement.Update(bounds, Canvas, ViewportPos, Viewport)!.Value;
        return (placement, layout);
    }

    private static Vector2 Screen(PlateViewerLayout layout, Vector2 canvasPoint) => layout.WindowPos + layout.CanvasOffset + (canvasPoint * layout.Scale);

    [Fact]
    public void Drag_StartsFromThePlate()
    {
        var (placement, layout) = Placed(Overflowing);
        var onPlate = Screen(layout, Canvas / 2f);

        Assert.Equal(PlateViewerRegion.Body, layout.HitTest(onPlate));
        Assert.True(placement.TryBeginDrag(layout.HitTest(onPlate), onPlate));
    }

    [Fact]
    public void Drag_StartsFromOverflowArt_OutsideTheLogicalPlate()
    {
        var (placement, layout) = Placed(Overflowing);
        var onOverflow = Screen(layout, new Vector2(-100, 400));

        Assert.Equal(PlateViewerRegion.Body, layout.HitTest(onOverflow));
        Assert.True(placement.TryBeginDrag(layout.HitTest(onOverflow), onOverflow));
    }

    [Fact]
    public void Close_DoesNotStartADrag()
    {
        var (placement, layout) = Placed(Overflowing);
        var close = layout.WindowPos + layout.CloseOffset + new Vector2(Control / 2f);

        Assert.Equal(PlateViewerRegion.Close, layout.HitTest(close));
        Assert.False(placement.TryBeginDrag(layout.HitTest(close), close));
        Assert.False(placement.IsDragging);
    }

    [Fact]
    public void RightClick_DoesNotStartADrag()
    {
        // Moves start only from a left press (the window calls TryBeginDrag only for the left
        // button); a right press opens the menu and leaves the placement untouched.
        var (placement, layout) = Placed(Overflowing);
        var before = placement.CanvasScreenPos;

        Assert.False(placement.IsDragging);
        placement.DragTo(Screen(layout, Canvas / 2f) + new Vector2(200, 200)); // mouse moves with no drag started
        Assert.Equal(before, placement.CanvasScreenPos);
    }

    [Fact]
    public void OutsideTheComposition_HitsNothing_SoTheGameGetsTheInput()
    {
        var (placement, layout) = Placed(Overflowing);
        var outside = layout.WindowPos + layout.WindowSize + new Vector2(5f);

        Assert.Equal(PlateViewerRegion.None, layout.HitTest(outside));
        Assert.Equal(PlateViewerRegion.None, layout.HitTest(layout.WindowPos - new Vector2(1f)));
        Assert.False(placement.TryBeginDrag(PlateViewerRegion.None, outside));
    }

    [Fact]
    public void Dragging_MovesPlateOverflowAndClose_AsOneUnit()
    {
        var (placement, before) = Placed(Overflowing);
        var start = Screen(before, Canvas / 2f);
        placement.TryBeginDrag(PlateViewerRegion.Body, start);
        placement.DragTo(start + new Vector2(-150, 40));
        placement.DragTo(start + new Vector2(-300, 90));
        placement.EndDrag();

        var after = placement.Update(Overflowing, Canvas, ViewportPos, Viewport)!.Value;
        var delta = new Vector2(-300, 90);

        Assert.Equal(before.WindowPos + delta, after.WindowPos);
        Assert.Equal(before.WindowSize, after.WindowSize);
        Assert.Equal(before.CanvasOffset, after.CanvasOffset);
        Assert.Equal(before.CloseOffset, after.CloseOffset);
        Assert.Equal(Screen(before, new Vector2(-100, 400)) + delta, Screen(after, new Vector2(-100, 400)));
    }

    [Fact]
    public void Dragging_IsFree_ButCloseStaysReachable()
    {
        var (placement, layout) = Placed(Overflowing);
        var start = Screen(layout, Canvas / 2f);

        placement.TryBeginDrag(PlateViewerRegion.Body, start);
        placement.DragTo(start + new Vector2(-1000, 500));
        placement.EndDrag();
        var leftDown = placement.Update(Overflowing, Canvas, ViewportPos, Viewport)!.Value;
        Assert.Equal(layout.WindowPos + new Vector2(-1000, 500), leftDown.WindowPos);
        Assert.True(leftDown.WindowPos.X < 0f);

        placement.TryBeginDrag(PlateViewerRegion.Body, start);
        placement.DragTo(start + new Vector2(99999, -99999));
        placement.EndDrag();
        var lost = placement.Update(Overflowing, Canvas, ViewportPos, Viewport)!.Value;
        var closeMin = lost.WindowPos + lost.CloseOffset;
        var closeMax = closeMin + new Vector2(lost.ControlSize);
        Assert.True(closeMin.X >= ViewportPos.X && closeMin.Y >= ViewportPos.Y && closeMax.X <= Viewport.X + 0.01f && closeMax.Y <= Viewport.Y + 0.01f);
        Assert.True(Math.Abs(closeMax.X - Viewport.X) < 0.01f && Math.Abs(closeMin.Y - ViewportPos.Y) < 0.01f);
    }

    // ---- Resizing -----------------------------------------------------------------------------

    [Fact]
    public void CtrlWheel_Up_Enlarges_Down_Shrinks_InConsistentSteps()
    {
        var (placement, _) = Placed(Plain);
        var start = placement.Scale;

        Assert.True(placement.ZoomByWheel(1f, Plain, Canvas, Viewport));
        Assert.Equal(start * PlateViewerPlacement.WheelStepFactor, placement.Scale, 4);
        Assert.True(placement.ZoomByWheel(1f, Plain, Canvas, Viewport));
        Assert.Equal(start * PlateViewerPlacement.WheelStepFactor * PlateViewerPlacement.WheelStepFactor, placement.Scale, 4);

        Assert.True(placement.ZoomByWheel(-2f, Plain, Canvas, Viewport));
        Assert.Equal(start, placement.Scale, 4);
        Assert.True(placement.ZoomByWheel(-1f, Plain, Canvas, Viewport));
        Assert.Equal(start / PlateViewerPlacement.WheelStepFactor, placement.Scale, 4);
    }

    [Fact]
    public void PlainWheel_DoesNotResize()
    {
        // The window only forwards the wheel with Ctrl held; with no notches nothing changes either.
        var (placement, _) = Placed(Plain);
        var scale = placement.Scale;
        var pos = placement.CanvasScreenPos;

        Assert.False(placement.ZoomByWheel(0f, Plain, Canvas, Viewport));
        Assert.Equal(scale, placement.Scale);
        Assert.Equal(pos, placement.CanvasScreenPos);
    }

    [Fact]
    public void Resize_StopsAtTheMinimum_WhereCloseStillFits()
    {
        var (placement, _) = Placed(Plain);

        placement.ZoomByWheel(-200f, Plain, Canvas, Viewport);

        Assert.Equal(PlateViewerLayout.MinScale(Plain), placement.Scale, 5);
        var layout = placement.Update(Plain, Canvas, ViewportPos, Viewport)!.Value;
        var composition = PlateViewerLayout.CompositionSize(Plain, placement.Scale);
        Assert.True(Math.Min(composition.X, composition.Y) >= Control - 0.01f);

        // Close still fits inside the composition.
        var compositionMin = layout.WindowPos + layout.CanvasOffset;
        var closeMin = layout.WindowPos + layout.CloseOffset;
        Assert.True(closeMin.X >= compositionMin.X - 0.5f && closeMin.Y >= compositionMin.Y - 0.5f);
        Assert.True(closeMin.X + Control <= compositionMin.X + composition.X + 0.5f && closeMin.Y + Control <= compositionMin.Y + composition.Y + 0.5f);
    }

    [Fact]
    public void Resize_StopsAtTheMaximum_WhereTheCompositionFitsTheScreen()
    {
        var (placement, _) = Placed(Overflowing);

        placement.ZoomByWheel(200f, Overflowing, Canvas, Viewport);
        var size = PlateViewerLayout.CompositionSize(Overflowing, placement.Scale);

        Assert.Equal(PlateViewerLayout.MaxScale(Overflowing, Viewport), placement.Scale, 5);
        Assert.True(size.X <= Viewport.X + 0.01f && size.Y <= Viewport.Y + 0.01f);
        Assert.False(placement.ZoomByWheel(1f, Overflowing, Canvas, Viewport)); // already at the limit
    }

    [Fact]
    public void Resize_PreservesAspectRatio_AndNeverChangesThePlate()
    {
        var document = ComponentDocuments.WithAnchors();
        var json = Persistence.PlateDocuments.ToJson(document).ToJsonString();
        var bounds = ProfileVisualBounds.Compute(document);
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);
        var placement = new PlateViewerPlacement();
        var before = placement.Update(bounds, canvas, ViewportPos, Viewport)!.Value;

        placement.ZoomByWheel(3f, bounds, canvas, Viewport);
        placement.SetPercent(75, bounds, canvas, Viewport);
        var after = placement.Update(bounds, canvas, ViewportPos, Viewport)!.Value;

        // The composition (not the window, which adds whole-pixel margins) keeps the Plate's proportions.
        var sizeBefore = PlateViewerLayout.CompositionSize(bounds, before.Scale);
        var sizeAfter = PlateViewerLayout.CompositionSize(bounds, after.Scale);
        Assert.NotEqual(before.Scale, after.Scale);
        Assert.Equal(canvas.X / canvas.Y, sizeBefore.X / sizeBefore.Y, 4);
        Assert.Equal(sizeBefore.X / sizeBefore.Y, sizeAfter.X / sizeAfter.Y, 4);
        Assert.Equal(json, Persistence.PlateDocuments.ToJson(document).ToJsonString());
    }

    [Fact]
    public void Resize_KeepsThePlateCenterInPlace_AndTheOverflowAligned()
    {
        var (placement, before) = Placed(Overflowing);
        var centerBefore = Screen(before, Canvas / 2f);
        var overflowPoint = new Vector2(-100, 400);
        var offsetBefore = (Screen(before, overflowPoint) - centerBefore) / before.Scale;

        placement.ZoomByWheel(2f, Overflowing, Canvas, Viewport);
        var after = placement.Update(Overflowing, Canvas, ViewportPos, Viewport)!.Value;

        Assert.True(Vector2.Distance(centerBefore, Screen(after, Canvas / 2f)) < 0.01f);
        var offsetAfter = (Screen(after, overflowPoint) - Screen(after, Canvas / 2f)) / after.Scale;
        Assert.True(Vector2.Distance(offsetBefore, offsetAfter) < 0.001f); // overflow stays where it belongs relative to the Plate
    }

    [Theory]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    public void Presets_SetThePlateToThatPercentOfItsOwnSize(int percent)
    {
        var (placement, before) = Placed(Plain);
        var center = Screen(before, Canvas / 2f);

        Assert.Contains(percent, PlateViewerPlacement.PresetPercents);
        placement.SetPercent(percent, Plain, Canvas, Viewport);

        Assert.Equal(PlateViewerLayout.ScaleForPercent(percent), placement.Scale, 4);
        Assert.Equal(percent, placement.Percent);
        var after = placement.Update(Plain, Canvas, ViewportPos, Viewport)!.Value;
        Assert.True(Vector2.Distance(center, Screen(after, Canvas / 2f)) < 0.01f);
    }

    [Fact]
    public void Presets_BeyondTheScreen_AreBoundedToTheMaximum()
    {
        var (placement, _) = Placed(Plain);
        var smallScreen = new Vector2(1600, 900);

        placement.SetPercent(200, Plain, Canvas, smallScreen);

        Assert.Equal(PlateViewerLayout.MaxScale(Plain, smallScreen), placement.Scale, 4);
        Assert.Equal([50, 75, 100, 125, 150, 200], PlateViewerPlacement.PresetPercents);
    }

    [Fact]
    public void ResetSize_ReturnsToTheDefaultSize_WithoutRecentering()
    {
        var (placement, first) = Placed(Plain);
        var start = Screen(first, Canvas / 2f);
        placement.TryBeginDrag(PlateViewerRegion.Body, start);
        placement.DragTo(start + new Vector2(-400, 150));
        placement.EndDrag();
        placement.SetPercent(150, Plain, Canvas, Viewport);
        var moved = placement.Update(Plain, Canvas, ViewportPos, Viewport)!.Value;
        var centerBefore = Screen(moved, Canvas / 2f);

        placement.ResetSize(Plain, Canvas, Viewport);
        var reset = placement.Update(Plain, Canvas, ViewportPos, Viewport)!.Value;

        Assert.Equal(PlateViewerLayout.DefaultScale(Plain, Viewport), placement.Scale, 5);
        Assert.True(Vector2.Distance(centerBefore, Screen(reset, Canvas / 2f)) < 0.01f); // not moved back to the center of the screen
    }

    [Fact]
    public void CenterOnScreen_CentersTheWholeComposition_KeepingTheSize()
    {
        var (placement, first) = Placed(Overflowing);
        var start = Screen(first, Canvas / 2f);
        placement.TryBeginDrag(PlateViewerRegion.Body, start);
        placement.DragTo(start + new Vector2(500, -200));
        placement.EndDrag();
        var scale = placement.Scale;

        placement.CenterOnScreen(Overflowing, ViewportPos, Viewport);
        var layout = placement.Update(Overflowing, Canvas, ViewportPos, Viewport)!.Value;
        var compositionCenter = (Screen(layout, Overflowing.Min) + Screen(layout, Overflowing.Max)) / 2f;

        Assert.Equal(scale, placement.Scale);
        Assert.True(Vector2.Distance(Viewport / 2f, compositionCenter) < 0.01f);
    }

    // ---- Session state --------------------------------------------------------------------------

    [Fact]
    public void Position_SurvivesAChangeOfPlate()
    {
        var (placement, first) = Placed(Overflowing);
        var start = Screen(first, Vector2.Zero);
        placement.TryBeginDrag(PlateViewerRegion.Body, start);
        placement.DragTo(start + new Vector2(200, 100));
        placement.EndDrag();
        var anchor = placement.CanvasScreenPos;

        var otherCanvas = new Vector2(1600, 900);
        var layout = placement.Update(new CanvasBounds(Vector2.Zero, otherCanvas), otherCanvas, ViewportPos, Viewport)!.Value;

        Assert.Equal(anchor, placement.CanvasScreenPos);
        Assert.Equal(anchor, layout.WindowPos + layout.CanvasOffset);
    }

    [Fact]
    public void PositionAndSize_SurviveReopening_InTheSession()
    {
        var (placement, _) = Placed(Overflowing);
        placement.TryBeginDrag(PlateViewerRegion.Body, Vector2.Zero);
        placement.DragTo(new Vector2(-250, 130));
        placement.EndDrag();
        placement.ZoomByWheel(-1f, Overflowing, Canvas, Viewport);
        var anchor = placement.CanvasScreenPos;
        var scale = placement.Scale;

        placement.EndDrag(); // the window closing
        placement.Update(Overflowing, Canvas, ViewportPos, Viewport); // and reopening

        Assert.Equal(anchor, placement.CanvasScreenPos);
        Assert.Equal(scale, placement.Scale);
    }

    [Fact]
    public void VisualBoundsChanges_DoNotRecenter_ThePlateStaysPut()
    {
        var (placement, before) = Placed(Plain);
        var plateOnScreen = Screen(before, Vector2.Zero);

        var after = placement.Update(Overflowing, Canvas, ViewportPos, Viewport)!.Value;

        Assert.Equal(plateOnScreen, Screen(after, Vector2.Zero));
        Assert.True(after.WindowPos.X < before.WindowPos.X && after.WindowPos.Y < before.WindowPos.Y);
        Assert.Equal(before.Scale, after.Scale);
    }

    [Fact]
    public void FirstShow_IsCentered_AtTheDefaultSize()
    {
        var (placement, layout) = Placed(Overflowing);
        var compositionCenter = (Screen(layout, Overflowing.Min) + Screen(layout, Overflowing.Max)) / 2f;

        Assert.Equal(PlateViewerLayout.DefaultScale(Overflowing, Viewport), placement.Scale);
        Assert.True(Vector2.Distance(Viewport / 2f, compositionCenter) < 0.01f);
    }

    // ---- Hint -----------------------------------------------------------------------------------

    [Fact]
    public void Hint_ShowsOncePerSession_ThenFadesOut()
    {
        var hint = new PlateViewerHint();
        Assert.Equal(0f, hint.Opacity(0));

        hint.ShowOnce(10.0);
        Assert.Equal(1f, hint.Opacity(10.0));
        Assert.Equal(1f, hint.Opacity(10.0 + PlateViewerHint.Duration - PlateViewerHint.FadeDuration - 0.01));
        Assert.InRange(hint.Opacity(10.0 + PlateViewerHint.Duration - (PlateViewerHint.FadeDuration / 2)), 0.4f, 0.6f);
        Assert.Equal(0f, hint.Opacity(10.0 + PlateViewerHint.Duration));

        hint.ShowOnce(100.0); // reopening later in the session: not shown again
        Assert.Equal(0f, hint.Opacity(100.0));
        Assert.Contains("Drag to move", PlateViewerHint.Text);
        Assert.Contains("Ctrl + Scroll to resize", PlateViewerHint.Text);
        Assert.Contains("Right-click for options", PlateViewerHint.Text);
    }

    [Fact]
    public void Hint_IsDismissedByInteraction()
    {
        var hint = new PlateViewerHint();
        hint.ShowOnce(0);
        hint.Dismiss();

        Assert.Equal(0f, hint.Opacity(1.0));
    }

    // ---- Presentation and Template preview ------------------------------------------------------

    [Fact]
    public void Viewer_UsesTheTransparentPresentation_AndTheSharedCloseLook()
    {
        Assert.True(CleanPreviewPresentation.RenderOptions.HideCanvasBackdrop);
        Assert.Equal(Vector4.Zero, CleanPreviewPresentation.BackgroundColor);
        Assert.False(CleanPreviewPresentation.AllowBackgroundBlur);

        // Close: dark backing, red on hover, a deeper, more opaque red while pressed.
        Assert.True(CleanPreviewPresentation.CloseBackingHovered.X > 0.5f && CleanPreviewPresentation.CloseBackingHovered.Y < 0.3f);
        Assert.True(CleanPreviewPresentation.CloseBackingPressed.X < CleanPreviewPresentation.CloseBackingHovered.X);
        Assert.True(CleanPreviewPresentation.CloseBackingPressed.W >= CleanPreviewPresentation.CloseBackingHovered.W);
    }

    [Fact]
    public async Task TemplatePreview_GetsTheSameLayout_AndIsNeverMutated()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var created = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Overflowing", new PlateStarterContent(null));
        var plate = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
        var ornament = ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);
        ornament.Corners = CornerMask.TopRight;
        ornament.Offset = new Vector2(-120, -40);
        ornament.Scale = 2.5f;
        plate.Components = [ornament];
        await fixture.PlateLibrary.SavePlateDocumentAsync(plate);
        var templateId = await templates.SaveAsTemplateAsync(created.PlateId, "Overflowing");

        var document = templates.GetSavedDocument(templateId)!;
        var json = Persistence.PlateDocuments.ToJson(document).ToJsonString();
        var bounds = ProfileVisualBounds.Compute(document);
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);
        Assert.True(bounds.Max.X > document.CanvasWidth && bounds.Min.Y < 0f);

        var placement = new PlateViewerPlacement();
        var layout = placement.Update(bounds, canvas, ViewportPos, Viewport)!.Value;
        placement.ZoomByWheel(1f, bounds, canvas, Viewport);
        var canvasScreen = layout.WindowPos + layout.CanvasOffset;
        AssertInsideWindow(layout, canvasScreen + (bounds.Min * layout.Scale), canvasScreen + (bounds.Max * layout.Scale));

        Assert.Equal(json, Persistence.PlateDocuments.ToJson(document).ToJsonString());
        Assert.Equal(json, Persistence.PlateDocuments.ToJson(templates.GetSavedDocument(templateId)!).ToJsonString());
    }

    // ---- Title bar order --------------------------------------------------------------------

    [Fact]
    public void TitleBar_IsMenuMinimizeClose_WithTheNativeCloseFarRight()
    {
        var order = TitleBarOrder.LeftToRight(new[]
        {
            ("Minimize", TitleBarOrder.Minimize),
            ("Menu", TitleBarOrder.DalamudMenu),
        }, nativeClose: "Close", showsNativeClose: true);

        Assert.Equal(["Menu", "Minimize", "Close"], order);
    }

    [Fact]
    public void TitleBar_FutureMaximize_GoesBetweenMinimizeAndClose()
    {
        var order = TitleBarOrder.LeftToRight(new[]
        {
            ("Maximize", TitleBarOrder.MaximizeRestore),
            ("Minimize", TitleBarOrder.Minimize),
            ("Menu", TitleBarOrder.DalamudMenu),
        }, nativeClose: "Close", showsNativeClose: true);

        Assert.Equal(["Menu", "Minimize", "Maximize", "Close"], order);
    }

    // ---- Advanced editor close guard ----------------------------------------------------------

    [Fact]
    public void CloseGuard_VetoesOnlyAnUnconfirmedCloseWithUnsavedWork()
    {
        // A dirty close from the title bar, Escape or elsewhere: kept open, the question is asked.
        Assert.True(CloseGuard.ShouldVeto(wasOpen: true, isOpen: false, closeConfirmed: false, hasPlate: true, isDirty: true));

        // Nothing unsaved, already answered (Save/Discard) or handed to the Basic editor, or no Plate: it closes.
        Assert.False(CloseGuard.ShouldVeto(wasOpen: true, isOpen: false, closeConfirmed: false, hasPlate: true, isDirty: false));
        Assert.False(CloseGuard.ShouldVeto(wasOpen: true, isOpen: false, closeConfirmed: true, hasPlate: true, isDirty: true));
        Assert.False(CloseGuard.ShouldVeto(wasOpen: true, isOpen: false, closeConfirmed: false, hasPlate: false, isDirty: true));

        // Not a close at all: still open, or already closed on an earlier frame.
        Assert.False(CloseGuard.ShouldVeto(wasOpen: true, isOpen: true, closeConfirmed: false, hasPlate: true, isDirty: true));
        Assert.False(CloseGuard.ShouldVeto(wasOpen: false, isOpen: false, closeConfirmed: false, hasPlate: true, isDirty: true));
    }

    [Fact]
    public void TitleBar_Priorities_NeverOverflowDalamudsSubtractingComparison()
    {
        int[] priorities = [TitleBarOrder.DalamudMenu, TitleBarOrder.Minimize, TitleBarOrder.MaximizeRestore];
        foreach (var a in priorities)
        {
            foreach (var b in priorities)
            {
                Assert.Equal(Math.Sign(b.CompareTo(a)), Math.Sign(TitleBarOrder.DalamudCompare(a, b)));
            }
        }
    }

    [Fact]
    public void TitleBar_ThePreviousPriorities_ReproduceTheReportedWrongOrder()
    {
        Assert.NotEqual(Math.Sign(int.MaxValue.CompareTo(TitleBarOrder.DalamudMenu)), Math.Sign(TitleBarOrder.DalamudCompare(TitleBarOrder.DalamudMenu, int.MaxValue)));
        Assert.NotEqual(Math.Sign(0.CompareTo(TitleBarOrder.DalamudMenu)), Math.Sign(TitleBarOrder.DalamudCompare(TitleBarOrder.DalamudMenu, 0)));

        var old = TitleBarOrder.LeftToRight(new[] { ("Minimize", 0), ("Close", int.MaxValue), ("Menu", TitleBarOrder.DalamudMenu) });
        Assert.Equal(["Minimize", "Close", "Menu"], old);
    }

    private static void AssertInsideWindow(PlateViewerLayout layout, Vector2 min, Vector2 max)
    {
        const float epsilon = 0.01f;
        var windowMax = layout.WindowPos + layout.WindowSize;
        Assert.True(min.X >= layout.WindowPos.X - epsilon && min.Y >= layout.WindowPos.Y - epsilon, $"{min} outside window at {layout.WindowPos}");
        Assert.True(max.X <= windowMax.X + epsilon && max.Y <= windowMax.Y + epsilon, $"{max} outside window ending {windowMax}");
    }
}
