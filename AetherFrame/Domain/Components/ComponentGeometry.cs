using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

public enum ComponentPrimitiveKind
{
    /// <summary>Filled convex quad A-B-C-D.</summary>
    Quad,

    /// <summary>Filled triangle A-B-C.</summary>
    Triangle,

    /// <summary>The component's image over quad A-B-C-D (top-left, top-right, bottom-right, bottom-left).</summary>
    Image,
}

/// <summary>One procedural drawing primitive in logical canvas coordinates.</summary>
public readonly record struct ComponentPrimitive(ComponentPrimitiveKind Kind, Vector2 A, Vector2 B, Vector2 C, Vector2 D, Vector4 Color);

/// <summary>
/// The procedural shapes behind every built-in <see cref="ComponentShape"/>, as plain filled quads
/// and triangles in logical canvas space. Pure math, so every surface draws identical geometry and
/// tests can check it without a renderer. Shapes are designed in the placement's own box (top-left
/// origin), then mirrored, then rotated around the box's center — so rotation and mirroring work
/// the same for every shape. Output size is bounded per shape (at most a few dozen primitives).
/// </summary>
public static class ComponentGeometry
{
    /// <summary>Bands used to approximate a fade.</summary>
    public const int FadeBands = 12;

    /// <summary>Appends the primitives for one placement of a component to <paramref name="output"/>.</summary>
    public static void Build(ProfileDocument profile, PlateComponent component, ComponentDefinition definition, ComponentPlacement placement, List<ComponentPrimitive> output)
    {
        var size = placement.Rect.Size;
        if (!(size.X > 0f) || !(size.Y > 0f) || !float.IsFinite(size.X) || !float.IsFinite(size.Y))
        {
            return;
        }

        var color = PlateComponentLimits.ClampColor(component.Color ?? definition.DefaultColor(profile));
        color.W *= PlateComponentLimits.ClampOpacity(component.Opacity);
        if (color.W <= 0f)
        {
            return;
        }

        var box = new Box(placement, output);
        var unit = ComponentPaintPlan.Unit(profile) * PlateComponentLimits.ClampScale(component.Scale);
        var w = size.X;
        var h = size.Y;
        var t = Math.Max(1f, 2f * unit);

        switch (definition.Shape)
        {
            case ComponentShape.Border:
                box.Border(0f, t, color);
                break;

            case ComponentShape.DoubleBorder:
                box.Border(0f, t * 1.5f, color);
                box.Border(t * 3.5f, Math.Max(1f, t * 0.6f), color);
                break;

            case ComponentShape.NotchedBorder:
            {
                var cut = Math.Min(Math.Min(w, h) * 0.25f, 18f * unit);
                box.Rect(cut, 0f, w - cut, t, color);
                box.Rect(cut, h - t, w - cut, h, color);
                box.Rect(0f, cut, t, h - cut, color);
                box.Rect(w - t, cut, w, h - cut, color);
                box.Stroke(new Vector2(0f, cut), new Vector2(cut, 0f), t, color);
                box.Stroke(new Vector2(w - cut, 0f), new Vector2(w, cut), t, color);
                box.Stroke(new Vector2(w, h - cut), new Vector2(w - cut, h), t, color);
                box.Stroke(new Vector2(cut, h), new Vector2(0f, h - cut), t, color);
                break;
            }

            case ComponentShape.CornerBrackets:
            {
                var arm = Math.Min(w, h) * 0.18f;
                var thick = t * 1.5f;
                box.Rect(0f, 0f, arm, thick, color);
                box.Rect(0f, 0f, thick, arm, color);
                box.Rect(w - arm, 0f, w, thick, color);
                box.Rect(w - thick, 0f, w, arm, color);
                box.Rect(0f, h - thick, arm, h, color);
                box.Rect(0f, h - arm, thick, h, color);
                box.Rect(w - arm, h - thick, w, h, color);
                box.Rect(w - thick, h - arm, w, h, color);
                break;
            }

            case ComponentShape.BottomFade:
            {
                var top = h * 0.55f;
                var band = (h - top) / FadeBands;
                for (var i = 0; i < FadeBands; i++)
                {
                    box.Rect(0f, top + (i * band), w, top + ((i + 1) * band), WithAlpha(color, (i + 1f) / FadeBands));
                }

                break;
            }

            case ComponentShape.Vignette:
            {
                const int bands = 8;
                var depth = Math.Min(w, h) * 0.2f;
                var band = depth / bands;
                for (var i = 0; i < bands; i++)
                {
                    var a = WithAlpha(color, (bands - i) / (float)bands / 3f);
                    var inner = (i + 1) * band;
                    var outer = i * band;
                    box.Rect(outer, outer, w - outer, inner, a);
                    box.Rect(outer, h - inner, w - outer, h - outer, a);
                    box.Rect(outer, inner, inner, h - inner, a);
                    box.Rect(w - inner, inner, w - outer, h - inner, a);
                }

                break;
            }

            case ComponentShape.Image:
                box.Image(color);
                break;

            case ComponentShape.Bar:
                box.Rect(0f, 0f, w, h, color);
                break;

            case ComponentShape.Ribbon:
            {
                var point = Math.Min(h * 0.5f, w * 0.25f);
                box.Rect(point, 0f, w - point, h, color);
                box.Triangle(new Vector2(point, 0f), new Vector2(point, h), new Vector2(0f, h / 2f), color);
                box.Triangle(new Vector2(w - point, 0f), new Vector2(w, h / 2f), new Vector2(w - point, h), color);
                break;
            }

            case ComponentShape.FadeBar:
            {
                var band = w / FadeBands;
                for (var i = 0; i < FadeBands; i++)
                {
                    box.Rect(i * band, 0f, (i + 1) * band, h, WithAlpha(color, (FadeBands - i) / (float)FadeBands));
                }

                break;
            }

            case ComponentShape.Rule:
                box.Rect(0f, (h - t) / 2f, w, (h + t) / 2f, color);
                break;

            case ComponentShape.DiamondRule:
            {
                var half = Math.Min(h * 0.3f, w * 0.1f);
                var gap = half * 1.8f;
                var mid = w / 2f;
                box.Rect(0f, (h - t) / 2f, mid - gap, (h + t) / 2f, color);
                box.Rect(mid + gap, (h - t) / 2f, w, (h + t) / 2f, color);
                box.Quad(new Vector2(mid, (h / 2f) - half), new Vector2(mid + half, h / 2f), new Vector2(mid, (h / 2f) + half), new Vector2(mid - half, h / 2f), color);
                break;
            }

            case ComponentShape.Underline:
            {
                var thin = Math.Max(1f, unit);
                box.Rect(0f, h - thin, w, h, color);
                break;
            }

            case ComponentShape.AccentTick:
            {
                var length = Math.Min(w, 32f * unit);
                box.Rect(0f, h - t, length, h, color);
                break;
            }

            case ComponentShape.CornerL:
            {
                var thick = t * 1.5f;
                box.Rect(0f, 0f, w, thick, color);
                box.Rect(0f, thick, thick, h, color);
                var dot = thick * 2f;
                box.Rect(thick * 3f, thick * 3f, (thick * 3f) + dot, (thick * 3f) + dot, color);
                break;
            }

            case ComponentShape.CornerDiamond:
            {
                var half = Math.Min(w, h) * 0.22f;
                var c = new Vector2(half, half);
                box.Quad(c - new Vector2(0f, half), c + new Vector2(half, 0f), c + new Vector2(0f, half), c - new Vector2(half, 0f), color);
                box.Rect(half * 2.3f, half - (t / 2f), w, half + (t / 2f), color);
                box.Rect(half - (t / 2f), half * 2.3f, half + (t / 2f), h, color);
                break;
            }
        }
    }

    private static Vector4 WithAlpha(Vector4 color, float factor) => new(color.X, color.Y, color.Z, color.W * Math.Clamp(factor, 0f, 1f));

    /// <summary>A placement's local drawing space: local box coordinates to logical canvas coordinates.</summary>
    private readonly struct Box
    {
        private readonly Vector2 min;
        private readonly Vector2 max;
        private readonly Vector2 center;
        private readonly float rotation;
        private readonly bool mirrorX;
        private readonly bool mirrorY;
        private readonly List<ComponentPrimitive> output;

        internal Box(ComponentPlacement placement, List<ComponentPrimitive> output)
        {
            min = placement.Rect.Position;
            max = placement.Rect.Position + placement.Rect.Size;
            center = (min + max) / 2f;
            rotation = float.IsFinite(placement.RotationDegrees) ? placement.RotationDegrees : 0f;
            mirrorX = placement.MirrorX;
            mirrorY = placement.MirrorY;
            this.output = output;
        }

        internal void Rect(float x0, float y0, float x1, float y1, Vector4 color)
        {
            if (x1 <= x0 || y1 <= y0)
            {
                return;
            }

            Quad(new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1), color);
        }

        internal void Border(float inset, float thickness, Vector4 color)
        {
            var w = max.X - min.X;
            var h = max.Y - min.Y;
            var x0 = inset;
            var y0 = inset;
            var x1 = w - inset;
            var y1 = h - inset;
            if (x1 - x0 <= 2f * thickness || y1 - y0 <= 2f * thickness)
            {
                return;
            }

            Rect(x0, y0, x1, y0 + thickness, color);
            Rect(x0, y1 - thickness, x1, y1, color);
            Rect(x0, y0 + thickness, x0 + thickness, y1 - thickness, color);
            Rect(x1 - thickness, y0 + thickness, x1, y1 - thickness, color);
        }

        /// <summary>A straight stroke of <paramref name="thickness"/> from a to b, as a quad.</summary>
        internal void Stroke(Vector2 a, Vector2 b, float thickness, Vector4 color)
        {
            var direction = b - a;
            var length = direction.Length();
            if (length <= 0f)
            {
                return;
            }

            var normal = new Vector2(-direction.Y, direction.X) / length * (thickness / 2f);
            Quad(a - normal, b - normal, b + normal, a + normal, color);
        }

        // A single mirror reverses winding; the vertex order is flipped back so every filled
        // primitive keeps one consistent winding (anti-aliased fills depend on it).
        internal void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 color) =>
            output.Add(mirrorX != mirrorY
                ? new ComponentPrimitive(ComponentPrimitiveKind.Quad, Map(a), Map(d), Map(c), Map(b), color)
                : new ComponentPrimitive(ComponentPrimitiveKind.Quad, Map(a), Map(b), Map(c), Map(d), color));

        internal void Triangle(Vector2 a, Vector2 b, Vector2 c, Vector4 color) =>
            output.Add(mirrorX != mirrorY
                ? new ComponentPrimitive(ComponentPrimitiveKind.Triangle, Map(a), Map(c), Map(b), Map(b), color)
                : new ComponentPrimitive(ComponentPrimitiveKind.Triangle, Map(a), Map(b), Map(c), Map(c), color));

        internal void Image(Vector4 color)
        {
            var w = max.X - min.X;
            var h = max.Y - min.Y;
            output.Add(new ComponentPrimitive(
                ComponentPrimitiveKind.Image,
                Map(Vector2.Zero), Map(new Vector2(w, 0f)), Map(new Vector2(w, h)), Map(new Vector2(0f, h)), color));
        }

        private Vector2 Map(Vector2 local)
        {
            var point = new Vector2(mirrorX ? max.X - local.X : min.X + local.X, mirrorY ? max.Y - local.Y : min.Y + local.Y);
            return rotation == 0f ? point : RotationGeometry.RotatePoint(point, center, rotation);
        }
    }
}
