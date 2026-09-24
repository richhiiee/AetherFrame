using System;
using System.Numerics;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// The Plate Viewer's transparent presentation: its window is exactly the Plate's composition —
/// the fitted visual bounds (canvas plus any intentional Component overflow, see
/// <see cref="ProfileVisualBounds"/>) — so only the Plate, its artwork and one small close control
/// show over the game, and only they take mouse input. Where the composition sits and how large it
/// is drawn (a uniform scale: never stretched) is <see cref="PlateViewerPlacement"/>.
///
/// <para>The close control sits just inside the composition's top-right corner. The scale is
/// bounded below so it always fits inside the composition, and above so the composition fits the
/// screen.</para>
///
/// <para>Pure logic, no Dalamud: nothing here changes the Plate.</para>
/// </summary>
/// <param name="WindowPos">Screen position of the viewer window.</param>
/// <param name="WindowSize">Screen size of the viewer window.</param>
/// <param name="CanvasOffset">The Plate canvas's (0, 0) relative to the window's top-left.</param>
/// <param name="Scale">Screen pixels per logical canvas unit.</param>
/// <param name="CloseOffset">Top-left of the close control, relative to the window.</param>
/// <param name="ControlSize">Edge of the (square) close control, in screen pixels.</param>
internal readonly record struct PlateViewerLayout(
    Vector2 WindowPos, Vector2 WindowSize, Vector2 CanvasOffset, float Scale, Vector2 CloseOffset, float ControlSize)
{
    /// <summary>Default control edge, before UI scaling.</summary>
    internal const float DefaultControlSize = 26f;

    /// <summary>
    /// The viewer's "100%": screen pixels per logical canvas unit at the natural default size. The
    /// Plate is drawn at three quarters of its own logical pixel size (a 1280x720 Plate shows as
    /// 960x540) — what was labelled 75% before the scale was rebased. Every percentage the viewer
    /// shows or accepts is relative to this, never to the Plate's own size.
    /// </summary>
    internal const float BaseScale = 0.75f;

    /// <summary>Transparent margin around the composition so whole-pixel rounding never clips an edge texel.</summary>
    internal const float SafeMargin = 1f;

    /// <summary>Gap between the close control and the composition's edges, as a fraction of its size.</summary>
    internal const float ControlInsetFraction = 0.3f;

    /// <summary>The composition's screen size at <paramref name="scale"/>.</summary>
    internal static Vector2 CompositionSize(CanvasBounds bounds, float scale) => bounds.Size * scale;

    /// <summary>
    /// The scale for a freshly opened (or reset) viewer: 100%, i.e. <see cref="BaseScale"/>, bounded
    /// (see <see cref="ClampScale"/>) — smaller only when the composition wouldn't fit the screen.
    /// </summary>
    internal static float DefaultScale(CanvasBounds bounds, Vector2 viewportSize, float controlSize = DefaultControlSize) =>
        ClampScale(BaseScale, bounds, viewportSize, controlSize);

    /// <summary>A viewer percentage (100 = <see cref="BaseScale"/>) as a scale.</summary>
    internal static float ScaleForPercent(float percent) => percent / 100f * BaseScale;

    /// <summary>A scale as a viewer percentage (100 = <see cref="BaseScale"/>).</summary>
    internal static float PercentForScale(float scale) => scale / BaseScale * 100f;

    /// <summary>The composition's top-left for <paramref name="scale"/>, centered on the screen.</summary>
    internal static Vector2 CenteredPosition(CanvasBounds bounds, float scale, Vector2 viewportPos, Vector2 viewportSize) =>
        viewportPos + ((viewportSize - CompositionSize(bounds, scale)) / 2f);

    /// <summary>The smallest scale: the close control (with its inset on both sides) fits inside the composition.</summary>
    internal static float MinScale(CanvasBounds bounds, float controlSize = DefaultControlSize)
    {
        var size = bounds.Size;
        if (!(size.X > 0f) || !(size.Y > 0f) || !float.IsFinite(size.X) || !float.IsFinite(size.Y))
        {
            return 0f;
        }

        var edge = controlSize + (2f * Inset(controlSize));
        return Math.Max(edge / size.X, edge / size.Y);
    }

    /// <summary>The largest scale: the whole composition fits the screen.</summary>
    internal static float MaxScale(CanvasBounds bounds, Vector2 viewportSize)
    {
        var size = bounds.Size;
        if (!(size.X > 0f) || !(size.Y > 0f) || !float.IsFinite(size.X) || !float.IsFinite(size.Y))
        {
            return 0f;
        }

        return viewportSize.X > 0f && viewportSize.Y > 0f ? Math.Min(viewportSize.X / size.X, viewportSize.Y / size.Y) : float.MaxValue;
    }

    /// <summary>
    /// Bounds a scale between <see cref="MinScale"/> and <see cref="MaxScale"/> (the screen wins if
    /// a screen is too small for both). 0 for bounds with no area.
    /// </summary>
    internal static float ClampScale(float scale, CanvasBounds bounds, Vector2 viewportSize, float controlSize = DefaultControlSize)
    {
        var min = MinScale(bounds, controlSize);
        var max = MaxScale(bounds, viewportSize);
        if (!(min > 0f) || !(max > 0f))
        {
            return 0f;
        }

        if (!float.IsFinite(scale) || scale <= 0f)
        {
            scale = min;
        }

        return min <= max ? Math.Clamp(scale, min, max) : max;
    }

    /// <summary>The window and close control for a composition at <paramref name="compositionPos"/> drawn at <paramref name="scale"/>, or null when there's nothing to draw.</summary>
    internal static PlateViewerLayout? Compute(Vector2 compositionPos, float scale, CanvasBounds bounds, float controlSize = DefaultControlSize)
    {
        var size = CompositionSize(bounds, scale);
        if (!(scale > 0f) || !(size.X > 0f) || !(size.Y > 0f) || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(compositionPos.X) || !float.IsFinite(compositionPos.Y))
        {
            return null;
        }

        var control = float.IsFinite(controlSize) ? Math.Max(0f, controlSize) : DefaultControlSize;
        var inset = Inset(control);
        var compMin = compositionPos;
        var compMax = compositionPos + size;
        var close = Round(new Vector2(compMax.X - inset - control, compMin.Y + inset));

        // The composition plus its margin, grown only if the close control reaches past it (a
        // composition below the minimum scale, which ClampScale normally prevents).
        var windowMin = Vector2.Min(new Vector2(MathF.Floor(compMin.X), MathF.Floor(compMin.Y)) - new Vector2(SafeMargin), close);
        var windowMax = Vector2.Max(new Vector2(MathF.Ceiling(compMax.X), MathF.Ceiling(compMax.Y)) + new Vector2(SafeMargin), close + new Vector2(control));

        return new PlateViewerLayout(windowMin, windowMax - windowMin, compositionPos - (bounds.Min * scale) - windowMin, scale, close - windowMin, control);
    }

    /// <summary>
    /// What a screen point hits, with the same priority the window gives its items: the close
    /// control first, then the composition body (the Plate, its overflow, or the space between them —
    /// anywhere in the window), else nothing (the game gets the input).
    /// </summary>
    internal PlateViewerRegion HitTest(Vector2 screenPoint)
    {
        var close = WindowPos + CloseOffset;
        if (screenPoint.X >= close.X && screenPoint.Y >= close.Y && screenPoint.X < close.X + ControlSize && screenPoint.Y < close.Y + ControlSize)
        {
            return PlateViewerRegion.Close;
        }

        var max = WindowPos + WindowSize;
        return screenPoint.X >= WindowPos.X && screenPoint.Y >= WindowPos.Y && screenPoint.X < max.X && screenPoint.Y < max.Y
            ? PlateViewerRegion.Body
            : PlateViewerRegion.None;
    }

    private static float Inset(float controlSize) => MathF.Round(controlSize * ControlInsetFraction);

    private static Vector2 Round(Vector2 value) => new(MathF.Round(value.X), MathF.Round(value.Y));
}

/// <summary>What a point in the Plate Viewer hits (see <see cref="PlateViewerLayout.HitTest"/>).</summary>
internal enum PlateViewerRegion
{
    /// <summary>Outside the viewer: the game gets the input.</summary>
    None,

    /// <summary>The composition (Plate, overflow, or the space between): a left drag moves the viewer.</summary>
    Body,

    Close,
}

/// <summary>
/// Where the Plate Viewer shows its Plate and how large: UI state only, never part of a Plate.
/// Anchored on the Plate canvas's own top-left on screen (not the visual bounds'), so when a
/// Plate's overflow or Components change the Plate stays exactly where the player put it and the
/// overflow grows around it. Kept by the viewer for the whole session: reopening the viewer or
/// showing another Plate keeps the position and size; only the very first show is placed by
/// default (centered, at 100%).
///
/// <para><b>Size.</b> A uniform scale in screen pixels per canvas unit, shown to the player as a
/// percentage of <see cref="PlateViewerLayout.BaseScale"/> (100% = the natural default size; 200% =
/// twice that). Every size change — Ctrl + wheel steps, presets, Reset Size — scales
/// around the Plate's center, which stays where it is on screen, and is bounded by
/// <see cref="PlateViewerLayout.ClampScale"/>.</para>
///
/// <para><b>Movement</b> is free; the only correction keeps the close control fully on screen, so
/// the viewer can always be closed and dragged back (the artwork itself may go off screen).</para>
/// </summary>
internal sealed class PlateViewerPlacement
{
    /// <summary>Size change per Ctrl + wheel notch (multiplicative, so every step feels the same at any size).</summary>
    internal const float WheelStepFactor = 1.1f;

    /// <summary>The size presets offered in the context menu, as viewer percentages (100 = <see cref="PlateViewerLayout.BaseScale"/>).</summary>
    internal static readonly int[] PresetPercents = [50, 75, 100, 125, 150, 200];

    private bool dragging;
    private Vector2 dragLastMouse;

    /// <summary>Whether a position has been chosen yet (by default placement or by the player).</summary>
    internal bool IsPlaced { get; private set; }

    /// <summary>Screen position of the Plate canvas's (0, 0).</summary>
    internal Vector2 CanvasScreenPos { get; private set; }

    /// <summary>Screen pixels per logical canvas unit.</summary>
    internal float Scale { get; private set; }

    internal bool IsDragging => dragging;

    /// <summary>The current size as a whole viewer percentage (100 = the natural default size).</summary>
    internal int Percent => (int)MathF.Round(PlateViewerLayout.PercentForScale(Scale));

    /// <summary>
    /// This frame's layout for <paramref name="bounds"/>: places the viewer by default the first
    /// time, bounds the scale (around the Plate's center if that changes it), keeps the close control
    /// on screen, then lays it out. Null when there's nothing to draw.
    /// </summary>
    internal PlateViewerLayout? Update(CanvasBounds bounds, Vector2 canvasSize, Vector2 viewportPos, Vector2 viewportSize, float controlSize = PlateViewerLayout.DefaultControlSize)
    {
        if (!IsPlaced)
        {
            var defaultScale = PlateViewerLayout.DefaultScale(bounds, viewportSize, controlSize);
            if (!(defaultScale > 0f))
            {
                return null;
            }

            Scale = defaultScale;
            CanvasScreenPos = PlateViewerLayout.CenteredPosition(bounds, Scale, viewportPos, viewportSize) - (bounds.Min * Scale);
            IsPlaced = true;
        }

        var scale = PlateViewerLayout.ClampScale(Scale, bounds, viewportSize, controlSize);
        if (!(scale > 0f))
        {
            return null;
        }

        if (scale != Scale)
        {
            ScaleAroundPlateCenter(scale, canvasSize);
        }

        if (PlateViewerLayout.Compute(CanvasScreenPos + (bounds.Min * Scale), Scale, bounds, controlSize) is not { } layout)
        {
            return null;
        }

        // Recovery: shift only as far as needed to bring the close control fully on screen.
        var closeMin = layout.WindowPos + layout.CloseOffset;
        var closeMax = closeMin + new Vector2(layout.ControlSize);
        var screenMax = viewportPos + viewportSize;
        var shift = new Vector2(
            closeMin.X < viewportPos.X ? viewportPos.X - closeMin.X : closeMax.X > screenMax.X ? screenMax.X - closeMax.X : 0f,
            closeMin.Y < viewportPos.Y ? viewportPos.Y - closeMin.Y : closeMax.Y > screenMax.Y ? screenMax.Y - closeMax.Y : 0f);
        if (shift == Vector2.Zero)
        {
            return layout;
        }

        CanvasScreenPos += shift;
        return PlateViewerLayout.Compute(CanvasScreenPos + (bounds.Min * Scale), Scale, bounds, controlSize);
    }

    /// <summary>Starts a move when a left press lands on the body; the close control never starts one.</summary>
    internal bool TryBeginDrag(PlateViewerRegion pressed, Vector2 mouse)
    {
        if (pressed != PlateViewerRegion.Body)
        {
            return false;
        }

        dragging = true;
        dragLastMouse = mouse;
        return true;
    }

    /// <summary>While moving: the whole composition (Plate, overflow, close control) follows the mouse.</summary>
    internal void DragTo(Vector2 mouse)
    {
        if (!dragging)
        {
            return;
        }

        CanvasScreenPos += mouse - dragLastMouse;
        dragLastMouse = mouse;
    }

    internal void EndDrag() => dragging = false;

    /// <summary>
    /// Ctrl + wheel: <paramref name="wheelNotches"/> steps of <see cref="WheelStepFactor"/> (positive
    /// = larger), around the Plate's center. Nothing for a plain wheel — the caller only calls this
    /// with Ctrl held. Returns whether the size changed.
    /// </summary>
    internal bool ZoomByWheel(float wheelNotches, CanvasBounds bounds, Vector2 canvasSize, Vector2 viewportSize, float controlSize = PlateViewerLayout.DefaultControlSize)
    {
        if (wheelNotches == 0f || !float.IsFinite(wheelNotches) || !IsPlaced)
        {
            return false;
        }

        return SetScale(Scale * MathF.Pow(WheelStepFactor, wheelNotches), bounds, canvasSize, viewportSize, controlSize);
    }

    /// <summary>A size preset: <paramref name="percent"/> of the natural default size (bounded), around the Plate's center.</summary>
    internal bool SetPercent(int percent, CanvasBounds bounds, Vector2 canvasSize, Vector2 viewportSize, float controlSize = PlateViewerLayout.DefaultControlSize) =>
        percent > 0 && SetScale(PlateViewerLayout.ScaleForPercent(percent), bounds, canvasSize, viewportSize, controlSize);

    /// <summary>Back to the default size (as on first open), around the Plate's center; the position is kept.</summary>
    internal bool ResetSize(CanvasBounds bounds, Vector2 canvasSize, Vector2 viewportSize, float controlSize = PlateViewerLayout.DefaultControlSize) =>
        SetScale(PlateViewerLayout.DefaultScale(bounds, viewportSize, controlSize), bounds, canvasSize, viewportSize, controlSize);

    /// <summary>Centers the composition (Plate plus overflow) on the screen, at its current size.</summary>
    internal void CenterOnScreen(CanvasBounds bounds, Vector2 viewportPos, Vector2 viewportSize)
    {
        if (IsPlaced && Scale > 0f)
        {
            CanvasScreenPos = PlateViewerLayout.CenteredPosition(bounds, Scale, viewportPos, viewportSize) - (bounds.Min * Scale);
        }
    }

    private bool SetScale(float requested, CanvasBounds bounds, Vector2 canvasSize, Vector2 viewportSize, float controlSize)
    {
        var scale = PlateViewerLayout.ClampScale(requested, bounds, viewportSize, controlSize);
        if (!(scale > 0f) || scale == Scale)
        {
            return false;
        }

        ScaleAroundPlateCenter(scale, canvasSize);
        return true;
    }

    // The logical Plate's center stays at the same screen point; the overflow scales with it.
    private void ScaleAroundPlateCenter(float scale, Vector2 canvasSize)
    {
        var center = CanvasScreenPos + (canvasSize * Scale / 2f);
        Scale = scale;
        CanvasScreenPos = center - (canvasSize * Scale / 2f);
    }
}

/// <summary>
/// The Plate Viewer's short usage hint ("Drag to move | Ctrl + Scroll to resize | Right-click for
/// options"): shown once per session, when the viewer first shows a Plate, for a few seconds,
/// fading out at the end; any interaction with the viewer dismisses it early. Never permanent
/// chrome over the Plate.
/// </summary>
internal sealed class PlateViewerHint
{
    internal const string Text = "Drag to move   |   Ctrl + Scroll to resize   |   Right-click for options";

    /// <summary>Seconds shown in total, including the fade.</summary>
    internal const double Duration = 6.0;

    /// <summary>Seconds of fading out at the end.</summary>
    internal const double FadeDuration = 1.0;

    private double? shownAt;
    private bool dismissed;

    /// <summary>Starts the hint the first time it's asked to (later calls do nothing: once per session).</summary>
    internal void ShowOnce(double now) => shownAt ??= now;

    /// <summary>Hides it now (the player has already started using the viewer).</summary>
    internal void Dismiss() => dismissed = true;

    /// <summary>Its opacity at <paramref name="now"/>: 1 while shown, fading to 0 over the last second; 0 before, after, or once dismissed.</summary>
    internal float Opacity(double now)
    {
        if (dismissed || shownAt is not { } start || now < start)
        {
            return 0f;
        }

        var remaining = Duration - (now - start);
        return remaining <= 0 ? 0f : (float)Math.Min(1.0, remaining / FadeDuration);
    }
}
