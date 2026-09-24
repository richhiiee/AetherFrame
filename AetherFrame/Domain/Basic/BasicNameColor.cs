using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// The character name's theme styling in a Basic Plate. The name is the Plate's focal element, so
/// each theme gives it a dedicated treatment (<see cref="ProfileThemePreset.PreferredNameColor"/>,
/// <see cref="ProfileThemePreset.PreferredNameOutlineColor"/>, <see cref="ProfileThemePreset.PreferredNameOutlineStrength"/>):
/// a display color plus a subtle contrasting outline that keeps it readable over gradients,
/// patterns, images and Name Backings.
///
/// <para><b>Automatic or custom.</b> Nothing extra is stored. A name whose color is one its theme
/// has ever given it automatically is automatic: the current treatment, the first name treatment
/// (<see cref="FirstTreatment"/>: near-white on dark themes, a deep ink on light ones), the
/// theme's <see cref="ProfileThemePreset.TextColor"/> (what every name used before themes had a
/// name treatment), or the fixed <see cref="IdentityHeaderWhite"/> the first Identity Header gave
/// every name regardless of theme. Any other color is the player's (e.g. chosen
/// in the Advanced editor's Inspector) and is never changed by a theme. An automatic name's outline
/// follows the theme too, unless the player has set their own (enabled with a color that isn't the
/// theme's). Resetting (<see cref="Reset"/>, or resetting the name's style) makes the whole
/// treatment automatic again. Opacity (the color's alpha) is always the player's.</para>
/// </summary>
public static class BasicNameColor
{
    // Colors are compared per channel within this, well under one 8-bit step.
    private const float Tolerance = 0.5f / 255f;

    // Outline strength 0..1 maps to these (thickness in reference-canvas pixels, scaled with the Plate).
    private const float MinOutlineThickness = 0.8f;
    private const float OutlineThicknessRange = 1.4f;
    private const float MinOutlineOpacity = 0.3f;
    private const float OutlineOpacityRange = 0.55f;

    /// <summary>The automatic name color for <paramref name="theme"/>.</summary>
    public static Vector4 Automatic(ProfileThemePreset theme) => theme.PreferredNameColor;

    /// <summary>The automatic outline thickness (canvas units) for a theme's strength on <paramref name="profile"/>'s canvas.</summary>
    public static float OutlineThickness(ProfileThemePreset theme, ProfileDocument profile) =>
        MathF.Round((MinOutlineThickness + (OutlineThicknessRange * theme.PreferredNameOutlineStrength)) * AdventurePlateClassicLayout.FontScale(profile), 2);

    /// <summary>The automatic outline opacity for a theme's strength.</summary>
    public static float OutlineOpacity(ProfileThemePreset theme) =>
        MathF.Round(MinOutlineOpacity + (OutlineOpacityRange * theme.PreferredNameOutlineStrength), 3);

    /// <summary>The fixed name color the first Identity Header gave every name, whatever the theme (never a player's choice).</summary>
    public static readonly Vector4 IdentityHeaderWhite = new(0.96f, 0.96f, 0.97f, 1f);

    /// <summary>
    /// Each built-in theme's first name treatment (name color, outline color), superseded because it
    /// resolved to near-white on dark themes and a dark ink on light ones. Frozen: Plates saved with
    /// it are recognised as automatic and upgraded, never mistaken for a custom color.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (Vector4 Name, Vector4 Outline)> FirstTreatment =
        new Dictionary<string, (Vector4 Name, Vector4 Outline)>
        {
            ["Royal"] = (Rgb(0xEF, 0xE6, 0xFF), Rgb(0x14, 0x0F, 0x36)),
            ["Pastel"] = (Rgb(0x7A, 0x2E, 0x8C), Rgb(0xFF, 0xFF, 0xFF)),
            ["Dark"] = (Rgb(0xF2, 0xF5, 0xFA), Rgb(0x05, 0x07, 0x0A)),
            ["Warm"] = (Rgb(0xFF, 0xF1, 0xD6), Rgb(0x3A, 0x14, 0x06)),
            ["Cool"] = (Rgb(0xE2, 0xF6, 0xFF), Rgb(0x06, 0x22, 0x3A)),
            ["Forest"] = (Rgb(0xEA, 0xF3, 0xDA), Rgb(0x0A, 0x1D, 0x13)),
            ["Monochrome"] = (Rgb(0xFF, 0xFF, 0xFF), Rgb(0x10, 0x10, 0x10)),
            ["Midnight"] = (Rgb(0xE6, 0xEB, 0xFF), Rgb(0x05, 0x06, 0x1A)),
            ["Sunrise"] = (Rgb(0x7E, 0x15, 0x30), Rgb(0xFF, 0xF1, 0xDC)),
            ["Crimson"] = (Rgb(0xFF, 0xE6, 0xE8), Rgb(0x1F, 0x06, 0x0B)),
            ["Amethyst"] = (Rgb(0xF8, 0xE2, 0xFF), Rgb(0x23, 0x0A, 0x26)),
            ["Ocean"] = (Rgb(0xDD, 0xF8, 0xFF), Rgb(0x03, 0x1E, 0x24)),
            ["Ember"] = (Rgb(0xFF, 0xE7, 0xD4), Rgb(0x14, 0x08, 0x04)),
            ["Frost"] = (Rgb(0x0E, 0x5A, 0x8A), Rgb(0xFF, 0xFF, 0xFF)),
            ["Sakura"] = (Rgb(0x8C, 0x1F, 0x4F), Rgb(0xFF, 0xFF, 0xFF)),
            ["Gold"] = (Rgb(0xFF, 0xED, 0xC8), Rgb(0x0E, 0x0B, 0x05)),
            ["Slate"] = (Rgb(0xF0, 0xF4, 0xF8), Rgb(0x15, 0x18, 0x1D)),
            ["Emerald"] = (Rgb(0xDD, 0xFA, 0xEB), Rgb(0x04, 0x10, 0x09)),
            ["Rosewood"] = (Rgb(0xF8, 0xE3, 0xDC), Rgb(0x1E, 0x0D, 0x0D)),
            ["Lavender"] = (Rgb(0x5B, 0x2A, 0x9C), Rgb(0xFF, 0xFF, 0xFF)),
            ["Autumn"] = (Rgb(0xFF, 0xEA, 0xD2), Rgb(0x1F, 0x12, 0x0B)),
            ["Obsidian"] = (Rgb(0xED, 0xE4, 0xFA), Rgb(0x05, 0x04, 0x07)),
            ["Ivory"] = (Rgb(0x7A, 0x3B, 0x1E), Rgb(0xFF, 0xFD, 0xF6)),
            ["Mint"] = (Rgb(0x0F, 0x6B, 0x4F), Rgb(0xFF, 0xFF, 0xFF)),
            ["Peach"] = (Rgb(0x9A, 0x2F, 0x1C), Rgb(0xFF, 0xFF, 0xFF)),
            ["Neon"] = (Rgb(0x1E, 0x0B, 0x4B), Rgb(0xFF, 0xFF, 0xFF)),
            ["Citrus"] = (Rgb(0x6A, 0x1B, 0x4D), Rgb(0xFF, 0xF8, 0xD6)),
            ["Tropical"] = (Rgb(0x06, 0x2E, 0x3A), Rgb(0xFF, 0xF6, 0xE0)),
            ["Twilight"] = (Rgb(0xFF, 0xEA, 0xDC), Rgb(0x1C, 0x0F, 0x30)),
            ["Aurora"] = (Rgb(0xDE, 0xF8, 0xEE), Rgb(0x07, 0x25, 0x1C)),
        };

    /// <summary>True when <paramref name="color"/> is (one of) <paramref name="theme"/>'s automatic name colors, current or legacy.</summary>
    public static bool IsAutomatic(Vector4 color, ProfileThemePreset theme) =>
        SameRgb(color, theme.PreferredNameColor) || IsLegacyAutomatic(color, theme);

    /// <summary>True when <paramref name="color"/> is an automatic name color <paramref name="theme"/> no longer uses.</summary>
    public static bool IsLegacyAutomatic(Vector4 color, ProfileThemePreset theme) =>
        !SameRgb(color, theme.PreferredNameColor)
        && (SameRgb(color, theme.TextColor)
            || SameRgb(color, IdentityHeaderWhite)
            || (FirstTreatment.TryGetValue(theme.Id, out var first) && SameRgb(color, first.Name)));

    /// <summary>True when the Plate has a name and its color follows the Plate's theme.</summary>
    public static bool IsAutomatic(ProfileDocument profile) =>
        FindName(profile) is { } name && IsAutomatic(name.Color, AdventurePlateClassicLayout.ResolveTheme(profile));

    /// <summary>True when the name's outline is the theme's (or absent), so the theme may restyle it.</summary>
    public static bool OutlineFollows(TextProfileElement name, ProfileThemePreset theme) =>
        !name.OutlineEnabled
        || SameRgb(name.OutlineColor, theme.PreferredNameOutlineColor)
        || (FirstTreatment.TryGetValue(theme.Id, out var first) && SameRgb(name.OutlineColor, first.Outline));

    /// <summary>Gives <paramref name="name"/> <paramref name="theme"/>'s full automatic treatment (color and outline; opacity kept).</summary>
    public static void ApplyAutomatic(TextProfileElement name, ProfileThemePreset theme, ProfileDocument profile)
    {
        name.Color = Automatic(theme) with { W = name.Color.W };
        ApplyOutline(name, theme, profile);
    }

    /// <summary>
    /// Switching from <paramref name="from"/> to <paramref name="to"/>: an automatic name takes
    /// <paramref name="to"/>'s color (keeping its opacity) and, unless the player set their own
    /// outline, its outline; a custom name is left exactly as it is.
    /// </summary>
    public static void ApplyThemeChange(TextProfileElement name, ProfileThemePreset from, ProfileThemePreset to, ProfileDocument profile)
    {
        if (!IsAutomatic(name.Color, from))
        {
            return;
        }

        var outlineFollows = OutlineFollows(name, from);
        name.Color = Automatic(to) with { W = name.Color.W };
        if (outlineFollows)
        {
            ApplyOutline(name, to, profile);
        }
    }

    /// <summary>Makes the Plate's name styling automatic again (color and outline; opacity kept). False when there's no name or nothing changed.</summary>
    public static bool Reset(ProfileDocument profile)
    {
        if (FindName(profile) is not { } name)
        {
            return false;
        }

        var theme = AdventurePlateClassicLayout.ResolveTheme(profile);
        var before = (name.Color, name.OutlineEnabled, name.OutlineColor, name.OutlineThickness, name.OutlineOpacity);
        ApplyAutomatic(name, theme, profile);
        return before != (name.Color, name.OutlineEnabled, name.OutlineColor, name.OutlineThickness, name.OutlineOpacity);
    }

    /// <summary>
    /// Loading: a Basic Plate whose name still has an older automatic color (see
    /// <see cref="IsLegacyAutomatic"/>) gets the theme's current treatment — its outline too, unless
    /// the player had set their own. Custom colors, names already on
    /// the current treatment, and Plates that aren't Basic Plates are untouched. Returns whether
    /// anything changed.
    /// </summary>
    public static bool UpgradeLegacy(ProfileDocument profile)
    {
        if (profile.BasicPlate is null || FindName(profile) is not { } name)
        {
            return false;
        }

        var theme = AdventurePlateClassicLayout.ResolveTheme(profile);
        if (!IsLegacyAutomatic(name.Color, theme))
        {
            return false;
        }

        var outlineFollows = OutlineFollows(name, theme);
        name.Color = theme.PreferredNameColor with { W = name.Color.W };
        if (outlineFollows)
        {
            ApplyOutline(name, theme, profile);
        }

        return true;
    }

    private static void ApplyOutline(TextProfileElement name, ProfileThemePreset theme, ProfileDocument profile)
    {
        name.OutlineEnabled = theme.PreferredNameOutlineStrength > 0f;
        name.OutlineColor = theme.PreferredNameOutlineColor with { W = 1f };
        name.OutlineThickness = OutlineThickness(theme, profile);
        name.OutlineOpacity = OutlineOpacity(theme);

        // The name's drop shadow goes with its outline: full depth behind a dark outline, softened
        // behind a light halo (where a dark shadow would smudge it). Only an automatic shadow opacity
        // is adjusted — one the player set stays theirs.
        if (name.ShadowEnabled && (Near(name.ShadowOpacity, DarkOutlineShadowOpacity) || Near(name.ShadowOpacity, HaloShadowOpacity)))
        {
            name.ShadowOpacity = IsLightHalo(theme) ? HaloShadowOpacity : DarkOutlineShadowOpacity;
        }
    }

    /// <summary>The name's shadow opacity behind a dark outline (the Identity Header's default).</summary>
    public const float DarkOutlineShadowOpacity = 0.45f;

    /// <summary>The name's shadow opacity behind a light halo.</summary>
    public const float HaloShadowOpacity = 0.2f;

    /// <summary>True when the theme's name outline is a light halo (around a dark or saturated name).</summary>
    public static bool IsLightHalo(ProfileThemePreset theme) =>
        Components.ComponentDefinition.RelativeLuminance(theme.PreferredNameOutlineColor) > 0.5f;

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

    private static TextProfileElement? FindName(ProfileDocument profile) =>
        BasicSections.Find(profile, ProfileElementRole.BasicName) as TextProfileElement;

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private static bool SameRgb(Vector4 a, Vector4 b) =>
        Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance && Math.Abs(a.Z - b.Z) <= Tolerance;
}
