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
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}
