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

    /// <summary>Prefix of every bundled manifest resource name (see the plugin project's Assets folder).</summary>
    public const string ResourcePrefix = "AetherFrame.Assets.";

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

    public static readonly IReadOnlyList<BuiltInArtAsset> All = [AstrolabePivot];

    private static readonly Dictionary<string, BuiltInArtAsset> ById = BuildIndex();

    /// <summary>The artwork with this exact id, or null (ids are case-sensitive).</summary>
    public static BuiltInArtAsset? Find(string? id) => id is not null && ById.TryGetValue(id, out var art) ? art : null;

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
