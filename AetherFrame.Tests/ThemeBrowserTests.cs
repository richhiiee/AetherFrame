using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Basic editor's Theme browser: one searchable, filterable collection of the whole catalog.
/// Browsing never changes a Plate; applying a theme still stores its stable id.
/// </summary>
public class ThemeBrowserTests
{
    private static readonly ProfileThemePreset[] All = ProfileThemePresets.All;

    private static string[] Ids(System.Collections.Generic.IEnumerable<ProfileThemePreset> themes) => themes.Select(t => t.Id).ToArray();

    // ---------------------------------------------------------------- search

    [Theory]
    [InlineData("royal")]
    [InlineData("ROYAL")]
    [InlineData("RoYaL")]
    [InlineData("  royal  ")]
    public void Search_IsCaseInsensitive(string search)
    {
        Assert.Contains("Royal", Ids(ThemeBrowser.Filter(All, search, null)));
    }

    [Fact]
    public void Search_MatchesNamesDescriptionsAndFamilies()
    {
        Assert.Contains("Royal", Ids(ThemeBrowser.Filter(All, "indigo", null))); // its description
        Assert.All(ThemeBrowser.Filter(All, "pastel", null), theme =>
            Assert.True(theme.Family == ThemeFamily.Pastel || theme.Name.Contains("Pastel", System.StringComparison.OrdinalIgnoreCase)
                || theme.Description.Contains("pastel", System.StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Search_NeedsEveryWord()
    {
        var both = ThemeBrowser.Filter(All, "gold indigo", null);
        Assert.Contains("Royal", Ids(both));
        Assert.Empty(ThemeBrowser.Filter(All, "indigo zzzz-no-such-word", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearingTheSearch_RestoresTheWholeCatalog_InCatalogOrder(string? search)
    {
        Assert.Equal(Ids(All), Ids(ThemeBrowser.Filter(All, search, null)));
    }

    [Fact]
    public void AFilteredThenClearedSearch_ShowsEveryThemeAgain()
    {
        var state = new ThemeBrowserState { Search = "royal", Family = ThemeFamily.Special };
        Assert.True(state.IsFiltered);
        Assert.NotEqual(All.Length, ThemeBrowser.Filter(All, state.Search, state.Family).Count);

        state.Clear();

        Assert.False(state.IsFiltered);
        Assert.Equal(Ids(All), Ids(ThemeBrowser.Filter(All, state.Search, state.Family)));
    }

    [Fact]
    public void NoMatch_GivesAnEmptyCollection()
    {
        Assert.Empty(ThemeBrowser.Filter(All, "no theme is called this", null));
    }

    // ---------------------------------------------------------------- family filters

    [Fact]
    public void AFamilyFilter_ShowsOnlyThatFamily_AndCombinesWithSearch()
    {
        foreach (var (family, count) in ThemeBrowser.Families(All))
        {
            var shown = ThemeBrowser.Filter(All, null, family);
            Assert.Equal(count, shown.Count);
            Assert.All(shown, theme => Assert.Equal(family, theme.Family));
        }

        Assert.Empty(ThemeBrowser.Filter(All, "royal", ThemeFamily.Pastel));
        Assert.Equal(["Royal"], Ids(ThemeBrowser.Filter(All, "royal", ThemeFamily.Special)));
    }

    [Fact]
    public void Filters_AreOfferedOnlyForFamiliesThatHaveThemes_InFamilyOrder()
    {
        var families = ThemeBrowser.Families(All);

        Assert.Equal(ProfileThemePresets.FamilyOrder.Where(f => All.Any(t => t.Family == f)), families.Select(f => f.Family));
        Assert.Equal(All.Length, families.Sum(f => f.Count));
        Assert.Equal([ThemeFamily.Classic], ThemeBrowser.Families(All.Where(t => t.Family == ThemeFamily.Classic).ToArray()).Select(f => f.Family));
    }

    [Theory]
    [InlineData(500f, 118f, 8f, 4)]
    [InlineData(495f, 118f, 8f, 3)]
    [InlineData(100f, 118f, 8f, 1)]
    [InlineData(1200f, 118f, 8f, 9)]
    public void TheGrid_FitsAsManyCardsAsTheWidthAllows(float width, float card, float spacing, int columns)
    {
        Assert.Equal(columns, ThemeBrowser.Columns(width, card, spacing));
    }

    // ---------------------------------------------------------------- selection and data safety

    [Fact]
    public async Task ApplyingAThemeFromTheBrowser_StoresItsExistingId()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        var royal = ThemeBrowser.Filter(All, "royal", null).Single();

        harness.Basic.ApplyTheme(royal);

        Assert.Equal("Royal", harness.Document.BasicPlate!.ThemeId);
        Assert.Same(ProfileThemePresets.Find("Royal"), ThemeBrowser.Current(harness.Document));
    }

    [Fact]
    public async Task TheSelectedTheme_StaysSelected_WhateverTheBrowserShows()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        var dark = ProfileThemePresets.Find("Dark")!;
        harness.Basic.ApplyTheme(dark);
        var before = harness.Json();

        // Filter it out of view, then bring everything back.
        Assert.DoesNotContain("Dark", Ids(ThemeBrowser.Filter(All, "royal", null)));
        Assert.Same(dark, ThemeBrowser.Current(harness.Document));
        Assert.Contains("Dark", Ids(ThemeBrowser.Filter(All, string.Empty, null)));

        Assert.Same(dark, ThemeBrowser.Current(harness.Document));
        Assert.Equal(before, harness.Json());
    }

    [Fact]
    public async Task AnExistingSavedPlate_ReopensWithExactlyTheSameTheme_AndBrowsingChangesNothing()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.BasicPlate!.ThemeId = "Warm";
        using var harness = await BasicHarness.OpenDocumentAsync(document);
        var before = harness.Json();

        harness.SimulateBasicFrame();
        foreach (var (family, _) in ThemeBrowser.Families(All))
        {
            _ = ThemeBrowser.Filter(All, "a", family);
        }

        Assert.Equal("Warm", ThemeBrowser.Current(harness.Document)!.Id);
        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public void APlateWithoutATheme_HasNoCurrentTheme()
    {
        Assert.Null(ThemeBrowser.Current(BasicDocuments.Blank()));
    }

    [Fact]
    public void TheBrowser_ShowsEveryThemeIdExactlyOnce()
    {
        var shown = Ids(ThemeBrowser.Filter(All, null, null));
        Assert.Equal(shown.Length, shown.Distinct().Count());
        Assert.Equal(Ids(All), shown);
    }
}
