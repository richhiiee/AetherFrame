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

    internal override ProfileElement Clone() => new TextProfileElement
    {
        Id = Id,
        Visible = Visible,
        Locked = Locked,
        Position = Position,
        Size = Size,
        ZIndex = ZIndex,
        Text = Text,
        FontSize = FontSize,
        Color = Color,
        Alignment = Alignment,
        Wrap = Wrap,
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
        }
    }
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}
