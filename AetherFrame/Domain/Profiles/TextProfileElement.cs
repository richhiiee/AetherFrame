using System.Numerics;

namespace AetherFrame.Domain.Profiles;

public sealed class TextProfileElement : ProfileElement
{
    public const int MaxTextLength = 2000;
    public const float MinFontSize = 6f;
    public const float MaxFontSize = 96f;

    public string Text { get; set; } = string.Empty;

    public float FontSize { get; set; } = 16f;

    public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);

    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

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

    internal override ProfileElement Clone() => new TextProfileElement
    {
        Id = Id,
        Visible = Visible,
        Locked = Locked,
        Position = Position,
        Size = Size,
        ZIndex = ZIndex,
        Role = Role,
        Text = Text,
        FontSize = FontSize,
        Color = Color,
        Alignment = Alignment,
        Wrap = Wrap,
        FontFamily = FontFamily,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough,
    };

    internal override void CopyFrom(ProfileElement source)
    {
        base.CopyFrom(source);

        if (source is TextProfileElement text)
        {
            Text = text.Text;
            FontSize = text.FontSize;
            Color = text.Color;
            Alignment = text.Alignment;
            Wrap = text.Wrap;
            FontFamily = text.FontFamily;
            Bold = text.Bold;
            Italic = text.Italic;
            Underline = text.Underline;
            Strikethrough = text.Strikethrough;
        }
    }
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}
