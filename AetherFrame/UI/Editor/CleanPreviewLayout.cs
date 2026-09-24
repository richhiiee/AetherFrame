using System;
using System.Numerics;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Where Clean Preview puts its window and its close control: exactly around the Plate's fitted
/// visual bounds (canvas plus any intentional Component overflow — see <see cref="ProfileVisualBounds"/>),
/// inside the rectangle the Advanced editor occupied when the preview started. With
/// <see cref="CleanPreviewPresentation"/> the window draws nothing of its own, so only the Plate,
/// its artwork and the close control show over the game; and because the window is no larger than
/// that, it takes mouse input only there.
///
/// <para>The window can't be made fully click-through instead: Escape leaves Clean Preview only
/// while the editor window has focus (see <c>KeyboardShortcutService</c>), and the close control
/// needs clicks. So the interactive region is the visual-bounds rectangle (plus the close control
/// when a tiny Plate can't hold it).</para>
/// </summary>
/// <param name="WindowPos">Screen position of the preview window.</param>
/// <param name="WindowSize">Screen size of the preview window.</param>
/// <param name="CanvasOffset">The Plate canvas's (0, 0) relative to the window's top-left.</param>
/// <param name="Scale">Screen pixels per logical canvas unit.</param>
/// <param name="CloseButtonOffset">The close control's top-left, relative to the window's top-left.</param>
/// <param name="CloseButtonSize">The close control's (square) edge, in screen pixels.</param>
internal readonly record struct CleanPreviewLayout(Vector2 WindowPos, Vector2 WindowSize, Vector2 CanvasOffset, float Scale, Vector2 CloseButtonOffset, float CloseButtonSize)
{
    /// <summary>Transparent margin around the bounds so whole-pixel window rounding never clips an edge texel.</summary>
    internal const float SafeMargin = 1f;

    /// <summary>Smallest window edge (keeps a degenerate Plate from producing an unusable window).</summary>
    internal const float MinWindowEdge = 8f;

    /// <summary>Default close control edge, before UI scaling.</summary>
    internal const float DefaultCloseButtonSize = 26f;

    /// <summary>Gap between the close control and the visual bounds' top-right corner, as a fraction of its size.</summary>
    internal const float CloseButtonInsetFraction = 0.3f;

    /// <summary>
    /// The layout for a Plate with <paramref name="visualBounds"/> presented within the host
    /// rectangle (<paramref name="hostPos"/>, <paramref name="hostSize"/>), or null when there's
    /// nothing to present. The bounds are fitted to the host exactly as every other preview fits
    /// them (<see cref="PlateViewFit"/>) and stay at the same place within it. The close control
    /// sits just inside the bounds' top-right corner; only when the fitted bounds are too small to
    /// hold it does it (and so the window) extend past them, by at most its own size.
    /// </summary>
    internal static CleanPreviewLayout? Compute(Vector2 hostPos, Vector2 hostSize, CanvasBounds visualBounds, float closeButtonSize = DefaultCloseButtonSize)
    {
        var fit = PlateViewFit.Fit(hostSize, visualBounds);
        if (fit.Scale <= 0f)
        {
            return null;
        }

        var buttonSize = float.IsFinite(closeButtonSize) ? Math.Max(0f, closeButtonSize) : DefaultCloseButtonSize;
        var inset = MathF.Round(buttonSize * CloseButtonInsetFraction);

        var boundsMin = hostPos + fit.CanvasOffset + (visualBounds.Min * fit.Scale);
        var boundsMax = boundsMin + fit.Size;
        var buttonMin = new Vector2(MathF.Round(boundsMax.X - inset - buttonSize), MathF.Round(boundsMin.Y + inset));

        var windowMin = Vector2.Min(new Vector2(MathF.Floor(boundsMin.X), MathF.Floor(boundsMin.Y)) - new Vector2(SafeMargin), buttonMin);
        var windowMax = Vector2.Max(new Vector2(MathF.Ceiling(boundsMax.X), MathF.Ceiling(boundsMax.Y)) + new Vector2(SafeMargin), buttonMin + new Vector2(buttonSize));
        var windowSize = Vector2.Max(windowMax - windowMin, new Vector2(MinWindowEdge));

        return new CleanPreviewLayout(windowMin, windowSize, hostPos + fit.CanvasOffset - windowMin, fit.Scale, buttonMin - windowMin, buttonSize);
    }
}

/// <summary>
/// Everything Clean Preview's window asks for so that nothing but the Plate, its artwork and the
/// close control is drawn (applied by the Advanced editor window while the preview is up, and
/// undone when it ends). The Plate Viewer uses the same transparent presentation and control
/// colors. Kept here, free of ImGui types, so the choices are testable.
/// </summary>
internal static class CleanPreviewPresentation
{
    /// <summary>The Plate as authored, without the renderer's opaque workspace backdrop under it.</summary>
    internal static readonly ProfileRenderOptions RenderOptions = ProfileRenderOptions.Finished with { HideCanvasBackdrop = true };

    /// <summary>No window padding, so the window is exactly the layout's rectangle.</summary>
    internal static readonly Vector2 WindowPadding = Vector2.Zero;

    /// <summary>No window border.</summary>
    internal const float WindowBorderSize = 0f;

    /// <summary>Fully transparent: the root and any child background colors while the preview is up
    /// (on top of the window's NoBackground flag, so no theme or Dalamud style can paint one).</summary>
    internal static readonly Vector4 BackgroundColor = Vector4.Zero;

    /// <summary>Dalamud's optional background blur stays off: it would frost the window's whole rectangle.</summary>
    internal const bool AllowBackgroundBlur = false;

    /// <summary>No title bar, so none of the editor's title-bar buttons.</summary>
    internal const bool ShowTitleBar = false;

    // The close control: a translucent dark disc with a light ring and a light X, readable on bright
    // and dark Plates alike; its backing covers the control only.
    internal static readonly Vector4 CloseBacking = new(0.05f, 0.05f, 0.07f, 0.62f);
    internal static readonly Vector4 CloseBackingHovered = new(0.70f, 0.14f, 0.14f, 0.85f);

    /// <summary>Close while pressed: a deeper red than hover.</summary>
    internal static readonly Vector4 CloseBackingPressed = new(0.52f, 0.08f, 0.08f, 0.95f);

    // The Plate Viewer's short usage hint pill (see PlateViewerHint).
    internal static readonly Vector4 HintBacking = new(0.05f, 0.05f, 0.07f, 0.72f);
    internal static readonly Vector4 HintText = new(1f, 1f, 1f, 0.95f);
    internal static readonly Vector4 CloseRing = new(1f, 1f, 1f, 0.85f);
    internal static readonly Vector4 CloseShadow = new(0f, 0f, 0f, 0.45f);
    internal static readonly Vector4 CloseGlyph = new(1f, 1f, 1f, 0.95f);
}
