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
    };

    internal override void CopyFrom(ProfileElement source)
    {
        base.CopyFrom(source);

        if (source is ImageProfileElement image)
        {
            AssetId = image.AssetId;
            Opacity = image.Opacity;
            PreserveAspectRatio = image.PreserveAspectRatio;
        }
    }
}
