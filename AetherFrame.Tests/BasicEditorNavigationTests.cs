using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

/// <summary>The Basic editor's category navigation: state, mapping, status, summaries, and preview selection.</summary>
public class BasicEditorNavigationTests
{
    private static Task<BasicHarness> NewClassicAsync() =>
        BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));

    // ---------------------------------------------------------------- navigation state

    [Fact]
    public void TheEditor_StartsOnDesign()
    {
        var navigation = new BasicEditorNavigation();
        navigation.TrackPlate(Guid.NewGuid());

        Assert.Equal(BasicEditorCategory.Design, navigation.Selected);
        Assert.Equal(PreviewZoom.Fit, navigation.Zoom);
    }

    [Fact]
    public void AnyCategory_CanBeOpenedDirectly_InAnyOrder()
    {
        var navigation = new BasicEditorNavigation();
        navigation.TrackPlate(Guid.NewGuid());

        foreach (var category in new[] { BasicEditorCategory.Message, BasicEditorCategory.Portrait, BasicEditorCategory.Details, BasicEditorCategory.Design })
        {
            navigation.Select(category);
            Assert.Equal(category, navigation.Selected);
        }
    }

    [Fact]
    public async Task TheSelectedCategory_StaysPut_ThroughEditsUndoAndEditorSwitching()
    {
        using var harness = await NewClassicAsync();
        var navigation = new BasicEditorNavigation();
        navigation.TrackPlate(harness.PlateId);
        navigation.Select(BasicEditorCategory.Details);

        harness.Basic.SetText(ProfileElementRole.BasicWorld, "Odin");
        harness.Basic.CommitTextEdit();
        navigation.TrackPlate(harness.Document.ProfileId);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Session.Undo();
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        navigation.TrackPlate(harness.Document.ProfileId);

        Assert.Equal(BasicEditorCategory.Details, navigation.Selected);
    }

    [Fact]
    public async Task Preview_ReturnsToTheSameCategory_AndZoom()
    {
        using var harness = await NewClassicAsync();
        var navigation = new BasicEditorNavigation();
        navigation.TrackPlate(harness.PlateId);
        navigation.Select(BasicEditorCategory.Identity);
        navigation.Zoom = PreviewZoom.Larger;

        EditorPreview.Enter(harness.Session);
        navigation.TrackPlate(harness.Document.ProfileId);
        EditorPreview.Exit(harness.Session);
        navigation.TrackPlate(harness.Document.ProfileId);

        Assert.Equal(BasicEditorCategory.Identity, navigation.Selected);
        Assert.Equal(PreviewZoom.Larger, navigation.Zoom);
    }

    [Fact]
    public async Task ANewlyCreatedPlate_OpensOnDesign_EvenAfterAnotherPlateWasOnMessage()
    {
        using var harness = await NewClassicAsync();
        var navigation = new BasicEditorNavigation();
        navigation.TrackPlate(harness.PlateId);
        navigation.Select(BasicEditorCategory.Message);
        navigation.Zoom = PreviewZoom.Large;

        var created = await harness.Library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, starter: new PlateStarterContent(null));
        harness.Profiles.OpenPlate(created.PlateId);
        navigation.TrackPlate(harness.Profiles.OpenPlateId);

        Assert.Equal(BasicEditorCategory.Design, navigation.Selected);
        Assert.Equal(PreviewZoom.Fit, navigation.Zoom);
    }

    // ---------------------------------------------------------------- responsive layout

    [Theory]
    [InlineData(1400f, 1f, (int)BasicEditorLayoutMode.ThreeColumn)]
    [InlineData(1000f, 1f, (int)BasicEditorLayoutMode.ThreeColumn)]
    [InlineData(999f, 1f, (int)BasicEditorLayoutMode.TwoColumn)]
    [InlineData(760f, 1f, (int)BasicEditorLayoutMode.TwoColumn)]
    [InlineData(759f, 1f, (int)BasicEditorLayoutMode.Stacked)]
    [InlineData(500f, 1f, (int)BasicEditorLayoutMode.Stacked)]
    [InlineData(1400f, 1.5f, (int)BasicEditorLayoutMode.TwoColumn)]
    [InlineData(1100f, 1.5f, (int)BasicEditorLayoutMode.Stacked)]
    [InlineData(1500f, 1.5f, (int)BasicEditorLayoutMode.ThreeColumn)]
    public void TheLayout_FollowsTheSpace_AtTheUisScale(float width, float scale, int expected) =>
        Assert.Equal((BasicEditorLayoutMode)expected, BasicEditorView.ChooseLayout(width, scale));

    // ---------------------------------------------------------------- categories and sections

    [Fact]
    public void EverySection_BelongsToExactlyOneCategory()
    {
        foreach (var section in Enum.GetValues<BasicSection>())
        {
            var owners = BasicEditorView.Categories.Where(c => BasicEditorView.SectionsOf(c).Contains(section)).ToList();
            Assert.Single(owners);
            Assert.Equal(owners[0], BasicEditorView.CategoryOf(section));
        }

        Assert.Empty(BasicEditorView.SectionsOf(BasicEditorCategory.Design));
        Assert.Equal([BasicSection.World, BasicSection.Job, BasicSection.Level, BasicSection.FreeCompany], BasicEditorView.SectionsOf(BasicEditorCategory.Details));
        Assert.Equal([BasicSection.Playstyle, BasicSection.ActiveHours], BasicEditorView.SectionsOf(BasicEditorCategory.Playstyle));
        Assert.Equal(BasicEditorCategory.Identity, BasicEditorView.CategoryOf(BasicSection.Identity));
        Assert.Equal(BasicEditorCategory.Portrait, BasicEditorView.CategoryOf(BasicSection.Portrait));
        Assert.Equal(BasicEditorCategory.Message, BasicEditorView.CategoryOf(BasicSection.Message));
    }

    [Fact]
    public void Categories_AreInFlowOrder_AndCoverEveryPanelOnce()
    {
        Assert.Equal(
            [BasicEditorCategory.Design, BasicEditorCategory.Portrait, BasicEditorCategory.Identity, BasicEditorCategory.Details, BasicEditorCategory.Playstyle, BasicEditorCategory.Message],
            BasicEditorView.Categories);
        Assert.Equal(BasicEditorView.PanelOrder, BasicEditorView.Categories.SelectMany(BasicEditorView.PanelsOf));
        Assert.Equal([BasicEditorPanel.BackgroundTheme, BasicEditorPanel.PlateLayout], BasicEditorView.PanelsOf(BasicEditorCategory.Design));
        Assert.Equal(["Design", "Portrait", "Identity", "Character Details", "Activity", "Message"], BasicEditorView.Categories.Select(BasicEditorView.Title));
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public void AFreshPlate_HasQuietCategories()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);

        Assert.All(BasicEditorView.Categories, c => Assert.True(BasicEditorView.StatusOf(document, c).IsQuiet));
    }

    [Fact]
    public async Task AdvancedCustomization_MarksOnlyItsCategory_Quietly()
    {
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicWorldHeading, new Vector2(0, 3));

        var details = BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Details);
        Assert.True(details.Customized);
        Assert.False(details.NeedsAttention);
        Assert.Contains("Customized in the Advanced Editor.", BasicEditorView.Describe(details));
        Assert.All(BasicEditorView.Categories.Where(c => c != BasicEditorCategory.Details), c => Assert.True(BasicEditorView.StatusOf(harness.Document, c).IsQuiet));
    }

    [Fact]
    public void HiddenSections_MarkTheirCategory()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.SetSectionVisible(BasicSection.ActiveHours, false);
        BasicSections.Find(document, ProfileElementRole.BasicName)!.Visible = false;

        Assert.True(BasicEditorView.StatusOf(document, BasicEditorCategory.Playstyle).Hidden);
        Assert.True(BasicEditorView.StatusOf(document, BasicEditorCategory.Identity).Hidden);
        Assert.False(BasicEditorView.StatusOf(document, BasicEditorCategory.Playstyle).NeedsAttention);
        Assert.False(BasicEditorView.StatusOf(document, BasicEditorCategory.Details).Hidden);
    }

    [Fact]
    public async Task ACollision_NeedsAttention_InBothInvolvedCategories_Only()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Everyday });
        harness.DragInAdvanced(ProfileElementRole.BasicLevel, new Vector2(0, 2));
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        var details = BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Details);
        var playstyle = BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Playstyle);
        Assert.True(details.Collision);
        Assert.True(details.NeedsAttention);
        Assert.True(playstyle.Collision);
        Assert.False(BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Message).Collision);
        Assert.False(BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Identity).Collision);

        harness.Basic.ApplySectionLayout(BasicSections.LayoutGroupOf(BasicSection.Job));
        Assert.All(BasicEditorView.Categories, c => Assert.False(BasicEditorView.StatusOf(harness.Document, c).NeedsAttention));
    }

    [Fact]
    public async Task UnsupportedContent_IsFlaggedOnDesign()
    {
        var plateId = Guid.NewGuid();
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.ProfileId = plateId;
        var node = JsonSerializer.SerializeToNode(document, JsonOptions.Default)!.AsObject();
        node["Elements"]!.AsArray().Add(FutureData.UnknownElement());
        using var harness = await BasicHarness.OpenJsonAsync(node.ToJsonString(JsonOptions.Default), plateId);

        var design = BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Design);
        Assert.True(design.Unsupported);
        Assert.True(design.NeedsAttention);
        Assert.False(BasicEditorView.StatusOf(harness.Document, BasicEditorCategory.Identity).NeedsAttention);
    }

    // ---------------------------------------------------------------- summaries

    [Fact]
    public async Task Summaries_DescribeWhatEachCategoryHolds()
    {
        using var harness = await NewClassicAsync();
        harness.Identity.SetCustomTitle("The Heart of the Party");
        harness.Identity.Commit();
        harness.Basic.AddPlaystyle("Casual");
        harness.Basic.AddPlaystyle("Raiding");
        harness.Basic.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Weekends, StartMinutes = 20 * 60, EndMinutes = 23 * 60 });
        harness.Basic.SetText(ProfileElementRole.BasicMessage, new string('a', 100));
        harness.Basic.CommitTextEdit();
        var document = harness.Document;

        Assert.Equal(["Normal layout  ·  Royal theme"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Design));
        Assert.Equal(["No portrait yet"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Portrait));
        Assert.Equal(["Hero Example", "The Heart of the Party"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Identity));
        Assert.Equal(["Phoenix [Light]", "Lv. 100 Paladin", "Free Company: «ABC»"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Details));
        Assert.Equal(["2 playstyles", "Weekends  ·  8 PM - 11 PM"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Playstyle));
        var message = Assert.Single(BasicEditorView.SummaryOf(document, BasicEditorCategory.Message));
        Assert.EndsWith("...", message);
        Assert.True(message.Length <= 60);
    }

    [Fact]
    public void Summaries_OfAnEmptyPlate_SayWhatsMissing_WithoutTouchingIt()
    {
        var document = BasicDocuments.Classic(null);
        var before = JsonSerializer.Serialize(document, JsonOptions.Default);

        Assert.Equal(["No name shown"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Identity));
        Assert.Equal(["No Home World", "No Favorite Job", "No Free Company"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Details));
        Assert.Equal(["No playstyles yet", "No active hours"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Playstyle));
        Assert.Equal(["No message yet"], BasicEditorView.SummaryOf(document, BasicEditorCategory.Message));
        Assert.Equal(before, JsonSerializer.Serialize(document, JsonOptions.Default));
    }

    // ---------------------------------------------------------------- preview click selection

    private static Vector2 CenterOf(ProfileDocument document, ProfileElementRole role)
    {
        var element = BasicSections.Find(document, role)!;
        return element.Position + (element.Size / 2f);
    }

    [Fact]
    public void ClickingASectionInThePreview_OpensItsCategory()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.CreatePortrait(Guid.NewGuid());
        editor.SetPlaystyles(["Casual"]);
        editor.SetText(ProfileElementRole.BasicMessage, "Hello");
        var buffer = new List<ProfileElement>();

        Assert.Equal(BasicEditorCategory.Portrait, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicPortrait), buffer));
        Assert.Equal(BasicEditorCategory.Identity, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicName), buffer));
        Assert.Equal(BasicEditorCategory.Details, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicJob), buffer));
        Assert.Equal(BasicEditorCategory.Details, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicLevel), buffer));
        Assert.Equal(BasicEditorCategory.Playstyle, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicPlaystyleHeading), buffer));
        Assert.Equal(BasicEditorCategory.Playstyle, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicActiveHours), buffer));
        Assert.Equal(BasicEditorCategory.Message, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicMessage), buffer));
        Assert.Empty(buffer);
    }

    [Fact]
    public void PreviewClicks_IgnoreEmptySpace_HiddenSections_AndSuppressedHeadings()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var buffer = new List<ProfileElement>();

        // Nothing drawn there: no portrait yet, and the message heading is suppressed (no message).
        Assert.Null(BasicEditorView.CategoryAt(document, new Vector2(200, 300), buffer));
        Assert.Null(BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicMessageHeading), buffer));

        BasicSections.Find(document, ProfileElementRole.BasicWorld)!.Visible = false;
        BasicSections.Find(document, ProfileElementRole.BasicWorldHeading)!.Visible = false;
        Assert.Null(BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicWorld), buffer));
    }

    [Fact]
    public void PreviewClicks_LookThroughFreeformElements_ToTheSectionBeneath()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var job = BasicSections.Find(document, ProfileElementRole.BasicJob)!;
        document.Elements.Add(new TextProfileElement { Text = "Sticker", Position = job.Position, Size = job.Size, ZIndex = 999 });

        Assert.Equal(BasicEditorCategory.Details, BasicEditorView.CategoryAt(document, CenterOf(document, ProfileElementRole.BasicJob), new List<ProfileElement>()));
    }

    // ---------------------------------------------------------------- no mutation

    [Fact]
    public async Task NavigatingAndInspectingEveryCategory_ChangesNothing()
    {
        using var harness = await NewClassicAsync();
        harness.Character.CurrentInfo = FakeCharacter.Hero;
        var before = harness.Json();
        var navigation = new BasicEditorNavigation();
        var buffer = new List<ProfileElement>();

        for (var frame = 0; frame < 3; frame++)
        {
            harness.SimulateBasicFrame();
            navigation.TrackPlate(harness.Document.ProfileId);
            foreach (var category in BasicEditorView.Categories)
            {
                navigation.Select(category);
                _ = BasicEditorView.StatusOf(harness.Document, category);
                _ = BasicEditorView.SummaryOf(harness.Document, category);
            }

            if (frame % 2 == 0)
            {
                EditorPreview.Enter(harness.Session);
            }
            else
            {
                EditorPreview.Exit(harness.Session);
            }

            navigation.Zoom = (PreviewZoom)(frame % 3);
            _ = BasicEditorView.CategoryAt(harness.Document, new Vector2(600, 300), buffer);
        }

        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }
}
