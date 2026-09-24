using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Basic mode's Identity Header: the structured settings behind the Character Name, Title, and
/// Tagline elements (roles <see cref="ProfileElementRole.BasicName"/>,
/// <see cref="ProfileElementRole.BasicTitle"/>, <see cref="ProfileElementRole.BasicTagline"/>).
///
/// The three pieces of text always stay in their own <see cref="TextProfileElement"/>s — nothing
/// is ever merged into one string — and those elements remain ordinary, fully editable Advanced
/// elements. This object only holds what the elements themselves can't express: where the title
/// comes from, which curated layout places the three, and the header region that layout fills.
///
/// Null on <see cref="ProfileDocument.BasicIdentity"/> (every profile before this existed, and any
/// profile never touched by the Identity controls) means "not configured yet": Basic mode then
/// binds to whatever role elements already exist, exactly where they are, and writes nothing
/// until the user makes an Identity edit.
/// </summary>
public sealed class BasicIdentityHeader
{
    public const int MaxCustomTitleLength = 64;
    public const int MaxTaglineLength = 120;

    public IdentityTitleSource TitleSource { get; set; } = IdentityTitleSource.None;

    /// <summary>Row id of the chosen FFXIV Title (game data), or 0 for none chosen yet.</summary>
    public uint GameTitleId { get; set; }

    /// <summary>
    /// The chosen game title's own placement metadata: true for titles the game shows before
    /// (above) the character name, false for after (below). Captured when chosen, so it survives
    /// without game data (e.g. a profile rendered elsewhere).
    /// </summary>
    public bool GameTitleIsPrefix { get; set; }

    /// <summary>The Custom title text. Kept while another source is active, so switching back restores it.</summary>
    public string CustomTitle { get; set; } = string.Empty;

    public IdentityTitleLayout Layout { get; set; } = IdentityTitleLayout.Subtitle;

    /// <summary>Top-left of the region the header layout fills, in logical canvas pixels.</summary>
    public Vector2 RegionPosition { get; set; }

    /// <summary>Width of the header region, in logical canvas pixels.</summary>
    public float RegionWidth { get; set; }

    /// <summary>
    /// Where Basic mode last placed each element. While every existing element still sits exactly
    /// there, the header is "Basic-managed" and reflows when its content or style changes. Once
    /// any of them has been moved or resized elsewhere (the Advanced editor), the header counts as
    /// customized and Basic mode never moves it again on its own — only an explicit layout choice
    /// or "Apply Layout" does. Null: never applied (e.g. elements from an earlier version).
    /// </summary>
    public IdentityLayoutSnapshot? AppliedLayout { get; set; }

    /// <summary>
    /// The title look the current layout applied when it was chosen (Badge: size, weight, spacing;
    /// Accent: italic, spacing) and what the title had before, so leaving the layout undoes exactly
    /// what the layout changed — and nothing the user has changed since. Null when the current layout
    /// applied no look (or for a header from an earlier build; see <c>IdentityHeaderRules</c>).
    /// Layouts never write the title's text, prefix, or suffix: those are always the user's.
    /// </summary>
    public IdentityLayoutStyle? LayoutStyle { get; set; }

    /// <summary>Properties this build doesn't know, kept through clone and save unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public BasicIdentityHeader Clone() => new()
    {
        TitleSource = TitleSource,
        GameTitleId = GameTitleId,
        GameTitleIsPrefix = GameTitleIsPrefix,
        CustomTitle = CustomTitle,
        Layout = Layout,
        RegionPosition = RegionPosition,
        RegionWidth = RegionWidth,
        AppliedLayout = AppliedLayout?.Clone(),
        LayoutStyle = LayoutStyle?.Clone(),
        ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
    };

    public bool ContentEquals(BasicIdentityHeader? other) =>
        other is not null
        && TitleSource == other.TitleSource
        && GameTitleId == other.GameTitleId
        && GameTitleIsPrefix == other.GameTitleIsPrefix
        && CustomTitle == other.CustomTitle
        && Layout == other.Layout
        && RegionPosition == other.RegionPosition
        && RegionWidth.Equals(other.RegionWidth)
        && (AppliedLayout is null ? other.AppliedLayout is null : AppliedLayout.ContentEquals(other.AppliedLayout))
        && (LayoutStyle is null ? other.LayoutStyle is null : LayoutStyle.ContentEquals(other.LayoutStyle));
}

/// <summary>The rectangles Basic mode last assigned to the Identity Header's elements.</summary>
public sealed class IdentityLayoutSnapshot
{
    public ElementRect? Name { get; set; }

    public ElementRect? Title { get; set; }

    public ElementRect? Tagline { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public IdentityLayoutSnapshot Clone() => new() { Name = Name, Title = Title, Tagline = Tagline, ExtensionData = ProfileElement.CopyExtensionData(ExtensionData) };

    public bool ContentEquals(IdentityLayoutSnapshot? other) =>
        other is not null && Name == other.Name && Title == other.Title && Tagline == other.Tagline;
}

/// <summary>An element placement: logical Position and Size.</summary>
public readonly record struct ElementRect(Vector2 Position, Vector2 Size)
{
    /// <summary>Equality with a small tolerance, so float round-off never reads as "customized".</summary>
    public bool Matches(Vector2 position, Vector2 size) =>
        Vector2.DistanceSquared(Position, position) < 0.01f && Vector2.DistanceSquared(Size, size) < 0.01f;

    /// <summary>The smallest rectangle containing both.</summary>
    public ElementRect Union(ElementRect other)
    {
        var min = Vector2.Min(Position, other.Position);
        var max = Vector2.Max(Position + Size, other.Position + other.Size);
        return new ElementRect(min, max - min);
    }

    /// <summary>True when the two share any area (touching edges don't count).</summary>
    public bool Intersects(ElementRect other) =>
        Position.X < other.Position.X + other.Size.X && other.Position.X < Position.X + Size.X
        && Position.Y < other.Position.Y + other.Size.Y && other.Position.Y < Position.Y + Size.Y;
}

/// <summary>Where the title text comes from. Persisted numerically; append only.</summary>
public enum IdentityTitleSource
{
    /// <summary>No title: the title element is hidden (its text is kept).</summary>
    None = 0,

    /// <summary>An FFXIV character title from game data.</summary>
    GameTitle = 1,

    /// <summary>Arbitrary text, up to <see cref="BasicIdentityHeader.MaxCustomTitleLength"/>.</summary>
    Custom = 2,
}

/// <summary>Curated Identity Header layouts. Persisted numerically; append only.</summary>
public enum IdentityTitleLayout
{
    /// <summary>Title above the character name.</summary>
    Classic = 0,

    /// <summary>Character name above the title.</summary>
    Subtitle = 1,

    /// <summary>Title then character name, on one line.</summary>
    InlineBefore = 2,

    /// <summary>Character name then title, on one line.</summary>
    InlineAfter = 3,

    /// <summary>Character name with the title as a small, spaced, bold secondary line.</summary>
    Badge = 4,

    /// <summary>Character name with the title decorated by prefix/suffix symbols.</summary>
    Accent = 5,
}
