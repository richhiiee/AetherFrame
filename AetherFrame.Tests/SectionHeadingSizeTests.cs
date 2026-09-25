using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Basic section headings ("HOME WORLD", "MESSAGE"...): their 16 px Classic default, and the
/// Basic editor's one shared "Section heading size" control — every heading together, values never,
/// through the shared history, dirty state, save and revert, across both editors. Existing Plates
/// are never restyled just by being opened.
/// </summary>
public class SectionHeadingSizeTests
{
    private static readonly ProfileElementRole[] HeadingRoles =
    [
        ProfileElementRole.BasicWorldHeading, ProfileElementRole.BasicJobHeading, ProfileElementRole.BasicFreeCompanyHeading,
        ProfileElementRole.BasicPlaystyleHeading, ProfileElementRole.BasicActiveHoursHeading, ProfileElementRole.BasicMessageHeading,
    ];

    private static Task<BasicHarness> NewClassicAsync() =>
        BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));

    private static float[] HeadingSizes(ProfileDocument document) =>
        HeadingRoles.Select(role => BasicSections.FindText(document, role)!.FontSize).ToArray();

    /// <summary>Every element that isn't a heading, as JSON: what the heading control must never touch.</summary>
    private static string NonHeadings(ProfileDocument document) =>
        string.Join("\n", document.Elements.Where(e => !BasicSections.IsHeading(e.Role)).Select(e => System.Text.Json.JsonSerializer.Serialize<object>(e)));

    // ---------------------------------------------------------------- default

    [Fact]
    public void ANewClassicPlate_HasSixteenPixelHeadings()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);

        Assert.Equal(16f, AdventurePlateClassicLayout.DefaultHeadingFontSize);
        Assert.All(HeadingSizes(document), size => Assert.Equal(16f, size));
        Assert.Equal(6, BasicPlateEditor.Headings(document).Count);
    }

    [Fact]
    public void ANewClassicPlate_KeepsItsTwentyPixelValues()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);

        Assert.Equal(20f, BasicSections.FindText(document, ProfileElementRole.BasicWorld)!.FontSize);
        Assert.Equal(20f, BasicSections.FindText(document, ProfileElementRole.BasicFreeCompany)!.FontSize);
        Assert.Equal(20f, BasicSections.FindText(document, ProfileElementRole.BasicJob)!.FontSize);
    }

    [Fact]
    public void TheHeadingDefault_ScalesWithTheCanvas()
    {
        var document = BasicDocuments.Blank(1920f, 1080f);
        BasicDocuments.Editor(document).EnsureSection(BasicSection.World);

        Assert.Equal(24f, BasicSections.FindText(document, ProfileElementRole.BasicWorldHeading)!.FontSize);
    }

    [Fact]
    public void SixteenPixelHeadings_FitTheirRowWithoutShrinking()
    {
        // The Classic grid is unchanged: the heading row holds exactly 16 px (its height less the
        // text padding on both sides), so the default is never auto-fitted smaller.
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        foreach (var heading in BasicPlateEditor.Headings(document))
        {
            Assert.True(heading.Size.Y - (2f * TextProfileElement.LayoutPadding) >= heading.FontSize, $"{heading.Role} is clipped");
        }
    }

    [Fact]
    public void ResetSection_RestoresTheDefaultHeadingSize()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.SetHeadingSize(11f);

        editor.ResetSection(BasicSection.Message);

        Assert.Equal(16f, BasicSections.FindText(document, ProfileElementRole.BasicMessageHeading)!.FontSize);
        Assert.Equal(11f, BasicSections.FindText(document, ProfileElementRole.BasicWorldHeading)!.FontSize);
    }

    // ---------------------------------------------------------------- the shared control

    [Fact]
    public async Task SectionHeadingSize_UpdatesEveryStandardHeading()
    {
        using var harness = await NewClassicAsync();

        harness.Basic.SetHeadingSize(22f, continuous: false);

        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(22f, size));
        Assert.Equal(22f, BasicPlateEditor.HeadingSize(harness.Document));
        Assert.False(BasicPlateEditor.HeadingSizesDiffer(harness.Document));
    }

    [Fact]
    public async Task SectionHeadingSize_NeverTouchesValuesOrAnythingElse()
    {
        using var harness = await NewClassicAsync();
        var before = NonHeadings(harness.Document);
        var captions = HeadingRoles.Select(role => BasicSections.FindText(harness.Document, role)!.Text).ToArray();

        harness.Basic.SetHeadingSize(12f, continuous: false);

        Assert.Equal(before, NonHeadings(harness.Document));
        Assert.Equal(captions, HeadingRoles.Select(role => BasicSections.FindText(harness.Document, role)!.Text));
        Assert.Equal(20f, BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorld)!.FontSize);
    }

    [Fact]
    public async Task SectionHeadingSize_StaysWithinTheTextSizeRange()
    {
        using var harness = await NewClassicAsync();

        harness.Basic.SetHeadingSize(500f, continuous: false);
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(TextProfileElement.MaxFontSize, size));

        harness.Basic.SetHeadingSize(1f, continuous: false);
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(TextProfileElement.MinFontSize, size));
    }

    [Fact]
    public void AHeadingCreatedLater_JoinsTheSharedSize()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.SetText(ProfileElementRole.BasicWorld, "Phoenix");
        editor.SetHeadingSize(21f);

        editor.SetText(ProfileElementRole.BasicMessage, "Hello");

        Assert.Equal(21f, BasicSections.FindText(document, ProfileElementRole.BasicMessageHeading)!.FontSize);
    }

    [Fact]
    public async Task APlateWithoutHeadings_HasNothingToResize()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.Blank, null);

        Assert.Null(BasicPlateEditor.HeadingSize(harness.Document));
        harness.Basic.SetHeadingSize(20f, continuous: false);

        Assert.Empty(BasicPlateEditor.Headings(harness.Document));
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }

    // ---------------------------------------------------------------- history, dirty state, save, revert

    [Fact]
    public async Task ASliderDrag_IsOneUndoStep_AndRedoable()
    {
        using var harness = await NewClassicAsync();

        harness.Basic.SetHeadingSize(17f, continuous: true);
        harness.Basic.SetHeadingSize(19f, continuous: true);
        harness.Basic.SetHeadingSize(21f, continuous: true);
        Assert.True(harness.Session.IsDirty); // still dragging
        harness.Basic.CommitTextEdit();

        harness.Session.Undo();
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(16f, size));
        Assert.False(harness.Session.CanUndo);
        Assert.False(harness.Session.IsDirty);

        harness.Session.Redo();
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(21f, size));
        Assert.True(harness.Session.IsDirty);
    }

    [Fact]
    public async Task HeadingSize_IsSaved()
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetHeadingSize(19f, continuous: false);

        Assert.True(await harness.Session.SaveProfileAsync());
        harness.Session.SyncWithCurrentProfile();

        Assert.False(harness.Session.IsDirty);
        Assert.All(HeadingSizes(harness.Library.OpenDocumentForEditing(harness.PlateId)), size => Assert.Equal(19f, size));
    }

    [Fact]
    public async Task HeadingSize_IsReverted()
    {
        using var harness = await NewClassicAsync();
        var commands = new EditorDocumentCommands(harness.Profiles, harness.Session);
        harness.Basic.SetHeadingSize(19f, continuous: false);

        Assert.True(commands.Revert());

        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(16f, size));
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task HeadingSize_CarriesAcrossBothEditors()
    {
        using var harness = await NewClassicAsync();
        harness.SimulateBasicFrame();
        harness.Basic.SetHeadingSize(20f, continuous: false);

        // Basic -> Advanced: the same document, still unsaved, still undoable.
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(20f, size));
        Assert.True(harness.Session.IsDirty);

        // Advanced sizes one heading on its own.
        var message = BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessageHeading)!;
        harness.Session.ApplyImmediateEdit(message.Id, element => ((TextProfileElement)element).FontSize = 14f);

        // Advanced -> Basic: Basic sees the mixed sizes, and undo walks back through both editors' steps.
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        harness.SimulateBasicFrame();
        Assert.True(BasicPlateEditor.HeadingSizesDiffer(harness.Document));
        Assert.Equal(20f, BasicPlateEditor.HeadingSize(harness.Document));

        harness.Session.Undo();
        Assert.False(BasicPlateEditor.HeadingSizesDiffer(harness.Document));
        harness.Session.Undo();
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(16f, size));
        Assert.False(harness.Session.IsDirty);
    }

    // ---------------------------------------------------------------- existing Plates

    [Fact]
    public async Task AnExistingPlate_KeepsItsHeadingSizes_WhenOpened()
    {
        // A Plate saved before the new default, with one heading customized in the Advanced Editor.
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        foreach (var heading in BasicPlateEditor.Headings(document))
        {
            heading.FontSize = 13f;
        }

        BasicSections.FindText(document, ProfileElementRole.BasicMessageHeading)!.FontSize = 11f;
        using var harness = await BasicHarness.OpenDocumentAsync(document);

        harness.SimulateBasicFrame();
        harness.Surfaces.Show(EditorSurfaceKind.Advanced);
        harness.Surfaces.Show(EditorSurfaceKind.Basic);
        harness.SimulateBasicFrame();

        Assert.Equal(new[] { 13f, 13f, 13f, 13f, 13f, 11f }, HeadingSizes(harness.Document));
        Assert.True(BasicPlateEditor.HeadingSizesDiffer(harness.Document));
        Assert.False(harness.Session.IsDirty);
        Assert.False(harness.Session.CanUndo);
    }

    [Fact]
    public async Task AnExistingPlate_OnlyChangesWhenThePlayerAsks()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        foreach (var heading in BasicPlateEditor.Headings(document))
        {
            heading.FontSize = 13f;
        }

        using var harness = await BasicHarness.OpenDocumentAsync(document);
        harness.SimulateBasicFrame();

        harness.Basic.SetHeadingSize(16f, continuous: false);

        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(16f, size));
        Assert.True(harness.Session.IsDirty);
    }
}
