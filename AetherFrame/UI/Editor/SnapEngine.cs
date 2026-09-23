using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>A temporary editor-only alignment guide line, in logical canvas coordinates.</summary>
/// <param name="Vertical">True for a vertical line at x = <paramref name="Position"/>; false for a horizontal line at y = Position.</param>
/// <param name="SpanStart">Where the line starts along its own axis.</param>
/// <param name="SpanEnd">Where the line ends along its own axis.</param>
internal readonly record struct SnapGuide(bool Vertical, float Position, float SpanStart, float SpanEnd);

/// <summary>
/// Snapping for canvas moves/resizes: canvas edges and centers, plus every other visible
/// element's edges and centers (by its visual, rotation-aware bounds).
///
/// Targets are collected once when an interaction begins (nothing else moves during it), so each
/// frame of a drag is a linear scan over at most ~3 x 257 values per axis — responsive even at the
/// 256-element limit — and allocates nothing. Also produces the guide lines shown while snapped.
/// Runtime-only editor state; never persisted and never rendered outside the editor canvas.
/// </summary>
internal sealed class SnapEngine
{
    // How exactly an edge has to match a target to count as "aligned" for guide display.
    private const float AlignmentEpsilon = 0.05f;

    private readonly List<Target> targetsX = new();
    private readonly List<Target> targetsY = new();
    private readonly List<SnapGuide> guides = new();

    /// <summary>Guide lines for the current snapped state; empty when nothing is aligned.</summary>
    internal IReadOnlyList<SnapGuide> Guides => guides;

    /// <summary>Collects snap targets for an interaction on <paramref name="movingElementId"/>.</summary>
    internal void Begin(ProfileDocument profile, Guid movingElementId)
    {
        Clear();

        var width = profile.CanvasWidth;
        var height = profile.CanvasHeight;

        // Canvas guides span the whole canvas.
        targetsX.Add(new Target(0f, 0f, height));
        targetsX.Add(new Target(width / 2f, 0f, height));
        targetsX.Add(new Target(width, 0f, height));
        targetsY.Add(new Target(0f, 0f, width));
        targetsY.Add(new Target(height / 2f, 0f, width));
        targetsY.Add(new Target(height, 0f, width));

        foreach (var element in profile.Elements)
        {
            if (!element.Visible || element.Id == movingElementId)
            {
                continue;
            }

            var (min, max) = RotationGeometry.GetVisualBounds(element);
            var center = (min + max) / 2f;

            targetsX.Add(new Target(min.X, min.Y, max.Y));
            targetsX.Add(new Target(center.X, min.Y, max.Y));
            targetsX.Add(new Target(max.X, min.Y, max.Y));
            targetsY.Add(new Target(min.Y, min.X, max.X));
            targetsY.Add(new Target(center.Y, min.X, max.X));
            targetsY.Add(new Target(max.Y, min.X, max.X));
        }
    }

    internal void Clear()
    {
        targetsX.Clear();
        targetsY.Clear();
        guides.Clear();
    }

    internal void ClearGuides() => guides.Clear();

    /// <summary>
    /// The offset that snaps a moving rectangle's nearest edge or center to the nearest target on
    /// each axis, or zero on an axis with nothing within <paramref name="threshold"/>.
    /// </summary>
    internal Vector2 SnapMove(Vector2 min, Vector2 max, float threshold)
    {
        var center = (min + max) / 2f;
        return new Vector2(
            BestOffset(targetsX, min.X, center.X, max.X, threshold),
            BestOffset(targetsY, min.Y, center.Y, max.Y, threshold));
    }

    /// <summary>Snaps a single moving vertical edge (x) to the nearest target within the threshold.</summary>
    internal float SnapX(float x, float threshold) => BestOffset(targetsX, x, x, x, threshold);

    /// <summary>Snaps a single moving horizontal edge (y) to the nearest target within the threshold.</summary>
    internal float SnapY(float y, float threshold) => BestOffset(targetsY, y, y, y, threshold);

    /// <summary>
    /// Rebuilds <see cref="Guides"/> for a rectangle's final position: one guide per target that
    /// one of the rectangle's considered lines now sits exactly on, spanning both the target and
    /// the rectangle so the relationship is visible.
    /// </summary>
    internal void UpdateGuides(Vector2 min, Vector2 max, EdgeMask edges)
    {
        guides.Clear();
        var center = (min + max) / 2f;

        if ((edges & EdgeMask.Left) != 0) AddGuides(targetsX, vertical: true, min.X, min.Y, max.Y);
        if ((edges & EdgeMask.CenterX) != 0) AddGuides(targetsX, vertical: true, center.X, min.Y, max.Y);
        if ((edges & EdgeMask.Right) != 0) AddGuides(targetsX, vertical: true, max.X, min.Y, max.Y);
        if ((edges & EdgeMask.Top) != 0) AddGuides(targetsY, vertical: false, min.Y, min.X, max.X);
        if ((edges & EdgeMask.CenterY) != 0) AddGuides(targetsY, vertical: false, center.Y, min.X, max.X);
        if ((edges & EdgeMask.Bottom) != 0) AddGuides(targetsY, vertical: false, max.Y, min.X, max.X);
    }

    private static float BestOffset(List<Target> targets, float a, float b, float c, float threshold)
    {
        var best = 0f;
        var bestDistance = threshold;
        var found = false;

        foreach (var target in targets)
        {
            Consider(target.Value - a);
            Consider(target.Value - b);
            Consider(target.Value - c);
        }

        return found ? best : 0f;

        void Consider(float offset)
        {
            var distance = MathF.Abs(offset);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = offset;
                found = true;
            }
        }
    }

    private void AddGuides(List<Target> targets, bool vertical, float value, float spanMin, float spanMax)
    {
        foreach (var target in targets)
        {
            if (MathF.Abs(target.Value - value) > AlignmentEpsilon)
            {
                continue;
            }

            var start = MathF.Min(spanMin, target.SpanMin);
            var end = MathF.Max(spanMax, target.SpanMax);

            // Merge with an existing guide on the same line instead of stacking duplicates.
            var merged = false;
            for (var i = 0; i < guides.Count; i++)
            {
                var existing = guides[i];
                if (existing.Vertical == vertical && MathF.Abs(existing.Position - target.Value) <= AlignmentEpsilon)
                {
                    guides[i] = existing with { SpanStart = MathF.Min(existing.SpanStart, start), SpanEnd = MathF.Max(existing.SpanEnd, end) };
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                guides.Add(new SnapGuide(vertical, target.Value, start, end));
            }
        }
    }

    /// <param name="Value">The target line's coordinate on its axis.</param>
    /// <param name="SpanMin">Extent of the target's source along the other axis (for guide length).</param>
    /// <param name="SpanMax">See <paramref name="SpanMin"/>.</param>
    private readonly record struct Target(float Value, float SpanMin, float SpanMax);
}

/// <summary>Which lines of a rectangle take part in guide display.</summary>
[Flags]
internal enum EdgeMask
{
    None = 0,
    Left = 1,
    CenterX = 2,
    Right = 4,
    Top = 8,
    CenterY = 16,
    Bottom = 32,
    All = Left | CenterX | Right | Top | CenterY | Bottom,
}
