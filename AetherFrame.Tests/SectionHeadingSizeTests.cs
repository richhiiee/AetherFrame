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
    public async Task SectionHeadingSize_StaysWithinWhatTheLayoutShowsAtFullSize()
    {
        using var harness = await NewClassicAsync();

        Assert.Equal(32f, AdventurePlateClassicLayout.MaxHeadingFontSize(harness.Document));

        harness.Basic.SetHeadingSize(500f, continuous: false);
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(32f, size));

        harness.Basic.SetHeadingSize(1f, continuous: false);
        Assert.All(HeadingSizes(harness.Document), size => Assert.Equal(TextProfileElement.MinFontSize, size));
    }

    // ---------------------------------------------------------------- sizes above the default

    /// <summary>The Adventure Plate Classic heading above each value role.</summary>
    private static readonly (ProfileElementRole Heading, ProfileElementRole Value)[] HeadingValuePairs =
    [
        (ProfileElementRole.BasicWorldHeading, ProfileElementRole.BasicWorld),
        (ProfileElementRole.BasicJobHeading, ProfileElementRole.BasicJob),
        (ProfileElementRole.BasicFreeCompanyHeading, ProfileElementRole.BasicFreeCompany),
        (ProfileElementRole.BasicPlaystyleHeading, ProfileElementRole.BasicPlaystyle),
        (ProfileElementRole.BasicActiveHoursHeading, ProfileElementRole.BasicActiveHours),
        (ProfileElementRole.BasicMessageHeading, ProfileElementRole.BasicMessage),
    ];

    private static List<ElementRect> ValueRects(ProfileDocument document) =>
        document.Elements.Where(e => !BasicSections.IsHeading(e.Role)).Select(BasicDocuments.RectOf).ToList();

    /// <summary>
    /// What makes a heading render at its full size: one line of text at its font size fits its
    /// box's height less the padding (so auto fit leaves it alone), and a generous estimate of its
    /// caption's width (bold capitals at 0.75 em, plus letter spacing) fits the box's width.
    /// </summary>
    private static void AssertShownAtFullSize(TextProfileElement heading)
    {
        var padding = 2f * TextProfileElement.LayoutPadding;
        Assert.True(heading.Size.Y - padding >= heading.FontSize - 0.01f, $"{heading.Role}: {heading.FontSize} px text in a {heading.Size.Y} px box is shrunk");
        var widthEstimate = (heading.Text.Length * heading.FontSize * 0.75f) + (heading.LetterSpacing * (heading.Text.Length - 1));
        Assert.True(heading.Size.X - padding >= widthEstimate, $"{heading.Role}: \"{heading.Text}\" is too wide for its box");
    }

    [Theory]
    [InlineData(20f)]
    [InlineData(24f)]
    [InlineData(28f)]
    [InlineData(32f)]
    public async Task ASizeAboveSixteen_RendersAtThatSize_WithoutTouchingTheValueRow(float size)
    {
        using var harness = await NewClassicAsync();
        var values = ValueRects(harness.Document);

        harness.Basic.SetHeadingSize(size, continuous: false);

        foreach (var (headingRole, valueRole) in HeadingValuePairs)
        {
            var heading = BasicSections.FindText(harness.Document, headingRole)!;
            var value = BasicSections.Find(harness.Document, valueRole)!;
            Assert.Equal(size, heading.FontSize);
            AssertShownAtFullSize(heading);

            // It grew upward: its bottom edge still meets the value's top, never overlapping it.
            Assert.Equal(value.Position.Y, heading.Position.Y + heading.Size.Y, 3);
        }

        // Nothing else moved or resized, no section overlaps another, and all still follow the layout.
        Assert.Equal(values, ValueRects(harness.Document));
        Assert.Empty(BasicPlateEditor.FindOverlaps(harness.Document));
        Assert.Empty(BasicPlateEditor.CustomizedSections(harness.Document));
    }

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public void AtTheLargestSize_NoLayoutGroupIntersectsAnother(AdventurePlateOrientation orientation)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.SetOrientation(orientation);
        editor.SetHeadingSize(AdventurePlateClassicLayout.MaxHeadingFontSize(document));

        var groups = BasicSections.LayoutGroups.Select(g => g[0]).ToArray();
        for (var i = 0; i < groups.Length; i++)
        {
            for (var j = i + 1; j < groups.Length; j++)
            {
                var a = AdventurePlateClassicLayout.GetGroupBounds(groups[i], orientation, document);
                var b = AdventurePlateClassicLayout.GetGroupBounds(groups[j], orientation, document);
                Assert.False(a.Intersects(b), $"{groups[i]} intersects {groups[j]}");
            }
        }

        Assert.Empty(BasicPlateEditor.FindOverlaps(document));
    }

    [Fact]
    public void TheDefaultSize_KeepsTheOriginalRowGeometry()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);

        foreach (var heading in BasicPlateEditor.Headings(document))
        {
            Assert.Equal(0f, AdventurePlateClassicLayout.HeadingGrowth(heading.Role, document));
            Assert.Equal(24f, heading.Size.Y, 3);
        }
    }

    [Fact]
    public void OnALargerCanvas_TheRangeScalesWithIt()
    {
        var document = BasicDocuments.Blank(1920f, 1080f);
        var editor = BasicDocuments.Editor(document);
        foreach (var section in new[] { BasicSection.World, BasicSection.Job, BasicSection.Level, BasicSection.FreeCompany, BasicSection.Playstyle, BasicSection.ActiveHours, BasicSection.Message })
        {
            editor.EnsureSection(section);
        }

        Assert.Equal(52f, AdventurePlateClassicLayout.MaxHeadingFontSize(document));
        editor.SetHeadingSize(40f);

        Assert.All(BasicPlateEditor.Headings(document), heading =>
        {
            Assert.Equal(40f, heading.FontSize);
            AssertShownAtFullSize(heading);
        });
        Assert.Empty(BasicPlateEditor.FindOverlaps(document));
    }

    [Fact]
    public void ALargerSize_ThenSmallerAgain_ShrinksTheBoxBack()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);

        editor.SetHeadingSize(30f);
        editor.SetHeadingSize(16f);

        Assert.All(BasicPlateEditor.Headings(document), heading => Assert.Equal(24f, heading.Size.Y, 3));
    }

    [Fact]
    public void AHeadingCreatedLaterAtALargeSharedSize_GetsABoxThatFitsIt()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        editor.SetText(ProfileElementRole.BasicWorld, "Phoenix");
        editor.SetHeadingSize(28f);

        editor.SetText(ProfileElementRole.BasicMessage, "Hello");

        var message = BasicSections.FindText(document, ProfileElementRole.BasicMessageHeading)!;
        Assert.Equal(28f, message.FontSize);
        AssertShownAtFullSize(message);
        Assert.False(BasicPlateEditor.IsSectionCustomized(document, BasicSection.Message));
    }

    [Fact]
    public void ResetSection_PutsTheHeadingBackInTheDefaultRow()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.SetHeadingSize(30f);

        editor.ResetSection(BasicSection.World);

        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorldHeading)!;
        Assert.Equal(16f, world.FontSize);
        Assert.Equal(24f, world.Size.Y, 3);
    }

    [Fact]
    public async Task AHeadingCustomizedInAdvanced_KeepsItsOwnBox()
    {
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicWorldHeading, new System.Numerics.Vector2(0f, 10f));
        var moved = BasicDocuments.RectOf(BasicSections.Find(harness.Document, ProfileElementRole.BasicWorldHeading)!);

        harness.Basic.SetHeadingSize(24f, continuous: false);

        var world = BasicSections.FindText(harness.Document, ProfileElementRole.BasicWorldHeading)!;
        Assert.Equal(24f, world.FontSize);
        Assert.Equal(moved, BasicDocuments.RectOf(world));
        Assert.True(BasicPlateEditor.IsSectionCustomized(harness.Document, BasicSection.World));

        // Every heading Basic still places did grow to fit.
        AssertShownAtFullSize(BasicSections.FindText(harness.Document, ProfileElementRole.BasicMessageHeading)!);
    }

    [Fact]
    public async Task UndoingALargerSize_RestoresTheSizeAndTheBoxes()
    {
        using var harness = await NewClassicAsync();
        var before = harness.Json();

        harness.Basic.SetHeadingSize(28f, continuous: false);
        harness.Session.Undo();

        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);
    }

    [Fact]
    public async Task AnExistingPlateWithALargeAdvancedHeading_IsNotRePlacedWhenOpened()
    {
        // A heading made large in the Advanced Editor before this version, still in its old row box.
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        BasicSections.FindText(document, ProfileElementRole.BasicMessageHeading)!.FontSize = 30f;
        using var harness = await BasicHarness.OpenDocumentAsync(document);
        var before = harness.Json();

        harness.SimulateBasicFrame();

        Assert.Equal(before, harness.Json());
        Assert.False(harness.Session.IsDirty);
        Assert.Empty(BasicPlateEditor.CustomizedSections(harness.Document));
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
