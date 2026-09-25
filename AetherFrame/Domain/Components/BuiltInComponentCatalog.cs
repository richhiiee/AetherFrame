using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Components;

/// <summary>
/// The Components AetherFrame ships. Local, compile-time data: no network, no files, no third-party
/// assets — built-ins are drawn procedurally (<see cref="ComponentShape"/>) or from artwork bundled
/// inside the plugin assembly (<see cref="BuiltInArtCatalog"/>), except the Custom Image overlay,
/// which draws an image the user imported into their own managed assets.
///
/// <para><b>Stable ids.</b> Every <see cref="ComponentDefinition.Id"/> is frozen forever once
/// shipped: never renamed, never reassigned to a different look, never reused after removal. A
/// Plate referencing an id this build doesn't have keeps it verbatim and simply doesn't draw it
/// (see <see cref="ComponentPaintPlan"/>). Display names may change freely; nothing is keyed by them.</para>
///
/// <para>A future user-authored Component source only has to supply more
/// <see cref="ComponentDefinition"/>s through <see cref="IComponentCatalog"/>; nothing that stores
/// or renders components needs to change.</para>
/// </summary>
public static class BuiltInComponentCatalog
{
    public const string PlateFrameLine = "af.plate-frame.line";
    public const string PlateFrameDouble = "af.plate-frame.double";
    public const string PlateFrameNotched = "af.plate-frame.notched";

    public const string PortraitFrameLine = "af.portrait-frame.line";
    public const string PortraitFrameDouble = "af.portrait-frame.double";
    public const string PortraitFrameBrackets = "af.portrait-frame.brackets";

    public const string PortraitOverlayFade = "af.portrait-overlay.fade";
    public const string PortraitOverlayVignette = "af.portrait-overlay.vignette";
    public const string PortraitOverlayImage = "af.portrait-overlay.image";

    public const string NameBackingBar = "af.name-backing.bar";
    public const string NameBackingRibbon = "af.name-backing.ribbon";
    public const string NameBackingFade = "af.name-backing.fade";

    public const string CornerOrnamentBracket = "af.corner-ornament.bracket";
    public const string CornerOrnamentDiamond = "af.corner-ornament.diamond";
    public const string CornerOrnamentAstrolabePivot = "af.corner-ornament.astrolabe-pivot";

    public const string DividerLine = "af.divider.line";
    public const string DividerDiamond = "af.divider.diamond";

    public const string SectionHeaderUnderline = "af.section-header.underline";
    public const string SectionHeaderTick = "af.section-header.tick";

    // Celestial Sakura: one full-color family across the kinds (see BuiltInArtCatalog).
    public const string BackgroundCelestialSakura = "af.background.celestial-sakura";
    public const string PlateFrameCelestialSakura = "af.plate-frame.celestial-sakura";
    public const string PortraitFrameCelestialSakura = "af.portrait-frame.celestial-sakura";
    public const string NameBackingCelestialSakura = "af.name-backing.celestial-sakura";
    public const string DividerCelestialSakuraOrnate = "af.divider.celestial-sakura-ornate";
    public const string DividerCelestialSakuraSlim = "af.divider.celestial-sakura-slim";
    public const string CornerOrnamentCelestialSakura = "af.corner-ornament.celestial-sakura";

    public static readonly IReadOnlyList<ComponentDefinition> All =
    [
        ComponentDefinition.ForArt(BackgroundCelestialSakura, "Celestial Sakura: a twilight sky with cherry branches and a crescent moon, over the whole Plate.", BuiltInArtCatalog.CelestialSakuraBackgroundArt, ComponentColorSource.White),

        new(PlateFrameLine, PlateComponentKind.PlateFrame, "Line", "A thin border around the Plate.", ComponentShape.Border, ComponentColorSource.ThemeAccent, 0.85f),
        new(PlateFrameDouble, PlateComponentKind.PlateFrame, "Double Line", "A border with a fine inner line.", ComponentShape.DoubleBorder, ComponentColorSource.ThemeAccent, 0.85f),
        new(PlateFrameNotched, PlateComponentKind.PlateFrame, "Notched", "A border with cut corners.", ComponentShape.NotchedBorder, ComponentColorSource.ThemeAccent, 0.85f),
        ComponentDefinition.ForArt(PlateFrameCelestialSakura, "Celestial Sakura: gold filigree with cherry blossom corners and a crescent crest, edge to edge.", BuiltInArtCatalog.CelestialSakuraPlateFrameArt, ComponentColorSource.White),

        new(PortraitFrameLine, PlateComponentKind.PortraitFrame, "Line", "A thin border around the portrait.", ComponentShape.Border, ComponentColorSource.ThemeAccent, 0.9f),
        new(PortraitFrameDouble, PlateComponentKind.PortraitFrame, "Double Line", "A portrait border with a fine inner line.", ComponentShape.DoubleBorder, ComponentColorSource.ThemeAccent, 0.9f),
        new(PortraitFrameBrackets, PlateComponentKind.PortraitFrame, "Corner Brackets", "Brackets on the portrait's corners.", ComponentShape.CornerBrackets, ComponentColorSource.ThemeAccent, 0.9f),
        ComponentDefinition.ForArt(PortraitFrameCelestialSakura, "Celestial Sakura: a slim gold portrait border with cherry blossoms and pearls.", BuiltInArtCatalog.CelestialSakuraPortraitFrameArt, ComponentColorSource.White),

        new(PortraitOverlayFade, PlateComponentKind.PortraitOverlay, "Bottom Fade", "Darkens the bottom of the portrait.", ComponentShape.BottomFade, ComponentColorSource.Shadow, 0.75f),
        new(PortraitOverlayVignette, PlateComponentKind.PortraitOverlay, "Vignette", "Softly darkens the portrait's edges.", ComponentShape.Vignette, ComponentColorSource.Shadow, 0.6f),
        new(PortraitOverlayImage, PlateComponentKind.PortraitOverlay, "Custom Image", "An image of your own over the portrait.", ComponentShape.Image, ComponentColorSource.White, 1f),

        new(NameBackingBar, PlateComponentKind.NameBacking, "Bar", "A soft bar behind the name.", ComponentShape.Bar, ComponentColorSource.NameBackdrop, 0.35f),
        new(NameBackingRibbon, PlateComponentKind.NameBacking, "Ribbon", "A bar with pointed ends behind the name.", ComponentShape.Ribbon, ComponentColorSource.ThemeBackground, 0.8f),
        new(NameBackingFade, PlateComponentKind.NameBacking, "Fade", "A backing that fades out to the right.", ComponentShape.FadeBar, ComponentColorSource.NameBackdrop, 0.5f),
        ComponentDefinition.ForArt(NameBackingCelestialSakura, "Celestial Sakura: an ivory enamel nameplate with blossom ends behind the name.", BuiltInArtCatalog.CelestialSakuraNameplateArt, ComponentColorSource.White),

        new(CornerOrnamentBracket, PlateComponentKind.CornerOrnament, "Bracket", "An angled mark in each corner.", ComponentShape.CornerL, ComponentColorSource.ThemeAccent, 0.9f),
        new(CornerOrnamentDiamond, PlateComponentKind.CornerOrnament, "Diamond", "A small diamond in each corner.", ComponentShape.CornerDiamond, ComponentColorSource.ThemeAccent, 0.9f),
        ComponentDefinition.ForArt(CornerOrnamentAstrolabePivot, "Celestial Dream: an astrolabe's arcs and pivot star in each corner.", BuiltInArtCatalog.AstrolabePivot, ComponentColorSource.ThemeAccent),
        ComponentDefinition.ForArt(CornerOrnamentCelestialSakura, "Celestial Sakura: a cluster of cherry blossoms, gold filigree and a crescent in each corner.", BuiltInArtCatalog.CelestialSakuraCornerOrnamentArt, ComponentColorSource.White),

        new(DividerLine, PlateComponentKind.Divider, "Line", "A rule under the name.", ComponentShape.Rule, ComponentColorSource.ThemeAccent, 0.7f),
        new(DividerDiamond, PlateComponentKind.Divider, "Diamond", "A rule with a center diamond under the name.", ComponentShape.DiamondRule, ComponentColorSource.ThemeAccent, 0.8f),
        ComponentDefinition.ForArt(DividerCelestialSakuraOrnate, "Celestial Sakura: curling gold with blossom clusters and a crescent-set gem under the name.", BuiltInArtCatalog.CelestialSakuraOrnateDividerArt, ComponentColorSource.White),
        ComponentDefinition.ForArt(DividerCelestialSakuraSlim, "Celestial Sakura: a fine gold line with one star and one blossom under the name.", BuiltInArtCatalog.CelestialSakuraSlimDividerArt, ComponentColorSource.White),

        new(SectionHeaderUnderline, PlateComponentKind.SectionHeader, "Underline", "A fine line under each section heading.", ComponentShape.Underline, ComponentColorSource.ThemeAccent, 0.6f),
        new(SectionHeaderTick, PlateComponentKind.SectionHeader, "Accent Mark", "A short mark under each section heading.", ComponentShape.AccentTick, ComponentColorSource.ThemeAccent, 0.9f),
    ];

    /// <summary>The catalog view of the built-ins (the one <see cref="IComponentCatalog"/> in use today).</summary>
    public static readonly IComponentCatalog Instance = new BuiltInCatalog();

    private static readonly Dictionary<string, ComponentDefinition> ById = BuildIndex();

    /// <summary>The definition with this exact id, or null (ids are case-sensitive).</summary>
    public static ComponentDefinition? Find(string? id) => id is not null && ById.TryGetValue(id, out var definition) ? definition : null;

    /// <summary>Every built-in of one kind, in display order.</summary>
    public static IEnumerable<ComponentDefinition> OfKind(PlateComponentKind kind)
    {
        foreach (var definition in All)
        {
            if (definition.Kind == kind)
            {
                yield return definition;
            }
        }
    }

    private static Dictionary<string, ComponentDefinition> BuildIndex()
    {
        var index = new Dictionary<string, ComponentDefinition>(StringComparer.Ordinal);
        foreach (var definition in All)
        {
            index.Add(definition.Id, definition); // throws on a duplicate id: caught by any test run
        }

        return index;
    }

    private sealed class BuiltInCatalog : IComponentCatalog
    {
        public ComponentDefinition? Find(string? id) => BuiltInComponentCatalog.Find(id);
    }
}

/// <summary>Resolves definition ids. The built-ins are the only source in V1; a user Component
/// library would be another implementation (or a composite), with no change to Plates or rendering.</summary>
public interface IComponentCatalog
{
    ComponentDefinition? Find(string? id);
}
