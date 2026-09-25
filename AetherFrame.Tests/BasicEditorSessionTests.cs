using System;
using System.IO;
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

/// <summary>
/// The Basic editor through the real editing stack: history, dirty state, the shared document,
/// and the one-surface handoff to the Advanced editor.
/// </summary>
public class BasicEditorSessionTests
{
    private static Task<BasicHarness> NewClassicAsync(BasicCharacterInfo? character = null) =>
        BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(character ?? FakeCharacter.Hero));

    private static ElementRect LayoutRect(ProfileDocument document, ProfileElementRole role) =>
        AdventurePlateClassicLayout.GetRect(role, BasicPlateEditor.GetOrientation(document), document)!.Value;

    // ---------------------------------------------------------------- opening

    [Fact]
    public async Task OpeningBasic_RepeatedlyChangesNothing_AndIsNotDirty()
    {
        using var harness = await NewClassicAsync();
        harness.Character.CurrentInfo = FakeCharacter.Hero with { HomeWorld = "Odin", Level = 50 };
        var before = harness.Json();
        var count = harness.Document.Elements.Count;

        for (var i = 0; i < 5; i++)
        {
            harness.SimulateBasicFrame();
            harness.Surfaces.Show(EditorSurfaceKind.Advanced);
            harness.Surfaces.Show(EditorSurfaceKind.Basic);
        }

        Assert.Equal(before, harness.Json());
        Assert.Equal(count, harness.Document.Elements.Count);
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task OpeningBasic_OnALegacyPlate_WritesNoSettings_AndNothingToDisk()
    {
        var plateId = Guid.NewGuid();
        var json = LegacyData.VersionOneDocument(plateId, owner: 0);
        using var harness = await BasicHarness.OpenJsonAsync(json, plateId);
        var memoryBefore = harness.Json();

        harness.SimulateBasicFrame();
        harness.SimulateBasicFrame();

        Assert.Null(harness.Document.BasicPlate);
        Assert.Null(harness.Document.BasicIdentity);
        Assert.Equal(1920f, harness.Document.CanvasWidth);
        Assert.Equal(memoryBefore, harness.Json());
        Assert.Equal(json, harness.Fixture.ReadPlateJson(plateId));
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public void CanResetLayout_IsFalse_ForADocumentWithNoBasicSections_LikeOneFromBlankCanvas()
    {
        // This is the exact predicate a Template-created Plate's default editor (Basic vs
        // Advanced) is derived from — a Blank Canvas document must never look like it has Basic
        // structure to reset.
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Blank", DateTime.UtcNow);

        Assert.False(BasicEditorSession.CanResetLayout(document));
    }

    [Fact]
    public async Task OpeningBasic_OnABlankCanvasPlate_CreatesNoSections()
    {
        // Preserves the existing "opening Basic mode never mutates a Plate" invariant for content
        // shaped like the new Blank Canvas built-in Template — no special-casing was added for it.
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var before = harness.Json();

        harness.SimulateBasicFrame();
        harness.SimulateBasicFrame();

        Assert.Empty(harness.Document.Elements);
        Assert.Null(harness.Document.BasicPlate);
        Assert.Null(harness.Document.BasicIdentity);
        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task LegacyBasicElements_CountAsCustomized_AndAnOrientationChangeLeavesThemAlone()
    {
        var plateId = Guid.NewGuid();
        using var harness = await BasicHarness.OpenJsonAsync(LegacyData.VersionOneDocument(plateId, owner: 0), plateId);
        var portrait = BasicSections.Find(harness.Document, ProfileElementRole.BasicPortrait)!;
        var at = BasicDocuments.RectOf(portrait);

        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Portrait));
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(at, BasicDocuments.RectOf(portrait));
        Assert.Equal(AdventurePlateOrientation.Mirrored, harness.Document.BasicPlate!.Orientation);

        // Only the Basic settings changed, and that alone is an unsaved, undoable change.
        Assert.True(harness.Session.IsDirty);
        harness.Session.Undo();
        Assert.Null(harness.Document.BasicPlate);
        Assert.False(harness.Session.IsDirty);
    }

    // ---------------------------------------------------------------- message

    [Fact]
    public async Task Message_IsCreatedOnlyByTyping_AsOneUndoStep()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.Blank, null);
        harness.SimulateBasicFrame();
        Assert.Equal(0, harness.Count(ProfileElementRole.BasicMessage));

        harness.Basic.SetText(ProfileElementRole.BasicMessage, "H");
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Hi");
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Hi there");
        harness.Basic.CommitTextEdit();

        Assert.Equal(1, harness.Count(ProfileElementRole.BasicMessage));
        Assert.Equal(1, harness.Count(ProfileElementRole.BasicMessageHeading));
        Assert.Equal("Hi there", BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessage)!.Text);
        Assert.True(harness.Session.IsDirty);

        harness.Session.Undo();

        Assert.Empty(harness.Document.Elements);
        Assert.Null(harness.Document.BasicPlate);
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task Message_RespectsTheTextLimit()
    {
        using var harness = await NewClassicAsync();

        harness.Basic.SetText(ProfileElementRole.BasicMessage, new string('x', TextProfileElement.MaxTextLength + 50));
        harness.Basic.CommitTextEdit();

        Assert.Equal(TextProfileElement.MaxTextLength, BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessage)!.Text.Length);
    }

    // ---------------------------------------------------------------- dirty state and undo

    [Fact]
    public async Task Visibility_IsUndoable_AndDirty_AndKeepsContent()
    {
        using var harness = await NewClassicAsync();

        harness.Basic.SetSectionVisible(BasicSection.World, false);

        Assert.True(harness.Session.IsDirty);
        Assert.False(BasicSections.IsVisible(harness.Document, BasicSection.World));
        Assert.Equal("Phoenix [Light]", BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.Text);

        harness.Session.Undo();
        Assert.True(BasicSections.IsVisible(harness.Document, BasicSection.World));
        Assert.False(harness.Session.IsDirty);

        harness.Session.Redo();
        Assert.False(BasicSections.IsVisible(harness.Document, BasicSection.World));
    }

    [Fact]
    public async Task EveryLayoutAction_IsExactlyOneUndoStep()
    {
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicWorld, new Vector2(30, 30));
        harness.DragInAdvanced(ProfileElementRole.BasicName, new Vector2(0, 30));
        var clean = harness.Json();

        foreach (var action in new Action[]
        {
            () => harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored),
            () => harness.Basic.ApplyLayout(),
            () => harness.Basic.ApplySectionLayout(BasicSection.World),
            () => harness.Basic.ResetSection(BasicSection.World),
            () => harness.Basic.ResetSection(BasicSection.Identity),
            () => harness.Basic.ResetBasicLayout(),
            () => harness.Basic.ApplyTheme(ProfileThemePresets.All[4]),
            () => harness.Basic.AddPlaystyle("Casual"),
            () => harness.Basic.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Weekends }),
        })
        {
            action();
            Assert.NotEqual(clean, harness.Json());
            harness.Session.Undo();
            Assert.Equal(clean, harness.Json());
        }
    }

    [Fact]
    public async Task SliderStyleEdits_CoalesceIntoOneStep()
    {
        using var harness = await NewClassicAsync();
        var world = BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!;

        for (var size = 21; size <= 30; size++)
        {
            var value = size;
            harness.Basic.EditSectionStyle(ProfileElementRole.BasicWorld, e => e.FontSize = value, continuous: true);
        }

        harness.Basic.CommitTextEdit();
        Assert.Equal(30f, world.FontSize);

        // Undo restores snapshots, so look the element up again.
        harness.Session.Undo();
        Assert.Equal(20f, BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.FontSize);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task SaveMakesItClean_AndTheSavedFileHasTheBasicSettings()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.AddPlaystyle("Roleplay");

        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();

        Assert.False(harness.Session.IsDirty);
        var saved = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.Equal(AdventurePlateOrientation.Mirrored, saved.BasicPlate!.Orientation);
        Assert.Equal(["Roleplay"], saved.BasicPlate.Playstyles);
    }

    // ---------------------------------------------------------------- Advanced customization

    [Fact]
    public async Task DraggingInAdvanced_MarksTheSectionCustomized_AndBasicEditsNeverMoveIt()
    {
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicFreeCompany, new Vector2(40, 25));
        var element = BasicSections.Find(harness.Document, ProfileElementRole.BasicFreeCompany)!;
        var draggedTo = BasicDocuments.RectOf(element);

        Assert.Equal([BasicSection.FreeCompany], BasicEditorSession.CustomizedSections(harness.Document));

        harness.Basic.SetText(ProfileElementRole.BasicFreeCompany, "The Long Name");
        harness.Basic.CommitTextEdit();
        harness.Basic.EditSectionStyle(ProfileElementRole.BasicFreeCompany, e => e.Bold = true, continuous: false);
        harness.Basic.SetSectionVisible(BasicSection.FreeCompany, false);
        harness.Basic.SetSectionVisible(BasicSection.FreeCompany, true);
        harness.Basic.ApplyTheme(ProfileThemePresets.All[5]);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(draggedTo, BasicDocuments.RectOf(element));
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.FreeCompany));
        Assert.True(((TextProfileElement)element).Bold);

        harness.Basic.ApplySectionLayout(BasicSection.FreeCompany);
        Assert.Equal(LayoutRect(harness.Document, ProfileElementRole.BasicFreeCompany), BasicDocuments.RectOf(element));
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
    }

    [Fact]
    public async Task IdentityHeader_CustomizedInAdvanced_StaysPut_ThroughOrientationChanges()
    {
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicName, new Vector2(-200, 300));
        var name = BasicSections.Find(harness.Document, ProfileElementRole.BasicName)!;
        var at = BasicDocuments.RectOf(name);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Identity.SetCustomTitle("Hello");
        harness.Identity.Commit();

        Assert.Equal(at, BasicDocuments.RectOf(name));
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));

        harness.Basic.ApplySectionLayout(BasicSection.Identity);
        Assert.Equal(40f, name.Position.X);
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
    }

    [Fact]
    public async Task ManagedIdentityHeader_FollowsTheOrientation()
    {
        using var harness = await NewClassicAsync();

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(40f, BasicSections.Find(harness.Document, ProfileElementRole.BasicName)!.Position.X);
        Assert.Equal(new Vector2(40, 44), harness.Document.BasicIdentity!.RegionPosition);
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
    }

    [Fact]
    public async Task ResetBasicLayout_KeepsAdvancedElements_AndImportedAssets()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetPortrait(harness.ImportablePng());
        var portraitAsset = harness.Basic.Portrait!.AssetId;
        var freeformId = harness.Session.AddTextElement("Advanced only")!.Value;
        harness.DragInAdvanced(ProfileElementRole.BasicPortrait, new Vector2(100, 0));
        var freeform = harness.Document.Elements.Single(e => e.Id == freeformId).Clone();
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        var count = harness.Document.Elements.Count;

        harness.Basic.ResetBasicLayout();

        Assert.Equal(count, harness.Document.Elements.Count);
        Assert.True(harness.Document.Elements.Single(e => e.Id == freeformId).ContentEquals(freeform));
        Assert.Equal(portraitAsset, harness.Basic.Portrait!.AssetId);
        Assert.NotNull(harness.Assets.ResolveAssetPath(portraitAsset));
        Assert.Equal(AdventurePlateOrientation.Normal, harness.Document.BasicPlate!.Orientation);
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
        Assert.Equal(40f, harness.Basic.Portrait.Position.X);
    }

    // ---------------------------------------------------------------- portrait

    [Fact]
    public async Task ReplacingThePortrait_KeepsItsPlacementAndFit()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetPortrait(harness.ImportablePng("a.png"));
        harness.DragInAdvanced(ProfileElementRole.BasicPortrait, new Vector2(10, 10));
        harness.Session.ApplyImmediateEdit(harness.Basic.Portrait!.Id, e => ((ImageProfileElement)e).DisplayMode = ProfileImageFit.Fit);
        var before = harness.Basic.Portrait!.Clone();

        harness.Basic.SetPortrait(harness.ImportablePng("b.png", 300, 300));

        var after = harness.Basic.Portrait!;
        Assert.Equal(1, harness.Count(ProfileElementRole.BasicPortrait));
        Assert.NotEqual(((ImageProfileElement)before).AssetId, after.AssetId);
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.Size, after.Size);
        Assert.Equal(ProfileImageFit.Fit, after.DisplayMode);
    }

    [Fact]
    public async Task RemovingThePortrait_IsUndoable()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetPortrait(harness.ImportablePng());
        var id = harness.Basic.Portrait!.Id;

        harness.Basic.RemovePortrait();
        Assert.Null(harness.Basic.Portrait);

        harness.Session.Undo();
        Assert.Equal(id, harness.Basic.Portrait!.Id);
    }

    // ---------------------------------------------------------------- character data

    [Fact]
    public async Task WithNoCharacter_UseCurrentActionsDoNothing()
    {
        using var harness = await NewClassicAsync(FakeCharacter.Hero);
        harness.Character.CurrentInfo = null;
        var before = harness.Json();

        harness.Basic.UseCurrentWorld();
        harness.Basic.UseCurrentJob();
        harness.Basic.UseCurrentFreeCompany();
        harness.Identity.UseCharacterName();

        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.CanUndo);
        Assert.Null(harness.Identity.CharacterName);
    }

    [Fact]
    public async Task LiveCharacterData_OnlyChangesThePlateOnRequest()
    {
        using var harness = await NewClassicAsync(FakeCharacter.Hero);
        harness.Character.CurrentInfo = FakeCharacter.Hero with { HomeWorld = "Odin", DataCenter = "Light", JobName = "Dancer", JobId = 38, Level = 92, FreeCompanyTag = "XYZ" };

        harness.SimulateBasicFrame();
        Assert.Equal("Phoenix [Light]", BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.Text);
        Assert.False(harness.Session.IsDirty);

        harness.Basic.UseCurrentWorld();
        harness.Basic.UseCurrentJob();
        harness.Basic.UseCurrentFreeCompany();

        Assert.Equal("Odin [Light]", BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.Text);
        // Use current adds the current job after the Favorite Jobs already chosen (never a level).
        Assert.Equal("Paladin, Dancer", BasicSections.FindText(harness.Document, ProfileElementRole.BasicJob)!.Text);
        Assert.Null(BasicSections.Find(harness.Document, ProfileElementRole.BasicLevel));
        Assert.Equal("«XYZ»", BasicSections.FindText(harness.Document, ProfileElementRole.BasicFreeCompany)!.Text);
        Assert.Equal([19u, 38u], harness.Document.BasicPlate!.FavoriteJobIds);
        Assert.Equal(19u, harness.Document.BasicPlate.FavoriteJobId);
    }

    [Fact]
    public async Task UserValues_OverrideCharacterDefaults()
    {
        using var harness = await NewClassicAsync(FakeCharacter.Hero);
        harness.Character.CurrentInfo = FakeCharacter.Hero;

        harness.Basic.SetText(ProfileElementRole.BasicWorld, "Anywhere I roam");
        harness.Basic.CommitTextEdit();
        harness.SimulateBasicFrame();

        Assert.Equal("Anywhere I roam", BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.Text);
    }

    // ---------------------------------------------------------------- Basic <-> Advanced

    [Fact]
    public async Task SwitchingEditors_KeepsTheSameDocument_Dirtiness_History_AndSelection()
    {
        using var harness = await NewClassicAsync();
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        var document = harness.Document;

        // Mid-typing in Basic when the user switches: the typing run is committed, not lost.
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Half-typed");
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);

        Assert.False(harness.BasicSurface.IsOpen);
        Assert.True(harness.AdvancedSurface.IsOpen);
        Assert.Same(document, harness.Document);
        Assert.True(harness.Session.IsDirty);
        Assert.False(harness.Session.HasPendingDocumentEdit);

        // An Advanced edit, then back to Basic: both are in the one shared history.
        var messageId = BasicSections.Find(document, ProfileElementRole.BasicMessage)!.Id;
        harness.Session.Select(messageId);
        harness.DragInAdvanced(ProfileElementRole.BasicMessage, new Vector2(0, 5));
        harness.Surfaces.Show(EditorSurfaceKind.Basic);

        Assert.Same(document, harness.Document);
        Assert.Equal(messageId, harness.Session.SelectedElementId);
        Assert.True(BasicEditorSession.IsSectionCustomized(document, BasicSection.Message));

        harness.Session.Undo();
        Assert.False(BasicEditorSession.IsSectionCustomized(document, BasicSection.Message));
        harness.Session.Undo();
        Assert.Equal(string.Empty, BasicSections.FindText(document, ProfileElementRole.BasicMessage)!.Text);
        Assert.False(harness.Session.IsDirty);
    }

    // ---------------------------------------------------------------- Plate creation

    [Fact]
    public async Task CreatingAClassicPlate_WithStarterContent_StillFollowsTheActiveRules()
    {
        using var harness = await NewClassicAsync();
        var first = await harness.Library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice, starter: new PlateStarterContent(FakeCharacter.Hero));
        var second = await harness.Library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice, starter: new PlateStarterContent(FakeCharacter.Hero));

        Assert.True(first.BecameActive);
        Assert.False(second.BecameActive);
        Assert.Equal(first.PlateId, harness.Library.GetActivePlateId(Characters.Alice.ContentId));

        var saved = harness.Library.OpenDocumentForEditing(second.PlateId);
        Assert.Equal("Hero Example", BasicSections.FindText(saved, ProfileElementRole.BasicName)!.Text);
        Assert.Empty(BasicPlateEditor.CustomizedSections(saved));
        Assert.Equal(0ul, saved.OwnerContentId);
    }

    // ---------------------------------------------------------------- forward compatibility

    [Fact]
    public async Task BasicEdits_PreserveUnknownFields_AndUnsupportedElements()
    {
        var plateId = Guid.NewGuid();
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.ProfileId = plateId;
        var node = JsonSerializer.SerializeToNode(document, JsonOptions.Default)!.AsObject();
        node["BasicPlate"]!["FuturePlateSetting"] = "gilded";
        node["BasicPlate"]!["Placements"]![0]!["FutureAnchor"] = "top";
        node["Elements"]!.AsArray().Add(FutureData.UnknownElement());
        node["Elements"]![0]!["FutureGlow"] = 4;

        using var harness = await BasicHarness.OpenJsonAsync(node.ToJsonString(JsonOptions.Default), plateId);
        Assert.True(harness.Document.HasUnsupportedElements);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.ResetBasicLayout();
        harness.Basic.SetText(ProfileElementRole.BasicWorld, "Odin");
        harness.Basic.CommitTextEdit();
        await harness.Session.SaveProfileAsync();

        var saved = JsonNode.Parse(harness.Fixture.ReadPlateJson(plateId))!.AsObject();
        Assert.Equal("gilded", saved["BasicPlate"]!["FuturePlateSetting"]!.GetValue<string>());
        Assert.Contains(saved["BasicPlate"]!["Placements"]!.AsArray(), p => p!["FutureAnchor"]?.GetValue<string>() == "top");
        Assert.Contains(saved["Elements"]!.AsArray(), e => JsonNode.DeepEquals(e, FutureData.UnknownElement()));
        Assert.Contains(saved["Elements"]!.AsArray(), e => e!["FutureGlow"]?.GetValue<int>() == 4);
    }

    [Fact]
    public async Task OpeningAndClosingBasic_OnARichPlate_LeavesItsFileByteForByte()
    {
        var plateId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(SampleDocuments.Rich(plateId, "Rich", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), JsonOptions.Default);
        using var harness = await BasicHarness.OpenJsonAsync(json, plateId);

        harness.SimulateBasicFrame();
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        harness.Profiles.CloseDocument();
        harness.Session.SyncWithCurrentProfile();

        Assert.Equal(json, harness.Fixture.ReadPlateJson(plateId));
    }
}
