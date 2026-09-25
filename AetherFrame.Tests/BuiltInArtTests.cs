using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Persistence;
using AetherFrame.Services.Packages;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Built-in graphical Components: the Astrolabe Pivot artwork, its metadata, placement and portability.</summary>
public class BuiltInArtTests
{
    private const string AstrolabeDefinition = BuiltInComponentCatalog.CornerOrnamentAstrolabePivot;

    private static ComponentDefinition Astrolabe => BuiltInComponentCatalog.Find(AstrolabeDefinition)!;

    [Fact]
    public void StableIds_AreFrozen()
    {
        Assert.Equal("af.corner-ornament.astrolabe-pivot", BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);
        Assert.Equal("af.asset.celestial-dream.corner-ornament.astrolabe-pivot", BuiltInArtCatalog.CelestialDreamAstrolabePivot);
        Assert.Same(BuiltInArtCatalog.AstrolabePivot, Astrolabe.Art);
        Assert.Equal(BuiltInArtCatalog.CelestialDreamAstrolabePivot, Astrolabe.Art!.Id);
    }

    [Fact]
    public void ArtLookup_IsExact_NeverByNameOrResource()
    {
        Assert.Same(BuiltInArtCatalog.AstrolabePivot, BuiltInArtCatalog.Find(BuiltInArtCatalog.CelestialDreamAstrolabePivot));
        Assert.Null(BuiltInArtCatalog.Find("Astrolabe Pivot"));
        Assert.Null(BuiltInArtCatalog.Find("AstrolabePivot.png"));
        Assert.Null(BuiltInArtCatalog.Find(BuiltInArtCatalog.AstrolabePivot.ResourceName));
        Assert.Null(BuiltInArtCatalog.Find(BuiltInArtCatalog.CelestialDreamAstrolabePivot.ToUpperInvariant()));
        Assert.Null(BuiltInArtCatalog.Find(null));
        Assert.Equal(BuiltInArtCatalog.All.Count, BuiltInArtCatalog.All.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Metadata_DescribesATintableRotatedCornerOrnament()
    {
        var art = BuiltInArtCatalog.AstrolabePivot;
        Assert.Equal(PlateComponentKind.CornerOrnament, art.Kind);
        Assert.Equal("Astrolabe Pivot", art.Name);
        Assert.True(art.Tintable);
        Assert.Equal(CornerArtPlacement.Rotate, art.CornerPlacement);
        Assert.InRange(art.DefaultOpacity, 0.01f, 1f);
        Assert.InRange(art.SizeFactor, 1f, 4f);
        Assert.Equal((512, 512), (art.PixelWidth, art.PixelHeight));

        var definition = Astrolabe;
        Assert.Equal(PlateComponentKind.CornerOrnament, definition.Kind);
        Assert.Equal(ComponentShape.Art, definition.Shape);
        Assert.False(definition.RequiresAsset);
        Assert.Equal(art.DefaultOpacity, definition.DefaultAlpha);
        Assert.Equal(ComponentColorSource.ThemeAccent, definition.ColorSource);
    }

    [Fact]
    public void EveryArtDefinition_MatchesItsArtworkKind_AndOnlyArtShapesCarryArt()
    {
        foreach (var definition in BuiltInComponentCatalog.All)
        {
            Assert.Equal(definition.Shape == ComponentShape.Art, definition.Art is not null);
            if (definition.Art is { } art)
            {
                Assert.Equal(art.Kind, definition.Kind);
                Assert.Same(art, BuiltInArtCatalog.Find(art.Id));
            }
        }

        Assert.All(BuiltInArtCatalog.All, art => Assert.Contains(BuiltInComponentCatalog.All, d => ReferenceEquals(d.Art, art)));
    }

    [Fact]
    public void KindMismatch_IsNeverDrawn()
    {
        var component = new PlateComponent { Kind = PlateComponentKind.PlateFrame, DefinitionId = AstrolabeDefinition };
        Assert.Equal(ComponentStatus.KindMismatch, ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out var definition));
        Assert.Null(definition);

        var correct = ComponentDocuments.Of(AstrolabeDefinition);
        Assert.Equal(ComponentStatus.Ready, ComponentPaintPlan.Resolve(correct, BuiltInComponentCatalog.Instance, out _));
    }

    [Fact]
    public void BasicPicker_OffersAstrolabePivot_ForCornerOrnamentsOnly()
    {
        Assert.Contains(BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament), d => d.Id == AstrolabeDefinition && !d.RequiresAsset);
        foreach (var kind in Enum.GetValues<PlateComponentKind>().Where(k => k != PlateComponentKind.CornerOrnament))
        {
            Assert.DoesNotContain(BuiltInComponentCatalog.OfKind(kind), d => d.Id == AstrolabeDefinition);
        }

        var document = ComponentDocuments.WithAnchors();
        Assert.True(PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, AstrolabeDefinition, BuiltInComponentCatalog.Instance));
        var slot = PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament)!;
        Assert.Equal(AstrolabeDefinition, slot.DefinitionId);
        Assert.Null(slot.AssetId);
        Assert.Throws<ArgumentException>(() => PlateComponentEditor.SetSlot(document, PlateComponentKind.Divider, AstrolabeDefinition, BuiltInComponentCatalog.Instance));
    }

    [Fact]
    public void SwitchingBetweenProceduralAndArt_KeepsAdvancedRefinements()
    {
        var document = ComponentDocuments.WithAnchors();
        PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, BuiltInComponentCatalog.CornerOrnamentBracket, BuiltInComponentCatalog.Instance);
        var id = PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament)!.Id;
        PlateComponentEditor.Update(document, id, c =>
        {
            c.Color = new Vector4(0.2f, 0.8f, 0.4f, 1f);
            c.Scale = 1.5f;
            c.RotationDegrees = 10f;
            c.Offset = new Vector2(4, 6);
            c.Opacity = 0.5f;
        });

        PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, AstrolabeDefinition, BuiltInComponentCatalog.Instance);
        var slot = PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament)!;

        Assert.Equal(id, slot.Id);
        Assert.Equal(AstrolabeDefinition, slot.DefinitionId);
        Assert.Equal(new Vector4(0.2f, 0.8f, 0.4f, 1f), slot.Color);
        Assert.Equal(1.5f, slot.Scale);
        Assert.Equal(10f, slot.RotationDegrees);
        Assert.Equal(new Vector2(4, 6), slot.Offset);
        Assert.Equal(0.5f, slot.Opacity);
    }

    [Fact]
    public void Corners_OneArtworkServesAllFour_ByRotation_NotMirroring()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(AstrolabeDefinition)];

        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();

        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.False(s.Placement.MirrorX || s.Placement.MirrorY));
        Assert.Equal([0f, 90f, 270f, 180f], steps.Select(s => s.Placement.RotationDegrees));

        var unit = ComponentPaintPlan.Unit(document);
        var size = ComponentPaintPlan.CornerSize * unit * BuiltInArtCatalog.AstrolabePivot.SizeFactor;
        var inset = ComponentPaintPlan.CornerInset * unit;
        Assert.All(steps, s => Assert.Equal(new Vector2(size), s.Placement.Rect.Size));
        Assert.Equal(new Vector2(inset, inset), steps[0].Placement.Rect.Position);
        Assert.Equal(new Vector2(document.CanvasWidth - inset - size, inset), steps[1].Placement.Rect.Position);
        Assert.Equal(new Vector2(inset, document.CanvasHeight - inset - size), steps[2].Placement.Rect.Position);
        Assert.Equal(new Vector2(document.CanvasWidth - inset - size, document.CanvasHeight - inset - size), steps[3].Placement.Rect.Position);
    }

    [Fact]
    public void Corners_ArtworkTopLeftTexel_LandsInEachCanvasCorner()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(AstrolabeDefinition)];
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);

        var corners = new List<Vector2>();
        foreach (var step in ComponentDocuments.Plan(document).Where(s => !s.IsElement))
        {
            var output = new List<ComponentPrimitive>();
            ComponentGeometry.Build(document, step.Component!, step.Definition!, step.Placement, output);
            var art = Assert.Single(output);
            Assert.Equal(ComponentPrimitiveKind.Art, art.Kind);
            corners.Add(art.A); // A carries the artwork's top-left texel (the pivot's corner)
        }

        // The art's own top-left corner points at the canvas corner it decorates.
        Assert.True(corners[0].X < canvas.X / 2 && corners[0].Y < canvas.Y / 2);
        Assert.True(corners[1].X > canvas.X / 2 && corners[1].Y < canvas.Y / 2);
        Assert.True(corners[2].X < canvas.X / 2 && corners[2].Y > canvas.Y / 2);
        Assert.True(corners[3].X > canvas.X / 2 && corners[3].Y > canvas.Y / 2);
        Assert.All(corners, c => Assert.True(Math.Min(Math.Min(c.X, canvas.X - c.X), Math.Min(c.Y, canvas.Y - c.Y)) < ComponentPaintPlan.CornerInset * ComponentPaintPlan.Unit(document) + 0.01f));
    }

    [Fact]
    public void Corners_OffsetMovesAllFourSymmetrically_AndRotationScaleApply()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(AstrolabeDefinition);
        document.Components = [component];
        var before = ComponentDocuments.Plan(document).Where(s => !s.IsElement).Select(s => s.Placement).ToList();

        component.Offset = new Vector2(10, 5);
        component.Scale = 2f;
        component.RotationDegrees = 15f;
        var after = ComponentDocuments.Plan(document).Where(s => !s.IsElement).Select(s => s.Placement).ToList();

        var signs = new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) };
        for (var i = 0; i < 4; i++)
        {
            var centerBefore = before[i].Rect.Position + (before[i].Rect.Size / 2f);
            var centerAfter = after[i].Rect.Position + (after[i].Rect.Size / 2f);
            Assert.Equal(new Vector2(10, 5) * signs[i], centerAfter - centerBefore);
            Assert.Equal(before[i].Rect.Size * 2f, after[i].Rect.Size);
            Assert.Equal(before[i].RotationDegrees + 15f, after[i].RotationDegrees);
        }
    }

    [Fact]
    public void Tint_UsesComponentColor_TimesOpacity_AndFollowsTheThemeByDefault()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(AstrolabeDefinition);
        var placement = new ComponentPlacement(new ElementRect(new Vector2(20, 20), new Vector2(80, 80)), 0f, false, false);

        var themed = Single(document, component, placement);
        Assert.Equal(Astrolabe.DefaultColor(document), themed.Color);

        foreach (var color in new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(0.2f, 0.6f, 1f, 0.8f), new Vector4(1f, 1f, 1f, 1f) })
        {
            component.Color = color;
            component.Opacity = 0.5f;
            var tinted = Single(document, component, placement);
            Assert.Equal(new Vector4(color.X, color.Y, color.Z, color.W * 0.5f), tinted.Color);
        }

        component.Opacity = 0f;
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, Astrolabe, placement, output);
        Assert.Empty(output);
    }

    [Fact]
    public void NonTintableArt_KeepsItsOwnColors_OnlyOpacityApplies()
    {
        var document = ComponentDocuments.WithAnchors();
        var art = BuiltInArtCatalog.AstrolabePivot with { Id = "af.asset.test.corner-ornament.colored", Tintable = false };
        var definition = ComponentDefinition.ForArt("af.corner-ornament.test-colored", "test", art, ComponentColorSource.ThemeAccent);
        var component = new PlateComponent { Kind = PlateComponentKind.CornerOrnament, DefinitionId = definition.Id, Color = new Vector4(1f, 0f, 0f, 0.8f), Opacity = 0.5f };
        var placement = new ComponentPlacement(new ElementRect(new Vector2(0, 0), new Vector2(80, 80)), 0f, false, false);

        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, definition, placement, output);

        Assert.Equal(new Vector4(1f, 1f, 1f, 0.4f), Assert.Single(output).Color);
        Assert.Equal(ComponentColorSource.White, definition.ColorSource);
    }

    [Fact]
    public void Rendering_DependsOnArtIdentity_NotDisplayText()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(AstrolabeDefinition);
        var renamed = Astrolabe with { Name = "Other", Description = "Other", Art = Astrolabe.Art! with { Name = "Other" } };
        var placement = new ComponentPlacement(new ElementRect(new Vector2(20, 20), new Vector2(80, 80)), 30f, false, false);

        Assert.Equal(Single(document, component, placement), Single(document, component, placement, renamed));
    }

    [Fact]
    public void Serialization_StoresOnlyTheLogicalDefinitionId_NoPathsOrBytes()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(AstrolabeDefinition)];

        var json = PlateDocuments.ToJson(document).ToJsonString();
        var stored = PlateDocuments.ToJson(document)["Components"]!.AsArray().Single()!.AsObject();

        Assert.Equal(AstrolabeDefinition, (string?)stored["DefinitionId"]);
        Assert.Equal((int)PlateComponentKind.CornerOrnament, (int)stored["Kind"]!);
        AssertNoPhysicalReference(json);
    }

    [Fact]
    public async Task SaveAndReload_PreservesTheGraphicalComponent()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var plateId = await CreatePlateWithAstrolabeAsync(library);

        var reloaded = await fixture.LoadAsync(); // a fresh service, reading from disk
        var component = reloaded.OpenDocumentForEditing(plateId).Components!.Single();

        Assert.Equal(AstrolabeDefinition, component.DefinitionId);
        Assert.Equal(new Vector4(0.9f, 0.3f, 0.7f, 1f), component.Color);
        Assert.Equal(1.25f, component.Scale);
        Assert.Equal(20f, component.RotationDegrees);
        Assert.Null(component.AssetId);
        AssertNoPhysicalReference(fixture.ReadPlateJson(plateId));
    }

    [Fact]
    public async Task Template_PreservesTheGraphicalComponent_WithoutAssetStorage()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithAstrolabeAsync(fixture.PlateLibrary);
        var source = fixture.PlateLibrary.OpenDocumentForEditing(sourceId);

        var templateId = await templates.SaveAsTemplateAsync(sourceId, "Celestial");
        Assert.True(PlateComponent.ListsEqual(source.Components, templates.GetSavedDocument(templateId)!.Components));
        AssertNoPhysicalReference(fixture.ReadTemplateJson(templateId));

        var created = await templates.InstantiateAsync(templateId, null);
        var fromTemplate = fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId);
        Assert.True(PlateComponent.ListsEqual(source.Components, fromTemplate.Components));
    }

    [Fact]
    public async Task Duplicate_PreservesTheReference_WithNoImageBytes()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var sourceId = await CreatePlateWithAstrolabeAsync(library);

        var copyId = await library.DuplicatePlateAsync(sourceId);

        Assert.True(PlateComponent.ListsEqual(library.OpenDocumentForEditing(sourceId).Components, library.OpenDocumentForEditing(copyId).Components));
        var scan = await library.ScanAssetReferencesAsync();
        Assert.True(scan.IsComplete);
        Assert.Empty(scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task Package_RoundTrip_CarriesTheLogicalId_NotTheArtwork()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithAstrolabeAsync(library);

        var path = fixture.Export(packages, plateId);
        var entries = PackageFiles.Read(path);
        Assert.DoesNotContain(entries, e => e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        foreach (var entry in entries)
        {
            AssertNoPhysicalReference(Encoding.UTF8.GetString(entry.Bytes));
        }

        var profile = PackageFiles.Json(PackageFiles.Entry(entries, PackagePaths.ProfilePath));
        Assert.Equal(AstrolabeDefinition, (string?)profile["Components"]![0]!["DefinitionId"]);

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Empty(staged.Diagnostics.Warnings);
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.ToString());

        var original = library.OpenDocumentForEditing(plateId).Components!.Single();
        var imported = library.OpenDocumentForEditing(result.PlateId).Components!.Single();
        Assert.True(original.ContentEquals(imported));
    }

    [Fact]
    public async Task Package_UnknownFutureArtComponent_IsKeptVerbatim_NeverReplaced()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var plateId = await CreatePlateWithAstrolabeAsync(library);
        var path = fixture.Export(packages, plateId);
        var future = JsonNode.Parse("""
            [ { "Kind": 5, "DefinitionId": "af.corner-ornament.moonlit-sextant", "Color": { "X": 1, "Y": 0.5, "Z": 0.25, "W": 1 }, "Scale": 1.5, "FutureArtHint": { "Variant": 2 } } ]
            """)!;
        var crafted = PackageFiles.Rewrite(path, e => PackageFiles.EditProfile(e, profile => profile["Components"] = future.DeepClone()));

        using var staged = packages.Inspect(crafted);
        Assert.True(staged.CanImport, string.Join("; ", staged.Diagnostics.Errors));
        Assert.Contains(staged.Diagnostics.Warnings, w => w.Code == PackageWarningCode.UnrecognizedSetting); // "kept, but may look different here"
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded);

        var saved = JsonNode.Parse(fixture.Library.ReadPlateJson(result.PlateId))!["Components"]![0]!;
        Assert.Equal("af.corner-ornament.moonlit-sextant", (string?)saved["DefinitionId"]);
        Assert.Equal(2, (int)saved["FutureArtHint"]!["Variant"]!);
        var imported = library.OpenDocumentForEditing(result.PlateId).Components!.Single();
        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(imported, BuiltInComponentCatalog.Instance, out _));
    }

    [Fact]
    public void MissingArtDefinition_IsKeptUndrawn_AndReplaceable()
    {
        var document = ComponentDocuments.WithAnchors();
        var unknown = new PlateComponent { Kind = PlateComponentKind.CornerOrnament, DefinitionId = "af.corner-ornament.moonlit-sextant", Scale = 1.5f };
        document.Components = [unknown, ComponentDocuments.Of(BuiltInComponentCatalog.DividerLine)];

        // Not drawn, but nothing else is affected.
        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        Assert.DoesNotContain(steps, s => ReferenceEquals(s.Component, unknown));
        Assert.Contains(steps, s => s.Component!.DefinitionId == BuiltInComponentCatalog.DividerLine);

        // Survives serialization verbatim.
        var reloaded = PlateDocuments.Deserialize(PlateDocuments.ToJson(document))!;
        Assert.Equal("af.corner-ornament.moonlit-sextant", reloaded.Components![0].DefinitionId);

        // The slot still binds to it, and choosing a style replaces it in place (the player's choice, never automatic).
        Assert.Same(unknown, PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament));
        Assert.True(PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, AstrolabeDefinition, BuiltInComponentCatalog.Instance));
        Assert.Equal(AstrolabeDefinition, unknown.DefinitionId);
        Assert.Equal(1.5f, unknown.Scale);
    }

    [Fact]
    public void CatalogWithoutTheArt_TreatsItAsMissing_NotAsAnotherStyle()
    {
        var older = new CatalogWithout(AstrolabeDefinition);
        var component = ComponentDocuments.Of(AstrolabeDefinition);

        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(component, older, out var definition));
        Assert.Null(definition);
        Assert.Equal(AstrolabeDefinition, component.DefinitionId);

        var document = ComponentDocuments.WithAnchors();
        document.Components = [component];
        var drawn = document.Elements.OrderBy(e => e.ZIndex).ToList();
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, drawn, older, steps);
        Assert.All(steps, s => Assert.True(s.IsElement));
    }

    [Fact]
    public void ProceduralCornerOrnaments_AreUnchanged()
    {
        var document = ComponentDocuments.WithAnchors();
        var unit = ComponentPaintPlan.Unit(document);
        foreach (var id in new[] { BuiltInComponentCatalog.CornerOrnamentBracket, BuiltInComponentCatalog.CornerOrnamentDiamond })
        {
            Assert.Null(BuiltInComponentCatalog.Find(id)!.Art);
            document.Components = [ComponentDocuments.Of(id)];
            var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();

            Assert.Equal(4, steps.Count);
            Assert.All(steps, s => Assert.Equal(0f, s.Placement.RotationDegrees));
            Assert.All(steps, s => Assert.Equal(new Vector2(ComponentPaintPlan.CornerSize * unit), s.Placement.Rect.Size));
            Assert.Equal([(false, false), (true, false), (false, true), (true, true)], steps.Select(s => (s.Placement.MirrorX, s.Placement.MirrorY)));
        }

        Assert.Equal(26, BuiltInComponentCatalog.All.Count); // 18 procedural, the Astrolabe Pivot, 7 Celestial Sakura
        Assert.Equal(18, BuiltInComponentCatalog.All.Count(d => d.Art is null));
        Assert.Equal(4, BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament).Count());
    }

    [Fact]
    public void OldPlates_LoadAndSaveUnchanged()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();
        var before = PlateDocuments.ToJson(document);

        var reloaded = PlateDocuments.Deserialize((JsonObject)before.DeepClone())!;
        var after = PlateDocuments.ToJson(reloaded);

        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.All(reloaded.Components!, c => Assert.Null(BuiltInComponentCatalog.Find(c.DefinitionId)!.Art));
    }

    // ---- Bundled resource integrity -------------------------------------------------------

    [Fact]
    public void BundledLineArt_IsEmbeddedValidSquareRgbaPng_WithRealTransparency()
    {
        // Tinted line art (full-color families are checked in their own tests, e.g. CelestialSakuraTests).
        foreach (var art in BuiltInArtCatalog.All.Where(a => a.Tintable))
        {
            Assert.StartsWith(BuiltInArtCatalog.ResourcePrefix, art.ResourceName);
            var bytes = ReadResource(art.ResourceName);
            Assert.True(bytes.Length < 512 * 1024, $"{art.Id} runtime PNG is {bytes.Length} bytes");

            var image = BundledArtImage.DecodePng(bytes);
            Assert.Equal((art.PixelWidth, art.PixelHeight), (image.Width, image.Height));
            Assert.Equal(image.Width, image.Height);
            Assert.Equal(0, image.Width & (image.Width - 1)); // power of two: every level halves exactly

            var alpha = Enumerable.Range(0, image.Width * image.Height).Select(i => image.Rgba[(i * 4) + 3]).ToArray();
            var transparent = alpha.Count(a => a == 0) / (double)alpha.Length;
            var soft = alpha.Count(a => a is > 0 and < 255) / (double)alpha.Length;
            Assert.InRange(transparent, 0.3, 0.95); // mostly empty space: no baked background or checkerboard
            Assert.True(soft > 0.05, "anti-aliased glow must survive as partial alpha");
            Assert.Contains(alpha, a => a >= 200); // the line cores

            // Four image corners away from the drawing are fully transparent.
            Assert.Equal(0, image.Rgba[(((image.Height - 1) * image.Width) + (image.Width - 1)) * 4 + 3]);
        }
    }

    [Fact]
    public void BundledArt_ResourceNames_AreThePathBelowAssetsWithDots_OnEveryOs()
    {
        var expected = ExpectedArtResourceNames();
        Assert.NotEmpty(expected);
        Assert.All(BuiltInArtCatalog.All, art => Assert.Contains(art.ResourceName, expected));

        // The whole set, not just the catalog: a separator leaking into any name ('/' on Linux) fails here.
        Assert.Equal(expected, EmbeddedPngNames(typeof(BuiltInArtTests).Assembly.GetManifestResourceNames()));
    }

    [Fact]
    public void PluginAssembly_EmbedsTheArtUnderTheNamesTheTextureCacheRequests()
    {
        // BuiltInArtTextureCache reads from the plugin assembly, which this project deliberately
        // doesn't reference; its manifest is read as metadata, so no Dalamud type ever loads.
        // CI builds the plugin first and names it in AETHERFRAME_PLUGIN_ASSEMBLY (then it must
        // exist); locally the plugin's own build output is checked when there is one.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var names = metadata.ManifestResources.Select(h => metadata.GetString(metadata.GetManifestResource(h).Name));

        Assert.Equal(ExpectedArtResourceNames(), EmbeddedPngNames(names));
    }

    [Fact]
    public void BundledArt_IsGreyscale_SoTintTakesTheComponentColorExactly()
    {
        var image = BundledArtImage.DecodePng(ReadResource(BuiltInArtCatalog.AstrolabePivot.ResourceName));
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            Assert.True(image.Rgba[i] == image.Rgba[i + 1] && image.Rgba[i + 1] == image.Rgba[i + 2], $"texel {i / 4} is not grey");
            if (image.Rgba[i + 3] == 0)
            {
                Assert.Equal(255, image.Rgba[i]); // transparent texels are white: bilinear edges never darken a tint
            }
        }
    }

    [Fact]
    public void BundledArt_Levels_KeepFineLinesAndRelativeAlpha()
    {
        var top = BundledArtImage.DecodePng(ReadResource(BuiltInArtCatalog.AstrolabePivot.ResourceName));
        var levels = BundledArtImage.BuildLevels(top);

        Assert.Equal([512, 256, 128, 64, 32], levels.Select(l => l.Width));
        Assert.All(levels, l => Assert.Equal(l.Width, l.Height));
        var coverage = levels.Select(l => Enumerable.Range(0, l.Width * l.Height).Average(i => l.Rgba[(i * 4) + 3] / 255.0)).ToList();
        Assert.All(coverage, c => Assert.InRange(c, coverage[0] * 0.97, coverage[0] * 1.03)); // average coverage is conserved at every level

        foreach (var level in levels)
        {
            for (var i = 0; i < level.Rgba.Length; i += 4)
            {
                Assert.True(level.Rgba[i] == level.Rgba[i + 1] && level.Rgba[i + 1] == level.Rgba[i + 2]);
                if (level.Rgba[i + 3] == 0)
                {
                    Assert.Equal(255, level.Rgba[i]);
                }
            }
        }
    }

    // ---- Decoder and level selection ---------------------------------------------------------

    [Fact]
    public void Decoder_ReadsEveryRowFilter()
    {
        var pixels = new byte[4 * 4 * 4];
        new Random(7).NextBytes(pixels);
        for (var filter = 0; filter <= 4; filter++)
        {
            var decoded = BundledArtImage.DecodePng(EncodePng(4, pixels, (byte)filter));
            Assert.Equal((4, 4), (decoded.Width, decoded.Height));
            Assert.Equal(pixels, decoded.Rgba);
        }
    }

    [Theory]
    [InlineData(16, 6)] // 16-bit
    [InlineData(8, 3)] // palette
    [InlineData(8, 0)] // greyscale
    [InlineData(8, 4)] // greyscale + alpha
    public void Decoder_RejectsAnythingButRgbaOrRgb8(int bitDepth, int colorType)
    {
        var png = EncodePng(4, new byte[64], 0);
        png[8 + 8 + 8] = (byte)bitDepth;
        png[8 + 8 + 9] = (byte)colorType;
        Assert.Throws<InvalidDataException>(() => BundledArtImage.DecodePng(png));
    }

    [Fact]
    public void Decoder_RejectsNonPngAndTruncatedData()
    {
        Assert.Throws<InvalidDataException>(() => BundledArtImage.DecodePng("GIF89a"u8.ToArray()));
        var png = EncodePng(4, new byte[64], 0);
        Assert.ThrowsAny<Exception>(() => BundledArtImage.DecodePng(png.AsSpan(0, png.Length - 20)));
    }

    [Theory]
    [InlineData(20f, 4)]
    [InlineData(32f, 4)]
    [InlineData(33f, 3)]
    [InlineData(80f, 2)]
    [InlineData(150f, 1)]
    [InlineData(256f, 1)]
    [InlineData(300f, 0)]
    [InlineData(2000f, 0)]
    [InlineData(float.NaN, 0)]
    public void LevelSelection_PicksTheSmallestLevelAtLeastTheScreenSize(float screenPixels, int expected)
    {
        Assert.Equal(expected, BundledArtImage.SelectLevel([512, 256, 128, 64, 32], screenPixels));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static ComponentPrimitive Single(ProfileDocument document, PlateComponent component, ComponentPlacement placement, ComponentDefinition? definition = null)
    {
        var output = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, definition ?? Astrolabe, placement, output);
        var primitive = Assert.Single(output);
        Assert.Equal(ComponentPrimitiveKind.Art, primitive.Kind);
        return primitive;
    }

    private static async Task<Guid> CreatePlateWithAstrolabeAsync(Services.Plates.PlateLibraryService library)
    {
        var created = await library.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, null, "Celestial", new PlateStarterContent(null));
        var document = library.OpenDocumentForEditing(created.PlateId);
        var component = ComponentDocuments.Of(AstrolabeDefinition);
        component.Color = new Vector4(0.9f, 0.3f, 0.7f, 1f);
        component.Scale = 1.25f;
        component.RotationDegrees = 20f;
        document.Components = [component];
        await library.SavePlateDocumentAsync(document);
        return created.PlateId;
    }

    private static void AssertNoPhysicalReference(string text)
    {
        Assert.DoesNotContain(".png", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AstrolabePivot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CelestialDream", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BuiltInArtCatalog.ResourcePrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("AetherFrameAssets", text, StringComparison.Ordinal);
    }

    /// <summary>Every PNG under the plugin's Assets folder, named as the runtime requests it:
    /// the prefix plus its path below Assets with '.' separators, whatever the OS separator.</summary>
    private static List<string> ExpectedArtResourceNames()
    {
        var assets = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Assets");
        return Directory.GetFiles(assets, "*.png", SearchOption.AllDirectories)
            .Select(file => BuiltInArtCatalog.ResourcePrefix + string.Join('.', Path.GetRelativePath(assets, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> EmbeddedPngNames(IEnumerable<string> names) =>
        names.Where(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(BuiltInArtTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] EncodePng(int size, byte[] rgba, byte filter)
    {
        var stride = size * 4;
        var raw = new byte[(stride + 1) * size];
        for (var y = 0; y < size; y++)
        {
            raw[y * (stride + 1)] = filter;
            for (var i = 0; i < stride; i++)
            {
                int left = i >= 4 ? rgba[(y * stride) + i - 4] : 0;
                int up = y > 0 ? rgba[((y - 1) * stride) + i] : 0;
                int upLeft = y > 0 && i >= 4 ? rgba[((y - 1) * stride) + i - 4] : 0;
                var predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                raw[(y * (stride + 1)) + 1 + i] = (byte)(rgba[(y * stride) + i] - predictor);
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
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;
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
        stream.Write(new byte[4]); // CRC: not checked by the decoder (the bundled art is verified by these tests instead)
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private sealed class CatalogWithout(string excludedId) : IComponentCatalog
    {
        public ComponentDefinition? Find(string? id) => id == excludedId ? null : BuiltInComponentCatalog.Find(id);
    }
}
