using System;
using System.Numerics;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// The profile's background: one shared, persisted style model used by the Advanced editor, the
/// Basic editor, and Profile View alike (there is deliberately no editor-specific background
/// system). Painted beneath every <see cref="ProfileElement"/>; never selectable or Z-ordered.
///
/// Every mode's settings live side by side and are kept when switching <see cref="Mode"/>, so
/// e.g. trying a gradient and switching back to Image restores the same image. Colors are stored
/// with alpha, but the style's own <see cref="Opacity"/> is what the UI exposes; renderers multiply
/// both.
/// </summary>
public sealed class ProfileBackground
{
    public const float MinTextureScale = 4f;
    public const float MaxTextureScale = 128f;

    public static readonly Vector4 DefaultPrimaryColor = new(0.11f, 0.12f, 0.18f, 1f);
    public static readonly Vector4 DefaultSecondaryColor = new(0.33f, 0.27f, 0.50f, 1f);

    public ProfileBackgroundMode Mode { get; set; } = ProfileBackgroundMode.None;

    /// <summary>Solid fill color; gradient start color; textured fill base color.</summary>
    public Vector4 PrimaryColor { get; set; } = DefaultPrimaryColor;

    /// <summary>Gradient end color; textured fill pattern color.</summary>
    public Vector4 SecondaryColor { get; set; } = DefaultSecondaryColor;

    /// <summary>
    /// Direction of the linear gradient, in degrees [0, 360), clockwise on screen: 0 runs from
    /// <see cref="PrimaryColor"/> on the left to <see cref="SecondaryColor"/> on the right, 90 from
    /// top to bottom. The gradient always spans the whole canvas along that direction.
    /// </summary>
    public float GradientAngle { get; set; } = 90f;

    /// <summary>Opacity of the whole background layer, over the canvas backdrop.</summary>
    public float Opacity { get; set; } = 1f;

    public ProfileBackgroundTexture Texture { get; set; } = ProfileBackgroundTexture.FineNoise;

    /// <summary>Pattern strength [0, 1] over <see cref="PrimaryColor"/>.</summary>
    public float TextureIntensity { get; set; } = 0.35f;

    /// <summary>Pattern feature size (period / grain), in logical canvas pixels.</summary>
    public float TextureScale { get; set; } = 24f;

    /// <summary>Clockwise pattern rotation in degrees; only meaningful for line/dot/grid patterns.</summary>
    public float TextureRotation { get; set; }

    /// <summary>Managed asset id of the background image, or null for none.</summary>
    public Guid? ImageAssetId { get; set; }

    public ProfileImageFit ImageFit { get; set; } = ProfileImageFit.Fill;

    public bool ImageFlipX { get; set; }

    public bool ImageFlipY { get; set; }

    /// <summary>True when this style would actually draw an image (Image mode with an asset set).</summary>
    public bool HasImage => Mode == ProfileBackgroundMode.Image && ImageAssetId is not null;

    /// <summary>True for texture kinds where rotating the pattern is visually meaningful.</summary>
    public static bool SupportsRotation(ProfileBackgroundTexture texture) =>
        texture is ProfileBackgroundTexture.Dots or ProfileBackgroundTexture.Grid
            or ProfileBackgroundTexture.DiagonalLines or ProfileBackgroundTexture.Crosshatch;

    public ProfileBackground Clone() => new()
    {
        Mode = Mode,
        PrimaryColor = PrimaryColor,
        SecondaryColor = SecondaryColor,
        GradientAngle = GradientAngle,
        Opacity = Opacity,
        Texture = Texture,
        TextureIntensity = TextureIntensity,
        TextureScale = TextureScale,
        TextureRotation = TextureRotation,
        ImageAssetId = ImageAssetId,
        ImageFit = ImageFit,
        ImageFlipX = ImageFlipX,
        ImageFlipY = ImageFlipY,
    };

    public bool ContentEquals(ProfileBackground? other) =>
        other is not null
        && Mode == other.Mode
        && PrimaryColor == other.PrimaryColor
        && SecondaryColor == other.SecondaryColor
        && GradientAngle.Equals(other.GradientAngle)
        && Opacity.Equals(other.Opacity)
        && Texture == other.Texture
        && TextureIntensity.Equals(other.TextureIntensity)
        && TextureScale.Equals(other.TextureScale)
        && TextureRotation.Equals(other.TextureRotation)
        && ImageAssetId == other.ImageAssetId
        && ImageFit == other.ImageFit
        && ImageFlipX == other.ImageFlipX
        && ImageFlipY == other.ImageFlipY;

    /// <summary>
    /// Builds the equivalent style for a profile saved before this model existed, from its three
    /// legacy fields: an image (if one was set) at its old fit mode and opacity, or no background.
    /// </summary>
    internal static ProfileBackground FromLegacy(Guid? assetId, BackgroundFitMode? fitMode, float? opacity) => new()
    {
        Mode = assetId is null ? ProfileBackgroundMode.None : ProfileBackgroundMode.Image,
        ImageAssetId = assetId,
        ImageFit = fitMode switch
        {
            BackgroundFitMode.Contain => ProfileImageFit.Fit,
            BackgroundFitMode.Stretch => ProfileImageFit.Stretch,
            _ => ProfileImageFit.Fill, // Cover, or absent (the old default)
        },
        Opacity = Math.Clamp(opacity ?? 1f, 0f, 1f),
    };
}

/// <summary>Persisted as its numeric value; append only.</summary>
public enum ProfileBackgroundMode
{
    None = 0,
    SolidColor = 1,
    LinearGradient = 2,
    TexturedFill = 3,
    Image = 4,
}

/// <summary>
/// Lightweight procedural patterns for <see cref="ProfileBackgroundMode.TexturedFill"/>, generated
/// at runtime (never bundled bitmaps). Persisted as its numeric value; append only.
/// </summary>
public enum ProfileBackgroundTexture
{
    None = 0,
    FineNoise = 1,
    Dots = 2,
    Grid = 3,
    DiagonalLines = 4,
    Crosshatch = 5,
    SubtlePaper = 6,
}
