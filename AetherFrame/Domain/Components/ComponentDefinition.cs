using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>
/// An immutable Component definition: what a <see cref="PlateComponent"/> references by
/// <see cref="Id"/>. Built-in definitions (see <see cref="BuiltInComponentCatalog"/>) are fixed at
/// compile time; a Plate stores only the id, never a copy, so a definition can never be edited
/// through a Plate. Rendering is selected by <see cref="Shape"/> (a closed, compile-time list of
/// procedural generators — see <see cref="ComponentGeometry"/> — plus <see cref="ComponentShape.Art"/>
/// for bundled artwork, see <see cref="Art"/>), never by <see cref="Name"/> or any other display
/// text, and never by instantiating a type named in data.
/// </summary>
/// <param name="Id">Stable, frozen-forever id ("af.&lt;kind&gt;.&lt;style&gt;" for built-ins). Never reused.</param>
/// <param name="Kind">The Component type this definition is for. A component whose stored kind
/// disagrees is treated as unresolved (never drawn), not re-interpreted.</param>
/// <param name="Name">Display label only.</param>
/// <param name="Description">Display text only.</param>
/// <param name="Shape">The procedural generator that draws it.</param>
/// <param name="ColorSource">Where the default color comes from when the component has no override.</param>
/// <param name="DefaultAlpha">Alpha of the default color.</param>
public sealed record ComponentDefinition(
    string Id,
    PlateComponentKind Kind,
    string Name,
    string Description,
    ComponentShape Shape,
    ComponentColorSource ColorSource,
    float DefaultAlpha)
{
    /// <summary>True for definitions drawn from a managed image (<see cref="PlateComponent.AssetId"/>).
    /// Those need an image chosen first, so the Basic editor's constrained slots never offer them.</summary>
    public bool RequiresAsset => Shape == ComponentShape.Image;

    /// <summary>The bundled artwork a <see cref="ComponentShape.Art"/> definition draws; null for every
    /// other shape. Chosen at compile time, so a Plate stores only <see cref="Id"/>.</summary>
    public BuiltInArtAsset? Art { get; init; }

    /// <summary>A graphical definition drawing <paramref name="art"/>, tinted from <paramref name="colorSource"/>.</summary>
    public static ComponentDefinition ForArt(string id, string description, BuiltInArtAsset art, ComponentColorSource colorSource) =>
        new(id, art.Kind, art.Name, description, ComponentShape.Art, art.Tintable ? colorSource : ComponentColorSource.White, art.DefaultOpacity) { Art = art };

    /// <summary>The color a component of this definition uses without an override on <paramref name="profile"/>:
    /// derived from the Plate's Basic theme, so frames and backings follow a theme change.</summary>
    public Vector4 DefaultColor(ProfileDocument profile)
    {
        var theme = Basic.AdventurePlateClassicLayout.ResolveTheme(profile);
        var rgb = ColorSource switch
        {
            ComponentColorSource.ThemeText => theme.TextColor,
            ComponentColorSource.ThemeSoft => theme.SoftTextColor,
            ComponentColorSource.ThemeBackground => theme.PrimaryColor,
            ComponentColorSource.Shadow => new Vector4(0f, 0f, 0f, 1f),
            ComponentColorSource.White => Vector4.One,
            ComponentColorSource.NameBackdrop => NameBackdrop(profile, theme),
            _ => theme.AccentTextColor,
        };

        return new Vector4(rgb.X, rgb.Y, rgb.Z, DefaultAlpha);
    }

    /// <summary>
    /// A backing that lifts the character name off the background: black behind a light name, white
    /// behind a dark one — from the name's actual color (custom or automatic), else the theme's.
    /// </summary>
    private static Vector4 NameBackdrop(ProfileDocument profile, ProfileThemePreset theme)
    {
        var name = Basic.BasicSections.Find(profile, ProfileElementRole.BasicName) is TextProfileElement text ? text.Color : theme.PreferredNameColor;
        return RelativeLuminance(name) > 0.35f ? new Vector4(0f, 0f, 0f, 1f) : Vector4.One;
    }

    /// <summary>WCAG relative luminance of a color's RGB (sRGB, alpha ignored).</summary>
    internal static float RelativeLuminance(Vector4 color)
    {
        static float Linear(float c) => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return (0.2126f * Linear(color.X)) + (0.7152f * Linear(color.Y)) + (0.0722f * Linear(color.Z));
    }
}

/// <summary>
/// The procedural generators. Not persisted (a Plate stores <see cref="ComponentDefinition.Id"/>),
/// so this list may be reorganized freely.
/// </summary>
public enum ComponentShape
{
    /// <summary>A single border inside the placement.</summary>
    Border,

    /// <summary>A heavier outer border with a thin inner border.</summary>
    DoubleBorder,

    /// <summary>A border with cut corners.</summary>
    NotchedBorder,

    /// <summary>Only the four corners of a border.</summary>
    CornerBrackets,

    /// <summary>A fade from transparent to the color toward the bottom edge.</summary>
    BottomFade,

    /// <summary>A fade from every edge inward.</summary>
    Vignette,

    /// <summary>A managed image stretched over the placement.</summary>
    Image,

    /// <summary>A solid bar filling the placement.</summary>
    Bar,

    /// <summary>A bar with pointed ends.</summary>
    Ribbon,

    /// <summary>A bar fading out toward the right.</summary>
    FadeBar,

    /// <summary>A horizontal rule through the placement's middle.</summary>
    Rule,

    /// <summary>A horizontal rule with a diamond at its center.</summary>
    DiamondRule,

    /// <summary>A thin line along the placement's bottom edge.</summary>
    Underline,

    /// <summary>A short, heavier mark at the start of the bottom edge.</summary>
    AccentTick,

    /// <summary>An L-shaped corner mark (drawn for the top-left corner, mirrored for the others).</summary>
    CornerL,

    /// <summary>A small diamond with two short arms (drawn for the top-left corner, mirrored for the others).</summary>
    CornerDiamond,

    /// <summary>Bundled artwork (<see cref="ComponentDefinition.Art"/>) stretched over the placement.</summary>
    Art,
}

/// <summary>Where a definition's default color comes from. Not persisted.</summary>
public enum ComponentColorSource
{
    ThemeAccent,
    ThemeText,
    ThemeSoft,
    ThemeBackground,
    Shadow,
    White,

    /// <summary>Contrasts with the character name: black behind a light name, white behind a dark one.</summary>
    NameBackdrop,
}
