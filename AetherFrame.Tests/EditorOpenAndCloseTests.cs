using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Which editor a Plate opens in when the player doesn't choose (My Plates' Edit, double-click, Use
/// Template), and the unsaved-changes protection both editors share when their window closes.
/// </summary>
public class EditorOpenAndCloseTests
{
    private static (EditorDocumentCommands Commands, EditorCloseGuard Guard) OpenWindow(BasicHarness harness)
    {
        var commands = new EditorDocumentCommands(harness.Profiles, harness.Session);
        var guard = new EditorCloseGuard(harness.Session, commands);
        Assert.True(guard.PreOpenCheck(isOpen: true)); // the window has been open for a frame
        return (commands, guard);
    }

    private static async Task<bool> WaitForSaveToCloseAsync(EditorCloseGuard guard)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (guard.Advance())
            {
                return true;
            }

            if (!guard.IsSaving)
            {
                return false;
            }

            await Task.Delay(5);
        }

        return false;
    }

    // ---------------------------------------------------------------- automatic editor choice

    [Fact]
    public void AClassicPlate_OpensInBasic()
    {
        Assert.Equal(EditorSurfaceKind.Basic, EditorSurfaceChooser.ForDocument(BasicDocuments.Classic(FakeCharacter.Hero)));
        Assert.Equal(EditorSurfaceKind.Basic, EditorSurfaceChooser.ForDocument(BasicDocuments.Classic()));
    }

    [Fact]
    public void ABlankCanvasPlate_OpensInAdvanced()
    {
        Assert.Equal(EditorSurfaceKind.Advanced, EditorSurfaceChooser.ForDocument(BasicDocuments.Blank()));
    }

    [Fact]
    public void AFreeformPlate_OpensInAdvanced()
    {
        var document = BasicDocuments.Blank();
        document.Elements.Add(new TextProfileElement { Text = "Freeform", ZIndex = 0 });
        document.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 1 });

        Assert.Equal(EditorSurfaceKind.Advanced, EditorSurfaceChooser.ForDocument(document));
    }

    [Fact]
    public void AnUnreadablePlate_OpensInAdvanced()
    {
        Assert.Equal(EditorSurfaceKind.Advanced, EditorSurfaceChooser.ForDocument(null));
    }

    [Fact]
    public void AFreeformPlateWithOneBasicSection_OpensInBasic()
    {
        // The same content rule Use Template has always used: any Basic section means Basic.
        var document = BasicDocuments.Blank();
        document.Elements.Add(new TextProfileElement { Text = "Freeform", ZIndex = 0 });
        BasicDocuments.Editor(document).SetText(ProfileElementRole.BasicMessage, "Hello");

        Assert.Equal(EditorSurfaceKind.Basic, EditorSurfaceChooser.ForDocument(document));
    }

    [Fact]
    public void AClassicPlateWithEverySectionHidden_StillOpensInBasic()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        foreach (var section in BasicSections.ElementSections)
        {
            if (section != BasicSection.Portrait)
            {
                editor.SetSectionVisible(section, false);
            }
        }

        Assert.Equal(EditorSurfaceKind.Basic, EditorSurfaceChooser.ForDocument(document));
    }

    [Theory]
    [InlineData(PlateStartingLayout.AdventurePlateClassic, true)]
    [InlineData(PlateStartingLayout.Blank, false)]
    public async Task ASavedPlate_OpensInTheEditorItsContentSuits(PlateStartingLayout layout, bool basic)
    {
        using var harness = await BasicHarness.CreatePlateAsync(layout, new PlateStarterContent(FakeCharacter.Hero));

        var expected = basic ? EditorSurfaceKind.Basic : EditorSurfaceKind.Advanced;
        Assert.Equal(expected, EditorSurfaceChooser.ForDocument(harness.Library.GetSavedDocument(harness.PlateId)));
    }

    [Fact]
    public async Task AnImportedClassicPlate_OpensInBasic_AndAnImportedBlankOneInAdvanced()
    {
        // An imported Plate is an ordinary saved Plate: Edit judges its content the same way.
        using var classic = await BasicHarness.OpenDocumentAsync(BasicDocuments.Classic(FakeCharacter.Hero));
        using var blank = await BasicHarness.OpenDocumentAsync(BasicDocuments.Blank());

        Assert.Equal(EditorSurfaceKind.Basic, EditorSurfaceChooser.ForDocument(classic.Library.GetSavedDocument(classic.PlateId)));
        Assert.Equal(EditorSurfaceKind.Advanced, EditorSurfaceChooser.ForDocument(blank.Library.GetSavedDocument(blank.PlateId)));
    }

    // ---------------------------------------------------------------- close protection

    [Fact]
    public async Task ClosingACleanPlate_JustCloses()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (_, guard) = OpenWindow(harness);

        Assert.False(guard.PreOpenCheck(isOpen: false));
        Assert.False(guard.IsAsking);
        Assert.False(guard.ConsumePromptRequest());
        Assert.False(guard.ShouldReopenOnClose());
    }

    [Fact]
    public async Task ClosingWithUnsavedChanges_IsRefused_AndAsksOnce()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (_, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.True(guard.PreOpenCheck(isOpen: false)); // stays open
        Assert.True(guard.IsAsking);
        Assert.True(guard.CanSave);
        Assert.True(guard.ConsumePromptRequest());
        Assert.False(guard.ConsumePromptRequest());
    }

    [Fact]
    public async Task Cancel_KeepsTheEditorOpen_WithTheEditsIntact()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (commands, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        Assert.True(guard.PreOpenCheck(isOpen: false));

        guard.Cancel();

        Assert.False(guard.IsAsking);
        Assert.True(guard.PreOpenCheck(isOpen: true));
        Assert.Equal(AdventurePlateOrientation.Mirrored, BasicEditorSession.GetOrientation(harness.Document));
        Assert.True(commands.IsDirty);
        Assert.True(commands.CanUndo);

        // And the next close asks again.
        Assert.True(guard.PreOpenCheck(isOpen: false));
        Assert.True(guard.IsAsking);
    }

    [Fact]
    public async Task Discard_RestoresTheSavedState_AndCloses()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var saved = harness.Json();
        var (commands, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.AddPlaystyle("Roleplay");
        Assert.True(guard.PreOpenCheck(isOpen: false));

        Assert.True(guard.Discard());

        Assert.Equal(saved, harness.Json());
        Assert.False(commands.IsDirty);
        Assert.False(guard.PreOpenCheck(isOpen: false)); // the close goes through
        Assert.False(guard.ShouldReopenOnClose());
        Assert.Equal(AdventurePlateOrientation.Normal, harness.Library.OpenDocumentForEditing(harness.PlateId).BasicPlate!.Orientation);
    }

    [Fact]
    public async Task Save_SavesThenCloses()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (commands, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        Assert.True(guard.PreOpenCheck(isOpen: false));

        guard.Save();
        Assert.True(await WaitForSaveToCloseAsync(guard));

        Assert.False(guard.IsAsking);
        Assert.False(commands.IsDirty);
        Assert.Equal(AdventurePlateOrientation.Mirrored, harness.Library.OpenDocumentForEditing(harness.PlateId).BasicPlate!.Orientation);
        Assert.False(guard.PreOpenCheck(isOpen: false));
        Assert.False(guard.ShouldReopenOnClose());
    }

    [Fact]
    public async Task AnEditStillInProgress_CountsAsUnsaved_AndIsKeptOnCancel()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (commands, guard) = OpenWindow(harness);

        // Typing in a field that hasn't been committed yet.
        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Half-typed");

        Assert.True(guard.PreOpenCheck(isOpen: false));
        guard.Cancel();

        Assert.Equal("Half-typed", BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessage)!.Text);
        Assert.True(commands.IsDirty);
        commands.Undo();
        Assert.False(commands.IsDirty);
    }

    [Fact]
    public async Task HandingOffToTheOtherEditor_IsNeverGuarded_AndKeepsTheEdits()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (commands, guard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        guard.ConfirmClose();

        Assert.False(guard.PreOpenCheck(isOpen: false));
        Assert.False(guard.IsAsking);
        Assert.False(guard.ShouldReopenOnClose());
        Assert.True(commands.IsDirty);

        // Back in this editor later, a real close asks again.
        Assert.True(guard.PreOpenCheck(isOpen: true));
        Assert.True(guard.PreOpenCheck(isOpen: false));
        Assert.True(guard.IsAsking);
    }

    [Fact]
    public async Task ACloseThatSlipsPastTheCheck_ReopensAndAsks()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var commands = new EditorDocumentCommands(harness.Profiles, harness.Session);
        var guard = new EditorCloseGuard(harness.Session, commands);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.True(guard.ShouldReopenOnClose());
        Assert.True(guard.IsAsking);
        Assert.True(guard.ConsumePromptRequest());
    }

    [Fact]
    public async Task WithNoPlateOpen_NothingIsGuarded()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (_, guard) = OpenWindow(harness);
        harness.Profiles.CloseDocument();
        harness.Session.SyncWithCurrentProfile();

        Assert.False(guard.PreOpenCheck(isOpen: false));
        Assert.False(guard.ShouldReopenOnClose());
    }

    [Fact]
    public async Task TheBasicAndAdvancedEditors_EachGuardTheirOwnClose()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (_, basicGuard) = OpenWindow(harness);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        // Basic hands over to Advanced; Advanced's own guard protects its close.
        basicGuard.ConfirmClose();
        Assert.False(basicGuard.PreOpenCheck(isOpen: false));
        var (_, advancedGuard) = OpenWindow(harness);

        Assert.True(advancedGuard.PreOpenCheck(isOpen: false));
        Assert.True(advancedGuard.IsAsking);
    }
}
