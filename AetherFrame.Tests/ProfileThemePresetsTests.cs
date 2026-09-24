using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The curated theme library: every preset must have a unique, stable <see cref="ProfileThemePreset.Id"/>
/// (what a saved Plate actually persists — see <c>BasicPlateSettings.ThemeId</c>) that never depends
/// on <see cref="ProfileThemePreset.Name"/> or its position in <see cref="ProfileThemePresets.All"/>,
/// fully opaque colors (the linear gradient math in <c>ProfileBackgroundRenderer</c> assumes opaque
/// endpoint colors and applies transparency only through the background's own Opacity), and exactly
/// one <see cref="ThemeFamily"/> for the Design browser to group it under.
/// </summary>
public class ProfileThemePresetsTests
{
    // The 22 themes that shipped before Id/Family existed. Their Id must equal this exact original
    // Name string forever — that's the whole back-compat mechanism: a Plate saved before this split
    // stored the Name as ThemeName (now ThemeId), and must keep resolving to the same theme.
    private static readonly string[] LegacyThemeIds =
    [
        "Royal", "Pastel", "Dark", "Warm", "Cool", "Forest", "Monochrome", "Midnight", "Sunrise",
        "Crimson", "Amethyst", "Ocean", "Ember", "Frost", "Sakura", "Gold", "Slate", "Emerald",
        "Rosewood", "Lavender", "Autumn", "Obsidian",
    ];

    private static readonly Dictionary<string, ThemeFamily> ExpectedFamily = new()
    {
        ["Royal"] = ThemeFamily.Special,
        ["Pastel"] = ThemeFamily.Pastel,
        ["Dark"] = ThemeFamily.Classic,
        ["Warm"] = ThemeFamily.Gradient,
        ["Cool"] = ThemeFamily.Gradient,
        ["Forest"] = ThemeFamily.Classic,
        ["Monochrome"] = ThemeFamily.Classic,
        ["Midnight"] = ThemeFamily.Special,
        ["Sunrise"] = ThemeFamily.Vibrant,
        ["Crimson"] = ThemeFamily.Special,
        ["Amethyst"] = ThemeFamily.Special,
        ["Ocean"] = ThemeFamily.Gradient,
        ["Ember"] = ThemeFamily.Special,
        ["Frost"] = ThemeFamily.Pastel,
        ["Sakura"] = ThemeFamily.Pastel,
        ["Gold"] = ThemeFamily.Special,
        ["Slate"] = ThemeFamily.Classic,
        ["Emerald"] = ThemeFamily.Special,
        ["Rosewood"] = ThemeFamily.Classic,
        ["Lavender"] = ThemeFamily.Pastel,
        ["Autumn"] = ThemeFamily.Classic,
        ["Obsidian"] = ThemeFamily.Special,
        ["Ivory"] = ThemeFamily.Classic,
        ["Mint"] = ThemeFamily.Pastel,
        ["Peach"] = ThemeFamily.Pastel,
        ["Neon"] = ThemeFamily.Vibrant,
        ["Citrus"] = ThemeFamily.Vibrant,
        ["Tropical"] = ThemeFamily.Vibrant,
        ["Twilight"] = ThemeFamily.Gradient,
        ["Aurora"] = ThemeFamily.Gradient,
    };

    public static TheoryData<ProfileThemePreset> AllThemes() => new(ProfileThemePresets.All);

    public static TheoryData<string> LegacyIds() => new(LegacyThemeIds);

    // ---------------------------------------------------------------- catalog size (task: ~30)

    [Fact]
    public void CatalogSize_IsInTheRequestedRange()
    {
        Assert.InRange(ProfileThemePresets.All.Length, 28, 34);
        Assert.Equal(30, ProfileThemePresets.All.Length);
    }

    // ---------------------------------------------------------------- stable ids (task: verify/test)

    [Fact]
    public void EveryTheme_HasAUniqueId()
    {
        var ids = ProfileThemePresets.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    [Fact]
    public void EveryTheme_HasAUniqueName()
    {
        var names = ProfileThemePresets.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Theory]
    [MemberData(nameof(LegacyIds))]
    public void ALegacyTheme_HasAnIdEqualToItsOriginalName(string originalName)
    {
        // Pins the back-compat contract itself: renaming a theme's display Name later must never
        // change what a Plate saved today resolves to, because ThemeId still holds this exact string.
        var preset = ProfileThemePresets.Find(originalName);
        Assert.NotNull(preset);
        Assert.Equal(originalName, preset!.Id);
    }

    [Fact]
    public void Find_ResolvesByIdRegardlessOfArrayPosition()
    {
        foreach (var preset in ProfileThemePresets.All)
        {
            Assert.Same(preset, ProfileThemePresets.Find(preset.Id));
        }
    }

    [Fact]
    public void Find_ReturnsNull_ForUnknownOrEmptyId()
    {
        Assert.Null(ProfileThemePresets.Find(""));
        Assert.Null(ProfileThemePresets.Find(null));
        Assert.Null(ProfileThemePresets.Find("a theme from some future build"));
    }

    // ---------------------------------------------------------------- families (task: organize into families)

    [Fact]
    public void EveryTheme_BelongsToExactlyOneKnownFamily()
    {
        var known = new HashSet<ThemeFamily>(ProfileThemePresets.FamilyOrder);
        foreach (var preset in ProfileThemePresets.All)
        {
            Assert.Contains(preset.Family, known);
        }
    }

    [Fact]
    public void EveryFamily_HasAtLeastOneTheme()
    {
        foreach (var family in ProfileThemePresets.FamilyOrder)
        {
            Assert.True(ProfileThemePresets.All.Any(p => p.Family == family), $"{family} has no themes.");
        }
    }

    [Fact]
    public void FamilyAssignments_MatchTheCuratedMap()
    {
        foreach (var preset in ProfileThemePresets.All)
        {
            Assert.True(ExpectedFamily.TryGetValue(preset.Name, out var expected), $"{preset.Name} is missing from the test's expected-family map.");
            Assert.Equal(expected, preset.Family);
        }

        // And the reverse: nothing in the map names a theme that no longer exists.
        foreach (var name in ExpectedFamily.Keys)
        {
            Assert.Contains(ProfileThemePresets.All, p => p.Name == name);
        }
    }

    [Fact]
    public void FamilyOrder_IsDeterministic()
    {
        ThemeFamily[] expected = [ThemeFamily.Classic, ThemeFamily.Pastel, ThemeFamily.Vibrant, ThemeFamily.Gradient, ThemeFamily.Special];
        Assert.Equal(expected, ProfileThemePresets.FamilyOrder);
    }

    // ---------------------------------------------------------------- visual/product invariants

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryTheme_HasFullyOpaqueColors(ProfileThemePreset preset)
    {
        Assert.Equal(1f, preset.PrimaryColor.W);
        Assert.Equal(1f, preset.SecondaryColor.W);
        Assert.Equal(1f, preset.TextColor.W);
        Assert.Equal(1f, preset.AccentTextColor.W);
        Assert.Equal(1f, preset.SoftTextColor.W);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryTheme_HasAValidGradientAngleAndIntensity(ProfileThemePreset preset)
    {
        Assert.InRange(preset.GradientAngle, 0f, 360f);
        Assert.InRange(preset.TextureIntensity, 0f, 1f);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryTheme_HasANonEmptyDescription(ProfileThemePreset preset)
    {
        Assert.False(string.IsNullOrWhiteSpace(preset.Description));
    }

    /// <summary>Coarse relative luminance (sRGB values, no gamma decode) — not a WCAG contrast
    /// ratio, but enough to catch a genuinely broken pairing like light-gray text on a near-white
    /// background.</summary>
    private static float Luminance(System.Numerics.Vector4 color) => (0.2126f * color.X) + (0.7152f * color.Y) + (0.0722f * color.Z);

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryTheme_HasReadableTextContrast_AgainstBothBackgroundColors(ProfileThemePreset preset)
    {
        const float minDelta = 0.25f;
        var textLuminance = Luminance(preset.TextColor);

        var vsPrimary = System.MathF.Abs(textLuminance - Luminance(preset.PrimaryColor));
        var vsSecondary = System.MathF.Abs(textLuminance - Luminance(preset.SecondaryColor));

        Assert.True(vsPrimary >= minDelta, $"{preset.Name}: Name text contrast against PrimaryColor is only {vsPrimary:0.00}.");
        Assert.True(vsSecondary >= minDelta, $"{preset.Name}: Name text contrast against SecondaryColor is only {vsSecondary:0.00}.");
    }

    /// <summary>
    /// A coarse but real differentiation check, catching the exact failure mode found and fixed
    /// during the product review (Midnight/Dark, Amethyst/Royal, Emerald/Forest, Autumn/Warm all
    /// had near-identical Primary+Secondary pairs): no two themes may sit within this tight a
    /// distance in both colors at once. Distinguishing by texture/accent alone isn't enough — the
    /// background itself must read as different at a glance.
    /// </summary>
    [Fact]
    public void NoTwoThemes_HaveNearIdenticalBackgroundColors()
    {
        const float minDistance = 0.12f;
        var all = ProfileThemePresets.All;

        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                var primaryDistance = System.Numerics.Vector4.Distance(all[i].PrimaryColor, all[j].PrimaryColor);
                var secondaryDistance = System.Numerics.Vector4.Distance(all[i].SecondaryColor, all[j].SecondaryColor);
                Assert.True(primaryDistance > minDistance || secondaryDistance > minDistance,
                    $"{all[i].Name} and {all[j].Name} look nearly identical (primary Δ={primaryDistance:0.000}, secondary Δ={secondaryDistance:0.000}).");
            }
        }
    }
}
