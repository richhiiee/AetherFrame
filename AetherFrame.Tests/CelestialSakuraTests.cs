using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace AetherFrame.Tests;

/// <summary>
/// The Celestial Sakura Component family: seven full-color artworks bundled exactly as approved,
/// their catalog entries, resource names, default placements on the Adventure Plate, and that the
/// whole set loads and paints together (the showcase Plate's prerequisites).
/// </summary>
public class CelestialSakuraTests(ITestOutputHelper output)
{
    /// <summary>Definition id, art id, kind, runtime file, pixel size, SHA-256 of the approved PNG.</summary>
    public static readonly (string DefinitionId, string ArtId, PlateComponentKind Kind, string File, int Width, int Height, string Sha256)[] Family =
    [
        ("af.background.celestial-sakura", "af.asset.celestial-sakura.background.twilight", PlateComponentKind.Background,
            "CelestialSakura_Background.png", 1672, 941, "c7a939df17daad81db9786ae65d62b7ebd8f059d589a97e1c9cc2cb899bf7148"),
        ("af.plate-frame.celestial-sakura", "af.asset.celestial-sakura.plate-frame.blossom", PlateComponentKind.PlateFrame,
            "CelestialSakura_PlateFrame.png", 1672, 941, "0c5c407d36dfd840142a5a8f58a13103f0348e0fb2daf8e5f8b8dab46af6dad9"),
        ("af.portrait-frame.celestial-sakura", "af.asset.celestial-sakura.portrait-frame.blossom", PlateComponentKind.PortraitFrame,
            "CelestialSakura_PortraitFrame.png", 992, 1586, "4278abbe91ebd11fcda78ed1670e440557cb19739794b704732667b423fa4dde"),
        ("af.name-backing.celestial-sakura", "af.asset.celestial-sakura.name-backing.nameplate", PlateComponentKind.NameBacking,
            "CelestialSakura_Nameplate.png", 2172, 724, "57ef50f047181a5566f85838feca13c99b3d004b46694f0e7fa10c3b7f99af96"),
        ("af.divider.celestial-sakura-ornate", "af.asset.celestial-sakura.divider.ornate", PlateComponentKind.Divider,
            "CelestialSakura_Divider_Ornate.png", 2172, 724, "099b678f1c29cdc0a4465e47857baadf582dc2534c9032bef268bfd7c94e442a"),
        ("af.divider.celestial-sakura-slim", "af.asset.celestial-sakura.divider.slim", PlateComponentKind.Divider,
            "CelestialSakura_Divider_Slim.png", 2172, 724, "a1798774be478f33de3fbf8edd74e14a1eebb900e7e38beecb394c031fab98f4"),
        ("af.corner-ornament.celestial-sakura", "af.asset.celestial-sakura.corner-ornament.blossom", PlateComponentKind.CornerOrnament,
            "CelestialSakura_CornerOrnament.png", 1254, 1254, "e58f67f3bd596294aeafa245ed1c1c778c128724c2221459bdf2335bf928899e"),
    ];

    public static IEnumerable<object[]> DefinitionIds() => Family.Select(f => new object[] { f.DefinitionId });

    private const string ResourceFolder = "AetherFrame.Assets.Components.CelestialSakura.";

    private static ComponentDefinition Definition(string id) => BuiltInComponentCatalog.Find(id)!;

    // ---- Catalog ------------------------------------------------------------------------------

    [Fact]
    public void StableIds_AreFrozen()
    {
        Assert.Equal("af.background.celestial-sakura", BuiltInComponentCatalog.BackgroundCelestialSakura);
        Assert.Equal("af.plate-frame.celestial-sakura", BuiltInComponentCatalog.PlateFrameCelestialSakura);
        Assert.Equal("af.portrait-frame.celestial-sakura", BuiltInComponentCatalog.PortraitFrameCelestialSakura);
        Assert.Equal("af.name-backing.celestial-sakura", BuiltInComponentCatalog.NameBackingCelestialSakura);
        Assert.Equal("af.divider.celestial-sakura-ornate", BuiltInComponentCatalog.DividerCelestialSakuraOrnate);
        Assert.Equal("af.divider.celestial-sakura-slim", BuiltInComponentCatalog.DividerCelestialSakuraSlim);
        Assert.Equal("af.corner-ornament.celestial-sakura", BuiltInComponentCatalog.CornerOrnamentCelestialSakura);

        Assert.Equal("af.asset.celestial-sakura.background.twilight", BuiltInArtCatalog.CelestialSakuraBackground);
        Assert.Equal("af.asset.celestial-sakura.plate-frame.blossom", BuiltInArtCatalog.CelestialSakuraPlateFrame);
        Assert.Equal("af.asset.celestial-sakura.portrait-frame.blossom", BuiltInArtCatalog.CelestialSakuraPortraitFrame);
        Assert.Equal("af.asset.celestial-sakura.name-backing.nameplate", BuiltInArtCatalog.CelestialSakuraNameplate);
        Assert.Equal("af.asset.celestial-sakura.divider.ornate", BuiltInArtCatalog.CelestialSakuraOrnateDivider);
        Assert.Equal("af.asset.celestial-sakura.divider.slim", BuiltInArtCatalog.CelestialSakuraSlimDivider);
        Assert.Equal("af.asset.celestial-sakura.corner-ornament.blossom", BuiltInArtCatalog.CelestialSakuraCornerOrnament);

        Assert.Equal(8, (int)PlateComponentKind.Background); // persisted: frozen forever
    }

    [Theory]
    [MemberData(nameof(DefinitionIds))]
    public void EveryEntry_IsAFullColorArtDefinition_OfItsKind(string definitionId)
    {
        var expected = Family.Single(f => f.DefinitionId == definitionId);
        var definition = Definition(definitionId);
        var art = definition.Art!;

        Assert.Equal(expected.Kind, definition.Kind);
        Assert.Equal(expected.Kind, art.Kind);
        Assert.Equal(expected.ArtId, art.Id);
        Assert.Same(art, BuiltInArtCatalog.Find(expected.ArtId));
        Assert.Equal(ComponentShape.Art, definition.Shape);
        Assert.False(definition.RequiresAsset); // offered by the Basic slots of its kind
        Assert.False(art.Tintable); // its own colors; only opacity applies
        Assert.Equal(ComponentColorSource.White, definition.ColorSource);
        Assert.Equal(1f, definition.DefaultAlpha);
        Assert.Equal((expected.Width, expected.Height), (art.PixelWidth, art.PixelHeight));
        Assert.Equal((float)expected.Width / expected.Height, art.AspectRatio);
        Assert.Equal(ComponentStatus.Ready, ComponentPaintPlan.Resolve(ComponentDocuments.Of(definitionId), BuiltInComponentCatalog.Instance, out _));
    }

    [Fact]
    public void TheFamily_IsExactlyTheSevenPieces_InCatalogOrder_AndNothingElseJoinsIt()
    {
        var family = BuiltInArtCatalog.OfFamily(BuiltInArtCatalog.CelestialSakuraFamily).ToList();
        Assert.Equal(Family.Select(f => f.ArtId), family.Select(a => a.Id));
        Assert.Equal(Family.Select(f => f.DefinitionId).Order(StringComparer.Ordinal), BuiltInComponentCatalog.All.Where(d => d.Family == "Celestial Sakura").Select(d => d.Id).Order(StringComparer.Ordinal));
        Assert.Null(Definition(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot).Family); // Celestial Dream untouched
        Assert.All(BuiltInComponentCatalog.All.Where(d => d.Art is null), d => Assert.Null(d.Family));
    }

    [Theory]
    [MemberData(nameof(DefinitionIds))]
    public void BrowserText_ShowsTheFamily(string definitionId)
    {
        var definition = Definition(definitionId);
        Assert.StartsWith("Celestial Sakura", definition.Name, StringComparison.Ordinal);
        Assert.StartsWith("Celestial Sakura: ", definition.Description, StringComparison.Ordinal);
        Assert.Equal("Celestial Sakura", definition.Family);
    }

    [Fact]
    public void KindsWithTwoPieces_HaveDistinctNames()
    {
        foreach (var kind in Enum.GetValues<PlateComponentKind>())
        {
            var names = BuiltInComponentCatalog.OfKind(kind).Select(d => d.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.Equal(["Celestial Sakura Ornate", "Celestial Sakura Slim"], BuiltInComponentCatalog.OfKind(PlateComponentKind.Divider).Where(d => d.Family is not null).Select(d => d.Name));
    }

    [Theory]
    [InlineData("Celestial Sakura")]
    [InlineData("celestial sakura")]
    [InlineData("sakura")]
    [InlineData("cherry blossom")]
    [InlineData("moon")]
    [InlineData("rose")]
    [InlineData("gold")]
    [InlineData("  GOLD ")]
    [InlineData("")]
    public void Search_EveryPieceAnswersToTheFamilyWords(string query)
    {
        Assert.All(BuiltInArtCatalog.OfFamily(BuiltInArtCatalog.CelestialSakuraFamily), art => Assert.True(art.MatchesSearch(query), $"{art.Id} for '{query}'"));
    }

    [Theory]
    [InlineData("frame", new[] { PlateComponentKind.PlateFrame, PlateComponentKind.PortraitFrame })]
    [InlineData("portrait", new[] { PlateComponentKind.PortraitFrame })]
    [InlineData("background", new[] { PlateComponentKind.Background })]
    [InlineData("nameplate", new[] { PlateComponentKind.NameBacking })]
    [InlineData("divider", new[] { PlateComponentKind.Divider, PlateComponentKind.Divider })]
    [InlineData("ornament", new[] { PlateComponentKind.CornerOrnament })]
    [InlineData("corner", new[] { PlateComponentKind.CornerOrnament })]
    [InlineData("astrolabe", new PlateComponentKind[0])]
    public void Search_RoleWordsFindTheirPieces(string query, PlateComponentKind[] kinds)
    {
        var found = BuiltInArtCatalog.OfFamily(BuiltInArtCatalog.CelestialSakuraFamily).Where(a => a.MatchesSearch(query)).Select(a => a.Kind);
        Assert.Equal(kinds, found);
    }

    [Fact]
    public void Search_OtherFamiliesDoNotAnswerToSakura()
    {
        Assert.False(BuiltInArtCatalog.AstrolabePivot.MatchesSearch("sakura"));
        Assert.True(BuiltInArtCatalog.AstrolabePivot.MatchesSearch("astrolabe"));
    }

    [Fact]
    public void BasicSlots_OfferThePieces_WhoseKindHasASlot()
    {
        var document = ComponentDocuments.WithAnchors();
        foreach (var (definitionId, _, kind, _, _, _, _) in Family.Where(f => f.Kind != PlateComponentKind.Background))
        {
            Assert.Contains(kind, PlateComponentEditor.BasicSlots.Concat(PlateComponentEditor.BasicDecorations));
            Assert.True(PlateComponentEditor.SetSlot(document, kind, definitionId, BuiltInComponentCatalog.Instance) || PlateComponentEditor.FindSlot(document, kind)!.DefinitionId == definitionId);
            Assert.Equal(definitionId, PlateComponentEditor.FindSlot(document, kind)!.DefinitionId);
        }

        // Background has no Basic slot: it is added in the Advanced editor, listed first.
        Assert.DoesNotContain(PlateComponentKind.Background, PlateComponentEditor.BasicSlots.Concat(PlateComponentEditor.BasicDecorations));
        Assert.Equal([PlateComponentKind.Background], PlateComponentEditor.AdvancedOnlyKinds);
        Assert.Equal("Background", PlateComponentEditor.KindLabel(PlateComponentKind.Background));
        Assert.Equal([BuiltInComponentCatalog.BackgroundCelestialSakura], BuiltInComponentCatalog.OfKind(PlateComponentKind.Background).Select(d => d.Id));
    }

    // ---- Resource names -------------------------------------------------------------------------

    [Fact]
    public void ResourceNames_AreTheCanonicalDottedNames()
    {
        foreach (var (definitionId, _, _, file, _, _, _) in Family)
        {
            Assert.Equal(ResourceFolder + file, Definition(definitionId).Art!.ResourceName);
        }
    }

    [Theory]
    [InlineData("Components\\CelestialSakura\\")] // MSBuild's %(RecursiveDir) on Windows
    [InlineData("Components/CelestialSakura/")] // ... and on Linux
    public void ResourceNames_AreIdentical_ForWindowsAndLinuxPaths(string recursiveDir)
    {
        foreach (var (definitionId, _, _, file, _, _, _) in Family)
        {
            Assert.Equal(Definition(definitionId).Art!.ResourceName, LogicalName(recursiveDir, file));
        }
    }

    [Theory]
    [InlineData("AetherFrame/AetherFrame.csproj")]
    [InlineData("AetherFrame.Tests/AetherFrame.Tests.csproj")]
    public void ProjectFiles_UseTheLogicalNameRuleTheTestsReplicate(string project)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, project));
        Assert.Contains("<LogicalName>AetherFrame.Assets.$([System.String]::Copy('%(RecursiveDir)').Replace('\\', '.').Replace('/', '.'))%(Filename)%(Extension)</LogicalName>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFiles_AreExactlyTheApprovedPngs_ByteForByte()
    {
        var folder = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Assets", "Components", "CelestialSakura");
        Assert.Equal(Family.Select(f => f.File).Order(StringComparer.Ordinal), Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var (definitionId, _, _, file, _, _, sha256) in Family)
        {
            Assert.Equal(sha256, Sha256(File.ReadAllBytes(Path.Combine(folder, file))));
            Assert.Equal(sha256, Sha256(ReadResource(Definition(definitionId).Art!.ResourceName))); // embedded unchanged
        }
    }

    [Fact]
    public void PluginAssembly_EmbedsEveryPiece_UnderItsCanonicalName()
    {
        // As BuiltInArtTests: CI names the built plugin in AETHERFRAME_PLUGIN_ASSEMBLY; locally the
        // checkout's own build of this configuration is inspected when it exists.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var names = metadata.ManifestResources.Select(h => metadata.GetString(metadata.GetManifestResource(h).Name)).ToHashSet(StringComparer.Ordinal);

        foreach (var (definitionId, _, _, file, _, _, _) in Family)
        {
            Assert.Contains(ResourceFolder + file, names);
            Assert.Contains(Definition(definitionId).Art!.ResourceName, names);
        }

        Assert.DoesNotContain(names, n => n.Contains("CelestialSakura", StringComparison.Ordinal) && (n.Contains('/') || n.Contains('\\')));
    }

    // ---- Loading (the texture cache's own path) -----------------------------------------------

    [Fact]
    public void TheWholeFamily_LoadsTogether_ThroughTheTextureCachePath()
    {
        var loaded = new Dictionary<string, IReadOnlyList<ArtLevel>>(StringComparer.Ordinal);
        foreach (var art in BuiltInArtCatalog.OfFamily(BuiltInArtCatalog.CelestialSakuraFamily))
        {
            loaded.Add(art.Id, BundledArtImage.LoadLevels(ReadResource(art.ResourceName), art));
        }

        Assert.Equal(7, loaded.Count);
        foreach (var (id, levels) in loaded)
        {
            var art = BuiltInArtCatalog.Find(id)!;
            Assert.Equal((art.PixelWidth, art.PixelHeight), (levels[0].Width, levels[0].Height));
            Assert.Equal(levels.Select(l => l.LongSide).OrderDescending(), levels.Select(l => l.LongSide)); // what SelectLevel expects
            Assert.True(Math.Min(levels[^1].Width, levels[^1].Height) >= BundledArtImage.MinLevelSize);
            Assert.All(levels, l => Assert.Equal(l.Width * l.Height * 4, l.Rgba.Length));
        }
    }

    [Theory]
    [MemberData(nameof(DefinitionIds))]
    public void Loading_KeepsEveryVisibleTexel_Exactly(string definitionId)
    {
        var art = Definition(definitionId).Art!;
        var png = ReadResource(art.ResourceName);
        var decoded = BundledArtImage.DecodePng(png);
        var loaded = BundledArtImage.LoadLevels(png, art)[0];

        var bled = 0;
        for (var i = 0; i < decoded.Rgba.Length; i += 4)
        {
            Assert.Equal(decoded.Rgba[i + 3], loaded.Rgba[i + 3]); // transparency never changes
            if (decoded.Rgba[i + 3] != 0)
            {
                Assert.True(decoded.Rgba.AsSpan(i, 3).SequenceEqual(loaded.Rgba.AsSpan(i, 3)), $"visible texel {i / 4} changed");
            }
            else if (!decoded.Rgba.AsSpan(i, 3).SequenceEqual(loaded.Rgba.AsSpan(i, 3)))
            {
                bled++;
            }
        }

        // Only the invisible texels bordering the drawing take its color (for bilinear edges).
        if (art.Kind != PlateComponentKind.Background)
        {
            Assert.True(bled > 0);
        }
    }

    [Fact]
    public void Background_IsOpaque_AndTheFramesAreOpenInTheMiddle()
    {
        var background = BundledArtImage.DecodePng(ReadResource(BuiltInArtCatalog.CelestialSakuraBackgroundArt.ResourceName));
        Assert.True(Enumerable.Range(0, background.Width * background.Height).All(i => background.Rgba[(i * 4) + 3] == 255));

        foreach (var frame in new[] { BuiltInArtCatalog.CelestialSakuraPlateFrameArt, BuiltInArtCatalog.CelestialSakuraPortraitFrameArt })
        {
            var image = BundledArtImage.DecodePng(ReadResource(frame.ResourceName));
            Assert.True(MaxAlpha(image, image.Width / 4, image.Height / 4, image.Width * 3 / 4, image.Height * 3 / 4) <= 1, $"{frame.Id} center half");
            Assert.True(MaxAlpha(image, 0, 0, image.Width, image.Height) > 200);
        }
    }

    [Fact]
    public void PlateFrame_Artwork_ReachesEveryEdgeOfItsCanvas()
    {
        // The frame is the full 16:9 shape (not a shorter band inside a taller image): its drawn
        // border starts within 1% of every edge, so drawn over the Plate it spans the Plate.
        var image = BundledArtImage.DecodePng(ReadResource(BuiltInArtCatalog.CelestialSakuraPlateFrameArt.ResourceName));
        var (left, top, right, bottom) = OpaqueBounds(image, 64);
        Assert.True(left <= image.Width / 100 && image.Width - 1 - right <= image.Width / 100, $"x {left}..{right}");
        Assert.True(top <= image.Height / 100 && image.Height - 1 - bottom <= image.Height / 100, $"y {top}..{bottom}");
    }

    // ---- Default placements -----------------------------------------------------------------------

    [Fact]
    public void PlateFrame_DefaultBounds_AreTheFullPlateCanvas()
    {
        var document = ClassicPlate();
        var step = Assert.Single(PlanOf(document, BuiltInComponentCatalog.PlateFrameCelestialSakura));

        Assert.Equal(new ElementRect(Vector2.Zero, new Vector2(1280, 720)), step.Placement.Rect);
        Assert.Equal(PlateLayer.PlateFrame, step.Layer);
        Assert.Equal(0f, step.Placement.RotationDegrees);
    }

    [Fact]
    public void Background_DefaultBounds_CoverThePlateCanvas_AndPaintFirst()
    {
        var document = ClassicPlate();
        document.Components = ShowcaseComponents();
        var plan = ComponentDocuments.Plan(document);

        var first = plan[0];
        Assert.Equal(BuiltInComponentCatalog.BackgroundCelestialSakura, first.Definition!.Id);
        Assert.Equal(PlateLayer.Background, first.Layer);
        Assert.Equal(new ElementRect(Vector2.Zero, new Vector2(1280, 720)), first.Placement.Rect);
        Assert.Single(plan, s => s.Layer == PlateLayer.Background);
    }

    [Fact]
    public void PortraitFrame_DefaultBounds_AreThePortrait()
    {
        var document = ClassicPlate();
        var portrait = document.Elements.Single(e => e.Role == ProfileElementRole.BasicPortrait);
        var step = Assert.Single(PlanOf(document, BuiltInComponentCatalog.PortraitFrameCelestialSakura));

        Assert.Equal(new ElementRect(portrait.Position, portrait.Size), step.Placement.Rect); // 5:8 art on the 400 x 640 portrait
        Assert.Equal(new ElementRect(new Vector2(40, 40), new Vector2(400, 640)), step.Placement.Rect);
    }

    [Fact]
    public void Nameplate_DefaultBounds_AreAHorizontalPlaqueCenteredOnTheName()
    {
        var document = ClassicPlate();
        var name = document.Elements.Single(e => e.Role == ProfileElementRole.BasicName);
        var step = Assert.Single(PlanOf(document, BuiltInComponentCatalog.NameBackingCelestialSakura));
        var rect = step.Placement.Rect;

        AssertAspect(3f, rect.Size);
        Assert.InRange(rect.Size.X, 300f, 900f); // a useful width, whatever the name
        AssertInsideCanvas(document, rect);
        var center = rect.Position + (rect.Size / 2f);
        Assert.InRange(center.X, name.Position.X, name.Position.X + name.Size.X);
        Assert.Equal(name.Position.Y + (name.Size.Y / 2f), center.Y, 3);
        Assert.True(rect.Position.Y + rect.Size.Y <= FirstHeadingTop(document), $"{rect} reaches the first section heading");
    }

    [Theory]
    [InlineData(BuiltInComponentCatalog.DividerCelestialSakuraOrnate)]
    [InlineData(BuiltInComponentCatalog.DividerCelestialSakuraSlim)]
    public void Dividers_DefaultBounds_Are3To1_CenteredOnTheDividerLine(string definitionId)
    {
        var document = ClassicPlate();
        var procedural = Assert.Single(PlanOf(document, BuiltInComponentCatalog.DividerLine)).Placement.Rect;
        var step = Assert.Single(PlanOf(document, definitionId));
        var rect = step.Placement.Rect;

        AssertAspect(3f, rect.Size);
        Assert.Equal(ComponentPaintPlan.DividerHeight * Definition(definitionId).Art!.SizeFactor, rect.Size.Y, 3);
        AssertClose(procedural.Position + (procedural.Size / 2f), rect.Position + (rect.Size / 2f)); // same center line
        AssertInsideCanvas(document, rect);
        Assert.True(rect.Position.Y + rect.Size.Y <= FirstHeadingTop(document), $"{rect} reaches the first section heading");
    }

    [Fact]
    public void CornerOrnament_DefaultBounds_AreMirroredSquaresInEveryCorner()
    {
        var document = ClassicPlate();
        var steps = PlanOf(document, BuiltInComponentCatalog.CornerOrnamentCelestialSakura);

        var size = ComponentPaintPlan.CornerSize * 3f;
        var inset = ComponentPaintPlan.CornerInset;
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.Equal(new Vector2(size), s.Placement.Rect.Size));
        Assert.Equal(
            [new Vector2(inset, inset), new Vector2(1280 - inset - size, inset), new Vector2(inset, 720 - inset - size), new Vector2(1280 - inset - size, 720 - inset - size)],
            steps.Select(s => s.Placement.Rect.Position));
        Assert.Equal([(false, false), (true, false), (false, true), (true, true)], steps.Select(s => (s.Placement.MirrorX, s.Placement.MirrorY)));
        Assert.All(steps, s => Assert.Equal(0f, s.Placement.RotationDegrees));
    }

    [Fact]
    public void EveryDefaultPlacement_StaysOnThePlate()
    {
        var document = ClassicPlate();
        document.Components = ShowcaseComponents();
        foreach (var step in ComponentDocuments.Plan(document).Where(s => !s.IsElement))
        {
            AssertInsideCanvas(document, step.Placement.Rect);
        }
    }

    [Fact]
    public void DefaultPlacements_RemainFreelyAdjustable()
    {
        var document = ClassicPlate();
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameCelestialSakura);
        document.Components = [frame];
        frame.Scale = 0.5f;
        frame.Offset = new Vector2(30, -10);
        frame.RotationDegrees = 5f;

        var rect = Assert.Single(ComponentDocuments.Plan(document), s => !s.IsElement).Placement;
        Assert.Equal(new Vector2(640, 360), rect.Rect.Size);
        Assert.Equal(new Vector2(640 + 30, 360 - 10), rect.Rect.Position + (rect.Rect.Size / 2f));
        Assert.Equal(5f, rect.RotationDegrees);

        Assert.True(PlateComponentEditor.ResetTransform(document, frame.Id));
        Assert.Equal(new ElementRect(Vector2.Zero, new Vector2(1280, 720)), Assert.Single(ComponentDocuments.Plan(document), s => !s.IsElement).Placement.Rect);
    }

    [Fact]
    public void OnAnotherCanvasShape_TheFrameIsFitted_NeverStretched()
    {
        var document = ClassicPlate();
        document.CanvasWidth = 1200; // the Card preset: 3:2
        document.CanvasHeight = 800;
        var rect = Assert.Single(PlanOf(document, BuiltInComponentCatalog.PlateFrameCelestialSakura)).Placement.Rect;

        Assert.Equal(1200f, rect.Size.X);
        Assert.Equal(1200f * 941f / 1672f, rect.Size.Y, 3);
        Assert.Equal(new Vector2(600, 400), rect.Position + (rect.Size / 2f));
    }

    [Theory]
    [InlineData(1280f, 720f, 1.7768331f, 1280f, 720f)] // within 0.1%: drawn over the box
    [InlineData(400f, 640f, 0.6254729f, 400f, 640f)]
    [InlineData(1200f, 800f, 16f / 9f, 1200f, 675f)] // taller box: width-limited
    [InlineData(2800f, 96f, 3f, 288f, 96f)] // wider box: height-limited
    [InlineData(100f, 100f, 1f, 100f, 100f)]
    [InlineData(100f, 100f, float.NaN, 100f, 100f)]
    [InlineData(0f, 100f, 3f, 0f, 100f)]
    public void FitAspect_FitsInside_KeepsAspect_ToleratesExportRounding(float boxX, float boxY, float aspect, float expectedX, float expectedY)
    {
        var fitted = ComponentPaintPlan.FitAspect(new Vector2(boxX, boxY), aspect);
        Assert.Equal(expectedX, fitted.X, 3);
        Assert.Equal(expectedY, fitted.Y, 3);
    }

    [Fact]
    public void AstrolabePivot_PlacementIsUnchanged()
    {
        // Square art in square corner boxes: the aspect fit never changes it.
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot)];
        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        Assert.All(steps, s => Assert.Equal(new Vector2(ComponentPaintPlan.CornerSize * 2f), s.Placement.Rect.Size));
        Assert.Equal([0f, 90f, 270f, 180f], steps.Select(s => s.Placement.RotationDegrees));
    }

    // ---- The showcase set ---------------------------------------------------------------------

    [Fact]
    public void ShowcasePlate_PaintsEveryPiece_InLayerOrder_AndWritesItsLayout()
    {
        var document = ClassicPlate();
        document.Components = ShowcaseComponents();
        var plan = ComponentDocuments.Plan(document);
        var components = plan.Where(s => !s.IsElement).ToList();

        // Every piece is drawn (the corner four times), every drawn piece is bundled art that loads.
        Assert.Equal(5 + 4, components.Count); // six pieces (one divider), the corner in four corners
        Assert.All(document.Components!, c => Assert.Contains(components, s => ReferenceEquals(s.Component, c)));
        Assert.All(components, s => Assert.NotNull(s.Definition!.Art));

        // Background first; Plate Frame last; the portrait frame right after the portrait.
        Assert.Equal(PlateLayer.Background, plan[0].Layer);
        Assert.Equal(PlateLayer.PlateFrame, plan[^1].Layer);
        var portrait = plan.FindIndex(s => s.Element?.Role == ProfileElementRole.BasicPortrait);
        Assert.Equal(PlateLayer.PortraitFrame, plan[portrait + 1].Layer);

        output.WriteLine("Adventure Plate Classic anchors (logical px):");
        foreach (var element in document.Elements.Where(e => e.Role is ProfileElementRole.BasicPortrait or ProfileElementRole.BasicName or ProfileElementRole.BasicTitle or ProfileElementRole.BasicWorldHeading))
        {
            output.WriteLine(FormattableString.Invariant($"  {element.Role,-18} x {element.Position.X,7:0.0}  y {element.Position.Y,6:0.0}  w {element.Size.X,7:0.0}  h {element.Size.Y,6:0.0}"));
        }

        output.WriteLine("Celestial Sakura default placements on the 1280 x 720 Adventure Plate Classic (logical px):");
        foreach (var step in components)
        {
            var r = step.Placement.Rect;
            output.WriteLine(FormattableString.Invariant(
                $"  {PlateComponentEditor.KindLabel(step.Component!.Kind),-16} {step.Definition!.Name,-24} x {r.Position.X,7:0.0}  y {r.Position.Y,6:0.0}  w {r.Size.X,7:0.0}  h {r.Size.Y,6:0.0}{(step.Placement.MirrorX || step.Placement.MirrorY ? $"  mirror {(step.Placement.MirrorX ? "X" : "")}{(step.Placement.MirrorY ? "Y" : "")}" : "")}"));
        }
    }

    [Fact]
    public void ShowcasePlate_SavesOnlyLogicalIds_AndReloadsIdentically()
    {
        var document = ClassicPlate();
        document.Components = ShowcaseComponents();

        var json = PlateDocuments.ToJson(document).ToJsonString();
        Assert.DoesNotContain(".png", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CelestialSakura", json, StringComparison.Ordinal);
        Assert.DoesNotContain(BuiltInArtCatalog.ResourcePrefix, json, StringComparison.Ordinal);
        Assert.DoesNotContain("af.asset.", json, StringComparison.Ordinal);

        var reloaded = ComponentDocuments.RoundTrip(document);
        Assert.True(PlateComponent.ListsEqual(document.Components, reloaded.Components));
        Assert.Equal((int)PlateComponentKind.Background, (int)ComponentDocuments.ComponentJson(document, 0)["Kind"]!);
    }

    [Fact]
    public async Task ShowcasePlate_Package_RoundTripsWithoutWarnings_OrArtworkFiles()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Celestial Sakura", new PlateStarterContent(null));
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Components = ShowcaseComponents();
        await library.SavePlateDocumentAsync(document);

        var path = fixture.Export(packages, created.PlateId);
        Assert.DoesNotContain(PackageFiles.Read(path), e => e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Empty(staged.Diagnostics.Warnings); // every piece is recognized here, Background included
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(PlateComponent.ListsEqual(document.Components, library.OpenDocumentForEditing(result.PlateId).Components));
    }

    // ---- Unknown Components --------------------------------------------------------------------

    [Theory]
    [InlineData(9)]
    [InlineData(42)]
    [InlineData(1000)]
    public void UnknownKinds_AreStillKeptAndNotDrawn_BesideTheNewBackground(int kind)
    {
        var document = ClassicPlate();
        var future = new PlateComponent { Kind = (PlateComponentKind)kind, DefinitionId = "af.hologram.sparkle" };
        document.Components = [future, ComponentDocuments.Of(BuiltInComponentCatalog.BackgroundCelestialSakura)];

        Assert.False(ComponentPaintPlan.IsKnownKind((PlateComponentKind)kind));
        Assert.Equal(ComponentStatus.UnknownKind, ComponentPaintPlan.Resolve(future, BuiltInComponentCatalog.Instance, out _));
        var plan = ComponentDocuments.Plan(document);
        Assert.DoesNotContain(plan, s => ReferenceEquals(s.Component, future));
        Assert.Equal(BuiltInComponentCatalog.BackgroundCelestialSakura, plan[0].Definition!.Id);

        var reloaded = ComponentDocuments.RoundTrip(document);
        Assert.Equal(kind, (int)reloaded.Components![0].Kind);
        Assert.Equal("af.hologram.sparkle", reloaded.Components[0].DefinitionId);
    }

    [Fact]
    public void Background_WithAnotherKindsDefinition_IsAKindMismatch_NeverDrawn()
    {
        var mismatched = new PlateComponent { Kind = PlateComponentKind.Background, DefinitionId = BuiltInComponentCatalog.PlateFrameCelestialSakura };
        Assert.Equal(ComponentStatus.KindMismatch, ComponentPaintPlan.Resolve(mismatched, BuiltInComponentCatalog.Instance, out _));

        var missing = new PlateComponent { Kind = PlateComponentKind.Background, DefinitionId = "af.background.some-future-art" };
        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(missing, BuiltInComponentCatalog.Instance, out _));
    }

    // ---- Decoder support the family needs ------------------------------------------------------

    [Fact]
    public void Decoder_ReadsRgbAsOpaqueRgba()
    {
        var rgb = new byte[3 * 2 * 3];
        new Random(3).NextBytes(rgb);
        var decoded = BundledArtImage.DecodePng(EncodePng(3, 2, 3, rgb, filter: 4));

        Assert.Equal((3, 2), (decoded.Width, decoded.Height));
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(rgb.AsSpan(i * 3, 3).ToArray(), decoded.Rgba.AsSpan(i * 4, 3).ToArray());
            Assert.Equal(255, decoded.Rgba[(i * 4) + 3]);
        }
    }

    [Fact]
    public void Decoder_AcceptsAnySizeUpToTheLimit_AndRejectsLarger()
    {
        var odd = BundledArtImage.DecodePng(EncodePng(5, 3, 4, new byte[5 * 3 * 4], 0));
        Assert.Equal((5, 3), (odd.Width, odd.Height));
        var tooLarge = EncodePng(1, 1, 4, new byte[4], 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(tooLarge.AsSpan(16), BundledArtImage.MaxSize + 1);
        Assert.Throws<InvalidDataException>(() => BundledArtImage.DecodePng(tooLarge));
    }

    [Fact]
    public void Levels_OfOddSizes_RoundUp_AndConserveCoverage()
    {
        var top = new ArtLevel(135, 67, Enumerable.Range(0, 135 * 67).SelectMany(i => new byte[] { 200, 100, 50, (byte)(i % 3 == 0 ? 255 : 0) }).ToArray());
        var levels = BundledArtImage.BuildLevels(top);

        Assert.Equal([(135, 67), (68, 34)], levels.Select(l => (l.Width, l.Height)));
        var coverage = levels.Select(l => Enumerable.Range(0, l.Width * l.Height).Average(i => l.Rgba[(i * 4) + 3] / 255.0)).ToList();
        Assert.InRange(coverage[1], coverage[0] * 0.95, coverage[0] * 1.05);
    }

    [Fact]
    public void Bleed_ColorsOnlyTransparentTexelsBesideTheDrawing()
    {
        // 4x1: [red opaque][transparent black][transparent black][transparent black]
        var level = new ArtLevel(4, 1, [255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        BundledArtImage.BleedIntoTransparentTexels(level);

        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, level.Rgba);
    }

    [Fact]
    public void TintableArt_IsNeverBled_ItsTransparentTexelsStayWhite()
    {
        var art = BuiltInArtCatalog.AstrolabePivot;
        foreach (var level in BundledArtImage.LoadLevels(ReadResource(art.ResourceName), art))
        {
            for (var i = 0; i < level.Rgba.Length; i += 4)
            {
                if (level.Rgba[i + 3] == 0)
                {
                    Assert.Equal(255, level.Rgba[i]);
                }
            }
        }
    }

    [Fact]
    public void LoadLevels_RejectsASizeTheCatalogDoesNotName()
    {
        var art = BuiltInArtCatalog.CelestialSakuraCornerOrnamentArt with { PixelWidth = 1024, PixelHeight = 1024 };
        Assert.Throws<InvalidDataException>(() => BundledArtImage.LoadLevels(ReadResource(art.ResourceName), art));
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>The Adventure Plate Classic starter (Identity Header, sections) on its 1280 x 720 canvas,
    /// with a portrait image chosen the way the Basic editor places it.</summary>
    private static ProfileDocument ClassicPlate()
    {
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Celestial Sakura", ComponentDocuments.Now, new PlateStarterContent(FakeCharacter.Hero));
        BasicDocuments.Editor(document).CreatePortrait(Guid.NewGuid());
        return document;
    }

    /// <summary>One of every piece, the corner in all four corners.</summary>
    private static List<PlateComponent> ShowcaseComponents() =>
        Family.Where(f => f.DefinitionId != BuiltInComponentCatalog.DividerCelestialSakuraSlim).Select(f => ComponentDocuments.Of(f.DefinitionId)).ToList();

    private static List<PaintStep> PlanOf(ProfileDocument document, string definitionId)
    {
        document.Components = [ComponentDocuments.Of(definitionId)];
        return ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
    }

    /// <summary>MSBuild's LogicalName rule for bundled art (see the project files), in C#.</summary>
    private static string LogicalName(string recursiveDir, string fileName) =>
        "AetherFrame.Assets." + recursiveDir.Replace('\\', '.').Replace('/', '.') + fileName;

    private static float FirstHeadingTop(ProfileDocument document) =>
        document.Elements.Single(e => e.Role == ProfileElementRole.BasicWorldHeading).Position.Y;

    private static void AssertAspect(float aspect, Vector2 size) => Assert.Equal(aspect, size.X / size.Y, 3);

    private static void AssertClose(Vector2 expected, Vector2 actual) => Assert.True(Vector2.Distance(expected, actual) < 0.01f, $"{expected} vs {actual}");

    private static void AssertInsideCanvas(ProfileDocument document, ElementRect rect)
    {
        const float epsilon = 0.01f;
        Assert.True(rect.Position.X >= -epsilon && rect.Position.Y >= -epsilon, $"{rect} starts off the Plate");
        Assert.True(rect.Position.X + rect.Size.X <= document.CanvasWidth + epsilon && rect.Position.Y + rect.Size.Y <= document.CanvasHeight + epsilon, $"{rect} ends off the Plate");
    }

    private static int MaxAlpha(ArtLevel image, int x0, int y0, int x1, int y1)
    {
        var max = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                max = Math.Max(max, image.Rgba[(((y * image.Width) + x) * 4) + 3]);
            }
        }

        return max;
    }

    private static (int Left, int Top, int Right, int Bottom) OpaqueBounds(ArtLevel image, byte threshold)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Rgba[(((y * image.Width) + x) * 4) + 3] >= threshold)
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                }
            }
        }

        return (left, top, right, bottom);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(CelestialSakuraTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>A minimal PNG: 8-bit RGB (3 channels) or RGBA (4), every row with one filter type.</summary>
    private static byte[] EncodePng(int width, int height, int channels, byte[] pixels, byte filter)
    {
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = filter;
            for (var i = 0; i < stride; i++)
            {
                int left = i >= channels ? pixels[(y * stride) + i - channels] : 0;
                int up = y > 0 ? pixels[((y - 1) * stride) + i] : 0;
                int upLeft = y > 0 && i >= channels ? pixels[((y - 1) * stride) + i - channels] : 0;
                var predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                raw[(y * (stride + 1)) + 1 + i] = (byte)(pixels[(y * stride) + i] - predictor);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)(channels == 4 ? 6 : 2);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(new byte[4]); // CRC: not checked by the decoder
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
