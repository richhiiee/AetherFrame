using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The Basic Favorite Job row ("Lv. 100   Astrologian") as one compact inline row.</summary>
public class FavoriteJobRowTests
{
    public static IEnumerable<object[]> Levels()
    {
        foreach (var orientation in new[] { AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored })
        {
            foreach (var level in new[] { 1, 9, 50, 99, 100 })
            {
                yield return [(int)orientation, level];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void JobStarts_TheCompactGapAfterTheRenderedLevelText(int orientationValue, int levelValue)
    {
        var orientation = (AdventurePlateOrientation)orientationValue;
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Astrologian", Level = levelValue });
        BasicDocuments.Editor(document).SetOrientation(orientation);
        var (level, job, world) = Row(document);
        var padding = TextProfileElement.LayoutPadding;
        var scale = AdventurePlateClassicLayout.CanvasScale(document).X;

        Assert.Equal($"Lv. {levelValue}", level.Text);
        var levelTextEnd = level.Position.X + padding + AdventurePlateClassicLayout.MeasureLevelText(level.Text, level.FontSize, level.LetterSpacing);
        var jobTextStart = job.Position.X + padding;

        Assert.Equal(AdventurePlateClassicLayout.LevelJobGap * scale, jobTextStart - levelTextEnd, 3);

        // Left-aligned and flush with the column (Home World's own edge), on one line, boxes touching but never overlapping.
        Assert.Equal(world.Position.X, level.Position.X);
        Assert.Equal(TextAlignment.Left, level.Alignment);
        Assert.Equal(TextAlignment.Left, job.Alignment);
        Assert.Equal(level.Position.Y, job.Position.Y);
        Assert.Equal(level.Position.X + level.Size.X, job.Position.X, 3);
    }

    [Fact]
    public void ShorterLevels_UseLessRoom_NothingIsReservedForTheWidest()
    {
        var jobX = new List<float>();
        foreach (var levelValue in new[] { 1, 9, 50, 99, 100 })
        {
            var (_, job, world) = Row(BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Paladin", Level = levelValue }));
            jobX.Add(job.Position.X - world.Position.X);
        }

        Assert.Equal(jobX[0], jobX[1], 3);  // Lv. 1 and Lv. 9: same width (tabular digits)
        Assert.True(jobX[2] > jobX[1]);     // two digits take more room than one
        Assert.Equal(jobX[2], jobX[3], 3);
        Assert.True(jobX[4] > jobX[3]);
        Assert.True(jobX[4] < AdventurePlateClassicLayout.EmptyLevelWidth + 80f, "the old fixed 80px column is gone");
    }

    [Fact]
    public void HiddenLevel_PutsTheJobFlushAtTheColumn_AndShowingItBringsTheGapBack()
    {
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Paladin", Level = 90 });
        var editor = BasicDocuments.Editor(document);

        editor.SetSectionVisible(BasicSection.Level, false);
        var (level, job, world) = Row(document);
        Assert.Equal(world.Position.X + (AdventurePlateClassicLayout.EmptyLevelWidth * AdventurePlateClassicLayout.CanvasScale(document).X), job.Position.X, 3);
        Assert.True(level.Position.X + level.Size.X <= job.Position.X + 0.001f);

        editor.SetSectionVisible(BasicSection.Level, true);
        (level, job, _) = Row(document);
        Assert.Equal(level.Position.X + level.Size.X, job.Position.X, 3);
        Assert.True(job.Position.X > world.Position.X + 30f);
    }

    [Fact]
    public void CustomizedRow_IsNeverMoved_ByLevelChanges()
    {
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Paladin", Level = 90 });
        var editor = BasicDocuments.Editor(document);
        BasicSections.Find(document, ProfileElementRole.BasicJob)!.Position += new Vector2(25, 0); // moved in the Advanced editor
        var job = BasicDocuments.RectOf(BasicSections.Find(document, ProfileElementRole.BasicJob)!);
        var level = BasicDocuments.RectOf(BasicSections.Find(document, ProfileElementRole.BasicLevel)!);

        editor.SetSectionVisible(BasicSection.Level, false);
        editor.SetSectionVisible(BasicSection.Level, true);

        Assert.Equal(job, BasicDocuments.RectOf(BasicSections.Find(document, ProfileElementRole.BasicJob)!));
        Assert.Equal(level, BasicDocuments.RectOf(BasicSections.Find(document, ProfileElementRole.BasicLevel)!));
    }

    [Fact]
    public void ApplyLayout_PlacesTheRowCompactly_InEitherOrientation()
    {
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Astrologian", Level = 100 });
        var editor = BasicDocuments.Editor(document);
        BasicSections.Find(document, ProfileElementRole.BasicJob)!.Position += new Vector2(40, 3);

        editor.SetOrientation(AdventurePlateOrientation.Mirrored);
        editor.ApplyLayout(BasicSection.Job);

        var (level, job, world) = Row(document);
        Assert.Equal(world.Position.X, level.Position.X);
        Assert.Equal(level.Position.X + level.Size.X, job.Position.X, 3);
        Assert.False(BasicPlateEditor.IsSectionCustomized(document, BasicSection.Job));
    }

    [Fact]
    public void OlderPlate_WithTheFixedLevelColumn_IsMadeCompactOnLoad_AndStaysManaged()
    {
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Astrologian", Level = 100 });
        PlaceWithTheOldFixedColumn(document);
        Assert.False(BasicPlateEditor.IsSectionCustomized(document, BasicSection.Job));

        var loaded = Persistence.PlateDocuments.Materialize(Persistence.PlateDocuments.ToJson(document));

        var (level, job, _) = Row(loaded);
        Assert.Equal(level.Position.X + level.Size.X, job.Position.X, 3);
        Assert.Equal(AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicJob, AdventurePlateOrientation.Normal, loaded), BasicDocuments.RectOf(job));
        Assert.False(BasicPlateEditor.IsSectionCustomized(loaded, BasicSection.Job));
    }

    [Fact]
    public void OlderPlate_WithACustomizedRow_LoadsExactlyAsSaved()
    {
        var document = BasicDocuments.LegacyClassic(FakeCharacter.Hero with { JobName = "Astrologian", Level = 100 });
        PlaceWithTheOldFixedColumn(document);
        BasicSections.Find(document, ProfileElementRole.BasicJob)!.Position += new Vector2(30, 0);
        var json = Persistence.PlateDocuments.ToJson(document);

        var loaded = Persistence.PlateDocuments.Materialize((System.Text.Json.Nodes.JsonObject)json.DeepClone());

        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(json, Persistence.PlateDocuments.ToJson(loaded)));
    }

    /// <summary>The row as the previous layout placed (and recorded) it: an 80px level column, the job 2px after.</summary>
    private static void PlaceWithTheOldFixedColumn(ProfileDocument document)
    {
        var scale = AdventurePlateClassicLayout.CanvasScale(document);
        var (level, job, world) = Row(document);
        var column = world.Position.X;
        var columnWidth = world.Size.X;
        level.Position = new Vector2(column, level.Position.Y);
        level.Size = new Vector2(80f * scale.X, level.Size.Y);
        job.Position = new Vector2(column + (82f * scale.X), job.Position.Y);
        job.Size = new Vector2(columnWidth - (82f * scale.X), job.Size.Y);
        document.BasicPlate!.SetPlacement(ProfileElementRole.BasicLevel, BasicDocuments.RectOf(level));
        document.BasicPlate!.SetPlacement(ProfileElementRole.BasicJob, BasicDocuments.RectOf(job));
    }

    [Fact]
    public void LevelGlyphWidths_MatchTheEmbeddedFont()
    {
        // The layout measures "Lv. N" from built-in advance widths; they must be the real font's.
        var font = File.ReadAllBytes(FindRepoFile(Path.Combine("AetherFrame", "Fonts", "PTSans-Regular.ttf")));
        var (unitsPerEm, advance) = ReadAdvances(font);

        Assert.Equal(1000, unitsPerEm);
        foreach (var text in new[] { "Lv. 1", "Lv. 50", "Lv. 100", "Lv. 1234567890" })
        {
            var expected = 0;
            foreach (var character in text)
            {
                expected += advance(character);
            }

            Assert.Equal(expected / 1000f * 20f, AdventurePlateClassicLayout.MeasureLevelText(text, 20f, 0f), 3);
        }
    }

    private static (TextProfileElement Level, TextProfileElement Job, TextProfileElement World) Row(ProfileDocument document) =>
        (BasicSections.FindText(document, ProfileElementRole.BasicLevel)!,
         BasicSections.FindText(document, ProfileElementRole.BasicJob)!,
         BasicSections.FindText(document, ProfileElementRole.BasicWorld)!);

    private static string FindRepoFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }

    /// <summary>Minimal TrueType reader: unitsPerEm and a character's advance width (cmap format 4 + hmtx).</summary>
    private static (int UnitsPerEm, Func<char, int> Advance) ReadAdvances(byte[] font)
    {
        var span = font.AsSpan();
        var tables = new Dictionary<string, int>();
        for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(span[4..]); i++)
        {
            var record = 12 + (16 * i);
            tables[System.Text.Encoding.ASCII.GetString(font, record, 4)] = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(record + 8)..]);
        }

        var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(span[(tables["head"] + 18)..]);
        var metrics = BinaryPrimitives.ReadUInt16BigEndian(span[(tables["hhea"] + 34)..]);
        var hmtx = tables["hmtx"];
        var cmap = tables["cmap"];
        var format4 = -1;
        for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(span[(cmap + 2)..]); i++)
        {
            var offset = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(span[(cmap + 8 + (8 * i))..]);
            if (BinaryPrimitives.ReadUInt16BigEndian(span[offset..]) == 4)
            {
                format4 = offset;
                break;
            }
        }

        int Glyph(char c)
        {
            var segments = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(format4 + 6)) / 2;
            var ends = format4 + 14;
            var starts = ends + (2 * segments) + 2;
            var deltas = starts + (2 * segments);
            var ranges = deltas + (2 * segments);
            for (var s = 0; s < segments; s++)
            {
                var end = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(ends + (2 * s)));
                var start = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(starts + (2 * s)));
                if (c < start || c > end)
                {
                    continue;
                }

                var delta = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(deltas + (2 * s)));
                var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(ranges + (2 * s)));
                if (rangeOffset == 0)
                {
                    return (c + delta) & 0xFFFF;
                }

                var glyph = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(ranges + (2 * s) + rangeOffset + (2 * (c - start))));
                return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
            }

            return 0;
        }

        int Advance(char c) => BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(hmtx + (4 * Math.Min(Glyph(c), metrics - 1))));
        return (unitsPerEm, Advance);
    }
}
