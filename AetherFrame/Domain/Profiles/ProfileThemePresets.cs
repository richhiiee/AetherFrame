using System.Numerics;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// A named two-color starting point for a <see cref="ProfileBackground"/>. Applying one only
/// copies its values into the profile's own background (see <see cref="ApplyTo"/>) — nothing
/// references the preset afterwards, so every property stays freely editable and a profile never
/// depends on a preset's definition continuing to exist.
/// </summary>
public sealed record ProfileThemePreset(
    string Name,
    Vector4 PrimaryColor,
    Vector4 SecondaryColor,
    float GradientAngle,
    ProfileBackgroundTexture Texture,
    float TextureIntensity,
    Vector4 TextColor,
    Vector4 AccentTextColor,
    Vector4 SoftTextColor)
{
    // TextColor: primary text (the character name). AccentTextColor: the title. SoftTextColor:
    // quieter secondary text (the tagline). Chosen to read well over this theme's own background.

    /// <summary>
    /// Copies this preset's colors and suggested defaults into <paramref name="background"/>.
    /// Solid/gradient/texture modes keep their mode (so a preset recolors what the user already
    /// chose); None and Image switch to a linear gradient so the preset is actually visible —
    /// the image asset itself is kept, so switching back to Image restores it.
    /// </summary>
    public void ApplyTo(ProfileBackground background)
    {
        background.PrimaryColor = PrimaryColor;
        background.SecondaryColor = SecondaryColor;
        background.GradientAngle = GradientAngle;
        background.Texture = Texture;
        background.TextureIntensity = TextureIntensity;

        if (background.Mode is ProfileBackgroundMode.None or ProfileBackgroundMode.Image)
        {
            background.Mode = ProfileBackgroundMode.LinearGradient;
        }
    }
}

/// <summary>Curated presets and swatches shared by every editor's background controls.</summary>
public static class ProfileThemePresets
{
    public static readonly ProfileThemePreset[] All =
    [
        new("Royal", Rgb(0x1E, 0x1B, 0x4B), Rgb(0x7C, 0x3A, 0xED), 135f, ProfileBackgroundTexture.SubtlePaper, 0.25f,
            Rgb(0xF5, 0xF1, 0xFF), Rgb(0xE8, 0xC5, 0x6B), Rgb(0xC4, 0xB8, 0xE8)),
        new("Pastel", Rgb(0xF6, 0xD5, 0xE6), Rgb(0xC7, 0xE3, 0xF8), 120f, ProfileBackgroundTexture.FineNoise, 0.18f,
            Rgb(0x3B, 0x2F, 0x4A), Rgb(0xC0, 0x5A, 0x8C), Rgb(0x6E, 0x66, 0x80)),
        new("Dark", Rgb(0x0E, 0x10, 0x14), Rgb(0x2B, 0x30, 0x3A), 90f, ProfileBackgroundTexture.FineNoise, 0.30f,
            Rgb(0xEE, 0xF0, 0xF4), Rgb(0x8F, 0xB8, 0xFF), Rgb(0x9A, 0xA0, 0xAC)),
        new("Warm", Rgb(0x7A, 0x2A, 0x12), Rgb(0xF2, 0xA2, 0x3A), 45f, ProfileBackgroundTexture.SubtlePaper, 0.25f,
            Rgb(0xFF, 0xF6, 0xE8), Rgb(0xFF, 0xD2, 0x7A), Rgb(0xF0, 0xCF, 0xB0)),
        new("Cool", Rgb(0x0B, 0x2F, 0x4E), Rgb(0x3F, 0xB7, 0xE8), 110f, ProfileBackgroundTexture.DiagonalLines, 0.15f,
            Rgb(0xF0, 0xFA, 0xFF), Rgb(0x9C, 0xF0, 0xFF), Rgb(0xB8, 0xD4, 0xE6)),
        new("Forest", Rgb(0x0F, 0x2A, 0x1C), Rgb(0x4E, 0x8A, 0x5A), 100f, ProfileBackgroundTexture.SubtlePaper, 0.30f,
            Rgb(0xF2, 0xF5, 0xEC), Rgb(0xD9, 0xC2, 0x7A), Rgb(0xB5, 0xC9, 0xB0)),
        new("Monochrome", Rgb(0x16, 0x16, 0x16), Rgb(0x9A, 0x9A, 0x9A), 90f, ProfileBackgroundTexture.Grid, 0.20f,
            Rgb(0xFA, 0xFA, 0xFA), Rgb(0xD0, 0xD0, 0xD0), Rgb(0xA8, 0xA8, 0xA8)),
    ];

    /// <summary>Quick-pick swatches for solid colors: neutrals, then a hue wheel of muted and
    /// saturated tones that read well behind light or dark profile text.</summary>
    public static readonly Vector4[] SolidSwatches =
    [
        Rgb(0xFF, 0xFF, 0xFF), Rgb(0xD9, 0xD9, 0xDE), Rgb(0x8E, 0x90, 0x99), Rgb(0x3A, 0x3C, 0x44),
        Rgb(0x1B, 0x1C, 0x22), Rgb(0x0A, 0x0A, 0x0C), Rgb(0xF4, 0xEB, 0xD9), Rgb(0xC9, 0xA2, 0x4D),
        Rgb(0x8E, 0x1B, 0x2E), Rgb(0xD9, 0x4F, 0x4F), Rgb(0xE8, 0x8A, 0x3C), Rgb(0x6B, 0x8E, 0x4E),
        Rgb(0x1F, 0x6F, 0x5C), Rgb(0x2C, 0x7D, 0xA0), Rgb(0x2B, 0x3F, 0x8C), Rgb(0x5B, 0x3A, 0x8E),
    ];

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
