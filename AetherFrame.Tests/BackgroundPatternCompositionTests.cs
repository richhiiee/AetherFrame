using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Regression coverage for the "Pattern destroys Theme background" bug: selecting a Pattern must
/// only ever touch <see cref="ProfileBackground.Texture"/> — and <see cref="ProfileBackground.Mode"/>
/// only when there was no background at all (None) — never the base Theme's colors, gradient, or
/// Mode. <c>BackgroundStylePanel</c> (Windows/) isn't compiled into this test project, so its pick
/// handler's exact lambda is reproduced verbatim here (see <see cref="SelectPattern"/>), the same
/// way <c>PatternPreviewTests.cs</c> already does for the card-preview side of the same fix. Themes
/// are applied through the real <see cref="BasicPlateEditor.ApplyTheme"/> path so ThemeId and the
/// "None/Image become a visible gradient" rule are exercised for real, not re-implemented.
/// </summary>
public class BackgroundPatternCompositionTests
{
    /// <summary>The exact lambda <c>BackgroundStylePanel.DrawPatternPresets</c> hands to
    /// <c>EditorSession.ApplyBackgroundEdit</c> when a Pattern card is picked.</summary>
    private static void SelectPattern(ProfileBackground style, ProfileBackgroundTexture texture)
    {
        style.Texture = texture;
        if (texture != ProfileBackgroundTexture.None && style.Mode == ProfileBackgroundMode.None)
        {
            style.Mode = ProfileBackgroundMode.SolidColor;
        }
    }

    private static ProfileThemePreset GradientTheme => ProfileThemePresets.Find("Warm")!;
    private static ProfileThemePreset AnotherGradientTheme => ProfileThemePresets.Find("Cool")!;
    private static ProfileThemePreset SolidLookingTheme => ProfileThemePresets.Find("Dark")!;

    /// <summary>
    /// Every shipped Theme preset sets its own default Texture (e.g. "Warm" ships with
    /// SubtlePaper) — established, pre-existing, intentional behavior, unrelated to this bug. So
    /// "base Theme background" here means Mode/colors/gradient only, never Texture: picking an
    /// explicit Pattern (including explicitly picking None) always overrides whatever default
    /// texture a Theme initially suggested, exactly as it should.
    /// </summary>
    private static void AssertBaseUnchanged(ProfileBackground before, ProfileBackground after)
    {
        Assert.Equal(before.Mode, after.Mode);
        Assert.Equal(before.PrimaryColor, after.PrimaryColor);
        Assert.Equal(before.SecondaryColor, after.SecondaryColor);
        Assert.Equal(before.GradientAngle, after.GradientAngle);
        Assert.Equal(before.Opacity, after.Opacity);
    }

    // ---------------------------------------------------------------- the reported scenario, step by step

    [Fact]
    public void Step1To4_GradientTheme_ThenPattern_BaseThemeStateIsUnchanged()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.ApplyTheme(GradientTheme); // 1. Apply a gradient Theme
        var background = document.Background!;
        Assert.Equal(ProfileBackgroundMode.LinearGradient, background.Mode); // sanity: None -> LinearGradient, per ApplyTo's own rule

        // 2. Capture base background state
        var baseMode = background.Mode;
        var basePrimary = background.PrimaryColor;
        var baseSecondary = background.SecondaryColor;
        var baseAngle = background.GradientAngle;
        var baseThemeId = document.BasicPlate!.ThemeId;

        SelectPattern(background, ProfileBackgroundTexture.Dots); // 3. Select a Pattern

        // 4. Base Theme state remains unchanged
        Assert.Equal(baseMode, background.Mode);
        Assert.Equal(basePrimary, background.PrimaryColor);
        Assert.Equal(baseSecondary, background.SecondaryColor);
        Assert.Equal(baseAngle, background.GradientAngle);
        Assert.Equal(baseThemeId, document.BasicPlate!.ThemeId);
    }

    [Fact]
    public void Step5_RenderParameters_StillContainBothTheThemeGradientAndThePatternOverlay()
    {
        // Stands in for "render parameters": ProfileBackgroundRenderer.Draw renders the gradient
        // base whenever Mode == LinearGradient, and composes the Pattern overlay on top whenever
        // Texture != None — entirely independent of Mode. Both being true at once here is exactly
        // what "gradient plus pattern overlay" means; Windows/UI.Rendering aren't compiled into
        // this test project, so the actual draw calls can only be confirmed in game.
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var background = document.Background!;
        var basePrimary = background.PrimaryColor;
        var baseSecondary = background.SecondaryColor;

        SelectPattern(background, ProfileBackgroundTexture.Waves);

        Assert.Equal(ProfileBackgroundMode.LinearGradient, background.Mode);
        Assert.Equal(ProfileBackgroundTexture.Waves, background.Texture);
        Assert.Equal(basePrimary, background.PrimaryColor);
        Assert.Equal(baseSecondary, background.SecondaryColor);
    }

    [Fact]
    public void Step6To7_SelectNone_OriginalBaseThemeIsPresentExactly()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var background = document.Background!;
        var before = background.Clone();

        SelectPattern(background, ProfileBackgroundTexture.Crosshatch);
        SelectPattern(background, ProfileBackgroundTexture.None); // 6. Select None

        // 7. Original base Theme is still present exactly
        AssertBaseUnchanged(before, background);
        Assert.Equal(ProfileBackgroundTexture.None, background.Texture);
    }

    // ---------------------------------------------------------------- solid + gradient theme + pattern

    [Fact]
    public void SolidTheme_PlusPattern_BaseUnchanged()
    {
        var document = BasicDocuments.Blank();
        document.Background!.Mode = ProfileBackgroundMode.SolidColor; // ApplyTo "keeps mode" — stays Solid, not auto-switched to Gradient
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(SolidLookingTheme);
        var background = document.Background!;
        var before = background.Clone();

        SelectPattern(background, ProfileBackgroundTexture.Sparkle);

        Assert.Equal(ProfileBackgroundMode.SolidColor, background.Mode);
        Assert.Equal(before.PrimaryColor, background.PrimaryColor);
        Assert.Equal(before.SecondaryColor, background.SecondaryColor);
        Assert.Equal(ProfileBackgroundTexture.Sparkle, background.Texture);
    }

    [Fact]
    public void GradientTheme_PlusPattern_BaseUnchanged()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var background = document.Background!;
        var before = background.Clone();

        SelectPattern(background, ProfileBackgroundTexture.Honeycomb);

        Assert.Equal(ProfileBackgroundMode.LinearGradient, background.Mode);
        Assert.Equal(before.PrimaryColor, background.PrimaryColor);
        Assert.Equal(before.SecondaryColor, background.SecondaryColor);
        Assert.Equal(before.GradientAngle, background.GradientAngle);
    }

    // ---------------------------------------------------------------- switching patterns

    [Fact]
    public void SwitchingPatternAToPatternB_BaseStillUnchanged()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var background = document.Background!;
        var before = background.Clone();

        SelectPattern(background, ProfileBackgroundTexture.Diamonds);
        SelectPattern(background, ProfileBackgroundTexture.Chevron);

        Assert.Equal(ProfileBackgroundTexture.Chevron, background.Texture);
        Assert.Equal(before.Mode, background.Mode);
        Assert.Equal(before.PrimaryColor, background.PrimaryColor);
        Assert.Equal(before.SecondaryColor, background.SecondaryColor);
        Assert.Equal(before.GradientAngle, background.GradientAngle);
    }

    [Fact]
    public void PatternToNone_RevealsTheExactBaseUnderneath()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var background = document.Background!;
        var before = background.Clone();

        SelectPattern(background, ProfileBackgroundTexture.Grid);
        SelectPattern(background, ProfileBackgroundTexture.None);

        AssertBaseUnchanged(before, background);
        Assert.Equal(ProfileBackgroundTexture.None, background.Texture);
    }

    // ---------------------------------------------------------------- theme change while a pattern is active

    [Fact]
    public void ThemeChangeWhilePatternIsActive_AppliesTheNewThemeCleanly()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        SelectPattern(document.Background!, ProfileBackgroundTexture.Dots);

        editor.ApplyTheme(AnotherGradientTheme);

        Assert.Equal(AnotherGradientTheme.Id, document.BasicPlate!.ThemeId);
        Assert.Equal(AnotherGradientTheme.PrimaryColor, document.Background!.PrimaryColor);
        Assert.Equal(AnotherGradientTheme.SecondaryColor, document.Background.SecondaryColor);
        Assert.Equal(AnotherGradientTheme.GradientAngle, document.Background.GradientAngle);
        Assert.Equal(ProfileBackgroundMode.LinearGradient, document.Background.Mode);
    }

    // ---------------------------------------------------------------- explicit non-interference checks

    [Fact]
    public void PatternSelection_DoesNotChangeThemeId()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var themeId = document.BasicPlate!.ThemeId;

        SelectPattern(document.Background!, ProfileBackgroundTexture.Stripes);

        Assert.Equal(themeId, document.BasicPlate!.ThemeId);
    }

    [Fact]
    public void PatternSelection_DoesNotOverwritePrimaryOrSecondaryColors()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var primary = document.Background!.PrimaryColor;
        var secondary = document.Background.SecondaryColor;

        SelectPattern(document.Background, ProfileBackgroundTexture.Checkerboard);

        Assert.Equal(primary, document.Background.PrimaryColor);
        Assert.Equal(secondary, document.Background.SecondaryColor);
    }

    [Fact]
    public void PatternSelection_DoesNotOverwriteGradientState()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(GradientTheme);
        var angle = document.Background!.GradientAngle;
        var mode = document.Background.Mode;

        SelectPattern(document.Background, ProfileBackgroundTexture.Herringbone);

        Assert.Equal(angle, document.Background.GradientAngle);
        Assert.Equal(mode, document.Background.Mode);
    }

    [Fact]
    public void NoPattern_StillRendersExactlyAsBefore()
    {
        // Nothing in this fix touches ApplyTheme's own, pre-existing behavior (including that a
        // Theme sets its own default Texture, e.g. "Warm" ships with SubtlePaper) — applying a
        // Theme with no explicit Pattern selection at all produces exactly what ApplyTo defines,
        // identical to before this fix.
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.ApplyTheme(GradientTheme);

        var background = document.Background!;
        Assert.Equal(ProfileBackgroundMode.LinearGradient, background.Mode);
        Assert.Equal(GradientTheme.PrimaryColor, background.PrimaryColor);
        Assert.Equal(GradientTheme.SecondaryColor, background.SecondaryColor);
        Assert.Equal(GradientTheme.GradientAngle, background.GradientAngle);
        Assert.Equal(GradientTheme.Texture, background.Texture);
        Assert.Equal(GradientTheme.TextureIntensity, background.TextureIntensity);
    }

    [Fact]
    public void ExplicitlyChoosingNoPattern_OnADocumentThatNeverHadOne_ChangesNothing()
    {
        // Mode=None, Texture=None (a genuinely blank document, never themed or patterned, and its
        // Texture explicitly cleared from the class's own FineNoise field default) — selecting
        // None is then a true no-op, not a special case that needs its own branch.
        var document = BasicDocuments.Blank();
        var background = document.Background!;
        background.Texture = ProfileBackgroundTexture.None;
        var before = background.Clone();

        SelectPattern(background, ProfileBackgroundTexture.None);

        Assert.True(background.ContentEquals(before));
    }
}
