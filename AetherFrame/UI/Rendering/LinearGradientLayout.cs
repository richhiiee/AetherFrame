using System;
using System.Numerics;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Geometry of a CSS-style linear gradient over a rectangle, independent of ImGui so it can be
/// verified in isolation.
///
/// The gradient parameter t(p) = dot(p - center, d) / (2 * halfLength) + 0.5, with d the unit
/// direction for the angle and halfLength = (|W * dx| + |H * dy|) / 2, is an affine function of
/// position. That halfLength is exactly the largest |dot(corner - center, d)| over the four
/// corners, so every corner's t lies in [0, 1] (the two extreme corners exactly 0 and 1) and no
/// point inside the rectangle needs clamping. The color, a per-channel lerp in t, is therefore
/// affine in position too.
///
/// ImGui's AddRectFilledMultiColor emits two triangles (TL, TR, BR) and (TL, BR, BL); the GPU
/// interpolates vertex colors barycentrically within each triangle (orthographic projection, so
/// perspective-correct interpolation reduces to plain affine interpolation). Affine interpolation
/// of the vertex values of an affine function reproduces that function exactly at every point of
/// the triangle — so four corner colors give the true gradient at ANY angle, not an approximation.
/// The only error is ImGui's 8-bit-per-channel vertex color (at most 0.5/255 per channel), the
/// same quantization every ImGui color has and which subdividing into strips could not reduce.
///
/// That argument needs a constant alpha across the rectangle: with differing endpoint alphas,
/// non-premultiplied blending would make the blended result quadratic in t. The renderer therefore
/// always uses opaque endpoint colors and applies transparency as one uniform alpha (the
/// background's Opacity), which the editor's color pickers already guarantee (they never expose
/// the gradient colors' own alpha).
/// </summary>
internal static class LinearGradientLayout
{
    /// <summary>The gradient parameter at each rectangle corner: top-left, top-right, bottom-right, bottom-left.</summary>
    internal static (float TopLeft, float TopRight, float BottomRight, float BottomLeft) ComputeCornerT(Vector2 size, float angleDegrees)
    {
        var radians = angleDegrees * (MathF.PI / 180f);
        var direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var halfLength = (MathF.Abs(size.X * direction.X) + MathF.Abs(size.Y * direction.Y)) / 2f;
        var half = size / 2f;

        float T(Vector2 offsetFromCenter) =>
            halfLength > 0f ? Math.Clamp((Vector2.Dot(offsetFromCenter, direction) / (2f * halfLength)) + 0.5f, 0f, 1f) : 0.5f;

        return (
            T(new Vector2(-half.X, -half.Y)),
            T(new Vector2(half.X, -half.Y)),
            T(new Vector2(half.X, half.Y)),
            T(new Vector2(-half.X, half.Y)));
    }
}
