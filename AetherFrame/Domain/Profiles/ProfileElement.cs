using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "elementType")]
[JsonDerivedType(typeof(TextProfileElement), typeDiscriminator: "text")]
public abstract class ProfileElement
{
    // Sensible default layout for an element with no (valid) canvas placement yet, e.g. one
    // freshly added, or one being repaired by NormalizeLegacyLayout.
    public const float DefaultPositionX = 40f;
    public const float DefaultPositionY = 40f;
    public const float DefaultWidth = 220f;
    public const float DefaultHeight = 50f;

    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    /// <summary>Top-left corner, in logical canvas coordinates.</summary>
    public Vector2 Position { get; set; } = new(DefaultPositionX, DefaultPositionY);

    /// <summary>Width/height, in logical canvas coordinates.</summary>
    public Vector2 Size { get; set; } = new(DefaultWidth, DefaultHeight);

    /// <summary>Draw/hit-test order; higher values are visually on top.</summary>
    public int ZIndex { get; set; }

    /// <summary>
    /// Repairs an element loaded with an invalid (non-positive) canvas size: most commonly a
    /// legacy element saved before Position/Size existed (which then default-deserialize to
    /// <see cref="Vector2.Zero"/>), or one whose zero size was itself already persisted by an
    /// earlier build. Elements that already have a valid size — including any user-set
    /// Visible/Locked/Position — are left completely untouched.
    /// </summary>
    /// <returns>True if a repair was applied.</returns>
    internal bool NormalizeLegacyLayout()
    {
        if (Size.X > 0f && Size.Y > 0f)
        {
            return false;
        }

        Size = new Vector2(DefaultWidth, DefaultHeight);

        var maxX = Math.Max(0f, ProfileDocument.CanvasWidth - Size.X);
        var maxY = Math.Max(0f, ProfileDocument.CanvasHeight - Size.Y);
        Position = new Vector2(Math.Clamp(Position.X, 0f, maxX), Math.Clamp(Position.Y, 0f, maxY));

        // An element that never had a renderable size could not have been seen, or
        // intentionally hidden, by a user — surface it once it's repaired.
        Visible = true;

        return true;
    }
}
