using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Multiple Favorite Jobs: the ordered list of game job ids, its FAVORITE JOB / FAVORITE JOBS
/// heading, its stored full-names text and the display derived from it whenever the Plate is drawn
/// (full names when they fit the value's current font and width, else the game's abbreviations,
/// else auto fit), the lossless reading of Plates that stored one job, the retired Level, the shared
/// history and persistence, and templates and packages.
/// </summary>
public class FavoriteJobsTests
{
    private static string JobText(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicJob)?.Text ?? string.Empty;

    private static string Heading(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicJobHeading)!.Text;

    private static uint[] Ids(ProfileDocument document) => BasicFavoriteJobs.IdsOf(document).ToArray();

    private static TextProfileElement JobElement(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicJob)!;

    /// <summary>A deterministic "font": half the size per character, wider for the serif and mono families.</summary>
    private static float FakeWidth(TextProfileElement element, string text)
    {
        var family = element.FontFamily switch
        {
            ProfileFontFamilies.AetherFrameSerif => 1.15f,
            ProfileFontFamilies.AetherFrameMono => 1.2f,
            _ => 1f,
        };
        return text.Length * element.FontSize * 0.5f * family;
    }

    /// <summary>What the Plate shows for the Favorite Jobs right now, exactly as the renderer derives it.</summary>
    private static string Shown(ProfileDocument document, Func<string, float?>? measure = null)
    {
        var job = JobElement(document);
        var jobs = new FakeJobs();
        return BasicFavoriteJobs.DisplayText(document, job, jobs.Find, measure ?? (text => FakeWidth(job, text))) ?? job.GetDisplayText();
    }

    private static void EditJobStyle(BasicHarness harness, Action<TextProfileElement> change) =>
        harness.Session.ApplyImmediateEdit(JobElement(harness.Document).Id, element => change((TextProfileElement)element));

    private static void Add(BasicHarness harness, params FavoriteJob[] jobs)
    {
        foreach (var job in jobs)
        {
            harness.Basic.AddFavoriteJob(job.Id);
        }
    }

    // ---------------------------------------------------------------- one job saved before multiple Favorite Jobs

    [Fact]
    public async Task APlateThatStoredOneFavoriteJobId_ReadsAsAOneJobList_WithoutChangingIt()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var node = JsonSerializer.SerializeToNode(document, JsonOptions.Default)!.AsObject();
        node["BasicPlate"]!.AsObject().Remove("FavoriteJobIds"); // exactly as an earlier version saved it
        using var harness = await BasicHarness.OpenJsonAsync(node.ToJsonString(JsonOptions.Default), document.ProfileId);
        var before = harness.Json();

        harness.SimulateBasicFrame();

        Assert.Equal([19u], Ids(harness.Document));
        Assert.Empty(harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal("Paladin", JobText(harness.Document));
        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);

        // The first edit writes the list, keeping that job first.
        harness.Basic.AddFavoriteJob(FakeJobs.WhiteMage.Id);
        Assert.Equal([19u, 24u], harness.Document.BasicPlate.FavoriteJobIds);
        Assert.Equal(19u, harness.Document.BasicPlate.FavoriteJobId);
        Assert.Equal("Paladin, White Mage", JobText(harness.Document));
    }

    [Fact]
    public async Task ANullFavoriteJobIdsList_LoadsAsEmpty()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var node = JsonSerializer.SerializeToNode(document, JsonOptions.Default)!.AsObject();
        node["BasicPlate"]!.AsObject()["FavoriteJobIds"] = null;
        using var harness = await BasicHarness.OpenJsonAsync(node.ToJsonString(JsonOptions.Default), document.ProfileId);

        Assert.NotNull(harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal([19u], Ids(harness.Document)); // falls back to the primary id
    }

    // ---------------------------------------------------------------- one, many, order, heading

    [Fact]
    public async Task OneJob_ShowsItsFullName_UnderFavoriteJob()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.Astrologian);

        Assert.Equal("Astrologian", JobText(harness.Document));
        Assert.Equal(BasicFavoriteJobs.SingularHeading, Heading(harness.Document));
        Assert.Equal([33u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(33u, harness.Document.BasicPlate.FavoriteJobId);
    }

    [Fact]
    public async Task SeveralJobs_KeepTheirOrder_UnderFavoriteJobs()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);

        Assert.Equal("Astrologian, White Mage", JobText(harness.Document));
        Assert.Equal(BasicFavoriteJobs.PluralHeading, Heading(harness.Document));
        Assert.Equal([33u, 24u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(33u, harness.Document.BasicPlate.FavoriteJobId); // the primary favorite
    }

    [Fact]
    public async Task TheHeading_FollowsTheCount_AsJobsAreAddedAndRemoved()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.Dancer);
        Assert.Equal("FAVORITE JOB", Heading(harness.Document));
        Add(harness, FakeJobs.RedMage);
        Assert.Equal("FAVORITE JOBS", Heading(harness.Document));
        harness.Basic.RemoveFavoriteJobAt(0);
        Assert.Equal("FAVORITE JOB", Heading(harness.Document));
        harness.Basic.RemoveFavoriteJobAt(0);
        Assert.Equal("FAVORITE JOB", Heading(harness.Document));
        Assert.Equal(string.Empty, JobText(harness.Document));
        Assert.Empty(harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(0u, harness.Document.BasicPlate.FavoriteJobId);
    }

    [Fact]
    public async Task AHeadingGivenItsOwnCaption_IsLeftAlone()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        var heading = BasicSections.FindText(harness.Document, ProfileElementRole.BasicJobHeading)!;
        harness.Session.ApplyImmediateEdit(heading.Id, e => ((TextProfileElement)e).Text = "MAINS");

        Add(harness, FakeJobs.Dancer, FakeJobs.RedMage);

        Assert.Equal("MAINS", Heading(harness.Document));
    }

    [Fact]
    public void ResetSection_GivesTheHeadingTheCaptionForTheCount()
    {
        var document = BasicDocuments.Classic();
        var editor = BasicDocuments.Editor(document);
        editor.SetFavoriteJobs([FakeJobs.Dancer, FakeJobs.RedMage]);

        editor.ResetSection(BasicSection.Job);

        Assert.Equal(BasicFavoriteJobs.PluralHeading, Heading(document));
        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel)); // reset never creates a level
    }

    // ---------------------------------------------------------------- add, remove, reorder, duplicates, Use current

    [Fact]
    public async Task AddRemoveAndReorder_EditTheOrderedList()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.Paladin, FakeJobs.WhiteMage, FakeJobs.Dancer);

        harness.Basic.MoveFavoriteJob(2, -1);
        Assert.Equal([19u, 38u, 24u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal("Paladin, Dancer, White Mage", JobText(harness.Document));

        harness.Basic.MoveFavoriteJob(1, -1);
        Assert.Equal([38u, 19u, 24u], harness.Document.BasicPlate.FavoriteJobIds);
        Assert.Equal(38u, harness.Document.BasicPlate.FavoriteJobId); // the new primary

        harness.Basic.RemoveFavoriteJobAt(1);
        Assert.Equal([38u, 24u], harness.Document.BasicPlate.FavoriteJobIds);
        Assert.Equal("Dancer, White Mage", JobText(harness.Document));

        // Out-of-range moves and removals change nothing.
        var before = harness.Json();
        harness.Basic.MoveFavoriteJob(0, -1);
        harness.Basic.MoveFavoriteJob(1, 1);
        harness.Basic.RemoveFavoriteJobAt(5);
        Assert.Equal(before, harness.Json());
    }

    [Fact]
    public async Task AJobAlreadyChosen_IsNeverAddedTwice()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.Paladin, FakeJobs.WhiteMage);
        var undoSteps = harness.Session.CanUndo;

        harness.Basic.AddFavoriteJob(FakeJobs.Paladin.Id);

        Assert.Equal([19u, 24u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.False(BasicEditorSession.CanAddFavoriteJob(harness.Document, FakeJobs.Paladin.Id));
        Assert.Equal(undoSteps, harness.Session.CanUndo);
        Assert.Equal([19u, 24u], BasicFavoriteJobs.Normalize([FakeJobs.Paladin, FakeJobs.WhiteMage, FakeJobs.Paladin, new FavoriteJob(0, "None", string.Empty)]).Select(j => j.Id));
    }

    [Fact]
    public void TheList_HoldsAtMostEightJobs()
    {
        var many = Enumerable.Range(1, 12).Select(i => new FavoriteJob((uint)i, $"Job {i}", $"J{i}")).ToArray();

        var list = BasicFavoriteJobs.Normalize(many);

        Assert.Equal(BasicFavoriteJobs.MaxJobs, list.Count);
        Assert.Equal(many.Take(BasicFavoriteJobs.MaxJobs).Select(j => j.Id), list.Select(j => j.Id));
    }

    [Fact]
    public async Task UseCurrentJob_AddsTheCurrentJob_OnlyWhenItIsntChosenYet_AndNeverALevel()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.WhiteMage);
        harness.Character.CurrentInfo = FakeCharacter.Hero with { JobId = 38, JobName = "Dancer", Level = 92 };

        harness.Basic.UseCurrentJob();
        Assert.Equal([24u, 38u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal("White Mage, Dancer", JobText(harness.Document));

        var before = harness.Json();
        harness.Basic.UseCurrentJob();
        Assert.Equal(before, harness.Json());

        Assert.Null(BasicSections.Find(harness.Document, ProfileElementRole.BasicLevel));
        Assert.Equal(0, harness.Document.BasicPlate.Level);
    }

    [Fact]
    public async Task UseCurrentJob_OnAJobGameDataDoesntList_KeepsTheCharactersJobName()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        harness.Character.CurrentInfo = FakeCharacter.Hero with { JobId = 99, JobName = "Mystery Job" };

        harness.Basic.UseCurrentJob();

        Assert.Equal([99u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal("Mystery Job", JobText(harness.Document));
    }

    // ---------------------------------------------------------------- the derived display: full names, abbreviations, fitting

    [Fact]
    public async Task TheStoredText_IsAlwaysTheFullNames_WhateverIsShown()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);

        Assert.Equal("Red Mage, Astrologian, White Mage, Dancer", JobText(harness.Document));
        Assert.Equal("RDM \u00B7 AST \u00B7 WHM \u00B7 DNC", Shown(harness.Document));
    }

    [Fact]
    public async Task FullNames_AreShownWhenTheyFit()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.Dancer, FakeJobs.Astrologian, FakeJobs.RedMage);

        Assert.Equal("Dancer, Astrologian, Red Mage", Shown(harness.Document));
    }

    [Fact]
    public async Task WhenFullNamesDontFit_TheGamesAbbreviationsAreShown_InTheSameOrder()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);

        Assert.Equal("RDM \u00B7 AST \u00B7 WHM \u00B7 DNC", Shown(harness.Document));
        Assert.Equal([35u, 33u, 24u, 38u], harness.Document.BasicPlate!.FavoriteJobIds);

        // Removing one makes the full names fit again.
        harness.Basic.RemoveFavoriteJobAt(3);
        Assert.Equal("Red Mage, Astrologian, White Mage", Shown(harness.Document));
    }

    [Fact]
    public async Task ALargerFont_SwitchesTheSameListToAbbreviations_AndASmallerOneBack()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);
        Assert.Equal("Astrologian, White Mage", Shown(harness.Document)); // 20 px: fits

        EditJobStyle(harness, e => e.FontSize = 32f);
        Assert.Equal("AST \u00B7 WHM", Shown(harness.Document));

        EditJobStyle(harness, e => e.FontSize = 20f);
        Assert.Equal("Astrologian, White Mage", Shown(harness.Document));
        Assert.Equal([33u, 24u], harness.Document.BasicPlate!.FavoriteJobIds);
    }

    [Fact]
    public async Task AnotherFontFamily_IsMeasuredAgain()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);
        EditJobStyle(harness, e => e.FontSize = 28f);
        Assert.Equal("Astrologian, White Mage", Shown(harness.Document)); // fits in Sans

        EditJobStyle(harness, e => e.FontFamily = ProfileFontFamilies.AetherFrameSerif);
        Assert.Equal("AST \u00B7 WHM", Shown(harness.Document)); // the wider family doesn't

        EditJobStyle(harness, e => e.FontFamily = ProfileFontFamilies.AetherFrameSans);
        Assert.Equal("Astrologian, White Mage", Shown(harness.Document));
    }

    [Fact]
    public async Task AChangeOfWidth_FromTheLayoutOrAnotherEditor_IsMeasuredAgainst()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);

        // Narrowed in the Advanced editor.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        EditJobStyle(harness, e => e.Size = e.Size with { X = 200f });
        Assert.Equal("AST \u00B7 WHM", Shown(harness.Document));

        // Back in Basic: an orientation change keeps the customized width, and so the abbreviations...
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        Assert.Equal("AST \u00B7 WHM", Shown(harness.Document));

        // ...until Apply Layout gives the value its whole cell again.
        harness.Basic.ApplySectionLayout(BasicSection.Job);
        Assert.Equal("Astrologian, White Mage", Shown(harness.Document));
    }

    [Fact]
    public async Task ResetSection_IsMeasuredAgain()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);
        EditJobStyle(harness, e => e.FontSize = 40f);
        Assert.Equal("AST \u00B7 WHM", Shown(harness.Document));

        harness.Basic.ResetSection(BasicSection.Job);

        Assert.Equal("Astrologian, White Mage", Shown(harness.Document));
    }

    [Fact]
    public void TheChoice_IsMeasured_NotCounted()
    {
        var two = BasicDocuments.Classic();
        BasicDocuments.Editor(two).SetFavoriteJobs([FakeJobs.Astrologian, FakeJobs.WhiteMage]);
        var five = BasicDocuments.Classic();
        BasicDocuments.Editor(five).SetFavoriteJobs([FakeJobs.Paladin, FakeJobs.WhiteMage, FakeJobs.Astrologian, FakeJobs.RedMage, FakeJobs.Dancer]);

        Assert.Equal("AST \u00B7 WHM", Shown(two, _ => 1000f)); // two that don't fit
        Assert.Equal(JobText(five), Shown(five, _ => 100f));     // five that do
    }

    [Fact]
    public void AJobWithoutAnAbbreviation_KeepsItsName()
    {
        Assert.Equal("PLD \u00B7 Mystery", BasicFavoriteJobs.AbbreviatedText([FakeJobs.Paladin, new FavoriteJob(99, "Mystery", string.Empty)]));
    }

    [Fact]
    public async Task WhenEvenAbbreviationsDontFit_AutoFitShrinksThem_AndNoJobIsDropped()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);

        Add(harness, FakeJobs.All);

        var job = JobElement(harness.Document);
        Assert.Equal(string.Join(" \u00B7 ", FakeJobs.All.Select(j => j.Abbreviation)), Shown(harness.Document));
        Assert.True(FakeWidth(job, Shown(harness.Document)) > job.Size.X - (2f * TextProfileElement.LayoutPadding)); // still too wide...
        Assert.True(job.EffectiveAutoFit);                                                                           // ...so auto fit shrinks it
        Assert.True(job.AutoFitMinimumSize < job.FontSize);
        Assert.Equal(FakeJobs.All.Length, harness.Document.BasicPlate!.FavoriteJobIds.Count);
    }

    [Fact]
    public async Task WithoutAFontToMeasure_TheStoredFullNamesShow()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);

        Assert.Equal("Red Mage, Astrologian, White Mage, Dancer", Shown(harness.Document, _ => null));
    }

    [Fact]
    public async Task TextWrittenInAdvanced_IsShownAsWritten()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);

        EditJobStyle(harness, e => e.Text = "All the healers");

        Assert.Equal("All the healers", Shown(harness.Document));
    }

    [Fact]
    public async Task TheSymbols_AreKeptAroundTheAbbreviations()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);

        EditJobStyle(harness, e => { e.Prefix = "[ "; e.Suffix = " ]"; });

        Assert.Equal("[ RDM \u00B7 AST \u00B7 WHM \u00B7 DNC ]", Shown(harness.Document));
    }

    [Fact]
    public async Task DisplayFitting_NeverChangesThePlate_OrTheJobsAndTheirOrder()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);
        var before = harness.Json();

        foreach (var measure in new Func<string, float?>[] { _ => 1f, _ => 10000f, _ => null })
        {
            _ = Shown(harness.Document, measure);
        }

        Assert.Equal(before, harness.Json());
        Assert.Equal([35u, 33u, 24u, 38u], harness.Document.BasicPlate!.FavoriteJobIds);
    }

    // ---------------------------------------------------------------- layout

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public void ANewPlate_HasNoLevel_AndItsFavoriteJobsStayInTheirCell(AdventurePlateOrientation orientation)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.SetOrientation(orientation);
        editor.SetFavoriteJobs(FakeJobs.All);

        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel));
        Assert.Empty(BasicPlateEditor.FindOverlaps(document));

        // The value fills the Favorite Jobs cell (no level before it), inside its group's bounds.
        var job = BasicSections.Find(document, ProfileElementRole.BasicJob)!;
        var world = BasicSections.Find(document, ProfileElementRole.BasicWorld)!;
        var cell = AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Job, orientation, document);
        Assert.Equal(world.Position.X, job.Position.X, 3);
        Assert.Equal(world.Size.X, job.Size.X, 3);
        Assert.Equal(cell.Union(BasicDocuments.RectOf(job)), cell); // the value is inside its cell

        foreach (var group in BasicSections.LayoutGroups.Select(g => g[0]).Where(g => g != BasicSection.Job))
        {
            Assert.False(cell.Intersects(AdventurePlateClassicLayout.GetGroupBounds(group, orientation, document)), $"Favorite Jobs meets {group}");
        }
    }

    // ---------------------------------------------------------------- the retired Level

    [Fact]
    public async Task ALegacyLevel_IsPreservedExactly_AndNeverShownOrHit()
    {
        var legacy = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { Level = 90 });
        var savedLevel = System.Text.Json.JsonSerializer.Serialize<object>(BasicSections.Find(legacy, ProfileElementRole.BasicLevel)!);
        using var harness = await BasicHarness.OpenDocumentAsync(legacy);

        harness.SimulateBasicFrame();

        // Kept exactly as saved...
        var level = BasicSections.FindText(harness.Document, ProfileElementRole.BasicLevel)!;
        Assert.Equal(savedLevel, System.Text.Json.JsonSerializer.Serialize<object>(level));
        Assert.Equal(90, harness.Document.BasicPlate!.Level);
        Assert.False(harness.Session.IsDirty);

        // ...but not part of anything drawn or clickable, only listed for the Advanced Layers panel.
        var buffer = new System.Collections.Generic.List<ProfileElement>();
        AetherFrame.UI.Rendering.ProfilePaintOrder.Fill(harness.Document, buffer, includeHidden: false);
        Assert.DoesNotContain(level, buffer);
        AetherFrame.UI.Rendering.ProfilePaintOrder.Fill(harness.Document, buffer, includeHidden: true);
        Assert.Contains(level, buffer);

        // A click where the level sits reaches the Favorite Jobs underneath, never the level.
        Assert.Equal(BasicEditorCategory.Details, BasicEditorView.CategoryAt(harness.Document, level.Position + (level.Size / 2f), new System.Collections.Generic.List<ProfileElement>()));
    }

    [Fact]
    public async Task ALegacyPlate_PresentsItsFavoriteJobsInTheWholeCell_WithoutTheLevel()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.LegacyClassic(FakeCharacter.Hero with { Level = 90 }));

        var job = BasicSections.Find(harness.Document, ProfileElementRole.BasicJob)!;
        var world = BasicSections.Find(harness.Document, ProfileElementRole.BasicWorld)!;
        Assert.Equal(world.Position.X, job.Position.X, 3); // the load-time upgrade reclaimed the level's space
        Assert.Equal(world.Size.X, job.Size.X, 3);
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Job));
        Assert.Equal("Paladin", Shown(harness.Document));
        Assert.Empty(BasicPlateEditor.FindOverlaps(harness.Document));
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task ALegacyLevel_SurvivesSaveAndEveryLayoutAction()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.LegacyClassic(FakeCharacter.Hero with { Level = 90 }));
        var level = BasicDocuments.RectOf(BasicSections.Find(harness.Document, ProfileElementRole.BasicLevel)!);

        harness.Basic.AddFavoriteJob(FakeJobs.WhiteMage.Id);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.ApplyLayout();
        harness.Basic.ResetSection(BasicSection.Job);
        harness.Basic.ResetBasicLayout();
        Assert.True(await harness.Session.SaveProfileAsync());

        var saved = harness.Library.OpenDocumentForEditing(harness.PlateId);
        var savedLevel = BasicSections.FindText(saved, ProfileElementRole.BasicLevel)!;
        Assert.Equal("Lv. 90", savedLevel.Text);
        Assert.True(savedLevel.Visible);
        Assert.Equal(level, BasicDocuments.RectOf(savedLevel)); // never placed by Basic
        Assert.Equal(90, saved.BasicPlate!.Level);
    }

    [Fact]
    public async Task MovingALegacyLevelInAdvanced_DoesntCustomizeTheFavoriteJobs()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.LegacyClassic(FakeCharacter.Hero with { Level = 90 }));
        var level = BasicSections.Find(harness.Document, ProfileElementRole.BasicLevel)!;
        harness.Session.ApplyImmediateEdit(level.Id, e => e.Position += new System.Numerics.Vector2(0f, 40f));

        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Job));
        Assert.Empty(BasicPlateEditor.FindOverlaps(harness.Document));
    }

    [Fact]
    public async Task ALegacyLevel_RoundTripsThroughATemplate()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var created = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Legacy", new PlateStarterContent(FakeCharacter.Hero));
        var document = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
        BasicDocuments.AddLegacyLevel(document, 90);
        await fixture.PlateLibrary.SavePlateDocumentAsync(document);

        var templateId = await templates.SaveAsTemplateAsync(created.PlateId, "Legacy Template");
        var copy = fixture.PlateLibrary.GetSavedDocument((await templates.InstantiateAsync(templateId, null)).PlateId)!;

        Assert.Equal("Lv. 90", BasicSections.FindText(copy, ProfileElementRole.BasicLevel)!.Text);
        Assert.Equal(90, copy.BasicPlate!.Level);
    }

    [Fact]
    public async Task ALegacyLevel_ExportsAndImportsInAPackage()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library);
        var document = library.OpenDocumentForEditing(plateId);
        BasicDocuments.AddLegacyLevel(document, 90);
        await library.SavePlateDocumentAsync(document);

        using var staged = packages.Inspect(fixture.Export(packages, plateId));
        Assert.True(staged.CanImport); // never rejected for carrying a level
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        var imported = library.OpenDocumentForEditing(result.PlateId);
        Assert.Equal("Lv. 90", BasicSections.FindText(imported, ProfileElementRole.BasicLevel)!.Text);
        Assert.Equal(90, imported.BasicPlate!.Level);
        Assert.Equal([19u, 24u, 33u], imported.BasicPlate.FavoriteJobIds);
    }

    [Fact]
    public void Basic_OffersNoLevelAnywhere()
    {
        // Not a category's section, not a panel, and no Basic session operation edits or hides it.
        Assert.All(BasicEditorView.Categories, c => Assert.DoesNotContain(BasicSection.Level, BasicEditorView.SectionsOf(c)));
        Assert.DoesNotContain(Enum.GetNames<BasicEditorPanel>(), name => name.Contains("Level", StringComparison.Ordinal));
        var methods = typeof(BasicEditorSession).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, m => m.Name.Contains("Level", StringComparison.Ordinal) && !m.Name.Contains("Layout", StringComparison.Ordinal));
        Assert.True(BasicSections.IsRetired(ProfileElementRole.BasicLevel));
    }

    [Fact]
    public void NewPlates_NeverGetALevel()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero with { Level = 100 });
        var editor = BasicDocuments.Editor(document);

        editor.SetSectionVisible(BasicSection.Level, true);
        editor.EnsureSection(BasicSection.Level);
        editor.ResetSection(BasicSection.Job);
        editor.ApplyLayoutToAll();

        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel));
        Assert.Equal(0, document.BasicPlate!.Level);
        Assert.Null(AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicLevel, AdventurePlateOrientation.Normal, document));
    }

    // ---------------------------------------------------------------- history, state, persistence

    [Fact]
    public async Task EveryChange_IsOneUndoStep_AndRedoable()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        var start = harness.Json();

        Add(harness, FakeJobs.Paladin, FakeJobs.WhiteMage);
        harness.Basic.MoveFavoriteJob(1, -1);
        harness.Character.CurrentInfo = FakeCharacter.Hero with { JobId = 38, JobName = "Dancer" };
        harness.Basic.UseCurrentJob();
        harness.Basic.RemoveFavoriteJobAt(0);
        var end = harness.Json();

        harness.Session.Undo(); // remove
        Assert.Equal([24u, 19u, 38u], harness.Document.BasicPlate!.FavoriteJobIds);
        harness.Session.Undo(); // Use current
        Assert.Equal([24u, 19u], harness.Document.BasicPlate.FavoriteJobIds);
        harness.Session.Undo(); // reorder
        Assert.Equal([19u, 24u], harness.Document.BasicPlate.FavoriteJobIds);
        Assert.Equal("Paladin, White Mage", JobText(harness.Document));
        harness.Session.Undo();
        harness.Session.Undo();
        Assert.Equal(start, harness.Json());
        Assert.False(harness.Session.IsDirty);

        for (var i = 0; i < 5; i++)
        {
            harness.Session.Redo();
        }

        Assert.Equal(end, harness.Json());
        Assert.True(harness.Session.IsDirty);
    }

    [Fact]
    public async Task FavoriteJobs_AreSaved_AndReverted()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        var commands = new EditorDocumentCommands(harness.Profiles, harness.Session);
        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);

        Assert.True(await commands.SaveAsync());
        harness.Session.SyncWithCurrentProfile();
        Assert.False(commands.IsDirty);
        var saved = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.Equal([33u, 24u], saved.BasicPlate!.FavoriteJobIds);
        Assert.Equal("Astrologian, White Mage", JobText(saved));
        Assert.Equal(BasicFavoriteJobs.PluralHeading, Heading(saved));

        harness.Basic.RemoveFavoriteJobAt(0);
        Assert.True(commands.Revert());
        Assert.Equal([33u, 24u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(BasicFavoriteJobs.PluralHeading, Heading(harness.Document));
        Assert.False(commands.IsDirty);
    }

    [Fact]
    public async Task FavoriteJobs_CarryAcrossBothEditors()
    {
        using var harness = await BasicHarness.NewClassicAsync(character: null);
        harness.SimulateBasicFrame();
        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);

        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        Assert.Equal("Astrologian, White Mage", JobText(harness.Document)); // one ordinary text element there
        harness.Session.Undo();
        Assert.Equal([33u], harness.Document.BasicPlate!.FavoriteJobIds);

        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        harness.SimulateBasicFrame();
        Assert.Equal("Astrologian", JobText(harness.Document));
        Assert.Equal(BasicFavoriteJobs.SingularHeading, Heading(harness.Document));
        harness.Session.Redo();
        Assert.Equal([33u, 24u], harness.Document.BasicPlate.FavoriteJobIds);
        Assert.True(harness.Session.IsDirty);
    }

    [Fact]
    public async Task ATemplate_KeepsTheFavoriteJobsAndTheirOrder()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var created = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Source", new PlateStarterContent(null));
        var document = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
        BasicDocuments.Editor(document).SetFavoriteJobs([FakeJobs.RedMage, FakeJobs.Paladin, FakeJobs.Dancer]);
        await fixture.PlateLibrary.SavePlateDocumentAsync(document);

        var templateId = await templates.SaveAsTemplateAsync(created.PlateId, "Jobs Template");
        var instance = await templates.InstantiateAsync(templateId, null);
        var copy = fixture.PlateLibrary.GetSavedDocument(instance.PlateId)!;

        Assert.Equal([35u, 19u, 38u], copy.BasicPlate!.FavoriteJobIds);
        Assert.Equal(35u, copy.BasicPlate.FavoriteJobId);
        Assert.Equal(JobText(document), JobText(copy));
        Assert.Equal(BasicFavoriteJobs.PluralHeading, Heading(copy));
    }

    [Fact]
    public async Task APackage_KeepsTheFavoriteJobsAndTheirOrder()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var (plateId, _, _, _) = await fixture.CreateRichPlateAsync(library); // Paladin, White Mage, Astrologian
        var original = library.OpenDocumentForEditing(plateId);

        using var staged = packages.Inspect(fixture.Export(packages, plateId));
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());
        var imported = library.OpenDocumentForEditing(result.PlateId);

        Assert.Equal([19u, 24u, 33u], imported.BasicPlate!.FavoriteJobIds);
        Assert.Equal(JobText(original), JobText(imported));
        Assert.Equal(Heading(original), Heading(imported));
    }
}
