using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>An axis-aligned rectangle in logical canvas coordinates.</summary>
internal readonly record struct CanvasBounds(Vector2 Min, Vector2 Max)
{
    internal Vector2 Size => Max - Min;

    internal Vector2 Center => (Min + Max) / 2f;

    internal CanvasBounds Union(Vector2 min, Vector2 max) => new(Vector2.Min(Min, min), Vector2.Max(Max, max));
}

/// <summary>
/// What a presented Plate occupies. <see cref="Logical"/> is the Plate's own canvas (its saved
/// size — what elements, backgrounds, snapping and editing work in). <see cref="Compute(ProfileDocument, IReadOnlyList{PaintStep})"/>
/// is that canvas united with everything its Components paint outside it: Components are never
/// clipped to the Plate, so a scaled or offset ornament may intentionally overflow, and every
/// surface that fits a Plate into a viewport fits these bounds so the overflow stays visible
/// (see <see cref="PlateViewFit"/>). Pure logic, no Dalamud; nothing here changes the Plate.
///
/// <para>Component bounds come only from <see cref="ComponentPaintPlan.GetVisualBounds"/> over the
/// same paint plan the renderer draws, so hidden, unresolvable (missing definition, unknown kind,
/// kind mismatch, image not set) Components and unselected corners contribute nothing, while
/// scale, offset, rotation and art sizing are all included.</para>
/// </summary>
internal static class ProfileVisualBounds
{
    // Per thread (the render thread in the plugin; tests run in parallel), so the per-frame
    // convenience overload allocates nothing of its own.
    [ThreadStatic]
    private static List<ProfileElement>? paintOrderBuffer;

    [ThreadStatic]
    private static List<ProfileElement>? drawnBuffer;

    [ThreadStatic]
    private static List<PaintStep>? planBuffer;

    /// <summary>The Plate's own canvas: (0, 0) to its saved size.</summary>
    internal static CanvasBounds Logical(ProfileDocument profile) =>
        new(Vector2.Zero, new Vector2(Math.Max(0f, profile.CanvasWidth), Math.Max(0f, profile.CanvasHeight)));

    /// <summary><see cref="Logical"/> united with the painted bounds of every Component in <paramref name="plan"/> (from <see cref="ComponentPaintPlan.Build"/>).</summary>
    internal static CanvasBounds Compute(ProfileDocument profile, IReadOnlyList<PaintStep> plan)
    {
        var bounds = Logical(profile);
        if (profile.Components is not { Count: > 0 } components)
        {
            return bounds;
        }

        foreach (var component in components)
        {
            if (component is not null
                && ComponentPaintPlan.GetVisualBounds(plan, component) is var (min, max)
                && IsFinite(min) && IsFinite(max))
            {
                bounds = bounds.Union(min, max);
            }
        }

        return bounds;
    }

    /// <summary>Builds the paint plan exactly as <c>ProfileRenderer</c> does for <paramref name="options"/>, then <see cref="Compute(ProfileDocument, IReadOnlyList{PaintStep})"/>.</summary>
    internal static CanvasBounds Compute(ProfileDocument profile, in ProfileRenderOptions options)
    {
        if (profile.Components is not { Count: > 0 })
        {
            return Logical(profile);
        }

        var paintOrder = paintOrderBuffer ??= new List<ProfileElement>(ProfileDocument.MaxElementCount);
        var drawn = drawnBuffer ??= new List<ProfileElement>(ProfileDocument.MaxElementCount);
        var plan = planBuffer ??= new List<PaintStep>(ProfileDocument.MaxElementCount + 64);
        try
        {
            FillDrawnElements(profile, options, paintOrder, drawn);
            ComponentPaintPlan.Build(profile, drawn, BuiltInComponentCatalog.Instance, plan);
            return Compute(profile, plan);
        }
        finally
        {
            paintOrder.Clear();
            drawn.Clear();
            plan.Clear();
        }
    }

    /// <summary>The finished-rendering bounds (what every preview surface shows).</summary>
    internal static CanvasBounds Compute(ProfileDocument profile) => Compute(profile, ProfileRenderOptions.Finished);

    /// <summary>
    /// The elements the renderer paints, in paint order: visible elements, minus section headings
    /// with nothing under them unless <see cref="ProfileRenderOptions.ShowEmptySectionHeadings"/>.
    /// Shared with <c>ProfileRenderer</c> so bounds can never disagree with what is drawn.
    /// </summary>
    internal static void FillDrawnElements(ProfileDocument profile, in ProfileRenderOptions options, List<ProfileElement> paintOrder, List<ProfileElement> drawn)
    {
        ProfilePaintOrder.Fill(profile, paintOrder, includeHidden: false);
        drawn.Clear();
        foreach (var element in paintOrder)
        {
            if (options.ShowEmptySectionHeadings || BasicSections.IsDrawnInFinishedRendering(profile, element))
            {
                drawn.Add(element);
            }
        }
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}

/// <summary>
/// Fitting a Plate's visual bounds into a viewport: one uniform scale (never stretched), the bounds
/// centered, and the logical canvas placed inside them at its true position. With no overflow this
/// is exactly the classic fit of the canvas itself.
/// </summary>
/// <param name="Scale">Screen pixels per logical canvas unit.</param>
/// <param name="CanvasOffset">Where the canvas's (0, 0) lands, relative to the viewport's top-left.</param>
/// <param name="Size">Screen size of the fitted visual bounds.</param>
internal readonly record struct PlateViewFit(float Scale, Vector2 CanvasOffset, Vector2 Size)
{
    internal static readonly PlateViewFit None = new(0f, Vector2.Zero, Vector2.Zero);

    /// <summary>
    /// Fits <paramref name="bounds"/> into <paramref name="available"/>, times <paramref name="zoom"/>
    /// (1 = fit). When the zoomed bounds exceed the viewport they start at its top-left (the scroll
    /// origin) instead of being centered. <see cref="None"/> when there is nothing to draw.
    /// </summary>
    internal static PlateViewFit Fit(Vector2 available, CanvasBounds bounds, float zoom = 1f)
    {
        var size = bounds.Size;
        if (!(available.X >= 1f) || !(available.Y >= 1f) || !(size.X > 0f) || !(size.Y > 0f) || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || !(zoom > 0f))
        {
            return None;
        }

        var scale = Math.Min(available.X / size.X, available.Y / size.Y) * zoom;
        var screenSize = size * scale;
        var boundsOffset = Vector2.Max(Vector2.Zero, (available - screenSize) / 2f);
        return new PlateViewFit(scale, boundsOffset - (bounds.Min * scale), screenSize);
    }
}
