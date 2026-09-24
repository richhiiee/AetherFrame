using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The character details' refresh: never stuck on an old (or logged-out) read.</summary>
public class CharacterInfoCacheTests
{
    private sealed class Game
    {
        internal BasicCharacterInfo? Info { get; set; }

        internal int Reads { get; private set; }

        internal long Now { get; set; }

        internal CharacterInfoCache Cache() => new(() =>
        {
            Reads++;
            return Info;
        }, () => Now);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(123_456_789L)]
    [InlineData(long.MaxValue - 10)]
    [InlineData(long.MinValue + 10)]
    public void TheFirstAccess_AlwaysReads_WhateverTheClock(long start)
    {
        var game = new Game { Now = start, Info = FakeCharacter.Hero };

        Assert.Equal(FakeCharacter.Hero, game.Cache().Current);
        Assert.Equal(1, game.Reads);
    }

    [Fact]
    public void ALoggedOutRead_IsReplacedOnceTheCharacterLoads()
    {
        // The in-game bug: a first read before login must not stick for the rest of the session.
        var game = new Game { Now = 1_000_000 };
        var cache = game.Cache();
        Assert.Null(cache.Current);

        game.Info = FakeCharacter.Hero;
        game.Now += CharacterInfoCache.RefreshMilliseconds;

        Assert.Equal(FakeCharacter.Hero, cache.Current);
    }

    [Fact]
    public void Reads_AreReusedBriefly_ThenRefreshed()
    {
        var game = new Game { Now = 5_000, Info = FakeCharacter.Hero };
        var cache = game.Cache();
        _ = cache.Current;

        game.Info = FakeCharacter.Hero with { JobName = "Dancer", Level = 92 };
        game.Now += CharacterInfoCache.RefreshMilliseconds - 1;
        Assert.Equal("Paladin", cache.Current!.JobName);
        Assert.Equal(1, game.Reads);

        game.Now += 1;
        Assert.Equal("Dancer", cache.Current!.JobName);
        Assert.Equal(2, game.Reads);
    }

    [Fact]
    public void Invalidate_AndAClockGoingBackwards_ForceAFreshRead()
    {
        var game = new Game { Now = 10_000, Info = FakeCharacter.Hero };
        var cache = game.Cache();
        _ = cache.Current;

        game.Info = null;
        cache.Invalidate();
        Assert.Null(cache.Current);

        game.Info = FakeCharacter.Hero;
        game.Now -= 50;
        Assert.Equal(FakeCharacter.Hero, cache.Current);
        Assert.Equal(3, game.Reads);
    }

    [Fact]
    public async Task TheBasicEditor_SeesACharacterThatLoadsWhileItIsOpen()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(null));
        Assert.Null(harness.Identity.CharacterName);

        harness.Character.CurrentInfo = FakeCharacter.Hero;

        Assert.Equal("Hero Example", harness.Identity.CharacterName);
        harness.Basic.UseCurrentWorld();
        Assert.Equal("Phoenix [Light]", BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.Text);
    }
}

/// <summary>The Identity Header's default hierarchy: name first, title second, tagline third.</summary>
public class IdentityHierarchyTests
{
    [Fact]
    public async Task ANewClassicPlate_LeadsWithABoldShadowedName_ThenTitle()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Identity.SetCustomTitle("the Wanderer");
        harness.Identity.Commit();

        var name = BasicSections.FindText(harness.Document, ProfileElementRole.BasicName)!;
        var title = BasicSections.FindText(harness.Document, ProfileElementRole.BasicTitle)!;
        Assert.True(name.Bold);
        Assert.True(name.ShadowEnabled);
        Assert.InRange(name.ShadowOpacity, 0.2f, 0.6f);

        // The theme's dedicated name treatment: its display color with a subtle contrasting outline.
        var theme = ProfileThemePresets.All[0];
        Assert.Equal(theme.PreferredNameColor, name.Color);
        Assert.True(name.OutlineEnabled);
        Assert.Equal(theme.PreferredNameOutlineColor, name.OutlineColor);
        Assert.InRange(name.OutlineThickness, 0.5f, 3f);
        Assert.InRange(name.OutlineOpacity, 0.3f, 0.85f);
        Assert.Equal(43f, name.FontSize);
        Assert.False(title.Bold);
        Assert.False(title.ShadowEnabled);
        Assert.True(title.FontSize < name.FontSize * 0.6f);
        Assert.Equal(ProfileThemePresets.All[0].AccentTextColor, title.Color);

        // The larger name still leaves the header clear of the first section row.
        var header = new[] { name, title };
        var bottom = header.Max(e => e.Position.Y + e.Size.Y);
        Assert.True(bottom < BasicSections.Find(harness.Document, ProfileElementRole.BasicWorldHeading)!.Position.Y);
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
    }

    [Fact]
    public async Task ResetSection_AppliesTheNewDefaults_ButKeepsTheText()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Identity.EditStyle(ProfileElementRole.BasicName, e =>
        {
            e.Bold = false;
            e.ShadowEnabled = false;
            e.FontSize = 20;
        }, continuous: false);

        harness.Basic.ResetSection(BasicSection.Identity);

        var name = BasicSections.FindText(harness.Document, ProfileElementRole.BasicName)!;
        Assert.True(name.Bold);
        Assert.True(name.ShadowEnabled);
        Assert.Equal(43f, name.FontSize);
        Assert.Equal("Hero Example", name.Text);
    }

    [Fact]
    public async Task AnExistingCustomizedHeader_IsNeverRestyledOrMoved_ByOpeningOrOtherEdits()
    {
        var plateId = Guid.NewGuid();
        var document = BasicDocuments.Blank();
        document.ProfileId = plateId;
        document.BasicIdentity = new BasicIdentityHeader { Layout = IdentityTitleLayout.Subtitle, RegionPosition = new Vector2(300, 400), RegionWidth = 500 };
        var customized = new TextProfileElement
        {
            Role = ProfileElementRole.BasicName,
            Text = "Old Style",
            FontSize = 30,
            Color = new Vector4(1, 0, 0, 1),
            OutlineEnabled = true,
            Position = new Vector2(300, 400),
            Size = new Vector2(500, 50),
        };
        document.Elements.Add(customized);
        using var harness = await BasicHarness.OpenDocumentAsync(document);
        var before = BasicSections.Find(harness.Document, ProfileElementRole.BasicName)!.Clone();

        harness.SimulateBasicFrame();
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.SetText(ProfileElementRole.BasicWorld, "Phoenix");
        harness.Basic.CommitTextEdit();
        harness.Identity.SetCustomTitle("A new title");
        harness.Identity.Commit();

        var after = BasicSections.Find(harness.Document, ProfileElementRole.BasicName)!;
        Assert.True(after.ContentEquals(before));
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
    }

    [Fact]
    public async Task ApplyLayout_MovesButDoesNotRestyleTheName()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Identity.EditStyle(ProfileElementRole.BasicName, e => e.Bold = false, continuous: false);
        harness.DragInAdvanced(ProfileElementRole.BasicName, new Vector2(0, 200));

        harness.Basic.ApplyLayout();

        var name = BasicSections.FindText(harness.Document, ProfileElementRole.BasicName)!;
        Assert.False(name.Bold);
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
    }
}

/// <summary>The Basic editor's presentation rules: panel order and preview size.</summary>
public class BasicEditorViewTests
{
    [Fact]
    public void TheFlow_StartsWithLayoutThenThemeThenPortraitThenIdentity_AndShowsEveryPanelOnce()
    {
        Assert.Equal(
            [
                BasicEditorPanel.PlateLayout, BasicEditorPanel.BackgroundTheme, BasicEditorPanel.Portrait, BasicEditorPanel.Identity,
                BasicEditorPanel.HomeWorld, BasicEditorPanel.JobAndLevel, BasicEditorPanel.FreeCompany,
                BasicEditorPanel.Playstyle, BasicEditorPanel.ActiveHours, BasicEditorPanel.Message,
            ],
            BasicEditorView.PanelOrder);
        Assert.Equal(Enum.GetValues<BasicEditorPanel>().Length, BasicEditorView.PanelOrder.Distinct().Count());
    }

    [Fact]
    public void Fit_ShowsTheWholePlate_Centered()
    {
        var (scale, size) = BasicEditorView.ComputePreview(new Vector2(640, 600), 1280, 720, PreviewZoom.Fit);

        Assert.Equal(0.5f, scale);
        Assert.Equal(new Vector2(640, 360), size);
        Assert.Equal(new Vector2(0, 120), BasicEditorView.PreviewOffset(new Vector2(640, 600), size));
    }

    [Theory]
    [InlineData((int)PreviewZoom.Large, 0.75f)]
    [InlineData((int)PreviewZoom.Larger, 1f)]
    public void Zoom_EnlargesThePreviewOnly(int zoomValue, float expectedScale)
    {
        var zoom = (PreviewZoom)zoomValue;
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var name = BasicSections.FindText(document, ProfileElementRole.BasicName)!;
        var fontSize = name.FontSize;

        var (scale, size) = BasicEditorView.ComputePreview(new Vector2(640, 600), document.CanvasWidth, document.CanvasHeight, zoom);

        Assert.Equal(expectedScale, scale, 3);
        Assert.Equal(new Vector2(document.CanvasWidth, document.CanvasHeight) * expectedScale, size);
        Assert.Equal(Vector2.Zero, BasicEditorView.PreviewOffset(new Vector2(640, 600), new Vector2(1280, 720)));

        // The Plate itself never changes size.
        Assert.Equal(1280f, document.CanvasWidth);
        Assert.Equal(fontSize, name.FontSize);
    }

    [Fact]
    public void NoSpace_OrNoCanvas_DrawsNothing()
    {
        Assert.Equal(0f, BasicEditorView.ComputePreview(Vector2.Zero, 1280, 720, PreviewZoom.Fit).Scale);
        Assert.Equal(0f, BasicEditorView.ComputePreview(new Vector2(100, 100), 0, 720, PreviewZoom.Larger).Scale);
    }
}

/// <summary>Favorite Job and Level read as one unit on the Plate.</summary>
public class JobAndLevelLayoutTests
{
    [Fact]
    public void TheLevel_SitsJustBeforeTheJob_OnTheSameLine()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var level = BasicSections.FindText(document, ProfileElementRole.BasicLevel)!;
        var job = BasicSections.FindText(document, ProfileElementRole.BasicJob)!;

        Assert.Equal(level.Position.Y, job.Position.Y);
        Assert.InRange(job.Position.X - (level.Position.X + level.Size.X), 0f, 10f);
        Assert.Equal(TextAlignment.Left, level.Alignment);
        Assert.Equal(BasicSections.Find(document, ProfileElementRole.BasicJobHeading)!.Position.X, level.Position.X);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(100)]
    public void TheLevel_AlignsFlushWithTheOtherDetailValues(int levelValue)
    {
        // "Lv. 1" and "Lv. 100" are different widths; the level text must start at the same X either
        // way — the same column left edge every other Details value (Home World, Free Company...)
        // already starts at. Regression coverage for the level being right-aligned in its box, which
        // left a visible gap before short levels instead of a fixed, always-present indent.
        var document = BasicDocuments.Classic(FakeCharacter.Hero with { Level = levelValue });
        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorld)!;
        var level = BasicSections.FindText(document, ProfileElementRole.BasicLevel)!;

        Assert.Equal(TextAlignment.Left, level.Alignment);
        Assert.Equal(world.Position.X, level.Position.X);
    }

    [Fact]
    public void TheLevel_AlignsFlushWithTheOtherDetailValues_Mirrored()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero with { Level = 1 });
        BasicDocuments.Editor(document).SetOrientation(AdventurePlateOrientation.Mirrored);
        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorld)!;
        var level = BasicSections.FindText(document, ProfileElementRole.BasicLevel)!;

        Assert.Equal(TextAlignment.Left, level.Alignment);
        Assert.Equal(world.Position.X, level.Position.X);
    }

    [Fact]
    public async Task ApplyingAndResetting_JobAndLevelTogether_IsOneUndoStep()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.DragInAdvanced(ProfileElementRole.BasicJob, new Vector2(20, 20));
        harness.DragInAdvanced(ProfileElementRole.BasicLevel, new Vector2(20, 20));
        var dragged = harness.Json();

        harness.Basic.ApplySectionLayout([BasicSection.Job, BasicSection.Level]);
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
        harness.Session.Undo();
        Assert.Equal(dragged, harness.Json());

        harness.Basic.ResetSection([BasicSection.Job, BasicSection.Level]);
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
        harness.Session.Undo();
        Assert.Equal(dragged, harness.Json());
    }
}
