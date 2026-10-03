using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>What is under a point on the canvas: an element, a Component, or nothing.</summary>
internal readonly record struct CanvasHit(ProfileElement? Element, PlateComponent? Component)
{
    public bool IsEmpty => Element is null && Component is null;
}

/// <summary>
/// Hit testing over a Plate's whole paint sequence (<see cref="ComponentPaintPlan"/>), topmost
/// first, so a Component is found where it is drawn on top and an element above it still wins
/// (issue #115). Each Component is tested by its own drawn placements (a Corner Ornament by each
/// corner it is drawn in), with its rotation, and never by a union box, so a click between two
/// ornaments hits what is behind them.
///
/// Components are tested by the shape they draw, as far as a placement can say:
/// <list type="bullet">
/// <item>a Background covers the whole Plate under everything, and a Portrait Overlay the whole
/// portrait, so neither takes clicks: everything else on the Plate would become unreachable. Both
/// stay selectable from the editors' lists;</item>
/// <item>frames (Plate Frame, Portrait Frame) are hit only along their edges
/// (<see cref="FrameBand"/>), so the portrait or the text inside a frame is still clicked through it;</item>
/// <item>a Corner Ornament's artwork is hit only where it is drawn (<see cref="ArtCoverage"/>), so
/// a click on the clear part of its large box reaches the portrait or text under it;</item>
/// <item>everything else is hit inside its drawn box;</item>
/// <item>a decoration drawn over text (<see cref="YieldsToText"/>: a Section Header over its heading,
/// a Divider by the name) lets the text it covers take the click, so the heading or name is still
/// clicked through it. Where nothing is under it, it is hit as usual.</item>
/// </list>
/// A Component drawn invisible (opacity 0) takes no clicks.
/// </summary>
internal static class CanvasHitTest
{
    /// <summary>How far inside a frame's box its edge band reaches, in reference pixels (a frame's
    /// rail and corner pieces); it reaches half as far outside, where art corners overhang.</summary>
    internal const float FrameBand = 28f;

    /// <summary>Opacity below which a Component counts as not drawn.</summary>
    private const float MinVisibleOpacity = 0.02f;

    /// <summary>
    /// The topmost element or Component at <paramref name="point"/> (logical canvas coordinates) in
    /// <paramref name="plan"/> (paint order, bottom first). Elements <paramref name="skipElement"/>
    /// rejects are looked through, as are Components <paramref name="skipComponent"/> rejects.
    /// </summary>
    internal static CanvasHit Find(
        IReadOnlyList<PaintStep> plan, Vector2 point, float unit,
        Func<ProfileElement, bool>? skipElement = null, Func<PlateComponent, bool>? skipComponent = null) =>
        FindBelow(plan, plan.Count, point, unit, skipElement, skipComponent);

    /// <summary>The topmost hit among the steps below index <paramref name="above"/>.</summary>
    private static CanvasHit FindBelow(
        IReadOnlyList<PaintStep> plan, int above, Vector2 point, float unit,
        Func<ProfileElement, bool>? skipElement, Func<PlateComponent, bool>? skipComponent)
    {
        for (var i = above - 1; i >= 0; i--)
        {
            var step = plan[i];
            if (step.Element is { } element)
            {
                if ((skipElement is null || !skipElement(element)) && ProfilePaintOrder.Contains(element, point))
                {
                    return new CanvasHit(element, null);
                }

                continue;
            }

            if (step.Component is { } component && (skipComponent is null || !skipComponent(component)) && Hits(step, point, unit))
            {
                return YieldsToText(component.Kind) && FindBelow(plan, i, point, unit, skipElement, skipComponent) is { Element: { } covered }
                    ? new CanvasHit(covered, null)
                    : new CanvasHit(null, component);
            }
        }

        return default;
    }

    /// <summary>True for decorations drawn over the text they go with, which let that text take the click.</summary>
    internal static bool YieldsToText(PlateComponentKind kind) => kind is PlateComponentKind.SectionHeader or PlateComponentKind.Divider;

    /// <summary>True when a Component of <paramref name="kind"/> can be clicked on the canvas at all.</summary>
    internal static bool IsClickable(PlateComponentKind kind) => kind is not (PlateComponentKind.Background or PlateComponentKind.PortraitOverlay);

    /// <summary>Whether one placement of a Component is hit at <paramref name="point"/>.</summary>
    internal static bool Hits(in PaintStep step, Vector2 point, float unit)
    {
        if (step.Component is not { } component || !IsClickable(component.Kind)
            || PlateComponentLimits.ClampOpacity(component.Opacity) < MinVisibleOpacity)
        {
            return false;
        }

        var rect = step.Placement.Rect;
        if (!(rect.Size.X > 0f) || !(rect.Size.Y > 0f))
        {
            return false;
        }

        // Into the placement's own unrotated frame.
        var center = rect.Position + (rect.Size / 2f);
        var local = step.Placement.RotationDegrees == 0f ? point : RotationGeometry.RotatePoint(point, center, -step.Placement.RotationDegrees);
        var min = rect.Position;
        var max = rect.Position + rect.Size;

        if (component.Kind is not (PlateComponentKind.PlateFrame or PlateComponentKind.PortraitFrame))
        {
            if (!Inside(local, min, max))
            {
                return false;
            }

            // Whole-image artwork that was measured (Corner Ornaments) is hit where it is drawn: the
            // image fills the box, flipped with it when the placement mirrors it.
            if (step.Definition?.Art is { Slices: null, Frame: null } art && ArtCoverage.For(art) is { } coverage)
            {
                var u = (local.X - min.X) / rect.Size.X;
                var v = (local.Y - min.Y) / rect.Size.Y;
                return coverage.Covers(step.Placement.MirrorX ? 1f - u : u, step.Placement.MirrorY ? 1f - v : v);
            }

            return true;
        }

        var band = FrameBand * (float.IsFinite(unit) && unit > 0f ? unit : 1f);
        var inner = Math.Min(band, Math.Min(rect.Size.X, rect.Size.Y) / 2f);
        var outer = band / 2f;
        return Inside(local, min - new Vector2(outer), max + new Vector2(outer))
            && !Inside(local, min + new Vector2(inner), max - new Vector2(inner));
    }

    /// <summary>The four corners of each placement <paramref name="component"/> has in <paramref name="plan"/>
    /// (rotated, in logical canvas coordinates), for outlining it; nothing when it isn't drawn.</summary>
    internal static void Outlines(IReadOnlyList<PaintStep> plan, PlateComponent component, List<Vector2[]> output)
    {
        output.Clear();
        foreach (var step in plan)
        {
            if (ReferenceEquals(step.Component, component))
            {
                output.Add(RotationGeometry.GetRotatedCorners(step.Placement.Rect.Position, step.Placement.Rect.Size, step.Placement.RotationDegrees));
            }
        }
    }

    private static bool Inside(Vector2 point, Vector2 min, Vector2 max) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;
}
