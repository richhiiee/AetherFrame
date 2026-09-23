using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Maps an image into a target box for a <see cref="ProfileImageFit"/> mode — shared by image
/// elements and the background image so both behave identically.
///
/// The input is a normalized SOURCE REGION of the texture (UV space). Today every caller passes
/// the whole texture (<see cref="FullSource"/>); a future manual crop only needs to pass the
/// cropped region instead, and Fit/Fill/Stretch/flip keep working on top of it unchanged.
/// </summary>
internal static class ImageFitLayout
{
    internal static readonly (Vector2 Min, Vector2 Max) FullSource = (Vector2.Zero, Vector2.One);

    /// <summary>
    /// Returns the drawn sub-rectangle of the box (as box-relative offsets, same units as
    /// <paramref name="boxSize"/>) and the UV corners to draw it with, already flipped.
    /// <paramref name="textureSize"/> is the texture's full pixel size; non-positive sizes fall
    /// back to Stretch.
    /// </summary>
    internal static Result Compute(
        ProfileImageFit fit,
        Vector2 boxSize,
        Vector2 textureSize,
        (Vector2 Min, Vector2 Max) source,
        bool flipX,
        bool flipY)
    {
        var drawMin = Vector2.Zero;
        var drawMax = boxSize;
        var uvMin = source.Min;
        var uvMax = source.Max;

        var sourcePixels = (source.Max - source.Min) * textureSize;
        var valid = boxSize.X > 0f && boxSize.Y > 0f && sourcePixels.X > 0f && sourcePixels.Y > 0f;

        if (valid && fit != ProfileImageFit.Stretch)
        {
            var imageAspect = sourcePixels.X / sourcePixels.Y;
            var boxAspect = boxSize.X / boxSize.Y;

            if (fit == ProfileImageFit.Fit)
            {
                // Letterbox: shrink the drawn rect to the image's aspect, centered; full source UV.
                var drawSize = imageAspect > boxAspect
                    ? new Vector2(boxSize.X, boxSize.X / imageAspect)
                    : new Vector2(boxSize.Y * imageAspect, boxSize.Y);
                drawMin = (boxSize - drawSize) / 2f;
                drawMax = drawMin + drawSize;
            }
            else
            {
                // Fill: draw the whole box; crop the source UV window around its center.
                var sourceSpan = source.Max - source.Min;
                if (imageAspect > boxAspect)
                {
                    var visible = boxAspect / imageAspect;
                    var margin = sourceSpan.X * (1f - visible) / 2f;
                    uvMin.X += margin;
                    uvMax.X -= margin;
                }
                else
                {
                    var visible = imageAspect / boxAspect;
                    var margin = sourceSpan.Y * (1f - visible) / 2f;
                    uvMin.Y += margin;
                    uvMax.Y -= margin;
                }
            }
        }

        if (flipX)
        {
            (uvMin.X, uvMax.X) = (uvMax.X, uvMin.X);
        }

        if (flipY)
        {
            (uvMin.Y, uvMax.Y) = (uvMax.Y, uvMin.Y);
        }

        return new Result(drawMin, drawMax, uvMin, uvMax);
    }

    /// <summary>Box-relative drawn rectangle plus UVs for its top-left/bottom-right corners (flips applied).</summary>
    internal readonly record struct Result(Vector2 DrawMin, Vector2 DrawMax, Vector2 UvMin, Vector2 UvMax)
    {
        internal Vector2 UvTopLeft => UvMin;

        internal Vector2 UvTopRight => new(UvMax.X, UvMin.Y);

        internal Vector2 UvBottomRight => UvMax;

        internal Vector2 UvBottomLeft => new(UvMin.X, UvMax.Y);
    }
}
