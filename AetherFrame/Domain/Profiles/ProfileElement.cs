using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "elementType")]
[JsonDerivedType(typeof(TextProfileElement), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ImageProfileElement), typeDiscriminator: "image")]
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
    /// Identifies an element as a reserved slot owned by the Basic editor (e.g. its portrait or
    /// name field), rather than by list position or a hardcoded id. <see cref="ProfileElementRole.None"/>
    /// (the default, including for every legacy element with no persisted role) means the
    /// element is a free-form Advanced-editor element.
    /// </summary>
    public ProfileElementRole Role { get; set; } = ProfileElementRole.None;

    /// <summary>
    /// Repairs an element loaded with an invalid (non-positive) canvas size: most commonly a
    /// legacy element saved before Position/Size existed (which then default-deserialize to
    /// <see cref="Vector2.Zero"/>), or one whose zero size was itself already persisted by an
    /// earlier build. Elements that already have a valid size — including any user-set
    /// Visible/Locked/Position — are left completely untouched.
    /// </summary>
    /// <param name="canvasWidth">The owning profile's (already-resolved) canvas width.</param>
    /// <param name="canvasHeight">The owning profile's (already-resolved) canvas height.</param>
    /// <returns>True if a repair was applied.</returns>
    internal bool NormalizeLegacyLayout(float canvasWidth, float canvasHeight)
    {
        if (Size.X > 0f && Size.Y > 0f)
        {
            return false;
        }

        Size = new Vector2(DefaultWidth, DefaultHeight);

        var maxX = Math.Max(0f, canvasWidth - Size.X);
        var maxY = Math.Max(0f, canvasHeight - Size.Y);
        Position = new Vector2(Math.Clamp(Position.X, 0f, maxX), Math.Clamp(Position.Y, 0f, maxY));

        // An element that never had a renderable size could not have been seen, or
        // intentionally hidden, by a user — surface it once it's repaired.
        Visible = true;

        return true;
    }

    /// <summary>
    /// Creates an independent copy (including <see cref="Id"/>) of this element, for undo/redo
    /// snapshots and for <c>Duplicate</c>. Never touched by further live mutations.
    /// </summary>
    internal abstract ProfileElement Clone();

    /// <summary>
    /// Copies every editable property (except <see cref="Id"/>) from <paramref name="source"/>
    /// into this instance. Used to restore an undo/redo snapshot onto the live element in
    /// place via <c>ProfileService.UpdateElement</c>, without replacing its identity.
    /// </summary>
    internal virtual void CopyFrom(ProfileElement source)
    {
        Visible = source.Visible;
        Locked = source.Locked;
        Position = source.Position;
        Size = source.Size;
        ZIndex = source.ZIndex;
        Role = source.Role;
    }
}

/// <summary>
/// Semantic role of a reserved Basic-editor element within a <see cref="ProfileDocument"/>.
/// Persisted so the Basic editor can find its own elements without relying on list position or
/// hardcoded ids. Only roles Basic mode actually manages exist here; ordinary Advanced-editor
/// elements are always <see cref="None"/>.
/// </summary>
public enum ProfileElementRole
{
    /// <summary>Not owned by Basic mode. The default for every element, including all legacy
    /// elements deserialized from JSON written before this field existed.</summary>
    None = 0,
    BasicPortrait,
    BasicName,
    BasicTitle,
    BasicMessage,
}
