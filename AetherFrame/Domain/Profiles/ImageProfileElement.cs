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

    internal override ProfileElement Clone() => new ImageProfileElement
    {
        Id = Id,
        Visible = Visible,
        Locked = Locked,
        Position = Position,
        Size = Size,
        ZIndex = ZIndex,
        AssetId = AssetId,
        Opacity = Opacity,
        PreserveAspectRatio = PreserveAspectRatio,
        RotationDegrees = RotationDegrees,
    };

    internal override void CopyFrom(ProfileElement source)
    {
        base.CopyFrom(source);

        if (source is ImageProfileElement image)
        {
            AssetId = image.AssetId;
            Opacity = image.Opacity;
            PreserveAspectRatio = image.PreserveAspectRatio;
            RotationDegrees = image.RotationDegrees;
        }
    }
}
