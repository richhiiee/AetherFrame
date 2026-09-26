using System;
using System.Numerics;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The Advanced Editor's three-panel body — Layers, canvas, Inspector — sized for Dalamud's global
/// UI scale. Every width here is in unscaled pixels and multiplied by the scale, exactly as Dalamud
/// scales the window's own size constraints and ImGui's style, so the panels keep their proportions
/// to the (scaled) text inside them at any scale.
/// </summary>
internal readonly record struct AdvancedEditorLayout(float LayersWidth, float CanvasWidth, float InspectorWidth, float BodyHeight)
{
    internal const float LayersPanelWidth = 236f;
    internal const float InspectorPanelWidth = 344f;
    internal const float MinCanvasWidth = 360f;
    internal const float MinBodyHeight = 200f;

    /// <summary>The window's minimum size (Dalamud scales it): always room for both side panels
    /// and the smallest canvas, with the default style's spacing and padding.</summary>
    internal static readonly Vector2 MinimumWindowSize = new(980f, 560f);

    /// <summary>The window's size the first time it opens (see <see cref="FirstUseWindowSize"/>).</summary>
    internal static readonly Vector2 FirstUseSize = new(1280f, 760f);

    /// <param name="contentAvailable">The window's content region left below the toolbar.</param>
    /// <param name="statusBarHeight">The status bar's height (already at the current scale).</param>
    /// <param name="itemSpacing">The style's item spacing (already at the current scale).</param>
    /// <param name="scale">Dalamud's global UI scale.</param>
    internal static AdvancedEditorLayout Compute(Vector2 contentAvailable, float statusBarHeight, Vector2 itemSpacing, float scale)
    {
        scale = scale > 0f ? scale : 1f;
        var layers = LayersPanelWidth * scale;
        var inspector = InspectorPanelWidth * scale;
        var canvas = Math.Max(MinCanvasWidth * scale, contentAvailable.X - layers - inspector - (itemSpacing.X * 2f));
        var bodyHeight = Math.Max(MinBodyHeight * scale, contentAvailable.Y - statusBarHeight - itemSpacing.Y);
        return new AdvancedEditorLayout(layers, canvas, inspector, bodyHeight);
    }
}
