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
/// Basic layout ownership is atomic per layout group: a group (a section's heading and values,
/// or Favorite Job and Level together) follows the layout as a whole or is customized as a whole.
/// </summary>
public class BasicSectionOwnershipTests
{
    private static readonly Vector2 SmallNudge = new(0, 6);

    // Plates from before multiple Favorite Jobs, which still show a level: the Favorite Job and the
    // Level remain one layout group there.
    private static Task<BasicHarness> NewClassicAsync() =>
        BasicHarness.OpenDocumentAsync(BasicDocuments.LegacyClassic(FakeCharacter.Hero));

    private static ElementRect RectOf(ProfileDocument document, ProfileElementRole role) =>
        BasicDocuments.RectOf(BasicSections.Find(document, role)!);

    private static ElementRect LayoutRect(ProfileDocument document, ProfileElementRole role, AdventurePlateOrientation orientation) =>
        AdventurePlateClassicLayout.GetRect(role, orientation, document)!.Value;

    private static bool Overlap(ElementRect a, ElementRect b) =>
        a.Position.X < b.Position.X + b.Size.X && b.Position.X < a.Position.X + a.Size.X
        && a.Position.Y < b.Position.Y + b.Size.Y && b.Position.Y < a.Position.Y + a.Size.Y;

    // ---------------------------------------------------------------- the in-game bug

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored)]
    [InlineData(AdventurePlateOrientation.Mirrored, AdventurePlateOrientation.Normal)]
    public async Task MovingLevelInAdvanced_ThenSwitchingOrientationAndBack_MovesNeitherJobNorLevel(
        AdventurePlateOrientation start, AdventurePlateOrientation other)
    {
        using var harness = await NewClassicAsync();
        harness.Basic.SetOrientation(start);
        var document = harness.Document;

        harness.DragInAdvanced(ProfileElementRole.BasicLevel, SmallNudge);
        var job = RectOf(document, ProfileElementRole.BasicJob);
        var level = RectOf(document, ProfileElementRole.BasicLevel);

        Assert.True(BasicEditorSession.IsSectionCustomized(document, BasicSection.Level));
        Assert.True(BasicEditorSession.IsSectionCustomized(document, BasicSection.Job));
        Assert.Equal([BasicSection.Job], BasicEditorSession.CustomizedSections(document));

        harness.Basic.SetOrientation(other);
        Assert.Equal(job, RectOf(document, ProfileElementRole.BasicJob));
        Assert.Equal(level, RectOf(document, ProfileElementRole.BasicLevel));

        harness.Basic.SetOrientation(start);

        Assert.Equal(job, RectOf(document, ProfileElementRole.BasicJob));
        Assert.Equal(level, RectOf(document, ProfileElementRole.BasicLevel));
        Assert.True(BasicEditorSession.IsSectionCustomized(document, BasicSection.Job));
        Assert.False(Overlap(RectOf(document, ProfileElementRole.BasicJob), RectOf(document, ProfileElementRole.BasicLevel)));

        // Everything else still followed the orientation.
        Assert.Equal(LayoutRect(document, ProfileElementRole.BasicWorld, start), RectOf(document, ProfileElementRole.BasicWorld));

        // Reclaiming places both together, compactly, in the current orientation.
        harness.Basic.ApplyLayout();

        Assert.Equal(LayoutRect(document, ProfileElementRole.BasicJob, start), RectOf(document, ProfileElementRole.BasicJob));
        Assert.Equal(LayoutRect(document, ProfileElementRole.BasicLevel, start), RectOf(document, ProfileElementRole.BasicLevel));
        Assert.Empty(BasicEditorSession.CustomizedSections(document));
    }

    [Fact]
    public async Task MovingJobInAdvanced_KeepsLevelWithIt_Too()
    {
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicJob, SmallNudge);
        var level = RectOf(harness.Document, ProfileElementRole.BasicLevel);

        harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(level, RectOf(harness.Document, ProfileElementRole.BasicLevel));
        Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Level));
    }

    [Theory]
    [InlineData((int)BasicSection.Job)]
    [InlineData((int)BasicSection.Level)]
    public async Task ReclaimingFromEitherSection_PlacesBoth(int sectionValue)
    {
        var section = (BasicSection)sectionValue;
        foreach (var reclaim in new Action<BasicHarness>[]
        {
            h => h.Basic.ApplySectionLayout(section),
            h => h.Basic.ResetSection(section),
            h => h.Basic.ResetBasicLayout(),
            h => h.Basic.ApplyLayout(),
        })
        {
            using var harness = await NewClassicAsync();
            harness.DragInAdvanced(ProfileElementRole.BasicJob, new Vector2(0, 40));
            harness.DragInAdvanced(ProfileElementRole.BasicLevel, new Vector2(0, 80));

            reclaim(harness);

            var document = harness.Document;
            Assert.Equal(LayoutRect(document, ProfileElementRole.BasicJob, AdventurePlateOrientation.Normal), RectOf(document, ProfileElementRole.BasicJob));
            Assert.Equal(LayoutRect(document, ProfileElementRole.BasicLevel, AdventurePlateOrientation.Normal), RectOf(document, ProfileElementRole.BasicLevel));
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
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero);
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
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero);
        var heading = RectOf(document, ProfileElementRole.BasicMessageHeading);
        BasicSections.Find(document, ProfileElementRole.BasicMessage)!.Size += new Vector2(0, -20);

        BasicDocuments.Editor(document).SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(heading, RectOf(document, ProfileElementRole.BasicMessageHeading));
    }

    [Fact]
    public async Task IdentityHeader_StaysWholeWhenOneOfItsElementsIsMoved()
    {
        using var harness = await NewClassicAsync();
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
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero);
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
        using var harness = await NewClassicAsync();
        harness.DragInAdvanced(ProfileElementRole.BasicLevel, SmallNudge);
        var job = RectOf(harness.Document, ProfileElementRole.BasicJob);
        var level = RectOf(harness.Document, ProfileElementRole.BasicLevel);
        var world = RectOf(harness.Document, ProfileElementRole.BasicWorld);

        for (var i = 0; i < 5; i++)
        {
            harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored);
            harness.Basic.SetOrientation(AdventurePlateOrientation.Normal);
        }

        Assert.Equal(job, RectOf(harness.Document, ProfileElementRole.BasicJob));
        Assert.Equal(level, RectOf(harness.Document, ProfileElementRole.BasicLevel));
        Assert.Equal(world, RectOf(harness.Document, ProfileElementRole.BasicWorld));
    }
}

/// <summary>"Lv. 100  Paladin": a compact, overlap-free Job and Level unit on the finished Plate.</summary>
public class JobAndLevelSpacingTests
{
    // Conservative text width estimate for the curated sans font (its real glyphs are narrower
    // than half an em on average); auto fit shrinks anything wider at render time regardless.
    private const float EstimatedEmPerCharacter = 0.5f;

    public static IEnumerable<object[]> Cases()
    {
        foreach (var orientation in new[] { AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored })
        {
            foreach (var job in new[] { "Viper", "Astrologian", "Pictomancer" })
            {
                foreach (var level in new[] { 1, 100 })
                {
                    yield return [(int)orientation, job, level];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LevelAndJob_NeverOverlap_AndSitCloseTogether(int orientationValue, string jobName, int levelValue)
    {
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = jobName, Level = levelValue });
        BasicDocuments.Editor(document).SetOrientation((AdventurePlateOrientation)orientationValue);
        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorld)!;
        var level = BasicSections.FindText(document, ProfileElementRole.BasicLevel)!;
        var job = BasicSections.FindText(document, ProfileElementRole.BasicJob)!;
        var padding = TextProfileElement.LayoutPadding;

        // The level is measured exactly (the layout uses the embedded font's own glyph widths); the
        // job, any length, conservatively.
        float LevelWidth(TextProfileElement e) => AdventurePlateClassicLayout.MeasureLevelText(e.Text, e.FontSize, e.LetterSpacing);
        float TextWidth(TextProfileElement e) => e.Text.Length * e.FontSize * EstimatedEmPerCharacter;

        // Boxes: same line, level first, no overlap.
        Assert.Equal(level.Position.Y, job.Position.Y);
        Assert.True(level.Position.X + level.Size.X <= job.Position.X);

        // Both texts fit their boxes at the default size (no shrinking needed).
        Assert.True(LevelWidth(level) <= level.Size.X - (2 * padding), $"{level.Text} too wide");
        Assert.True(TextWidth(job) <= job.Size.X - (2 * padding), $"{job.Text} too wide");

        // Level and job are both left-aligned, like every other Details value — "Lv. 1" and "Lv. 100"
        // start at the exact same X (flush with the column, matching Home World's own left edge), not
        // wherever a right-aligned box happened to end for that many digits.
        Assert.Equal(TextAlignment.Left, level.Alignment);
        Assert.Equal(TextAlignment.Left, job.Alignment);
        Assert.Equal(world.Position.X, level.Position.X);

        // The two texts never overlap, and sit exactly the compact gap apart.
        var levelTextRight = level.Position.X + padding + LevelWidth(level);
        var jobTextLeft = job.Position.X + padding;
        Assert.True(jobTextLeft > levelTextRight, $"'{level.Text}' ({LevelWidth(level)}px) should end before '{job.Text}' starts");
        Assert.Equal(AdventurePlateClassicLayout.LevelJobGap * AdventurePlateClassicLayout.CanvasScale(document).X, jobTextLeft - levelTextRight, 3);
    }
}
