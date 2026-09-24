using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Pattern browser's cards went through two designs: first they cloned the profile's own
/// background but computed the shared renderer's tile scale relative to the full canvas width, which
/// at a small card size fell under the renderer's "too small to tile" fallback and silently rendered
/// a flat, near-invisible tint for every card regardless of pattern. That was fixed by decoupling the
/// card's render scale from canvas size — but the interim fix went further and swapped in a
/// standardized high-contrast palette instead of the profile's own colors, which broke truthful
/// side-by-side comparison (in-game testing wanted to compare candidates using the Plate's ACTUAL
/// current Base/Pattern colors, intensity, scale, and rotation, accepting that similar colors should
/// truthfully look subtle).
///
/// <see cref="PatternPreview.Create"/> is the current, final design: it clones the profile's own
/// current background and overrides only Mode and Texture, so a card differs from its neighbors in
/// exactly one respect — the candidate pattern. These tests cover everything about that construction
/// that's pure and Dalamud-free; on-screen legibility and live updates while dragging a color/slider
/// can only be confirmed in game.
/// </summary>
public class PatternPreviewTests
{
    private static readonly ProfileBackgroundTexture[] AllRealPatterns =
        Enum.GetValues<ProfileBackgroundTexture>().Where(t => t != ProfileBackgroundTexture.None).ToArray();

    public static TheoryData<ProfileBackgroundTexture> AllRealPatternsData() => new(AllRealPatterns);

    private static ProfileBackground CurrentPlateBackground() => new()
    {
        Mode = ProfileBackgroundMode.SolidColor,
        PrimaryColor = new Vector4(0.20f, 0.05f, 0.30f, 1f), // dark purple, matching the task's own example
        SecondaryColor = new Vector4(0.98f, 0.80f, 0.88f, 1f), // pale pink
        TextureIntensity = 0.42f,
        TextureScale = 51f,
        TextureRotation = 77f,
        Opacity = 0.88f,
    };

    // ---------------------------------------------------------------- copies every current setting

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_CopiesTheCurrentBaseColor(ProfileBackgroundTexture texture)
    {
        var current = CurrentPlateBackground();
        Assert.Equal(current.PrimaryColor, PatternPreview.Create(current, texture).PrimaryColor);
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_CopiesTheCurrentPatternColor(ProfileBackgroundTexture texture)
    {
        var current = CurrentPlateBackground();
        Assert.Equal(current.SecondaryColor, PatternPreview.Create(current, texture).SecondaryColor);
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_CopiesTheCurrentIntensity(ProfileBackgroundTexture texture)
    {
        var current = CurrentPlateBackground();
        Assert.Equal(current.TextureIntensity, PatternPreview.Create(current, texture).TextureIntensity);
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_CopiesTheCurrentScale(ProfileBackgroundTexture texture)
    {
        var current = CurrentPlateBackground();
        Assert.Equal(current.TextureScale, PatternPreview.Create(current, texture).TextureScale);
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_CopiesTheCurrentRotation(ProfileBackgroundTexture texture)
    {
        var current = CurrentPlateBackground();
        Assert.Equal(current.TextureRotation, PatternPreview.Create(current, texture).TextureRotation);
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_CopiesTheCurrentOpacity(ProfileBackgroundTexture texture)
    {
        var current = CurrentPlateBackground();
        Assert.Equal(current.Opacity, PatternPreview.Create(current, texture).Opacity);
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Preview_AlwaysShowsAsTexturedFill_RegardlessOfTheCurrentMode(ProfileBackgroundTexture texture)
    {
        foreach (var mode in Enum.GetValues<ProfileBackgroundMode>())
        {
            var current = CurrentPlateBackground();
            current.Mode = mode;
            Assert.Equal(ProfileBackgroundMode.TexturedFill, PatternPreview.Create(current, texture).Mode);
        }
    }

    // ---------------------------------------------------------------- only the candidate differs

    [Fact]
    public void OnlyTheCandidatePattern_DiffersBetweenTwoCardsOfTheSamePlate()
    {
        var current = CurrentPlateBackground();
        var a = PatternPreview.Create(current, ProfileBackgroundTexture.Diamonds);
        var b = PatternPreview.Create(current, ProfileBackgroundTexture.Chevron);

        Assert.NotEqual(a.Texture, b.Texture);
        Assert.Equal(a.PrimaryColor, b.PrimaryColor);
        Assert.Equal(a.SecondaryColor, b.SecondaryColor);
        Assert.Equal(a.TextureIntensity, b.TextureIntensity);
        Assert.Equal(a.TextureScale, b.TextureScale);
        Assert.Equal(a.TextureRotation, b.TextureRotation);
        Assert.Equal(a.Opacity, b.Opacity);
        Assert.Equal(a.Mode, b.Mode);
    }

    // ---------------------------------------------------------------- live updates

    [Fact]
    public void ChangingTheCurrentColors_ChangesTheNextGeneratedPreview()
    {
        var current = CurrentPlateBackground();
        var before = PatternPreview.Create(current, ProfileBackgroundTexture.Grid);

        current.PrimaryColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        current.SecondaryColor = new Vector4(0.1f, 0.9f, 0.1f, 1f);
        var after = PatternPreview.Create(current, ProfileBackgroundTexture.Grid);

        Assert.NotEqual(before.PrimaryColor, after.PrimaryColor);
        Assert.NotEqual(before.SecondaryColor, after.SecondaryColor);
        Assert.Equal(current.PrimaryColor, after.PrimaryColor);
        Assert.Equal(current.SecondaryColor, after.SecondaryColor);
    }

    [Fact]
    public void ChangingTheCurrentScaleOrRotation_ChangesTheNextGeneratedPreview()
    {
        var current = CurrentPlateBackground();
        var before = PatternPreview.Create(current, ProfileBackgroundTexture.Ripples);

        current.TextureScale = 12f;
        current.TextureRotation = 200f;
        var after = PatternPreview.Create(current, ProfileBackgroundTexture.Ripples);

        Assert.NotEqual(before.TextureScale, after.TextureScale);
        Assert.NotEqual(before.TextureRotation, after.TextureRotation);
    }

    // ---------------------------------------------------------------- no mutation, independent objects

    [Fact]
    public void Create_NeverMutatesTheCurrentBackgroundItWasGiven()
    {
        var current = CurrentPlateBackground();
        var before = current.Clone();

        foreach (var texture in AllRealPatterns)
        {
            _ = PatternPreview.Create(current, texture);
        }

        Assert.True(current.ContentEquals(before));
    }

    [Fact]
    public void Create_NeverTouchesAnyProfileDocument()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var before = JsonSerializer.Serialize(document, JsonOptions.Default);

        foreach (var texture in AllRealPatterns)
        {
            _ = PatternPreview.Create(document.Background!, texture);
        }

        var after = JsonSerializer.Serialize(document, JsonOptions.Default);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Create_ReturnsAnIndependentObject_NotTheSameInstance()
    {
        var current = CurrentPlateBackground();
        var preview = PatternPreview.Create(current, ProfileBackgroundTexture.Brick);

        Assert.NotSame(current, preview);
        preview.PrimaryColor = new Vector4(1f, 0f, 0f, 1f);
        Assert.NotEqual(current.PrimaryColor, preview.PrimaryColor);
    }

    // ---------------------------------------------------------------- every enum value maps to a preview

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void EveryRealPatternValue_ProducesAPreview(ProfileBackgroundTexture texture)
    {
        var preview = PatternPreview.Create(CurrentPlateBackground(), texture);
        Assert.NotNull(preview);
        Assert.Equal(texture, preview.Texture);
    }

    // ---------------------------------------------------------------- the pattern math itself is visible

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void EveryRealPattern_HasVisibleForegroundCoverageSomewhereInTheTile(ProfileBackgroundTexture texture)
    {
        var maxCoverage = 0f;
        for (var y = 0; y < ProceduralPatternMath.TileTexels; y += 2)
        {
            for (var x = 0; x < ProceduralPatternMath.TileTexels; x += 2)
            {
                maxCoverage = MathF.Max(maxCoverage, ProceduralPatternMath.Sample(texture, x, y));
            }
        }

        Assert.True(maxCoverage > 0.3f, $"{texture}'s peak coverage is only {maxCoverage:0.00}, likely imperceptible.");
    }

    [Fact]
    public void None_ProducesNoCoverageAnywhere()
    {
        // None is handled as a direct flat fill of the current Base color in the UI (no
        // PatternPreview.Create call at all) — this pins the underlying pattern math's own
        // contribution as exactly zero, which is what makes that special-casing correct.
        for (var y = 0; y < ProceduralPatternMath.TileTexels; y += 8)
        {
            for (var x = 0; x < ProceduralPatternMath.TileTexels; x += 8)
            {
                Assert.Equal(0f, ProceduralPatternMath.Sample(ProfileBackgroundTexture.None, x, y));
            }
        }
    }

    // ---------------------------------------------------------------- clicking a card applies the intended pattern

    [Fact]
    public void PickingACard_AppliesTexturedFillAndTheCandidatePattern_WithoutCopyingPreviewStateBack()
    {
        // The exact lambda body DrawPatternPresets hands to EditorSession.ApplyBackgroundEdit: it
        // touches only Mode and Texture, never anything a preview construction might have touched.
        static void OnPick(ProfileBackground style, ProfileBackgroundTexture candidate)
        {
            style.Mode = ProfileBackgroundMode.TexturedFill;
            style.Texture = candidate;
        }

        var background = new ProfileBackground
        {
            Mode = ProfileBackgroundMode.LinearGradient,
            PrimaryColor = new Vector4(0.2f, 0.05f, 0.3f, 1f),
            SecondaryColor = new Vector4(0.98f, 0.8f, 0.88f, 1f),
            TextureIntensity = 0.42f,
            TextureScale = 51f,
            TextureRotation = 77f,
            Texture = ProfileBackgroundTexture.Dots,
        };
        var before = background.Clone();

        // Browsing (previewing several candidates) must not itself change anything...
        foreach (var texture in AllRealPatterns)
        {
            _ = PatternPreview.Create(background, texture);
        }

        Assert.True(background.ContentEquals(before));

        // ...only the actual pick does, and only Mode/Texture.
        OnPick(background, ProfileBackgroundTexture.Honeycomb);

        Assert.Equal(ProfileBackgroundMode.TexturedFill, background.Mode);
        Assert.Equal(ProfileBackgroundTexture.Honeycomb, background.Texture);
        Assert.Equal(before.PrimaryColor, background.PrimaryColor);
        Assert.Equal(before.SecondaryColor, background.SecondaryColor);
        Assert.Equal(before.TextureIntensity, background.TextureIntensity);
        Assert.Equal(before.TextureScale, background.TextureScale);
        Assert.Equal(before.TextureRotation, background.TextureRotation);
    }
}
