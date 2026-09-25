using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Components;

/// <summary>
/// A piece of artwork bundled inside the plugin assembly, drawn by a graphical built-in
/// <see cref="ComponentDefinition"/> (<see cref="ComponentShape.Art"/>). Compile-time data like the
/// definitions themselves: nothing about it is persisted, so a Plate never stores a path, a file
/// name or image bytes — only the definition id that selects it.
/// </summary>
/// <param name="Id">Stable, frozen-forever logical id ("af.asset.&lt;family&gt;.&lt;kind&gt;.&lt;name&gt;"),
/// independent of where or how the image is bundled. Never reused.</param>
/// <param name="Name">Display label only.</param>
/// <param name="Kind">The one Component kind this artwork is drawn for.</param>
/// <param name="ResourceName">Manifest resource name of the runtime PNG inside the plugin assembly
/// (not a filesystem path). Always an 8-bit RGBA (or, for opaque art, RGB) PNG (see <c>BundledArtImage</c>).</param>
/// <param name="PixelWidth">The runtime PNG's width, in pixels.</param>
/// <param name="PixelHeight">The runtime PNG's height, in pixels. The artwork is always drawn at this
/// aspect ratio (fitted inside its placement box, never visibly stretched: see <c>ComponentPaintPlan</c>).</param>
/// <param name="Tintable">True when the artwork is white/greyscale and takes the Component's color;
/// false draws its own colors, with only the color's alpha applied.</param>
/// <param name="DefaultOpacity">Alpha of the definition's default color.</param>
/// <param name="CornerPlacement">For Corner Ornaments: how the one (top-left) drawing serves the
/// other three corners.</param>
/// <param name="SizeFactor">Size of the artwork's placement box relative to its kind's standard
/// procedural box (artwork needs more room than a line mark to read): a Corner Ornament's square,
/// anchored at the same corner; a Name Backing's or Divider's box, around the same center. Unused
/// (1) for the kinds whose art fills its whole anchor: Background, Plate Frame, Portrait Frame.</param>
public sealed record BuiltInArtAsset(
    string Id,
    string Name,
    PlateComponentKind Kind,
    string ResourceName,
    int PixelWidth,
    int PixelHeight,
    bool Tintable,
    float DefaultOpacity,
    CornerArtPlacement CornerPlacement,
    float SizeFactor)
{
    /// <summary>Width over height of the runtime artwork (1 for square art).</summary>
    public float AspectRatio => PixelHeight > 0 ? (float)PixelWidth / PixelHeight : 1f;

    /// <summary>The visual family the artwork was designed in, shown with it wherever Components are
    /// browsed ("Celestial Sakura"); null for a standalone piece. Display and grouping only.</summary>
    public string? Family { get; init; }

    /// <summary>Extra words the artwork answers to in a search (motifs, colors, its role), lower case.
    /// Display metadata only: nothing is ever resolved by them.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>True when <paramref name="query"/> (trimmed, case-insensitive) occurs in the name, the
    /// family or a keyword; an empty query matches everything.</summary>
    public bool MatchesSearch(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (Name.Contains(text, StringComparison.OrdinalIgnoreCase) || (Family?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        foreach (var keyword in Keywords)
        {
            if (keyword.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>How a corner drawing, designed for the top-left corner, is placed in the other corners.</summary>
public enum CornerArtPlacement
{
    /// <summary>Mirrored horizontally and/or vertically (what the procedural corner marks do).</summary>
    Mirror,

    /// <summary>Rotated by 90/180/270 degrees, so asymmetric details keep their handedness.</summary>
    Rotate,
}

/// <summary>The artwork AetherFrame bundles. Local and compile-time: no files outside the plugin
/// assembly, no network, no user packs.</summary>
public static class BuiltInArtCatalog
{
    public const string CelestialDreamAstrolabePivot = "af.asset.celestial-dream.corner-ornament.astrolabe-pivot";

    public const string CelestialSakuraBackground = "af.asset.celestial-sakura.background.twilight";
    public const string CelestialSakuraPlateFrame = "af.asset.celestial-sakura.plate-frame.blossom";
    public const string CelestialSakuraPortraitFrame = "af.asset.celestial-sakura.portrait-frame.blossom";
    public const string CelestialSakuraNameplate = "af.asset.celestial-sakura.name-backing.nameplate";
    public const string CelestialSakuraOrnateDivider = "af.asset.celestial-sakura.divider.ornate";
    public const string CelestialSakuraSlimDivider = "af.asset.celestial-sakura.divider.slim";
    public const string CelestialSakuraCornerOrnament = "af.asset.celestial-sakura.corner-ornament.blossom";

    /// <summary>The family name every Celestial Sakura piece carries (<see cref="BuiltInArtAsset.Family"/>).</summary>
    public const string CelestialSakuraFamily = "Celestial Sakura";

    /// <summary>Prefix of every bundled manifest resource name (see the plugin project's Assets folder).</summary>
    public const string ResourcePrefix = "AetherFrame.Assets.";

    private const string CelestialSakuraResources = ResourcePrefix + "Components.CelestialSakura.";

    public static readonly BuiltInArtAsset AstrolabePivot = new(
        CelestialDreamAstrolabePivot,
        "Astrolabe Pivot",
        PlateComponentKind.CornerOrnament,
        ResourcePrefix + "Components.CelestialDream.CornerOrnaments.AstrolabePivot.png",
        PixelWidth: 512,
        PixelHeight: 512,
        Tintable: true,
        DefaultOpacity: 0.9f,
        CornerPlacement: CornerArtPlacement.Rotate,
        SizeFactor: 2f);

    // Celestial Sakura: full-color artwork (champagne gold filigree, blush cherry blossoms, pearls,
    // a crescent moon) bundled exactly as approved — never tinted, at its generated resolution. The
    // Plate-sized pieces are 16:9 like the Adventure Plate canvas, the Portrait Frame 5:8 like its
    // portrait; each is drawn at its own aspect ratio (see Assets/README.md for the measurements).

    /// <summary>The words every Celestial Sakura piece answers to, before its own role words. Declared
    /// before the pieces: static fields initialize in order.</summary>
    private static readonly string[] CelestialSakuraKeywords =
        ["celestial sakura", "sakura", "cherry blossom", "blossom", "moon", "crescent", "rose", "pink", "gold", "pearl"];

    /// <summary>A twilight sky with cherry branches and a crescent moon, covering the whole Plate (opaque).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraBackgroundArt = CelestialSakura(
        CelestialSakuraBackground, "Celestial Sakura", PlateComponentKind.Background, "CelestialSakura_Background.png", 1672, 941, 1f,
        "background", "sky", "twilight", "landscape");

    /// <summary>A gold filigree border with blossom corners and a crescent crest, drawn edge to edge
    /// (the drawing keeps its own few-pixel margin).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraPlateFrameArt = CelestialSakura(
        CelestialSakuraPlateFrame, "Celestial Sakura", PlateComponentKind.PlateFrame, "CelestialSakura_PlateFrame.png", 1672, 941, 1f,
        "frame", "plate frame", "border");

    /// <summary>A slim gold portrait border with blossoms at opposing corners, fitted to the portrait.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraPortraitFrameArt = CelestialSakura(
        CelestialSakuraPortraitFrame, "Celestial Sakura", PlateComponentKind.PortraitFrame, "CelestialSakura_PortraitFrame.png", 992, 1586, 1f,
        "frame", "portrait", "portrait frame", "border");

    /// <summary>An ivory enamel plaque with blossom ends, behind the name. Its box is 1.5x the name
    /// backing's (the plaque's rails and ornaments surround a text area about a third of its height).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraNameplateArt = CelestialSakura(
        CelestialSakuraNameplate, "Celestial Sakura", PlateComponentKind.NameBacking, "CelestialSakura_Nameplate.png", 2172, 724, 1.5f,
        "nameplate", "name", "plaque", "banner");

    /// <summary>The primary divider: curling gold with blossom clusters and a crescent-set gem. 3:1, in a
    /// band 3x the procedural Divider's height: its drawing fills two thirds of that band, which keeps
    /// the crescent clear of the name above and the first section heading below on the Classic layout.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraOrnateDividerArt = CelestialSakura(
        CelestialSakuraOrnateDivider, "Celestial Sakura Ornate", PlateComponentKind.Divider, "CelestialSakura_Divider_Ornate.png", 2172, 724, 3f,
        "divider", "ornate", "line");

    /// <summary>The secondary divider: a fine tapering gold line with one star and one blossom, in a band
    /// 4x the procedural Divider's height (only its small star rises above the line, so it can be wider).</summary>
    public static readonly BuiltInArtAsset CelestialSakuraSlimDividerArt = CelestialSakura(
        CelestialSakuraSlimDivider, "Celestial Sakura Slim", PlateComponentKind.Divider, "CelestialSakura_Divider_Slim.png", 2172, 724, 4f,
        "divider", "slim", "line", "star");

    /// <summary>An L-shaped blossom cluster with a crescent, drawn for the top-left corner and mirrored
    /// into the others (so its hanging crystals hang down in both top corners), 3x the procedural box.</summary>
    public static readonly BuiltInArtAsset CelestialSakuraCornerOrnamentArt = CelestialSakura(
        CelestialSakuraCornerOrnament, "Celestial Sakura", PlateComponentKind.CornerOrnament, "CelestialSakura_CornerOrnament.png", 1254, 1254, 3f,
        "corner", "ornament", "corner ornament");

    public static readonly IReadOnlyList<BuiltInArtAsset> All =
    [
        AstrolabePivot,
        CelestialSakuraBackgroundArt,
        CelestialSakuraPlateFrameArt,
        CelestialSakuraPortraitFrameArt,
        CelestialSakuraNameplateArt,
        CelestialSakuraOrnateDividerArt,
        CelestialSakuraSlimDividerArt,
        CelestialSakuraCornerOrnamentArt,
    ];

    private static readonly Dictionary<string, BuiltInArtAsset> ById = BuildIndex();

    /// <summary>The artwork with this exact id, or null (ids are case-sensitive).</summary>
    public static BuiltInArtAsset? Find(string? id) => id is not null && ById.TryGetValue(id, out var art) ? art : null;

    /// <summary>Every artwork of one family, in catalog order (display grouping; never used to resolve).</summary>
    public static IEnumerable<BuiltInArtAsset> OfFamily(string family)
    {
        foreach (var art in All)
        {
            if (string.Equals(art.Family, family, StringComparison.Ordinal))
            {
                yield return art;
            }
        }
    }

    private static BuiltInArtAsset CelestialSakura(string id, string name, PlateComponentKind kind, string file, int width, int height, float sizeFactor, params string[] roleKeywords) =>
        new(id, name, kind, CelestialSakuraResources + file, width, height, Tintable: false, DefaultOpacity: 1f, CornerArtPlacement.Mirror, sizeFactor)
        {
            Family = CelestialSakuraFamily,
            Keywords = [.. CelestialSakuraKeywords, .. roleKeywords],
        };

    private static Dictionary<string, BuiltInArtAsset> BuildIndex()
    {
        var index = new Dictionary<string, BuiltInArtAsset>(StringComparer.Ordinal);
        foreach (var art in All)
        {
            index.Add(art.Id, art); // throws on a duplicate id: caught by any test run
        }

        return index;
    }
}
