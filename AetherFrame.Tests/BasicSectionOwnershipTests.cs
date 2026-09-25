using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Basic layout ownership is atomic per layout group: a group (a section's heading and values,
/// or Favorite Job and Level together) follows the layout as a whole or is customized as a whole.
/// </summary>
public class BasicSectionOwnershipTests
{
    private static readonly Vector2 SmallNudge = new(0, 6);

    private static ElementRect RectOf(ProfileDocument document, ProfileElementRole role) =>
        BasicDocuments.RectOf(BasicSections.Find(document, role)!);

    private static bool Overlap(ElementRect a, ElementRect b) =>
        a.Position.X < b.Position.X + b.Size.X && b.Position.X < a.Position.X + a.Size.X
        && a.Position.Y < b.Position.Y + b.Size.Y && b.Position.Y < a.Position.Y + a.Size.Y;

    // ---------------------------------------------------------------- the in-game bug

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored)]
    [InlineData(AdventurePlateOrientation.Mirrored, AdventurePlateOrientation.Normal)]
    public async Task MovingFavoriteJobsInAdvanced_ThenSwitchingOrientationAndBack_NeverMovesThem(
        AdventurePlateOrientation start, AdventurePlateOrientation other)
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Basic.SetOrientation(start);
        harness.DragInAdvanced(ProfileElementRole.BasicJob, SmallNudge);
        var job = RectOf(harness.Document, ProfileElementRole.BasicJob);
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Job));

        harness.Basic.SetOrientation(other);
        harness.Basic.SetOrientation(start);

        Assert.Equal(job, RectOf(harness.Document, ProfileElementRole.BasicJob));
    }

    [Fact]
    public async Task ReclaimingFavoriteJobs_PlacesThemInTheirCell()
    {
        foreach (var reclaim in new Action<BasicHarness>[]
        {
            h => h.Basic.ApplySectionLayout(BasicSection.Job),
            h => h.Basic.ResetSection(BasicSection.Job),
            h => h.Basic.ResetBasicLayout(),
            h => h.Basic.ApplyLayout(),
        })
        {
            using var harness = await BasicHarness.NewClassicAsync();
            harness.DragInAdvanced(ProfileElementRole.BasicJob, new Vector2(0, 40));

            reclaim(harness);

            var document = harness.Document;
            Assert.Equal(BasicDocuments.LayoutRect(document, ProfileElementRole.BasicJob, AdventurePlateOrientation.Normal), RectOf(document, ProfileElementRole.BasicJob));
            Assert.False(BasicEditorSession.IsSectionCustomized(document, BasicSection.Job));
        }
    }

    // ---------------------------------------------------------------- every multi-element section

    [Theory]
    [InlineData((int)BasicSection.World)]
    [InlineData((int)BasicSection.FreeCompany)]
    [InlineData((int)BasicSection.Playstyle)]
    [InlineData((int)BasicSection.ActiveHours)]
    [InlineData((int)BasicSection.Message)]
    public void MovingOnlyAHeading_KeepsItsValueWithIt_ThroughOrientationChanges(int sectionValue)
    {
        var section = (BasicSection)sectionValue;
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        var definition = BasicSections.Get(section);
        BasicSections.Find(document, definition.Heading!.Value)!.Position += SmallNudge;
        var before = BasicPlateEditor.GroupElements(document, section).ToDictionary(e => e.Id, BasicDocuments.RectOf);

        editor.SetOrientation(AdventurePlateOrientation.Mirrored);
        editor.SetOrientation(AdventurePlateOrientation.Normal);
        editor.SetOrientation(AdventurePlateOrientation.Mirrored);

        foreach (var element in BasicPlateEditor.GroupElements(document, section))
        {
            Assert.Equal(before[element.Id], BasicDocuments.RectOf(element));
        }

        Assert.Equal([section], BasicPlateEditor.CustomizedSections(document));
    }

    [Fact]
    public void MovingOnlyAValue_KeepsItsHeadingWithIt()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var heading = RectOf(document, ProfileElementRole.BasicMessageHeading);
        BasicSections.Find(document, ProfileElementRole.BasicMessage)!.Size += new Vector2(0, -20);

        BasicDocuments.Editor(document).SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(heading, RectOf(document, ProfileElementRole.BasicMessageHeading));
    }

    [Fact]
    public async Task IdentityHeader_StaysWholeWhenOneOfItsElementsIsMoved()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Identity.SetCustomTitle("Hello");
        harness.Identity.Commit();
        harness.DragInAdvanced(ProfileElementRole.BasicTitle, SmallNudge);
        var name = RectOf(harness.Document, ProfileElementRole.BasicName);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
        harness.Basic.SetOrientation(AdventurePlateOrientation.Normal);

        Assert.Equal(name, RectOf(harness.Document, ProfileElementRole.BasicName));
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
    }

    [Fact]
    public void EveryBasicSection_BelongsToExactlyOneLayoutGroup()
    {
        var grouped = BasicSections.LayoutGroups.SelectMany(g => g).ToList();

        Assert.Equal(Enum.GetValues<BasicSection>().OrderBy(s => s), grouped.OrderBy(s => s));
        Assert.Equal([BasicSection.Job, BasicSection.Level], BasicSections.LayoutGroupOf(BasicSection.Level));
        Assert.Equal([BasicSection.World], BasicSections.LayoutGroupOf(BasicSection.World));
    }

    // ---------------------------------------------------------------- drift

    [Fact]
    public void RepeatedOrientationRoundTrips_NeverDrift()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.CreatePortrait(Guid.NewGuid());
        var normal = BasicDocuments.Placements(document);
        editor.SetOrientation(AdventurePlateOrientation.Mirrored);
        var mirrored = BasicDocuments.Placements(document);

        for (var i = 0; i < 10; i++)
        {
            editor.SetOrientation(AdventurePlateOrientation.Normal);
            Assert.Equal(normal, BasicDocuments.Placements(document));
            editor.SetOrientation(AdventurePlateOrientation.Mirrored);
            Assert.Equal(mirrored, BasicDocuments.Placements(document));
        }

        Assert.Empty(BasicPlateEditor.CustomizedSections(document));
    }

    [Fact]
    public async Task RepeatedRoundTrips_WithACustomizedGroup_NeverDriftEither()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicJob, SmallNudge);
        var job = RectOf(harness.Document, ProfileElementRole.BasicJob);
        var world = RectOf(harness.Document, ProfileElementRole.BasicWorld);

        for (var i = 0; i < 5; i++)
        {
            harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
            harness.Basic.SetOrientation(AdventurePlateOrientation.Normal);
        }

        Assert.Equal(job, RectOf(harness.Document, ProfileElementRole.BasicJob));
        Assert.Equal(world, RectOf(harness.Document, ProfileElementRole.BasicWorld));
    }
}
