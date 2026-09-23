using System.Numerics;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// A block of plain text laid out inside its Position/Size box.
///
/// Every property added after the original text element shipped (outline, shadow, spacing,
/// vertical alignment, auto fit) defaults to a value that renders a legacy element exactly as it
/// did before that property existed: System.Text.Json runs these property initializers and only
/// overwrites properties actually present in the JSON, so an old profile never needs migrating.
/// All lengths (outline thickness, shadow offset, letter spacing) are logical canvas pixels, scaled
/// with the profile like Position/Size, so they look the same in the editor and in Profile View at
/// any zoom.
/// </summary>
public sealed class TextProfileElement : ProfileElement, IJsonOnDeserializing
{
    public const int MaxTextLength = 2000;
    public const float MinFontSize = 6f;
    public const float MaxFontSize = 96f;

    public const float MaxOutlineThickness = 16f;
    public const float MaxShadowOffset = 40f;
    public const float MinLetterSpacing = -10f;
    public const float MaxLetterSpacing = 40f;
    public const float MinLineSpacing = 0.5f;
    public const float MaxLineSpacing = 3f;

    /// <summary>Text laid out exactly as builds before text layout versions did: no wrapping
    /// (Wrap was stored but never applied), 4 SCREEN-pixel padding, and the whole text block
    /// aligned as one unit.</summary>
    public const int LegacyLayoutVersion = 0;

    /// <summary>Real word wrapping, canvas-unit padding (identical at every zoom), and per-line
    /// alignment.</summary>
    public const int CurrentLayoutVersion = 1;

    public string Text { get; set; } = string.Empty;

    public float FontSize { get; set; } = 16f;

    /// <summary>Fill color. Its alpha is the whole text's opacity (fill, outline, and shadow
    /// alike) — the Inspector's Opacity control edits exactly this.</summary>
    public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);

    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Vertical placement of the laid-out text block inside the element box.
    /// Top (the default) matches every legacy element.</summary>
    public TextVerticalAlignment VerticalAlignment { get; set; } = TextVerticalAlignment.Top;

    public bool Wrap { get; set; } = true;

    /// <summary>
    /// One of the ids in <see cref="ProfileFontFamilies"/>. Absent from JSON written before this
    /// field existed, which deserializes it as the default <see cref="ProfileFontFamilies.Default"/>
    /// — Dalamud's own default font, matching every legacy element's prior appearance exactly.
    /// An unrecognized id (e.g. a family removed in a later build) falls back to the default at
    /// render time rather than failing to load.
    /// </summary>
    public string FontFamily { get; set; } = ProfileFontFamilies.DalamudDefault;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Strikethrough { get; set; }

    /// <summary>Extra space between adjacent characters, in logical pixels (may be negative).</summary>
    public float LetterSpacing { get; set; }

    /// <summary>Line height as a multiple of the font size. 1 matches the legacy line height.</summary>
    public float LineSpacing { get; set; } = 1f;

    /// <summary>
    /// When true, the effective font size is reduced at render time (never below
    /// <see cref="AutoFitMinimumSize"/>) until the text fits inside the element box.
    /// <see cref="FontSize"/> and <see cref="Text"/> themselves are never modified.
    /// </summary>
    public bool AutoFitText { get; set; }

    public float AutoFitMinimumSize { get; set; } = 8f;

    public bool OutlineEnabled { get; set; }

    /// <summary>Outline RGB; its alpha is ignored in favor of <see cref="OutlineOpacity"/>.</summary>
    public Vector4 OutlineColor { get; set; } = new(0f, 0f, 0f, 1f);

    public float OutlineThickness { get; set; } = 2f;

    public float OutlineOpacity { get; set; } = 1f;

    public bool ShadowEnabled { get; set; }

    /// <summary>Shadow RGB; its alpha is ignored in favor of <see cref="ShadowOpacity"/>.</summary>
    public Vector4 ShadowColor { get; set; } = new(0f, 0f, 0f, 1f);

    public float ShadowOpacity { get; set; } = 0.6f;

    public float ShadowOffsetX { get; set; } = 3f;

    public float ShadowOffsetY { get; set; } = 3f;

    /// <summary>
    /// Which text layout rules this element renders with (see <see cref="LegacyLayoutVersion"/>
    /// and <see cref="CurrentLayoutVersion"/>). Elements created in code start at the current
    /// version; an element DESERIALIZED without this field — every element saved before it
    /// existed — is set to the legacy version by <see cref="OnDeserializing"/>, so an old profile
    /// keeps its exact previous appearance just by loading, with nothing rewritten on disk. It
    /// only moves to the current version by an explicit user action (changing Wrap or Auto Fit,
    /// or "Use Current Layout" in the Inspector).
    /// </summary>
    public int LayoutVersion { get; set; } = CurrentLayoutVersion;

    /// <summary>True while this element renders with the legacy layout rules.</summary>
    internal bool UsesLegacyLayout => LayoutVersion < CurrentLayoutVersion;

    /// <summary>Whether text actually wraps: never under the legacy layout, which never did.</summary>
    internal bool EffectiveWrap => Wrap && !UsesLegacyLayout;

    /// <summary>Whether auto fit actually applies: a current-layout feature only.</summary>
    internal bool EffectiveAutoFit => AutoFitText && !UsesLegacyLayout;

    /// <summary>
    /// Runs before System.Text.Json populates a deserialized element: marks it legacy, which a
    /// "LayoutVersion" value present in the JSON then overwrites. (Property initializers can't
    /// express "absent means legacy", since they also run for deserialized objects.)
    /// </summary>
    void IJsonOnDeserializing.OnDeserializing() => LayoutVersion = LegacyLayoutVersion;

    internal override ProfileElement Clone()
    {
        var clone = CloneBaseInto(new TextProfileElement());
        clone.CopyTextPropertiesFrom(this);
        return clone;
    }

    internal override void CopyFrom(ProfileElement source)
    {
        base.CopyFrom(source);

        if (source is TextProfileElement text)
        {
            CopyTextPropertiesFrom(text);
        }
    }

    internal override bool ContentEquals(ProfileElement other) =>
        base.ContentEquals(other)
        && other is TextProfileElement o
        && Text == o.Text
        && FontSize.Equals(o.FontSize)
        && Color == o.Color
        && Alignment == o.Alignment
        && VerticalAlignment == o.VerticalAlignment
        && Wrap == o.Wrap
        && FontFamily == o.FontFamily
        && Bold == o.Bold
        && Italic == o.Italic
        && Underline == o.Underline
        && Strikethrough == o.Strikethrough
        && LetterSpacing.Equals(o.LetterSpacing)
        && LineSpacing.Equals(o.LineSpacing)
        && AutoFitText == o.AutoFitText
        && AutoFitMinimumSize.Equals(o.AutoFitMinimumSize)
        && OutlineEnabled == o.OutlineEnabled
        && OutlineColor == o.OutlineColor
        && OutlineThickness.Equals(o.OutlineThickness)
        && OutlineOpacity.Equals(o.OutlineOpacity)
        && ShadowEnabled == o.ShadowEnabled
        && ShadowColor == o.ShadowColor
        && ShadowOpacity.Equals(o.ShadowOpacity)
        && ShadowOffsetX.Equals(o.ShadowOffsetX)
        && ShadowOffsetY.Equals(o.ShadowOffsetY)
        && LayoutVersion == o.LayoutVersion;

    private void CopyTextPropertiesFrom(TextProfileElement text)
    {
        Text = text.Text;
        FontSize = text.FontSize;
        Color = text.Color;
        Alignment = text.Alignment;
        VerticalAlignment = text.VerticalAlignment;
        Wrap = text.Wrap;
        FontFamily = text.FontFamily;
        Bold = text.Bold;
        Italic = text.Italic;
        Underline = text.Underline;
        Strikethrough = text.Strikethrough;
        LetterSpacing = text.LetterSpacing;
        LineSpacing = text.LineSpacing;
        AutoFitText = text.AutoFitText;
        AutoFitMinimumSize = text.AutoFitMinimumSize;
        OutlineEnabled = text.OutlineEnabled;
        OutlineColor = text.OutlineColor;
        OutlineThickness = text.OutlineThickness;
        OutlineOpacity = text.OutlineOpacity;
        ShadowEnabled = text.ShadowEnabled;
        ShadowColor = text.ShadowColor;
        ShadowOpacity = text.ShadowOpacity;
        ShadowOffsetX = text.ShadowOffsetX;
        ShadowOffsetY = text.ShadowOffsetY;
        LayoutVersion = text.LayoutVersion;
    }
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

public enum TextVerticalAlignment
{
    Top,
    Middle,
    Bottom,
}
