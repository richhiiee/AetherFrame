using System.Numerics;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// A named two-color starting point for a <see cref="ProfileBackground"/>. Applying one only
/// copies its values into the profile's own background (see <see cref="ApplyTo"/>) — nothing
/// references the preset afterwards, so every property stays freely editable and a profile never
/// depends on a preset's definition continuing to exist.
///
/// <see cref="Id"/> is the stable identifier: what a Plate actually persists
/// (<see cref="BasicPlateSettings.ThemeId"/>), frozen forever once shipped, and never reused for a
/// different theme even if <see cref="Name"/> (the display label) or any color later changes. For
/// every theme shipped before this distinction existed, Id is defined equal to that theme's
/// original Name string, so already-saved Plates keep resolving to the same theme unchanged. Array
/// position in <see cref="ProfileThemePresets.All"/> is likewise never persisted or meant to be
/// depended on outside this file — resolve a theme by <see cref="Id"/>, never by index.
/// </summary>
public sealed record ProfileThemePreset(
    string Id,
    string Name,
    ThemeFamily Family,
    string Description,
    Vector4 PrimaryColor,
    Vector4 SecondaryColor,
    float GradientAngle,
    ProfileBackgroundTexture Texture,
    float TextureIntensity,
    Vector4 TextColor,
    Vector4 AccentTextColor,
    Vector4 SoftTextColor)
{
    // TextColor: primary text (the Details values). AccentTextColor: the title. SoftTextColor:
    // quieter secondary text. Chosen to read well over this theme's own background.

    /// <summary>
    /// The character name's own display color — the name is the Plate's focal element, so every
    /// built-in theme chooses it deliberately (see <see cref="WithName"/>). It is display text, not
    /// body text, and not the raw accent: a bright, vibrant color from the theme's own hue family
    /// (a luminous version of a dark accent), white or warm white only where white suits the
    /// theme (Monochrome, bright vivid gradients), and never a dark or muddy ink — on light themes a
    /// vivid mid-tone. A restrained dark outline (<see cref="NameOutlineColor"/>) keeps every one
    /// readable over light or dark backgrounds, gradients, patterns, images and Name Backings.
    /// Null only for a theme without a name treatment, which falls back to <see cref="TextColor"/>.
    /// </summary>
    public Vector4? NameColor { get; init; }

    /// <summary>The color of the name's subtle contrasting outline (a deep shade of the theme's hue, or near-black).</summary>
    public Vector4? NameOutlineColor { get; init; }

    /// <summary>How strong the name's outline is, 0 (barely there) to 1 (a firm halo): its thickness and opacity (see <c>BasicNameColor</c>).</summary>
    public float? NameOutlineStrength { get; init; }

    /// <summary>The automatic (theme-controlled) character name color.</summary>
    public Vector4 PreferredNameColor => NameColor ?? TextColor;

    /// <summary>The automatic name outline color: the theme's, else black around a light name and white around a dark one.</summary>
    public Vector4 PreferredNameOutlineColor => NameOutlineColor
        ?? (Luminance(PreferredNameColor) > 0.35f ? new Vector4(0f, 0f, 0f, 1f) : Vector4.One);

    /// <summary>The automatic name outline strength (0–1).</summary>
    public float PreferredNameOutlineStrength => System.Math.Clamp(NameOutlineStrength ?? 0.45f, 0f, 1f);

    /// <summary>This theme with its name treatment: display color, outline color, outline strength.</summary>
    public ProfileThemePreset WithName(Vector4 name, Vector4 outline, float strength) =>
        this with { NameColor = name, NameOutlineColor = outline, NameOutlineStrength = strength };

    private static float Luminance(Vector4 c)
    {
        static float Linear(float v) => v <= 0.03928f ? v / 12.92f : System.MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        return (0.2126f * Linear(c.X)) + (0.7152f * Linear(c.Y)) + (0.0722f * Linear(c.Z));
    }

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

/// <summary>
/// The five browsing families the Design category groups themes into. Not persisted anywhere — a
/// Plate only stores a theme's <see cref="ProfileThemePreset.Id"/>; family is purely how the
/// catalog is organized for browsing today, and could change without touching saved data.
/// </summary>
public enum ThemeFamily
{
    Classic,
    Pastel,
    Vibrant,
    Gradient,
    Special,
}

/// <summary>Curated presets and swatches shared by every editor's background controls.</summary>
public static class ProfileThemePresets
{
    /// <summary>Display order for grouping themes by family in the Design browser. Fixed and
    /// deterministic — not derived from <see cref="ThemeFamily"/>'s declaration order so the two
    /// can be reordered independently without silently reshuffling the browser.</summary>
    public static readonly ThemeFamily[] FamilyOrder =
    [
        ThemeFamily.Classic, ThemeFamily.Pastel, ThemeFamily.Vibrant, ThemeFamily.Gradient, ThemeFamily.Special,
    ];

    // The first 22 entries keep their original array positions from before Id/Family existed —
    // some existing tests and defaults reference them by index (e.g. All[0] as the default new-Plate
    // theme). Only field VALUES changed for the four redesigned entries below; nothing was removed,
    // inserted before, or reordered. New themes are appended after index 21.
    public static readonly ProfileThemePreset[] All =
    [
        new ProfileThemePreset("Royal", "Royal", ThemeFamily.Special, "Deep indigo and violet with a gold accent",
            Rgb(0x1E, 0x1B, 0x4B), Rgb(0x7C, 0x3A, 0xED), 135f, ProfileBackgroundTexture.SubtlePaper, 0.25f,
            Rgb(0xF5, 0xF1, 0xFF), Rgb(0xE8, 0xC5, 0x6B), Rgb(0xC4, 0xB8, 0xE8)).WithName(Rgb(0xFF, 0xD6, 0x6E), Rgb(0x1A, 0x10, 0x40), 0.5f),

        new ProfileThemePreset("Pastel", "Pastel", ThemeFamily.Pastel, "Soft pink and sky blue, gentle and light",
            Rgb(0xF6, 0xD5, 0xE6), Rgb(0xC7, 0xE3, 0xF8), 120f, ProfileBackgroundTexture.FineNoise, 0.18f,
            Rgb(0x3B, 0x2F, 0x4A), Rgb(0xC0, 0x5A, 0x8C), Rgb(0x6E, 0x66, 0x80)).WithName(Rgb(0xD8, 0x40, 0x8E), Rgb(0x3A, 0x0E, 0x2C), 0.4f),

        new ProfileThemePreset("Dark", "Dark", ThemeFamily.Classic, "Neutral near-black with a cool blue accent",
            Rgb(0x0E, 0x10, 0x14), Rgb(0x2B, 0x30, 0x3A), 90f, ProfileBackgroundTexture.FineNoise, 0.30f,
            Rgb(0xEE, 0xF0, 0xF4), Rgb(0x8F, 0xB8, 0xFF), Rgb(0x9A, 0xA0, 0xAC)).WithName(Rgb(0x8F, 0xBC, 0xFF), Rgb(0x05, 0x07, 0x0A), 0.45f),

        new ProfileThemePreset("Warm", "Warm", ThemeFamily.Gradient, "Cozy brown-to-orange fire glow",
            Rgb(0x7A, 0x2A, 0x12), Rgb(0xF2, 0xA2, 0x3A), 45f, ProfileBackgroundTexture.SubtlePaper, 0.25f,
            Rgb(0xFF, 0xF6, 0xE8), Rgb(0xFF, 0xD2, 0x7A), Rgb(0xF0, 0xCF, 0xB0)).WithName(Rgb(0xFF, 0xD0, 0x6A), Rgb(0x3A, 0x14, 0x06), 0.7f),

        new ProfileThemePreset("Cool", "Cool", ThemeFamily.Gradient, "Navy to bright cyan, crisp and cool",
            Rgb(0x0B, 0x2F, 0x4E), Rgb(0x3F, 0xB7, 0xE8), 110f, ProfileBackgroundTexture.DiagonalLines, 0.15f,
            Rgb(0xF0, 0xFA, 0xFF), Rgb(0x9C, 0xF0, 0xFF), Rgb(0xB8, 0xD4, 0xE6)).WithName(Rgb(0x8F, 0xEA, 0xFF), Rgb(0x06, 0x22, 0x3A), 0.6f),

        new ProfileThemePreset("Forest", "Forest", ThemeFamily.Classic, "Deep forest green with a golden accent",
            Rgb(0x0F, 0x2A, 0x1C), Rgb(0x4E, 0x8A, 0x5A), 100f, ProfileBackgroundTexture.SubtlePaper, 0.30f,
            Rgb(0xF2, 0xF5, 0xEC), Rgb(0xD9, 0xC2, 0x7A), Rgb(0xB5, 0xC9, 0xB0)).WithName(Rgb(0xA6, 0xE3, 0x8C), Rgb(0x0A, 0x1D, 0x13), 0.5f),

        new ProfileThemePreset("Monochrome", "Monochrome", ThemeFamily.Classic, "Pure black and gray, no color at all",
            Rgb(0x16, 0x16, 0x16), Rgb(0x9A, 0x9A, 0x9A), 90f, ProfileBackgroundTexture.Grid, 0.20f,
            Rgb(0xFA, 0xFA, 0xFA), Rgb(0xD0, 0xD0, 0xD0), Rgb(0xA8, 0xA8, 0xA8)).WithName(Rgb(0xFF, 0xFF, 0xFF), Rgb(0x10, 0x10, 0x10), 0.5f),

        // Redesigned: was too close to Dark (both near-black-to-blue-gray with a blue accent). Now
        // a starry night sky — Speckle stands in for stars, and the hue leans indigo-violet instead
        // of gray-blue.
        new ProfileThemePreset("Midnight", "Midnight", ThemeFamily.Special, "Starry indigo night sky",
            Rgb(0x09, 0x0B, 0x1A), Rgb(0x2A, 0x2F, 0x7A), 100f, ProfileBackgroundTexture.Speckle, 0.18f,
            Rgb(0xE8, 0xEC, 0xFA), Rgb(0xAF, 0xCB, 0xFF), Rgb(0x8A, 0x90, 0xC2)).WithName(Rgb(0xB4, 0xC4, 0xFF), Rgb(0x05, 0x06, 0x1A), 0.45f),

        new ProfileThemePreset("Sunrise", "Sunrise", ThemeFamily.Vibrant, "Bright coral and gold morning light",
            Rgb(0xFF, 0x7A, 0x59), Rgb(0xFF, 0xD3, 0x6E), 60f, ProfileBackgroundTexture.FineNoise, 0.15f,
            Rgb(0x3B, 0x1F, 0x12), Rgb(0x9A, 0x2E, 0x1F), Rgb(0x6E, 0x4A, 0x38)).WithName(Rgb(0xFF, 0xF3, 0xDC), Rgb(0x6A, 0x1A, 0x12), 0.75f),

        new ProfileThemePreset("Crimson", "Crimson", ThemeFamily.Special, "Near-black to deep red, gold accent",
            Rgb(0x2B, 0x0A, 0x10), Rgb(0x8E, 0x1B, 0x2E), 120f, ProfileBackgroundTexture.SubtlePaper, 0.28f,
            Rgb(0xFB, 0xEA, 0xEA), Rgb(0xE8, 0xB5, 0x4D), Rgb(0xC7, 0x9A, 0x9E)).WithName(Rgb(0xFF, 0x8C, 0x9C), Rgb(0x1F, 0x06, 0x0B), 0.5f),

        // Redesigned: was too close to Royal (both indigo/violet with a gold accent). Now leans
        // magenta-orchid with a rose accent instead of gold, a genuinely different jewel tone.
        new ProfileThemePreset("Amethyst", "Amethyst", ThemeFamily.Special, "Plum to orchid with a rose accent",
            Rgb(0x2B, 0x0F, 0x2E), Rgb(0xB8, 0x4F, 0xD1), 130f, ProfileBackgroundTexture.FineNoise, 0.20f,
            Rgb(0xFB, 0xEE, 0xFF), Rgb(0xFF, 0x9A, 0xD1), Rgb(0xC2, 0x9F, 0xC7)).WithName(Rgb(0xF2, 0xA8, 0xFF), Rgb(0x23, 0x0A, 0x26), 0.55f),

        new ProfileThemePreset("Ocean", "Ocean", ThemeFamily.Gradient, "Deep teal to bright aqua",
            Rgb(0x06, 0x2B, 0x33), Rgb(0x15, 0x7A, 0x8C), 100f, ProfileBackgroundTexture.DiagonalLines, 0.15f,
            Rgb(0xE8, 0xFB, 0xFF), Rgb(0x4F, 0xE0, 0xC7), Rgb(0x7F, 0xB3, 0xBD)).WithName(Rgb(0x7C, 0xF2, 0xDE), Rgb(0x03, 0x1E, 0x24), 0.5f),

        // Redesigned: was too close to Warm (both brown-to-orange with SubtlePaper). Now reads as
        // glowing coals and sparks in darkness — Speckle instead of a smooth fill.
        new ProfileThemePreset("Ember", "Ember", ThemeFamily.Special, "Glowing embers and sparks in the dark",
            Rgb(0x1A, 0x0D, 0x08), Rgb(0xD1, 0x35, 0x0C), 75f, ProfileBackgroundTexture.Speckle, 0.25f,
            Rgb(0xFF, 0xED, 0xE0), Rgb(0xFF, 0x9A, 0x3C), Rgb(0xC9, 0x8F, 0x6E)).WithName(Rgb(0xFF, 0xAE, 0x6B), Rgb(0x14, 0x08, 0x04), 0.5f),

        new ProfileThemePreset("Frost", "Frost", ThemeFamily.Pastel, "Pale icy blue, clean and cool",
            Rgb(0xE7, 0xF3, 0xF8), Rgb(0xB8, 0xDC, 0xE8), 100f, ProfileBackgroundTexture.FineNoise, 0.12f,
            Rgb(0x1B, 0x2E, 0x36), Rgb(0x2C, 0x7D, 0xA0), Rgb(0x5A, 0x7A, 0x87)).WithName(Rgb(0x1E, 0x88, 0xD0), Rgb(0x0A, 0x26, 0x38), 0.4f),

        new ProfileThemePreset("Sakura", "Sakura", ThemeFamily.Pastel, "Soft cherry blossom pink",
            Rgb(0xFD, 0xE7, 0xEF), Rgb(0xF7, 0xB8, 0xCF), 110f, ProfileBackgroundTexture.FineNoise, 0.10f,
            Rgb(0x4A, 0x25, 0x36), Rgb(0xD6, 0x54, 0x7E), Rgb(0x8C, 0x65, 0x77)).WithName(Rgb(0xE0, 0x40, 0x7A), Rgb(0x3A, 0x0C, 0x20), 0.4f),

        new ProfileThemePreset("Gold", "Gold", ThemeFamily.Special, "Near-black and rich gold, luxe and bold",
            Rgb(0x14, 0x11, 0x0A), Rgb(0xB8, 0x89, 0x2B), 90f, ProfileBackgroundTexture.Grid, 0.18f,
            Rgb(0xFB, 0xF3, 0xDE), Rgb(0xE8, 0xC5, 0x6B), Rgb(0xB8, 0xA9, 0x7D)).WithName(Rgb(0xF5, 0xCD, 0x62), Rgb(0x0E, 0x0B, 0x05), 0.5f),

        // Tweaked slightly cooler/steelier than before, for extra separation from Dark.
        new ProfileThemePreset("Slate", "Slate", ThemeFamily.Classic, "Cool steel-blue gray",
            Rgb(0x23, 0x27, 0x2E), Rgb(0x5A, 0x7A, 0x99), 90f, ProfileBackgroundTexture.Grid, 0.20f,
            Rgb(0xED, 0xF1, 0xF5), Rgb(0x7F, 0xB8, 0xD9), Rgb(0x9A, 0xA5, 0xAE)).WithName(Rgb(0x9E, 0xD4, 0xF5), Rgb(0x15, 0x18, 0x1D), 0.45f),

        // Redesigned: was too close to Forest (both dark-green-to-lighter-green with a gold accent
        // and SubtlePaper). Now a brighter, faceted jewel green with Grid standing in for facets.
        new ProfileThemePreset("Emerald", "Emerald", ThemeFamily.Special, "Faceted emerald green jewel tone",
            Rgb(0x08, 0x14, 0x10), Rgb(0x0F, 0xB8, 0x76), 115f, ProfileBackgroundTexture.Grid, 0.18f,
            Rgb(0xE8, 0xFB, 0xF1), Rgb(0xF5, 0xE6, 0xA8), Rgb(0x8F, 0xBF, 0xA5)).WithName(Rgb(0x5C, 0xF2, 0xA6), Rgb(0x04, 0x10, 0x0A), 0.55f),

        new ProfileThemePreset("Rosewood", "Rosewood", ThemeFamily.Classic, "Muted dusty rose-brown",
            Rgb(0x2B, 0x14, 0x14), Rgb(0x7A, 0x3E, 0x3E), 100f, ProfileBackgroundTexture.SubtlePaper, 0.25f,
            Rgb(0xF5, 0xE8, 0xE4), Rgb(0xD1, 0x9A, 0x8C), Rgb(0xB0, 0x88, 0x80)).WithName(Rgb(0xF7, 0xB3, 0xA3), Rgb(0x1E, 0x0D, 0x0D), 0.5f),

        new ProfileThemePreset("Lavender", "Lavender", ThemeFamily.Pastel, "Pale soft purple",
            Rgb(0xF1, 0xEA, 0xFB), Rgb(0xD8, 0xC5, 0xF0), 120f, ProfileBackgroundTexture.FineNoise, 0.10f,
            Rgb(0x3B, 0x2E, 0x4A), Rgb(0x8A, 0x5A, 0xC2), Rgb(0x7A, 0x6E, 0x8C)).WithName(Rgb(0x8E, 0x4F, 0xE0), Rgb(0x22, 0x10, 0x3C), 0.4f),

        // Redesigned: was too close to Warm (both brown-to-orange with SubtlePaper). Now a more
        // muted, rustier red-brown with Herringbone standing in for a tweed weave.
        new ProfileThemePreset("Autumn", "Autumn", ThemeFamily.Classic, "Rusty tweed browns and burnt orange",
            Rgb(0x2E, 0x1B, 0x12), Rgb(0xA8, 0x4A, 0x2E), 70f, ProfileBackgroundTexture.Herringbone, 0.20f,
            Rgb(0xFB, 0xEE, 0xDD), Rgb(0xC9, 0x8A, 0x5A), Rgb(0xB0, 0x92, 0x7E)).WithName(Rgb(0xFF, 0xB5, 0x74), Rgb(0x1F, 0x12, 0x0B), 0.5f),

        // Tweaked: the automated differentiation check flagged this as too close to Dark (both
        // near-black-to-gray). Deepened the secondary into a clearly violet volcanic glass instead
        // of a barely-tinted gray, while staying much darker/moodier than Royal/Amethyst/Midnight.
        new ProfileThemePreset("Obsidian", "Obsidian", ThemeFamily.Special, "Near-black with a deep violet glass sheen",
            Rgb(0x08, 0x07, 0x0A), Rgb(0x45, 0x2A, 0x66), 100f, ProfileBackgroundTexture.FineNoise, 0.35f,
            Rgb(0xED, 0xE8, 0xF5), Rgb(0x9A, 0x7F, 0xD9), Rgb(0x88, 0x80, 0xA0)).WithName(Rgb(0xC6, 0xAB, 0xFF), Rgb(0x05, 0x04, 0x07), 0.45f),

        // ---------------------------------------------------------------- new themes (indices 22+)

        // Classic had no light option at all — every entry above is dark or medium-dark.
        new ProfileThemePreset("Ivory", "Ivory", ThemeFamily.Classic, "Warm cream and beige, quietly elegant",
            Rgb(0xF5, 0xF0, 0xE4), Rgb(0xE0, 0xD5, 0xC0), 100f, ProfileBackgroundTexture.SubtlePaper, 0.15f,
            Rgb(0x2E, 0x28, 0x1E), Rgb(0x8A, 0x6E, 0x3E), Rgb(0x7A, 0x70, 0x60)).WithName(Rgb(0xC8, 0x84, 0x1E), Rgb(0x3A, 0x24, 0x10), 0.45f),

        // Tweaked: the automated differentiation check flagged this as too close to Frost (both pale,
        // high-lightness pastels cluster in RGB distance regardless of hue). Pushed more saturated
        // and distinctly green rather than blue-adjacent.
        new ProfileThemePreset("Mint", "Mint", ThemeFamily.Pastel, "Pale cool mint green",
            Rgb(0xD9, 0xF7, 0xE8), Rgb(0x9E, 0xE8, 0xC4), 110f, ProfileBackgroundTexture.FineNoise, 0.10f,
            Rgb(0x1E, 0x3B, 0x30), Rgb(0x3A, 0x8A, 0x6C), Rgb(0x6E, 0x8C, 0x80)).WithName(Rgb(0x12, 0xA8, 0x7C), Rgb(0x05, 0x26, 0x1B), 0.4f),

        new ProfileThemePreset("Peach", "Peach", ThemeFamily.Pastel, "Soft warm peach",
            Rgb(0xFD, 0xE9, 0xDC), Rgb(0xF7, 0xC5, 0xA8), 110f, ProfileBackgroundTexture.FineNoise, 0.10f,
            Rgb(0x4A, 0x2E, 0x1F), Rgb(0xD1, 0x6E, 0x3E), Rgb(0x8C, 0x6E, 0x5E)).WithName(Rgb(0xEE, 0x5F, 0x32), Rgb(0x3E, 0x14, 0x08), 0.4f),

        new ProfileThemePreset("Neon", "Neon", ThemeFamily.Vibrant, "Electric cyan to magenta",
            Rgb(0x0A, 0xC7, 0xD1), Rgb(0xE0, 0x2A, 0x9C), 60f, ProfileBackgroundTexture.Waves, 0.15f,
            Rgb(0x12, 0x10, 0x1C), Rgb(0xFF, 0xE8, 0x4D), Rgb(0x3A, 0x38, 0x45)).WithName(Rgb(0xFF, 0xF1, 0x5C), Rgb(0x1E, 0x0B, 0x4B), 0.75f),

        new ProfileThemePreset("Citrus", "Citrus", ThemeFamily.Vibrant, "Punchy lime and orange",
            Rgb(0xEA, 0xE0, 0x2E), Rgb(0xFF, 0x7A, 0x1A), 50f, ProfileBackgroundTexture.Speckle, 0.12f,
            Rgb(0x2E, 0x1A, 0x08), Rgb(0x9C, 0x2E, 0x0A), Rgb(0x6E, 0x4A, 0x1E)).WithName(Rgb(0xFF, 0xFB, 0xEA), Rgb(0x5A, 0x1E, 0x06), 0.75f),

        new ProfileThemePreset("Tropical", "Tropical", ThemeFamily.Vibrant, "Turquoise to coral sunset",
            Rgb(0x0A, 0xC9, 0xB8), Rgb(0xFF, 0x4D, 0x6E), 100f, ProfileBackgroundTexture.Waves, 0.15f,
            Rgb(0x0C, 0x1E, 0x1C), Rgb(0xFF, 0xE0, 0x4D), Rgb(0x2E, 0x4A, 0x48)).WithName(Rgb(0xFF, 0xF8, 0xE6), Rgb(0x08, 0x3A, 0x3E), 0.75f),

        new ProfileThemePreset("Twilight", "Twilight", ThemeFamily.Gradient, "Purple dusk into burnt-orange sunset",
            Rgb(0x2E, 0x1A, 0x4D), Rgb(0xE8, 0x6A, 0x3C), 100f, ProfileBackgroundTexture.FineNoise, 0.15f,
            Rgb(0xFB, 0xF0, 0xE8), Rgb(0xFF, 0xC9, 0x7A), Rgb(0xC9, 0xA8, 0xAE)).WithName(Rgb(0xFF, 0xC3, 0x88), Rgb(0x1C, 0x0F, 0x30), 0.6f),

        new ProfileThemePreset("Aurora", "Aurora", ThemeFamily.Gradient, "Teal to blue-violet, aurora-like",
            Rgb(0x0A, 0x3D, 0x2E), Rgb(0x4F, 0x6A, 0xE0), 120f, ProfileBackgroundTexture.Waves, 0.15f,
            Rgb(0xE8, 0xF5, 0xF0), Rgb(0x7A, 0xE8, 0xC2), Rgb(0x9A, 0xB8, 0xC2)).WithName(Rgb(0x8C, 0xF5, 0xD0), Rgb(0x07, 0x25, 0x1C), 0.55f),
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

    /// <summary>The theme with this stable <see cref="ProfileThemePreset.Id"/>, or null if unknown
    /// (a Plate saved by a future build, or an empty/never-set id).</summary>
    public static ProfileThemePreset? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : System.Array.Find(All, p => p.Id == id);

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
