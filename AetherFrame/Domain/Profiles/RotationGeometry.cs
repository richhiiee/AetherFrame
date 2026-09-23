using System;
using System.Numerics;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Shared rotation math for <see cref="ImageProfileElement"/>. <see cref="ProfileElement.Position"/>
/// and <see cref="ProfileElement.Size"/> always describe the element's unrotated local rectangle;
/// rotation is applied on top of that, around the rectangle's own center, purely for rendering,
/// hit testing, and editor geometry. Nothing here mutates an element — callers own that.
/// </summary>
internal static class RotationGeometry
{
    /// <summary>Only images rotate (Part 3); every other element type is always axis-aligned.</summary>
    internal static float GetRotationDegrees(ProfileElement element) =>
        element is ImageProfileElement image ? image.RotationDegrees : 0f;

    internal static Vector2 GetCenter(Vector2 position, Vector2 size) => position + (size / 2f);

    /// <summary>Rotates <paramref name="point"/> by <paramref name="degrees"/> around <paramref name="pivot"/>.</summary>
    internal static Vector2 RotatePoint(Vector2 point, Vector2 pivot, float degrees)
    {
        if (degrees == 0f)
        {
            return point;
        }

        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var offset = point - pivot;

        return pivot + new Vector2((offset.X * cos) - (offset.Y * sin), (offset.X * sin) + (offset.Y * cos));
    }

    /// <summary>
    /// The four corners of the (possibly rotated) rectangle, in the same coordinate space as
    /// <paramref name="position"/>/<paramref name="size"/>, walked in perimeter order
    /// (TopLeft, TopRight, BottomRight, BottomLeft) — the winding order every caller (quad
    /// drawing, resize handles) relies on.
    /// </summary>
    internal static Vector2[] GetRotatedCorners(Vector2 position, Vector2 size, float degrees)
    {
        var corners = new[]
        {
            position,
            position + new Vector2(size.X, 0f),
            position + size,
            position + new Vector2(0f, size.Y),
        };

        if (degrees == 0f)
        {
            return corners;
        }

        var center = GetCenter(position, size);
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = RotatePoint(corners[i], center, degrees);
        }

        return corners;
    }

    /// <summary>
    /// The width/height of the axis-aligned bounding box of a <paramref name="size"/> rectangle
    /// rotated by <paramref name="degrees"/> around its own center — used to keep a rotated
    /// element's full visual extent inside the canvas without needing its actual corners.
    /// </summary>
    internal static Vector2 GetRotatedAabbSize(Vector2 size, float degrees)
    {
        if (degrees == 0f)
        {
            return size;
        }

        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Abs(MathF.Cos(radians));
        var sin = MathF.Abs(MathF.Sin(radians));

        return new Vector2((size.X * cos) + (size.Y * sin), (size.X * sin) + (size.Y * cos));
    }

    /// <summary>
    /// The axis-aligned bounds (min, max corners) of an element's full visual extent — its rotated
    /// rectangle for a rotated image, or simply its Position/Size box otherwise. Used for snapping
    /// and alignment, which operate on what the user actually sees.
    /// </summary>
    internal static (Vector2 Min, Vector2 Max) GetVisualBounds(ProfileElement element) =>
        GetVisualBounds(element.Position, element.Size, GetRotationDegrees(element));

    internal static (Vector2 Min, Vector2 Max) GetVisualBounds(Vector2 position, Vector2 size, float degrees)
    {
        var halfExtent = GetRotatedAabbSize(size, degrees) / 2f;
        var center = GetCenter(position, size);
        return (center - halfExtent, center + halfExtent);
    }

    /// <summary>Wraps a rotation value into [0, 360).</summary>
    internal static float NormalizeDegrees(float degrees)
    {
        var normalized = degrees % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }
}
