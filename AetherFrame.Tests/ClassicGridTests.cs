using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Adventure Plate Classic details grid, validated as whole layout groups: every group has its
/// own rectangle, separated from its neighbors, in both orientations and on every canvas preset.
/// </summary>
public class ClassicGridTests
{
    // Conservative text width estimate for the curated sans font (see JobAndLevelSpacingTests).
    private const float EstimatedEmPerCharacter = 0.5f;

    // The clear gutter the grid keeps between any two groups, in reference-canvas pixels.
    private const float MinimumGutter = 12f;

    private static readonly AdventurePlateOrientation[] Orientations = [AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored];

    private static readonly BasicSection[] Groups = BasicSections.LayoutGroups.Select(g => g[0]).ToArray();

    public static IEnumerable<object[]> PresetsAndOrientations() =>
        from preset in ProfileCanvasPreset.All
        from orientation in Orientations
        select new object[] { preset.Name, (int)orientation };

    private static ProfileDocument OnPreset(string presetName, BasicCharacterInfo? character = null)
    {
        var preset = ProfileCanvasPreset.All.Single(p => p.Name == presetName);
        var document = BasicDocuments.Classic(character);
        document.CanvasWidth = preset.Width;
        document.CanvasHeight = preset.Height;
        return document;
    }

    private static float Gap(ElementRect a, ElementRect b)
    {
        var dx = Math.Max(b.Position.X - (a.Position.X + a.Size.X), a.Position.X - (b.Position.X + b.Size.X));
        var dy = Math.Max(b.Position.Y - (a.Position.Y + a.Size.Y), a.Position.Y - (b.Position.Y + b.Size.Y));
        return Math.Max(dx, dy);
    }

    // ---------------------------------------------------------------- layout geometry

    [Theory]
    [MemberData(nameof(PresetsAndOrientations))]
    public void EveryLayoutGroup_HasItsOwnRectangle_WithAClearGutter(string preset, int orientationValue)
    {
        var document = OnPreset(preset);
        var orientation = (AdventurePlateOrientation)orientationValue;
        var scale = Math.Min(AdventurePlateClassicLayout.CanvasScale(document).X, AdventurePlateClassicLayout.CanvasScale(document).Y);
        var bounds = Groups.ToDictionary(g => g, g => AdventurePlateClassicLayout.GetGroupBounds(g, orientation, document));

        foreach (var (group, rect) in bounds)
        {
            Assert.InRange(rect.Position.X, 0f, document.CanvasWidth - rect.Size.X);
            Assert.InRange(rect.Position.Y, 0f, document.CanvasHeight - rect.Size.Y);
        }

        for (var i = 0; i < Groups.Length; i++)
        {
            for (var j = i + 1; j < Groups.Length; j++)
            {
                var a = bounds[Groups[i]];
                var b = bounds[Groups[j]];
                Assert.False(a.Intersects(b), $"{Groups[i]} intersects {Groups[j]}");
                Assert.True(Gap(a, b) >= (MinimumGutter * scale) - 0.01f, $"{Groups[i]} and {Groups[j]} are only {Gap(a, b)} apart");
            }
        }
    }

    [Theory]
    [MemberData(nameof(PresetsAndOrientations))]
    public void EveryElement_LiesInsideItsGroupsRectangle(string preset, int orientationValue)
    {
        var document = OnPreset(preset);
        var orientation = (AdventurePlateOrientation)orientationValue;

        foreach (var group in Groups.Where(g => g != BasicSection.Identity))
        {
            var bounds = AdventurePlateClassicLayout.GetGroupBounds(group, orientation, document);
            foreach (var role in BasicSections.LayoutGroupOf(group).SelectMany(BasicPlateEditor.RolesOf).Where(r => !BasicSections.IsRetired(r)))
            {
                var rect = AdventurePlateClassicLayout.GetRect(role, orientation, document)!.Value;
                Assert.Equal(bounds, bounds.Union(rect));
            }
        }
    }

    [Theory]
    [InlineData((int)AdventurePlateOrientation.Normal)]
    [InlineData((int)AdventurePlateOrientation.Mirrored)]
    public void TheRows_KeepTheSameReadingOrder_InBothOrientations(int orientationValue)
    {
        var document = BasicDocuments.Blank();
        var orientation = (AdventurePlateOrientation)orientationValue;
        ElementRect Group(BasicSection section) => AdventurePlateClassicLayout.GetGroupBounds(section, orientation, document);
        float Top(BasicSection s) => Group(s).Position.Y;
        float Bottom(BasicSection s) => Group(s).Position.Y + Group(s).Size.Y;
        float Left(BasicSection s) => Group(s).Position.X;

        // Row 1: Home World | Free Company. Row 2: Favorite Job & Level | Active Hours.
        Assert.Equal(Top(BasicSection.World), Top(BasicSection.FreeCompany));
        Assert.Equal(Bottom(BasicSection.World), Bottom(BasicSection.FreeCompany));
        Assert.Equal(Top(BasicSection.Job), Top(BasicSection.ActiveHours));
        Assert.Equal(Bottom(BasicSection.Job), Bottom(BasicSection.ActiveHours));
        Assert.True(Left(BasicSection.World) < Left(BasicSection.FreeCompany));
        Assert.True(Left(BasicSection.Job) < Left(BasicSection.ActiveHours));
        Assert.Equal(Left(BasicSection.World), Left(BasicSection.Job));
        Assert.Equal(Left(BasicSection.FreeCompany), Left(BasicSection.ActiveHours));

        // Identity above row 1; row 1 above row 2; Playstyle below both; Message below Playstyle.
        Assert.True(Bottom(BasicSection.Identity) < Top(BasicSection.World));
        Assert.True(Bottom(BasicSection.World) < Top(BasicSection.Job));
        Assert.True(Bottom(BasicSection.Job) < Top(BasicSection.Playstyle));
        Assert.True(Bottom(BasicSection.ActiveHours) < Top(BasicSection.Playstyle));
        Assert.True(Bottom(BasicSection.Playstyle) < Top(BasicSection.Message));

        // Playstyle and Message span the panel; the portrait is its own column beside it.
        Assert.Equal(Left(BasicSection.World), Left(BasicSection.Playstyle));
        Assert.Equal(Group(BasicSection.Identity).Size.X, Group(BasicSection.Message).Size.X);
        Assert.False(Group(BasicSection.Portrait).Intersects(Group(BasicSection.Identity).Union(Group(BasicSection.Message))));
    }

    [Fact]
    public void Mirroring_MovesOnlyThePanelAsAWhole()
    {
        var document = BasicDocuments.Blank();
        var shift = AdventurePlateClassicLayout.GetGroupBounds(BasicSection.World, AdventurePlateOrientation.Normal, document).Position
            - AdventurePlateClassicLayout.GetGroupBounds(BasicSection.World, AdventurePlateOrientation.Mirrored, document).Position;

        foreach (var group in Groups.Where(g => g != BasicSection.Portrait))
        {
            var normal = AdventurePlateClassicLayout.GetGroupBounds(group, AdventurePlateOrientation.Normal, document);
            var mirrored = AdventurePlateClassicLayout.GetGroupBounds(group, AdventurePlateOrientation.Mirrored, document);
            Assert.Equal(normal.Size, mirrored.Size);
            Assert.Equal(shift, normal.Position - mirrored.Position);
        }

        Assert.True(AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Portrait, AdventurePlateOrientation.Mirrored, document).Position.X
            > AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Message, AdventurePlateOrientation.Mirrored, document).Position.X);
    }

    [Fact]
    public void TheIdentityHeader_FitsItsReservedArea_InEveryTitleLayout()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var reserved = AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Identity, AdventurePlateOrientation.Normal, document);
        var name = BasicSections.FindText(document, ProfileElementRole.BasicName)!;
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        title.Text = "the Warrior of Light";

        foreach (var layout in Enum.GetValues<IdentityTitleLayout>())
        {
            var result = IdentityHeaderLayout.Compute(
                layout, reserved.Position, reserved.Size.X, TextAlignment.Left,
                new IdentityHeaderLayout.Line(true, true, name.FontSize, 200),
                new IdentityHeaderLayout.Line(true, true, layout == IdentityTitleLayout.Badge ? name.FontSize * 0.4f : title.FontSize, 200));

            foreach (var rect in new[] { result.Name, result.Title })
            {
                Assert.Equal(reserved, reserved.Union(rect!.Value));
            }
        }
    }

    // ---------------------------------------------------------------- content fits its cell

    [Theory]
    [InlineData("Astrologian")]
    [InlineData("Pictomancer")]
    [InlineData("Blue Mage")]
    public void LongJobNames_FitTheirCell(string job)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero with { JobName = job });
        AssertFits(document, ProfileElementRole.BasicJob);
        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel));
    }

    [Fact]
    public void LongActiveHours_SixPlaystyles_AndAMessage_FitTheirCells_AtWorstWithAutoFit()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.SetActiveHours(new BasicActiveHours
        {
            Days = BasicWeekdays.Monday | BasicWeekdays.Wednesday | BasicWeekdays.Friday | BasicWeekdays.Sunday,
            StartMinutes = (11 * 60) + 30,
            EndMinutes = (23 * 60) + 30,
            TimeZone = "Server Time",
        });
        editor.SetPlaystyles(["New Player Friendly", "Treasure Hunts", "Screenshots", "Gold Saucer", "Roleplay", "Crafting"]);
        editor.SetText(ProfileElementRole.BasicMessage, string.Concat(Enumerable.Repeat("Looking for friends to run old raids with. ", 8)));

        AssertFits(document, ProfileElementRole.BasicActiveHours);
        AssertFits(document, ProfileElementRole.BasicPlaystyle);
        AssertFits(document, ProfileElementRole.BasicMessage);
    }

    /// <summary>
    /// The text fits its box: on one line (or, for wrapping text, in as many lines as the box
    /// holds) at its own size — or, failing that, at its auto fit minimum. Either way it stays
    /// inside its own cell and can't reach a neighbor.
    /// </summary>
    private static void AssertFits(ProfileDocument document, ProfileElementRole role)
    {
        var element = BasicSections.FindText(document, role)!;
        var innerWidth = element.Size.X - (2 * TextProfileElement.LayoutPadding);
        var innerHeight = element.Size.Y - (2 * TextProfileElement.LayoutPadding);

        bool FitsAt(float size)
        {
            var width = element.Text.Length * size * EstimatedEmPerCharacter;
            var lineHeight = size * 1.2f * element.LineSpacing;
            var lines = element.Wrap ? Math.Max(1, (int)(innerHeight / lineHeight)) : 1;
            return width <= innerWidth * lines;
        }

        Assert.True(element.AutoFitText);
        Assert.True(FitsAt(element.FontSize) || FitsAt(element.AutoFitMinimumSize), $"{role}: \"{element.Text}\" doesn't fit {element.Size}");
    }

    // ---------------------------------------------------------------- a filled Plate, end to end

    [Fact]
    public async Task AFullyFilledPlate_HasNoOverlaps_InEitherOrientation_AndNeverDrifts()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero with { JobName = "Astrologian" }));
        harness.Basic.SetPortrait(harness.ImportablePng());
        harness.Identity.SetCustomTitle("the Warrior of Light");
        harness.Identity.Commit();
        harness.Basic.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Weekends, TimeZone = "Server Time" });
        foreach (var entry in BasicPlateText.SuggestedPlaystyles.Take(6))
        {
            harness.Basic.AddPlaystyle(entry);
        }

        harness.Basic.SetText(ProfileElementRole.BasicMessage, "Hello there!");
        harness.Basic.CommitTextEdit();

        var normal = BasicDocuments.Placements(harness.Document);
        Assert.Empty(BasicEditorSession.FindOverlaps(harness.Document));

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        var mirrored = BasicDocuments.Placements(harness.Document);
        Assert.Empty(BasicEditorSession.FindOverlaps(harness.Document));

        for (var i = 0; i < 4; i++)
        {
            harness.Basic.SetOrientation(AdventurePlateOrientation.Normal);
            Assert.Equal(normal, BasicDocuments.Placements(harness.Document));
            harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
            Assert.Equal(mirrored, BasicDocuments.Placements(harness.Document));
        }

        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
    }

    // ---------------------------------------------------------------- the in-game collision

    [Theory]
    [InlineData((int)ProfileElementRole.BasicJob, (int)BasicSection.Job, (int)BasicSection.ActiveHours)]
    [InlineData((int)ProfileElementRole.BasicActiveHoursHeading, (int)BasicSection.ActiveHours, (int)BasicSection.Portrait)]
    public async Task ACustomizedGroupLeftInPlace_IsReportedAsAnOverlap_AndReclaimingItClearsIt(
        int movedRole, int customizedValue, int otherValue)
    {
        // The in-game case: a group customized in Advanced keeps its Normal-side position (the
        // ownership rule), while the groups Basic owns mirror into the column it occupies.
        var customized = (BasicSection)customizedValue;
        var other = (BasicSection)otherValue;
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Basic.SetPortrait(harness.ImportablePng());
        harness.Basic.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Everyday });
        harness.DragInAdvanced((ProfileElementRole)movedRole, new Vector2(0, 2));
        var before = BasicPlateEditor.GroupElements(harness.Document, customized).ToDictionary(e => e.Id, BasicDocuments.RectOf);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        // Ownership held: the customized group didn't move at all...
        foreach (var element in BasicPlateEditor.GroupElements(harness.Document, customized))
        {
            Assert.Equal(before[element.Id], BasicDocuments.RectOf(element));
        }

        // ...so it now overlaps a group that did, and the editor can say which.
        var overlaps = BasicEditorSession.FindOverlaps(harness.Document);
        Assert.Contains(overlaps, o => (o.First == customized && o.Second == other) || (o.First == other && o.Second == customized));
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, customized));
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, other));

        // The explicit fix (the warning's button) puts the customized group into the Mirrored grid.
        harness.Basic.ApplySectionLayout(BasicSections.LayoutGroupOf(customized));

        Assert.Empty(BasicEditorSession.FindOverlaps(harness.Document));
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));
        Assert.Equal(
            AdventurePlateClassicLayout.GetGroupBounds(customized, AdventurePlateOrientation.Mirrored, harness.Document),
            BasicPlateEditor.CurrentGroupBounds(harness.Document, customized));
    }

    [Fact]
    public void HiddenSections_DontCountAsOverlapping()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        BasicSections.Find(document, ProfileElementRole.BasicJob)!.Position = BasicSections.Find(document, ProfileElementRole.BasicActiveHours)!.Position;
        editor.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Everyday });
        Assert.NotEmpty(BasicPlateEditor.FindOverlaps(document));

        editor.SetSectionVisible(BasicSection.ActiveHours, false);

        Assert.Empty(BasicPlateEditor.FindOverlaps(document));
    }
}
