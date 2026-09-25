using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Adventure Plate Classic geometry: Normal and Mirrored, on any canvas.</summary>
public class AdventurePlateLayoutTests
{
    private static ElementRect Rect(ProfileElementRole role, AdventurePlateOrientation orientation, ProfileDocument? document = null) =>
        AdventurePlateClassicLayout.GetRect(role, orientation, document ?? BasicDocuments.Blank())!.Value;

    // Every role the layout places (the retired Level isn't placed at all).
    private static IEnumerable<ProfileElementRole> SectionRoles() =>
        BasicSections.ElementSections.SelectMany(BasicPlateEditor.RolesOf).Where(r => !BasicSections.IsRetired(r));

    [Fact]
    public void Normal_PutsThePortraitLeft_AndTheDetailsPanelRight()
    {
        Assert.Equal(new ElementRect(new Vector2(40, 40), new Vector2(400, 640)), Rect(ProfileElementRole.BasicPortrait, AdventurePlateOrientation.Normal));
        Assert.Equal(new Vector2(480, 196), Rect(ProfileElementRole.BasicWorldHeading, AdventurePlateOrientation.Normal).Position);

        var (region, width) = AdventurePlateClassicLayout.GetIdentityRegion(AdventurePlateOrientation.Normal, BasicDocuments.Blank());
        Assert.Equal(new Vector2(480, 44), region);
        Assert.Equal(760, width);
    }

    [Fact]
    public void Mirrored_SwapsPortraitAndPanel_KeepingThePanelsReadingOrder()
    {
        Assert.Equal(new Vector2(840, 40), Rect(ProfileElementRole.BasicPortrait, AdventurePlateOrientation.Mirrored).Position);
        Assert.Equal(new Vector2(40, 196), Rect(ProfileElementRole.BasicWorldHeading, AdventurePlateOrientation.Mirrored).Position);
        Assert.Equal(40f, AdventurePlateClassicLayout.GetIdentityRegion(AdventurePlateOrientation.Mirrored, BasicDocuments.Blank()).Position.X);

        // Inside the panel nothing reorders: World stays left of Free Company, Favorite Jobs left of Active Hours.
        foreach (var orientation in new[] { AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored })
        {
            Assert.True(Rect(ProfileElementRole.BasicWorld, orientation).Position.X < Rect(ProfileElementRole.BasicFreeCompany, orientation).Position.X);
            Assert.True(Rect(ProfileElementRole.BasicJob, orientation).Position.X < Rect(ProfileElementRole.BasicActiveHours, orientation).Position.X);
            Assert.Null(AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicLevel, orientation, BasicDocuments.Blank()));
        }

        // Every panel element moves by the same amount.
        foreach (var role in SectionRoles().Where(r => r != ProfileElementRole.BasicPortrait))
        {
            Assert.Equal(440f, Rect(role, AdventurePlateOrientation.Normal).Position.X - Rect(role, AdventurePlateOrientation.Mirrored).Position.X, 3);
        }
    }

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public void EveryElement_FitsTheCanvas_AndNoTwoOverlap(AdventurePlateOrientation orientation)
    {
        var rects = SectionRoles().Select(r => Rect(r, orientation)).ToList();
        var (region, width) = AdventurePlateClassicLayout.GetIdentityRegion(orientation, BasicDocuments.Blank());
        rects.Add(new ElementRect(region, new Vector2(width, 150)));

        foreach (var rect in rects)
        {
            Assert.InRange(rect.Position.X, 0, 1280 - rect.Size.X);
            Assert.InRange(rect.Position.Y, 0, 720 - rect.Size.Y);
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                var overlaps = a.Position.X < b.Position.X + b.Size.X && b.Position.X < a.Position.X + a.Size.X
                    && a.Position.Y < b.Position.Y + b.Size.Y && b.Position.Y < a.Position.Y + a.Size.Y;
                Assert.False(overlaps, $"{i} overlaps {j}");
            }
        }
    }

    [Fact]
    public void Layout_ScalesToTheCanvas_WithoutResizingIt()
    {
        var legacy = BasicDocuments.Blank(1920, 1080);

        Assert.Equal(new ElementRect(new Vector2(60, 60), new Vector2(600, 960)), Rect(ProfileElementRole.BasicPortrait, AdventurePlateOrientation.Normal, legacy));
        Assert.Equal(1920f, legacy.CanvasWidth);
        Assert.Equal(30f, ((TextProfileElement)AdventurePlateClassicLayout.CreateElement(ProfileElementRole.BasicWorld, legacy)).FontSize);
    }

    [Fact]
    public void IdentityRoles_AndFreeformElements_AreNotPlacedIndividually()
    {
        Assert.Null(AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicName, AdventurePlateOrientation.Normal, BasicDocuments.Blank()));
        Assert.Null(AdventurePlateClassicLayout.GetRect(ProfileElementRole.None, AdventurePlateOrientation.Normal, BasicDocuments.Blank()));
    }

    [Fact]
    public void CreatedElements_UseTheLayoutsTypography_AndTheThemesColors()
    {
        var document = BasicDocuments.Blank();
        var heading = (TextProfileElement)AdventurePlateClassicLayout.CreateElement(ProfileElementRole.BasicWorldHeading, document);
        var value = (TextProfileElement)AdventurePlateClassicLayout.CreateElement(ProfileElementRole.BasicWorld, document);
        var portrait = (ImageProfileElement)AdventurePlateClassicLayout.CreateElement(ProfileElementRole.BasicPortrait, document);

        Assert.Equal("HOME WORLD", heading.Text);
        Assert.True(heading.Bold);
        Assert.Equal(ProfileThemePresets.All[0].AccentTextColor, heading.Color);
        Assert.Equal(string.Empty, value.Text);
        Assert.Equal(ProfileThemePresets.All[0].TextColor, value.Color);
        Assert.Equal(ProfileFontFamilies.AetherFrameSans, value.FontFamily);
        Assert.True(value.AutoFitText);
        Assert.Equal(ProfileImageFit.Fill, portrait.DisplayMode);
    }
}

/// <summary>What each structured section displays.</summary>
public class BasicPlateTextTests
{
    [Theory]
    [InlineData("Phoenix", "Light", "Phoenix [Light]")]
    [InlineData("Phoenix", null, "Phoenix")]
    [InlineData(null, "Light", "")]
    [InlineData("  ", "Light", "")]
    public void World(string? world, string? center, string expected) => Assert.Equal(expected, BasicPlateText.World(world, center));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(90, "Lv. 90")]
    [InlineData(250, "Lv. 100")]
    public void Level(int level, string expected) => Assert.Equal(expected, BasicPlateText.Level(level));

    [Fact]
    public void FreeCompanyTag_IsShownLikeTheGame_AndEmptyMeansNone()
    {
        Assert.Equal("«ABC»", BasicPlateText.FreeCompanyTag("ABC"));
        Assert.Equal(string.Empty, BasicPlateText.FreeCompanyTag(""));
        Assert.Equal(string.Empty, BasicPlateText.FreeCompanyTag(null));
    }

    [Fact]
    public void Playstyles_JoinWithASeparatorEveryFontHas()
    {
        Assert.Equal("Casual  ·  Raiding", BasicPlateText.Playstyles(["Casual", "", "Raiding"]));
        Assert.Equal(string.Empty, BasicPlateText.Playstyles([]));
        Assert.All(BasicPlateText.Separator, c => Assert.True(c <= 'ÿ'));
    }

    [Theory]
    [InlineData(BasicWeekdays.None, "")]
    [InlineData(BasicWeekdays.Everyday, "Every day")]
    [InlineData(BasicWeekdays.Weekdays, "Weekdays")]
    [InlineData(BasicWeekdays.Weekends, "Weekends")]
    [InlineData(BasicWeekdays.Monday | BasicWeekdays.Tuesday | BasicWeekdays.Wednesday | BasicWeekdays.Saturday, "Mon-Wed, Sat")]
    [InlineData(BasicWeekdays.Monday | BasicWeekdays.Tuesday, "Mon, Tue")]
    [InlineData(BasicWeekdays.Friday, "Fri")]
    public void Days(BasicWeekdays days, string expected) => Assert.Equal(expected, BasicPlateText.Days(days));

    [Fact]
    public void ActiveHours_FormatsDaysTimesAndZone()
    {
        var hours = new BasicActiveHours { Days = BasicWeekdays.Weekends, StartMinutes = 20 * 60, EndMinutes = 90, TimeZone = "EST" };
        Assert.Equal("Weekends  ·  8 PM - 1:30 AM EST", BasicPlateText.ActiveHours(hours));

        hours.Use24HourClock = true;
        hours.Days = BasicWeekdays.None;
        hours.TimeZone = string.Empty;
        Assert.Equal("20:00 - 01:30", BasicPlateText.ActiveHours(hours));

        hours.EndMinutes = hours.StartMinutes;
        Assert.Equal("All day", BasicPlateText.ActiveHours(hours));
        Assert.Equal(string.Empty, BasicPlateText.ActiveHours(null));
        Assert.Equal("12 AM", BasicPlateText.Time(0, use24HourClock: false));
        Assert.Equal("12:30 PM", BasicPlateText.Time(12 * 60 + 30, use24HourClock: false));
    }

    [Fact]
    public void Playstyle_IsSanitizedAndCapped()
    {
        Assert.Equal("Glamour", BasicPlateText.NormalizePlaystyle("  Glamour\n"));
        Assert.Equal(BasicPlateSettings.MaxPlaystyleLength, BasicPlateText.NormalizePlaystyle(new string('x', 60)).Length);
        Assert.Equal(string.Empty, BasicPlateText.NormalizePlaystyle("   "));
    }
}

/// <summary>Sections, finished rendering, and placeholders.</summary>
public class BasicSectionTests
{
    [Fact]
    public void EveryBasicRole_BelongsToExactlyOneSection_AndGetMatchesTheEnum()
    {
        // Every role Basic owns; the retired Tagline role (kept so older Plates load) is Advanced content.
        foreach (var role in Enum.GetValues<ProfileElementRole>().Where(r => r is not ProfileElementRole.None and not ProfileElementRole.BasicTagline))
        {
            Assert.NotNull(BasicSections.SectionOf(role));
            Assert.NotNull(ProfileElementNames.GetRoleLabel(role));
        }

        Assert.Null(BasicSections.SectionOf(ProfileElementRole.BasicTagline));
        Assert.Equal("Tagline", ProfileElementNames.GetRoleLabel(ProfileElementRole.BasicTagline));

        foreach (var section in Enum.GetValues<BasicSection>())
        {
            Assert.Equal(section, BasicSections.Get(section).Section);
        }

        Assert.Null(BasicSections.SectionOf(ProfileElementRole.None));
        Assert.Null(BasicSections.SectionOf((ProfileElementRole)999));
    }

    [Fact]
    public void RoleValues_AreStable()
    {
        // Persisted numerically: these must never change.
        Assert.Equal(5, (int)ProfileElementRole.BasicTagline);
        Assert.Equal(6, (int)ProfileElementRole.BasicWorld);
        Assert.Equal(10, (int)ProfileElementRole.BasicLevel);
        Assert.Equal(17, (int)ProfileElementRole.BasicMessageHeading);
        Assert.Equal(1, (int)AdventurePlateOrientation.Mirrored);
        Assert.Equal(0, (int)BasicPortraitSource.ImportedImage);
        Assert.Equal(1, (int)BasicPortraitSource.CurrentPortrait);
        Assert.Equal(2, (int)BasicPortraitSource.AetherFrameScene);
    }

    [Fact]
    public void FinishedRendering_SkipsHeadingsOfEmptyOrHiddenSections_Only()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        bool Drawn(ProfileElementRole role) =>
            BasicSections.IsDrawnInFinishedRendering(document, BasicSections.Find(document, role)!);

        Assert.True(Drawn(ProfileElementRole.BasicWorldHeading));
        Assert.False(Drawn(ProfileElementRole.BasicMessageHeading));
        Assert.False(Drawn(ProfileElementRole.BasicPlaystyleHeading));
        Assert.True(Drawn(ProfileElementRole.BasicWorld));
        Assert.True(Drawn(ProfileElementRole.BasicMessage));

        BasicSections.Find(document, ProfileElementRole.BasicWorld)!.Visible = false;
        Assert.False(Drawn(ProfileElementRole.BasicWorldHeading));

        var freeform = new TextProfileElement { Text = string.Empty };
        Assert.True(BasicSections.IsDrawnInFinishedRendering(document, freeform));
    }

    [Fact]
    public void Placeholders_ExistOnlyInEditorCanvasOptions()
    {
        Assert.Null(ProfileRenderOptions.Finished.PlaceholderProvider);
        Assert.False(ProfileRenderOptions.Finished.ShowElementBounds);
        Assert.False(ProfileRenderOptions.Finished.ShowEmptySectionHeadings);
        Assert.NotNull(EditorPlaceholders.CanvasOptions.PlaceholderProvider);
        Assert.True(EditorPlaceholders.CanvasOptionsWithoutGuides.ShowEmptySectionHeadings);

        Assert.Equal("Add a message", EditorPlaceholders.GetPlaceholder(new TextProfileElement { Role = ProfileElementRole.BasicMessage }));
        Assert.Equal("Free Company", EditorPlaceholders.GetPlaceholder(new TextProfileElement { Role = ProfileElementRole.BasicFreeCompany }));
        Assert.Null(EditorPlaceholders.GetPlaceholder(new ImageProfileElement { Role = ProfileElementRole.BasicPortrait }));
    }
}

/// <summary>The persisted Basic settings.</summary>
public class BasicPlateSettingsTests
{
    private static BasicPlateSettings Sample()
    {
        var settings = new BasicPlateSettings
        {
            Orientation = AdventurePlateOrientation.Mirrored,
            Playstyles = ["Casual", "Raiding"],
            ActiveHours = new BasicActiveHours { Days = BasicWeekdays.Weekends, TimeZone = "EST" },
            FavoriteJobId = 19,
            Level = 90,
            ThemeId = "Pastel",
        };
        settings.SetPlacement(ProfileElementRole.BasicWorld, new ElementRect(new Vector2(1, 2), new Vector2(3, 4)));
        return settings;
    }

    [Fact]
    public void Clone_IsIndependent_AndContentEqual()
    {
        var settings = Sample();
        var clone = settings.Clone();

        Assert.True(clone.ContentEquals(settings));
        clone.Playstyles.Add("PvP");
        clone.SetPlacement(ProfileElementRole.BasicWorld, new ElementRect(Vector2.Zero, Vector2.One));
        clone.ActiveHours!.Days = BasicWeekdays.None;

        Assert.Equal(2, settings.Playstyles.Count);
        Assert.Equal(new Vector2(1, 2), settings.GetPlacement(ProfileElementRole.BasicWorld)!.Value.Position);
        Assert.Equal(BasicWeekdays.Weekends, settings.ActiveHours!.Days);
        Assert.False(clone.ContentEquals(settings));
    }

    [Fact]
    public void RoundTripsThroughJson_WithUnknownFieldsPreserved()
    {
        var node = JsonSerializer.SerializeToNode(Sample(), JsonOptions.Default)!.AsObject();
        node["FutureSetting"] = "kept";
        node["Placements"]![0]!["FutureAnchor"] = 3;
        node["Placements"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["Role"] = 250,
            ["Rect"] = new System.Text.Json.Nodes.JsonObject { ["Position"] = new System.Text.Json.Nodes.JsonObject { ["X"] = 1, ["Y"] = 1 } },
        });

        var loaded = JsonSerializer.Deserialize<BasicPlateSettings>(node.ToJsonString(), JsonOptions.Default)!;
        var saved = JsonSerializer.SerializeToNode(loaded.Clone(), JsonOptions.Default)!.AsObject();

        Assert.Equal(AdventurePlateOrientation.Mirrored, loaded.Orientation);
        Assert.Equal(["Casual", "Raiding"], loaded.Playstyles);
        Assert.Equal("EST", loaded.ActiveHours!.TimeZone);
        Assert.Equal(new Vector2(3, 4), loaded.GetPlacement(ProfileElementRole.BasicWorld)!.Value.Size);
        Assert.Equal("kept", saved["FutureSetting"]!.GetValue<string>());
        Assert.Equal(3, saved["Placements"]![0]!["FutureAnchor"]!.GetValue<int>());
        Assert.Equal(250, saved["Placements"]![1]!["Role"]!.GetValue<int>());
    }
}
