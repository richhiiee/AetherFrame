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
/// heading, its text (full names when they fit, else the game's abbreviations, else auto fit), the
/// lossless reading of Plates that stored one job, Plates that still show a level, the shared
/// history and persistence, and templates and packages.
/// </summary>
public class FavoriteJobsTests
{
    private static Task<BasicHarness> NewClassicAsync() =>
        BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(null));

    private static string JobText(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicJob)?.Text ?? string.Empty;

    private static string Heading(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicJobHeading)!.Text;

    private static uint[] Ids(ProfileDocument document) => BasicFavoriteJobs.IdsOf(document).ToArray();

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
        using var harness = await NewClassicAsync();

        Add(harness, FakeJobs.Astrologian);

        Assert.Equal("Astrologian", JobText(harness.Document));
        Assert.Equal(BasicFavoriteJobs.SingularHeading, Heading(harness.Document));
        Assert.Equal([33u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(33u, harness.Document.BasicPlate.FavoriteJobId);
    }

    [Fact]
    public async Task SeveralJobs_KeepTheirOrder_UnderFavoriteJobs()
    {
        using var harness = await NewClassicAsync();

        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);

        Assert.Equal("Astrologian, White Mage", JobText(harness.Document));
        Assert.Equal(BasicFavoriteJobs.PluralHeading, Heading(harness.Document));
        Assert.Equal([33u, 24u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(33u, harness.Document.BasicPlate.FavoriteJobId); // the primary favorite
    }

    [Fact]
    public async Task TheHeading_FollowsTheCount_AsJobsAreAddedAndRemoved()
    {
        using var harness = await NewClassicAsync();

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
        using var harness = await NewClassicAsync();
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
        using var harness = await NewClassicAsync();
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
        using var harness = await NewClassicAsync();
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
        using var harness = await NewClassicAsync();
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
        using var harness = await NewClassicAsync();
        harness.Character.CurrentInfo = FakeCharacter.Hero with { JobId = 99, JobName = "Mystery Job" };

        harness.Basic.UseCurrentJob();

        Assert.Equal([99u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal("Mystery Job", JobText(harness.Document));
    }

    // ---------------------------------------------------------------- full names, abbreviations, fitting

    [Fact]
    public async Task FullNames_AreShownWhenTheyFit()
    {
        using var harness = await NewClassicAsync();

        Add(harness, FakeJobs.Dancer, FakeJobs.Astrologian, FakeJobs.RedMage);

        Assert.Equal("Dancer, Astrologian, Red Mage", JobText(harness.Document));
    }

    [Fact]
    public async Task WhenFullNamesDontFit_TheGamesAbbreviationsAreShown_InTheSameOrder()
    {
        using var harness = await NewClassicAsync();

        Add(harness, FakeJobs.RedMage, FakeJobs.Astrologian, FakeJobs.WhiteMage, FakeJobs.Dancer);

        Assert.Equal("RDM · AST · WHM · DNC", JobText(harness.Document));
        Assert.Equal([35u, 33u, 24u, 38u], harness.Document.BasicPlate!.FavoriteJobIds);

        // Removing one makes the full names fit again.
        harness.Basic.RemoveFavoriteJobAt(3);
        Assert.Equal("Red Mage, Astrologian, White Mage", JobText(harness.Document));
    }

    [Fact]
    public void TheChoice_IsMeasured_NotCounted()
    {
        var two = new[] { FakeJobs.Astrologian, FakeJobs.WhiteMage };
        var five = new[] { FakeJobs.Paladin, FakeJobs.WhiteMage, FakeJobs.Astrologian, FakeJobs.RedMage, FakeJobs.Dancer };

        Assert.Equal("AST · WHM", BasicFavoriteJobs.Choose(two, _ => 1000f, availableWidth: 200f)); // two that don't fit
        Assert.Equal(BasicFavoriteJobs.FullText(five), BasicFavoriteJobs.Choose(five, _ => 100f, availableWidth: 200f)); // five that do
        Assert.Equal(string.Empty, BasicFavoriteJobs.Choose([], _ => 0f, 200f));
    }

    [Fact]
    public void AJobWithoutAnAbbreviation_KeepsItsName()
    {
        Assert.Equal("PLD · Mystery", BasicFavoriteJobs.AbbreviatedText([FakeJobs.Paladin, new FavoriteJob(99, "Mystery", string.Empty)]));
    }

    [Fact]
    public async Task WhenEvenAbbreviationsDontFit_AutoFitShrinksThem_AndNoJobIsDropped()
    {
        using var harness = await NewClassicAsync();

        Add(harness, FakeJobs.All);

        var job = BasicSections.FindText(harness.Document, ProfileElementRole.BasicJob)!;
        Assert.Equal(string.Join(" · ", FakeJobs.All.Select(j => j.Abbreviation)), job.Text);
        Assert.Equal(FakeJobs.All.Length, harness.Document.BasicPlate!.FavoriteJobIds.Count);
        Assert.True(job.EffectiveAutoFit);
        Assert.True(job.AutoFitMinimumSize < job.FontSize);
    }

    [Fact]
    public async Task WithoutAFontToMeasure_AConservativeEstimateDecides()
    {
        using var harness = await NewClassicAsync();
        harness.Measurer.FontReady = false;

        Add(harness, FakeJobs.Astrologian, FakeJobs.WhiteMage);
        Assert.Equal("Astrologian, White Mage", JobText(harness.Document));

        Add(harness, FakeJobs.RedMage, FakeJobs.Dancer);
        Assert.Equal("AST · WHM · RDM · DNC", JobText(harness.Document));
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
        Assert.Equal(world.Position.X + (AdventurePlateClassicLayout.EmptyLevelWidth * AdventurePlateClassicLayout.CanvasScale(document).X), job.Position.X, 3);
        Assert.True(job.Size.X > world.Size.X - 2f);
        Assert.Equal(cell.Union(BasicDocuments.RectOf(job)), cell); // the value is inside its cell

        foreach (var group in BasicSections.LayoutGroups.Select(g => g[0]).Where(g => g != BasicSection.Job))
        {
            Assert.False(cell.Intersects(AdventurePlateClassicLayout.GetGroupBounds(group, orientation, document)), $"Favorite Jobs meets {group}");
        }
    }

    // ---------------------------------------------------------------- Plates that still show a level

    [Fact]
    public async Task APlateThatStillShowsALevel_LoadsUnchanged_AndKeepsItThroughJobEdits()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.LegacyClassic(FakeCharacter.Hero with { Level = 90 }));
        var before = harness.Json();

        harness.SimulateBasicFrame();
        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);

        harness.Basic.AddFavoriteJob(FakeJobs.WhiteMage.Id);

        Assert.Equal("Lv. 90", BasicSections.FindText(harness.Document, ProfileElementRole.BasicLevel)!.Text);
        Assert.Equal(90, harness.Document.BasicPlate!.Level);
        Assert.Equal("Paladin, White Mage", JobText(harness.Document));
        Assert.Empty(BasicPlateEditor.FindOverlaps(harness.Document));
    }

    [Fact]
    public async Task HidingTheLevel_KeepsIt_AndGivesTheJobsTheWholeCell()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(BasicDocuments.LegacyClassic(FakeCharacter.Hero with { Level = 90 }));

        harness.Basic.SetSectionVisible(BasicSection.Level, false);

        var level = BasicSections.FindText(harness.Document, ProfileElementRole.BasicLevel)!;
        Assert.False(level.Visible);
        Assert.Equal("Lv. 90", level.Text);
        Assert.Equal(90, harness.Document.BasicPlate!.Level);
        Assert.False(BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Details).Hidden); // retired, not "hidden"

        var job = BasicSections.Find(harness.Document, ProfileElementRole.BasicJob)!;
        var world = BasicSections.Find(harness.Document, ProfileElementRole.BasicWorld)!;
        Assert.Equal(world.Position.X + (AdventurePlateClassicLayout.EmptyLevelWidth * AdventurePlateClassicLayout.CanvasScale(harness.Document).X), job.Position.X, 3);

        harness.Session.Undo();
        Assert.True(BasicSections.Find(harness.Document, ProfileElementRole.BasicLevel)!.Visible);
    }

    [Fact]
    public void ShowingTheLevelSection_NeverCreatesALevel()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);

        editor.SetSectionVisible(BasicSection.Level, true);
        editor.EnsureSection(BasicSection.Level);
        editor.ApplyLayoutToAll();

        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel));
    }

    // ---------------------------------------------------------------- history, state, persistence

    [Fact]
    public async Task EveryChange_IsOneUndoStep_AndRedoable()
    {
        using var harness = await NewClassicAsync();
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
        using var harness = await NewClassicAsync();
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
        using var harness = await NewClassicAsync();
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
