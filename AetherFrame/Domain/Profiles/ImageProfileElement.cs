using System;

namespace AetherFrame.Domain.Profiles;

public sealed class ImageProfileElement : ProfileElement
{
    // Square default so a freshly-added image doesn't need its source texture loaded yet to
    // pick a size; the user resizes to taste (or PreserveAspectRatio keeps it square-ish).
    public const float DefaultSize = 200f;

    /// <summary>Identifies the imported file in AetherFrame-managed asset storage. Never an
    /// absolute path: the profile document must stay portable and must not leak local
    /// filesystem layout.</summary>
    public Guid AssetId { get; set; }

    public float Opacity { get; set; } = 1f;

    public bool PreserveAspectRatio { get; set; } = true;

    /// <summary>
    /// Clockwise rotation, in degrees, around the element's own center. Always normalized to
    /// [0, 360). Absent from JSON written before this field existed, which deserializes it as
    /// the default 0 — no migration needed. Text elements never rotate.
    /// </summary>
    public float RotationDegrees { get; set; }

    /// <summary>
    /// How the image's pixels map into the element box. <see cref="ProfileImageFit.Stretch"/> is
    /// the enum's zero value, so every legacy element (saved before this field existed) keeps
    /// rendering exactly as it always did — stretched to its box.
    ///
    /// The mapping is computed from a normalized "source region" of the texture that is currently
    /// always the whole image (see <c>ImageFitLayout</c>). A future manual crop only has to add a
    /// persisted source rectangle and feed it in there; Fit/Fill/Stretch then apply to the cropped
    /// region unchanged, so no second data-model rewrite is needed.
    /// </summary>
    public ProfileImageFit DisplayMode { get; set; } = ProfileImageFit.Stretch;

    /// <summary>Mirrors the image horizontally, within its (possibly rotated) box.</summary>
    public bool FlipX { get; set; }

    /// <summary>Mirrors the image vertically, within its (possibly rotated) box.</summary>
    public bool FlipY { get; set; }

    internal override ProfileElement Clone()
    {
        var clone = CloneBaseInto(new ImageProfileElement());
        clone.CopyImagePropertiesFrom(this);
        return clone;
    }

    internal override void CopyFrom(ProfileElement source)
    {
        base.CopyFrom(source);

        if (source is ImageProfileElement image)
        {
            CopyImagePropertiesFrom(image);
        }
    }

    internal override bool ContentEquals(ProfileElement other) =>
        base.ContentEquals(other)
        && other is ImageProfileElement o
        && AssetId == o.AssetId
        && Opacity.Equals(o.Opacity)
        && PreserveAspectRatio == o.PreserveAspectRatio
        && RotationDegrees.Equals(o.RotationDegrees)
        && DisplayMode == o.DisplayMode
        && FlipX == o.FlipX
        && FlipY == o.FlipY;

    private void CopyImagePropertiesFrom(ImageProfileElement image)
    {
        AssetId = image.AssetId;
        Opacity = image.Opacity;
        PreserveAspectRatio = image.PreserveAspectRatio;
        RotationDegrees = image.RotationDegrees;
        DisplayMode = image.DisplayMode;
        FlipX = image.FlipX;
        FlipY = image.FlipY;
    }
}

/// <summary>
/// How an image fills a target box. Shared by <see cref="ImageProfileElement.DisplayMode"/> and
/// <see cref="ProfileBackground.ImageFit"/>. Persisted as its numeric value, so the order here
/// must never change.
/// </summary>
public enum ProfileImageFit
{
    /// <summary>Fills the box exactly, ignoring the image's aspect ratio.</summary>
    Stretch = 0,

    /// <summary>Shows the whole image, letterboxed inside the box at its own aspect ratio.</summary>
    Fit = 1,

    /// <summary>Covers the whole box at the image's own aspect ratio, cropping the overflow.</summary>
    Fill = 2,
}
