using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The Basic character name's dedicated theme treatment (color and outline), custom colors, and Name Backing contrast.</summary>
public class BasicNameColorTests
{
    private static readonly ProfileThemePreset Dark = ProfileThemePresets.Find("Dark")!;
    private static readonly ProfileThemePreset Ivory = ProfileThemePresets.Find("Ivory")!;
    private static readonly ProfileThemePreset Warm = ProfileThemePresets.Find("Warm")!;
    private static readonly ProfileThemePreset Neon = ProfileThemePresets.Find("Neon")!;
    private static readonly ProfileThemePreset Pastel = ProfileThemePresets.Find("Pastel")!;

    private static readonly Vector4 Custom = new(0.9f, 0.2f, 0.6f, 1f);
    private static readonly Vector4 PlayerOutline = new(0.1f, 0.6f, 0.2f, 1f);

    private static Task<BasicHarness> NewClassicAsync() =>
        BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));

    private static TextProfileElement Name(ProfileDocument document) =>
        (TextProfileElement)BasicSections.Find(document, ProfileElementRole.BasicName)!;

    // ---- Theme metadata: an intentional treatment per theme -------------------------------

    [Fact]
    public void EveryBuiltInTheme_HasItsOwnNameTreatment()
    {
        foreach (var theme in ProfileThemePresets.All)
        {
            // Explicit metadata on every theme: nothing falls through to TextColor or the black/white outline fallback.
            Assert.NotNull(theme.NameColor);
            Assert.NotNull(theme.NameOutlineColor);
            Assert.NotNull(theme.NameOutlineStrength);
            Assert.InRange(theme.PreferredNameOutlineStrength, 0.3f, 0.8f);
            Assert.Equal(1f, theme.PreferredNameColor.W);
            Assert.NotEqual(theme.AccentTextColor, theme.PreferredNameColor); // not the raw accent
            Assert.NotEqual(theme.TextColor, theme.PreferredNameColor);       // not body text
            Assert.True(BasicNameColor.FirstTreatment.ContainsKey(theme.Id), theme.Id);
        }

        // Not one white name for everything.
        var distinct = ProfileThemePresets.All.Select(t => t.PreferredNameColor).Distinct().Count();
        Assert.True(distinct >= 29, $"{distinct} distinct name colors");
    }

    [Fact]
    public void EveryName_IsBrightDisplayText_NeverADarkOrMuddyInk()
    {
        foreach (var theme in ProfileThemePresets.All)
        {
            var name = theme.PreferredNameColor;
            var (_, saturation, _) = Hsv(name);
            var luminance = ComponentDefinition.RelativeLuminance(name);

            // Bright enough to be display text on every theme, light ones included.
            Assert.True(luminance >= 0.15f, $"{theme.Id}: luminance {luminance:0.00}");

            // A clearly theme-tinted color (not a gray), or white/near-white where white suits the theme.
            Assert.True(saturation >= 0.25f || luminance >= 0.85f, $"{theme.Id}: s {saturation:0.00} L {luminance:0.00}");

            // The outline is a restrained dark edge that contrasts with the fill.
            Assert.True(ComponentDefinition.RelativeLuminance(theme.PreferredNameOutlineColor) < 0.05f, theme.Id);
            Assert.True(Contrast(name, theme.PreferredNameOutlineColor) >= 3.5f, $"{theme.Id}: fill vs outline {Contrast(name, theme.PreferredNameOutlineColor):0.00}");
        }
    }

    [Fact]
    public void DarkAccentThemes_GetALuminousVersionOfTheirHue()
    {
        // These themes' previous name was a dark ink (or their accent is dark): now a bright color of the same family.
        foreach (var id in new[] { "Pastel", "Frost", "Sakura", "Lavender", "Ivory", "Mint", "Peach" })
        {
            var theme = ProfileThemePresets.Find(id)!;
            var old = BasicNameColor.FirstTreatment[id].Name;
            Assert.True(ComponentDefinition.RelativeLuminance(theme.PreferredNameColor) > ComponentDefinition.RelativeLuminance(old) * 1.5f, id);
            Assert.True(ComponentDefinition.RelativeLuminance(theme.PreferredNameColor) > ComponentDefinition.RelativeLuminance(theme.AccentTextColor) * 0.9f, id);
        }
    }

    [Fact]
    public void EveryAutomaticName_SeparatesFromTheBackgroundUnderIt()
    {
        // Large bold display text: along the name's line, in both orientations, either the fill or its
        // outline stands clear of the background everywhere; where the fill alone is soft (a bright name
        // on a bright gradient) the outline is strong enough to carry it.
        foreach (var theme in ProfileThemePresets.All)
        {
            var name = BasicNameColor.Automatic(theme);
            var outline = theme.PreferredNameOutlineColor;
            var separation = NameLine().Min(p => Math.Max(Contrast(name, BackgroundAt(theme, p)), Contrast(outline, BackgroundAt(theme, p))));
            Assert.True(separation >= 3.3f, $"{theme.Id}: separation {separation:0.00}");

            var fill = WorstNameContrast(theme, name);
            var strength = theme.PreferredNameOutlineStrength;
            Assert.True(fill >= 3f || strength >= (fill < 2f ? 0.7f : 0.4f), $"{theme.Id}: fill {fill:0.00}, outline strength {strength:0.00}");
        }
    }

    // ---- Automatic vs custom ----------------------------------------------------------------

    [Fact]
    public async Task AutomaticName_FollowsThemeChanges_ColorAndOutline()
    {
        using var harness = await NewClassicAsync();

        foreach (var theme in new[] { Dark, Ivory, Warm, Neon })
        {
            harness.Basic.ApplyTheme(theme);
            var name = Name(harness.Document);
            Assert.Equal(theme.PreferredNameColor, name.Color);
            Assert.True(name.OutlineEnabled);
            Assert.Equal(theme.PreferredNameOutlineColor, name.OutlineColor);
            Assert.Equal(BasicNameColor.OutlineOpacity(theme), name.OutlineOpacity);
            Assert.Equal(BasicNameColor.OutlineThickness(theme, harness.Document), name.OutlineThickness);
            Assert.True(BasicNameColor.IsAutomatic(harness.Document));
        }
    }

    [Fact]
    public async Task CustomName_SurvivesThemeChanges_Exactly_WithItsOutline()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.ApplyTheme(Dark);
        var name = Name(harness.Document);
        name.Color = Custom;
        var outline = (name.OutlineEnabled, name.OutlineColor, name.OutlineThickness, name.OutlineOpacity);

        harness.Basic.ApplyTheme(Ivory);
        harness.Basic.ApplyTheme(Neon);

        Assert.Equal(Custom, name.Color);
        Assert.Equal(outline, (name.OutlineEnabled, name.OutlineColor, name.OutlineThickness, name.OutlineOpacity));
        Assert.False(BasicNameColor.IsAutomatic(harness.Document));

        // Other Basic text still follows the theme.
        var world = (TextProfileElement?)BasicSections.Find(harness.Document, ProfileElementRole.BasicWorld);
        if (world is not null)
        {
            Assert.Equal(new Vector4(Neon.TextColor.X, Neon.TextColor.Y, Neon.TextColor.Z, world.Color.W), world.Color);
        }
    }

    [Fact]
    public async Task AutomaticName_WithThePlayersOwnOutline_KeepsThatOutline()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.ApplyTheme(Dark);
        var name = Name(harness.Document);
        name.OutlineColor = PlayerOutline;
        name.OutlineThickness = 3f;

        harness.Basic.ApplyTheme(Ivory);

        Assert.Equal(Ivory.PreferredNameColor, name.Color); // the color still follows the theme
        Assert.Equal(PlayerOutline, name.OutlineColor);    // the player's outline stays
        Assert.Equal(3f, name.OutlineThickness);
    }

    [Fact]
    public async Task TheNamesShadow_SitsBehindTheDarkOutline_AndAPlayersShadowStaysTheirs()
    {
        using var harness = await NewClassicAsync();
        var name = Name(harness.Document);
        Assert.True(name.ShadowEnabled);

        harness.Basic.ApplyTheme(Pastel);
        Assert.Equal(BasicNameColor.DarkOutlineShadowOpacity, name.ShadowOpacity);

        // A shadow the player tuned stays theirs.
        name.ShadowOpacity = 0.7f;
        harness.Basic.ApplyTheme(Ivory);
        Assert.Equal(0.7f, name.ShadowOpacity);
    }

    [Fact]
    public void EveryBuiltInTheme_UsesADarkOutline_NotALightHalo()
    {
        foreach (var theme in ProfileThemePresets.All)
        {
            Assert.False(BasicNameColor.IsLightHalo(theme), theme.Id);
        }
    }

    [Fact]
    public async Task ThemeChange_KeepsTheNamesOpacity()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.ApplyTheme(Dark);
        Name(harness.Document).Color = BasicNameColor.Automatic(Dark) with { W = 0.6f };

        harness.Basic.ApplyTheme(Ivory);

        Assert.Equal(BasicNameColor.Automatic(Ivory) with { W = 0.6f }, Name(harness.Document).Color);
    }

    [Fact]
    public async Task Reset_RestoresTheWholeThemeTreatment_AndThenFollowsTheTheme()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.ApplyTheme(Dark);
        var name = Name(harness.Document);
        name.Color = Custom with { W = 0.8f };
        name.OutlineEnabled = false;

        Assert.True(BasicNameColor.Reset(harness.Document));
        Assert.Equal(BasicNameColor.Automatic(Dark) with { W = 0.8f }, name.Color);
        Assert.True(name.OutlineEnabled);
        Assert.Equal(Dark.PreferredNameOutlineColor, name.OutlineColor);
        Assert.False(BasicNameColor.Reset(harness.Document)); // already automatic

        harness.Basic.ApplyTheme(Pastel);
        Assert.Equal(BasicNameColor.Automatic(Pastel) with { W = 0.8f }, name.Color);
        Assert.Equal(Pastel.PreferredNameOutlineColor, name.OutlineColor);
    }

    [Fact]
    public async Task IdentityStyleReset_AlsoRestoresTheThemeTreatment()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.ApplyTheme(Warm);
        var name = Name(harness.Document);
        name.Color = Custom;
        name.OutlineColor = PlayerOutline;

        IdentityHeaderRules.ApplyDefaultStyle(name, harness.Document);

        Assert.Equal(BasicNameColor.Automatic(Warm), name.Color);
        Assert.Equal(Warm.PreferredNameOutlineColor, name.OutlineColor);
        Assert.True(name.OutlineEnabled);
    }

    [Fact]
    public async Task SaveAndReload_PreserveAutomaticAndCustom()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.ApplyTheme(Neon);
        Assert.True(await harness.Session.SaveProfileAsync());
        var automatic = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.True(BasicNameColor.IsAutomatic(automatic));
        Assert.Equal(BasicNameColor.Automatic(Neon), Name(automatic).Color);
        Assert.Equal(Neon.PreferredNameOutlineColor, Name(automatic).OutlineColor);

        Name(harness.Document).Color = Custom;
        await harness.Library.SavePlateDocumentAsync(harness.Document);
        var custom = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.False(BasicNameColor.IsAutomatic(custom));
        Assert.Equal(Custom, Name(custom).Color);
    }

    [Fact]
    public async Task TemplateAndDuplicate_PreserveAutomaticAndCustom()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();

        foreach (var custom in new[] { false, true })
        {
            var created = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Named", new PlateStarterContent(FakeCharacter.Hero));
            var plate = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
            new BasicPlateEditor(plate, e => plate.Elements.Add(e), e => plate.Elements.Remove(e)).ApplyTheme(Ivory);
            if (custom)
            {
                Name(plate).Color = Custom;
            }

            await fixture.PlateLibrary.SavePlateDocumentAsync(plate);

            var templateId = await templates.SaveAsTemplateAsync(created.PlateId, "Named");
            var fromTemplate = fixture.PlateLibrary.OpenDocumentForEditing((await templates.InstantiateAsync(templateId, null)).PlateId);
            var duplicate = fixture.PlateLibrary.OpenDocumentForEditing(await fixture.PlateLibrary.DuplicatePlateAsync(created.PlateId));

            foreach (var copy in new[] { templates.GetSavedDocument(templateId)!, fromTemplate, duplicate })
            {
                Assert.Equal(!custom, BasicNameColor.IsAutomatic(copy));
                Assert.Equal(custom ? Custom : BasicNameColor.Automatic(Ivory), Name(copy).Color);
                Assert.Equal(Ivory.PreferredNameOutlineColor, Name(copy).OutlineColor);
            }
        }
    }

    // ---- Legacy Plates ---------------------------------------------------------------------------

    [Fact]
    public void LegacyAutomaticName_GetsTheThemeTreatmentOnLoad()
    {
        // Saved before themes had a name treatment: the name has Warm's TextColor and no outline.
        var document = PlateWithTheme(Warm, Warm.TextColor with { W = 0.9f });

        var loaded = PlateDocuments.Materialize(PlateDocuments.ToJson(document));

        Assert.Equal(Warm.PreferredNameColor with { W = 0.9f }, Name(loaded).Color);
        Assert.True(Name(loaded).OutlineEnabled);
        Assert.Equal(Warm.PreferredNameOutlineColor, Name(loaded).OutlineColor);
        Assert.True(BasicNameColor.IsAutomatic(loaded));
    }

    [Fact]
    public void LegacyAutomaticName_WithThePlayersOutline_KeepsThatOutline()
    {
        var document = PlateWithTheme(Dark, Dark.TextColor);
        var name = Name(document);
        name.OutlineEnabled = true;
        name.OutlineColor = PlayerOutline;

        var loaded = PlateDocuments.Materialize(PlateDocuments.ToJson(document));

        Assert.Equal(Dark.PreferredNameColor, Name(loaded).Color);
        Assert.Equal(PlayerOutline, Name(loaded).OutlineColor);
    }

    [Theory]
    [InlineData("Dark")]     // representative dark theme: its first treatment was near-white
    [InlineData("Ivory")]    // representative light theme: its first treatment was a dark brown ink
    [InlineData("Lavender")] // a previously dark accent/ink: now a luminous violet
    public void FirstTreatmentName_IsAutomatic_AndUpgradesWithItsOutline(string themeId)
    {
        var theme = ProfileThemePresets.Find(themeId)!;
        var (oldName, oldOutline) = BasicNameColor.FirstTreatment[themeId];
        var document = PlateWithTheme(theme, oldName);
        Name(document).OutlineEnabled = true;
        Name(document).OutlineColor = oldOutline;
        Assert.True(BasicNameColor.IsAutomatic(document)); // never classified as custom

        var loaded = PlateDocuments.Materialize(PlateDocuments.ToJson(document));

        Assert.Equal(theme.PreferredNameColor, Name(loaded).Color);
        Assert.Equal(theme.PreferredNameOutlineColor, Name(loaded).OutlineColor);
        Assert.Equal(BasicNameColor.OutlineOpacity(theme), Name(loaded).OutlineOpacity);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Ivory")]
    [InlineData("Neon")]
    public void IdentityHeaderWhite_IsAutomaticOnEveryTheme_AndUpgrades(string themeId)
    {
        // The first Identity Header gave every name this fixed white whatever the theme; it was never a player's choice.
        var theme = ProfileThemePresets.Find(themeId)!;
        var document = PlateWithTheme(theme, BasicNameColor.IdentityHeaderWhite);

        var loaded = PlateDocuments.Materialize(PlateDocuments.ToJson(document));

        Assert.Equal(theme.PreferredNameColor, Name(loaded).Color);
        Assert.True(Name(loaded).OutlineEnabled);
        Assert.True(BasicNameColor.IsAutomatic(loaded));
    }

    [Fact]
    public void AutomaticNameFromAnOlderModel_FollowsTheNextThemeChange()
    {
        var document = PlateWithTheme(Dark, BasicNameColor.FirstTreatment["Dark"].Name);
        var editor = new BasicPlateEditor(document, e => document.Elements.Add(e), e => document.Elements.Remove(e));

        editor.ApplyTheme(Pastel);

        Assert.Equal(Pastel.PreferredNameColor, Name(document).Color);
    }

    [Fact]
    public void ClickingThroughEveryTheme_AlwaysGivesThatThemesOwnName()
    {
        var document = PlateWithTheme(Dark, Dark.PreferredNameColor);
        var editor = new BasicPlateEditor(document, e => document.Elements.Add(e), e => document.Elements.Remove(e));
        foreach (var theme in ProfileThemePresets.All)
        {
            editor.ApplyTheme(theme);
            Assert.Equal(theme.PreferredNameColor, Name(document).Color);
            Assert.Equal(theme.PreferredNameOutlineColor, Name(document).OutlineColor);
        }
    }

    [Fact]
    public void LegacyCustomName_LoadsExactlyAsSaved()
    {
        var document = PlateWithTheme(Warm, Custom);
        var json = PlateDocuments.ToJson(document);

        var loaded = PlateDocuments.Materialize((JsonObject)json.DeepClone());

        Assert.True(JsonNode.DeepEquals(json, PlateDocuments.ToJson(loaded)));
    }

    [Fact]
    public void TheUpgrade_IsIdempotent()
    {
        var document = PlateWithTheme(Pastel, Pastel.TextColor);
        var once = PlateDocuments.ToJson(PlateDocuments.Materialize(PlateDocuments.ToJson(document)));

        var twice = PlateDocuments.ToJson(PlateDocuments.Materialize((JsonObject)once.DeepClone()));

        Assert.True(JsonNode.DeepEquals(once, twice));
    }

    [Fact]
    public void NonBasicPlates_AreNeverRestyled()
    {
        var document = PlateWithTheme(Warm, Warm.TextColor);
        document.BasicPlate = null;

        Assert.False(BasicNameColor.UpgradeLegacy(document));
        Assert.Equal(Warm.TextColor, Name(document).Color);
        Assert.False(Name(document).OutlineEnabled);
    }

    // ---- Name Backing ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Dark", true)]
    [InlineData("Midnight", true)]
    [InlineData("Ivory", false)]
    [InlineData("Pastel", false)]
    [InlineData("Frost", false)]
    public void NameBacking_ContrastsWithTheName(string themeId, bool expectDarkBacking)
    {
        var theme = ProfileThemePresets.Find(themeId)!;
        var document = PlateWithTheme(theme, BasicNameColor.Automatic(theme));

        foreach (var id in new[] { BuiltInComponentCatalog.NameBackingBar, BuiltInComponentCatalog.NameBackingFade })
        {
            var backing = BuiltInComponentCatalog.Find(id)!.DefaultColor(document);
            Assert.Equal(expectDarkBacking ? Vector3.Zero : Vector3.One, new Vector3(backing.X, backing.Y, backing.Z));
        }
    }

    [Fact]
    public void NameBacking_FollowsACustomNameColor()
    {
        var document = PlateWithTheme(Ivory, new Vector4(1f, 1f, 0.9f, 1f)); // a light custom name on a light theme
        var backing = BuiltInComponentCatalog.Find(BuiltInComponentCatalog.NameBackingBar)!.DefaultColor(document);

        Assert.Equal(Vector3.Zero, new Vector3(backing.X, backing.Y, backing.Z));
    }

    [Fact]
    public void ColoredNameOnLightTheme_ReadsThroughItsBacking()
    {
        // The regression: black 35% / 50% backings under a colored name on light themes.
        foreach (var theme in new[] { Pastel, Ivory, ProfileThemePresets.Find("Frost")! })
        {
            var name = BasicNameColor.Automatic(theme);
            var document = PlateWithTheme(theme, name);
            foreach (var id in new[] { BuiltInComponentCatalog.NameBackingBar, BuiltInComponentCatalog.NameBackingFade })
            {
                var backing = BuiltInComponentCatalog.Find(id)!.DefaultColor(document);
                var under = Blend(theme.PrimaryColor, backing);
                // The vivid fill stays distinct from the backing, and the dark outline gives the crisp edge.
                Assert.True(Contrast(name, under) >= 2.5f, $"{theme.Id} {id}: {Contrast(name, under):0.00}");
                Assert.True(Contrast(theme.PreferredNameOutlineColor, under) >= 7f, $"{theme.Id} {id}: outline {Contrast(theme.PreferredNameOutlineColor, under):0.00}");
            }
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static ProfileDocument PlateWithTheme(ProfileThemePreset theme, Vector4 nameColor)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Legacy", ComponentDocuments.Now);
        document.BasicPlate = new BasicPlateSettings { ThemeId = theme.Id };
        document.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicName, Text = "Hero", Color = nameColor, Position = new Vector2(480, 60), Size = new Vector2(700, 60) });
        return document;
    }

    private static Vector4 Blend(Vector4 under, Vector4 over) =>
        new(under.X + ((over.X - under.X) * over.W), under.Y + ((over.Y - under.Y) * over.W), under.Z + ((over.Z - under.Z) * over.W), 1f);

    private static float Contrast(Vector4 a, Vector4 b)
    {
        var la = ComponentDefinition.RelativeLuminance(a);
        var lb = ComponentDefinition.RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05f) / (Math.Min(la, lb) + 0.05f);
    }

    private static (float Hue, float Saturation, float Value) Hsv(Vector4 c)
    {
        var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        var min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        return (0f, max > 0f ? (max - min) / max : 0f, max);
    }

    private static Vector4 BackgroundAt(ProfileThemePreset theme, Vector2 point)
    {
        var size = new Vector2(1280, 720);
        var radians = theme.GradientAngle * (MathF.PI / 180f);
        var direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var halfLength = (MathF.Abs(size.X * direction.X) + MathF.Abs(size.Y * direction.Y)) / 2f;
        var t = (Vector2.Dot(point - (size / 2f), direction) / (2f * halfLength)) + 0.5f;
        return Vector4.Lerp(theme.PrimaryColor, theme.SecondaryColor, t);
    }

    private static System.Collections.Generic.IEnumerable<Vector2> NameLine()
    {
        foreach (var left in new[] { 480f, 40f })
        {
            for (var x = left; x <= left + 520f; x += 65f)
            {
                foreach (var y in new[] { 60f, 80f, 100f })
                {
                    yield return new Vector2(x, y);
                }
            }
        }
    }

    /// <summary>Worst contrast of <paramref name="name"/> along the name's line on the theme's gradient, both orientations (1280x720).</summary>
    private static float WorstNameContrast(ProfileThemePreset theme, Vector4 name) =>
        NameLine().Min(p => Contrast(name, BackgroundAt(theme, p)));

    private static Vector4 MeanBackgroundUnderName(ProfileThemePreset theme)
    {
        var points = NameLine().ToList();
        return points.Aggregate(Vector4.Zero, (sum, p) => sum + BackgroundAt(theme, p)) / points.Count;
    }
}
