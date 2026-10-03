using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Basic's Corner Ornament size and distance from the edge (issue #113): written through the same
/// Scale and Offset the Advanced editor edits, a new ornament starts larger and closer to the edge,
/// and an ornament already on a Plate keeps exactly what it was saved with.
/// </summary>
public class CornerOrnamentPlacementTests
{
    private const string Bracket = BuiltInComponentCatalog.CornerOrnamentBracket;
    private const string Sakura = BuiltInComponentCatalog.CornerOrnamentCelestialSakura;
    private const float Precision = 0.01f;

    /// <summary>Every Corner Ornament Basic offers: procedural, bundled and every Art Style's.</summary>
    public static IEnumerable<object[]> BasicOrnaments() =>
        BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament).Where(d => !d.RequiresAsset).Select(d => new object[] { d.Id });

    public static IEnumerable<object[]> CanvasSizes() =>
        ProfileCanvasPreset.All.Select(p => new object[] { p.Width, p.Height });

    // ---- Defaults ----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CanvasSizes))]
    public void ANewOrnament_IsLargerAndCloserToTheEdge_OnEverySupportedPlateSize(float width, float height)
    {
        foreach (var definition in BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament).Where(d => !d.RequiresAsset))
        {
            var document = Canvas(width, height);
            PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, definition.Id, BuiltInComponentCatalog.Instance);
            var ornament = PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament)!;
            var unit = ComponentPaintPlan.Unit(document);

            Assert.Equal(CornerOrnamentPlacement.DefaultScale, ornament.Scale);
            Assert.Equal(CornerOrnamentPlacement.DefaultEdgeDistance, CornerOrnamentPlacement.EdgeDistance(document, ornament, definition), Precision);

            // Every corner, as drawn: the default distance from its own two edges, and on the Plate.
            var rects = CornerRects(document, ornament);
            Assert.Equal(4, rects.Count);
            var edge = CornerOrnamentPlacement.DefaultEdgeDistance * unit;
            var side = ComponentPaintPlan.CornerSize * ComponentPaintPlan.ArtSizeFactor(definition) * CornerOrnamentPlacement.DefaultScale * unit;
            foreach (var rect in rects)
            {
                var near = Vector2.Min(rect.Position, new Vector2(width, height) - (rect.Position + rect.Size));
                Assert.Equal(edge, near.X, 0.05f);
                Assert.Equal(edge, near.Y, 0.05f);
                Assert.True(rect.Size.X <= side + Precision && rect.Size.Y <= side + Precision);
            }

            // Larger, and closer to the edge, than the layout's own placement it used to start at.
            Assert.True(CornerOrnamentPlacement.DefaultScale > 1f);
            Assert.True(CornerOrnamentPlacement.DefaultEdgeDistance < ComponentPaintPlan.CornerInset);

            // Two ornaments down one side never meet, even on the shortest supported Plate.
            Assert.True((2f * (edge + side)) < height, $"{definition.Id} on {width}x{height}");
        }
    }

    [Fact]
    public void AddingInAdvanced_StartsAtTheSameDefaults_AsBasic()
    {
        var document = ComponentDocuments.WithAnchors();
        var definition = BuiltInComponentCatalog.Find(Sakura)!;
        var added = PlateComponentEditor.Add(document, definition);

        Assert.True(CornerOrnamentPlacement.IsDefault(document, added, definition));
    }

    [Fact]
    public void OtherKinds_StillStartAtTheLayoutsOwnPlacement()
    {
        var document = ComponentDocuments.WithAnchors();
        foreach (var component in ComponentDocuments.OneOfEach().Where(c => c.Kind != PlateComponentKind.CornerOrnament))
        {
            var added = PlateComponentEditor.Add(document, BuiltInComponentCatalog.Find(component.DefinitionId)!);
            Assert.Equal(1f, added.Scale);
            Assert.Equal(Vector2.Zero, added.Offset);
        }
    }

    // ---- Existing Plates ---------------------------------------------------------------------------

    /// <summary>An ornament saved before this change (Scale 1, no Offset) loads, is shown and is
    /// restyled exactly as before: nothing applies the new defaults to it.</summary>
    [Fact]
    public async Task AnExistingOrnament_KeepsItsSavedPlacement_ThroughLoadingAndChangingStyle()
    {
        var document = ComponentDocuments.WithAnchors();
        var old = ComponentDocuments.Of(Bracket);
        old.Corners = CornerMask.TopLeft | CornerMask.BottomRight;
        document.Components = [old];

        using var harness = await BasicHarness.OpenDocumentAsync(document);
        harness.SimulateBasicFrame();
        var loaded = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.CornerOrnament)!;
        Assert.Equal(1f, loaded.Scale);
        Assert.Equal(Vector2.Zero, loaded.Offset);
        Assert.Equal(ComponentPaintPlan.CornerInset, CornerOrnamentPlacement.EdgeDistance(harness.Document, loaded, BuiltInComponentCatalog.Find(Bracket)!), Precision);
        Assert.False(harness.Session.IsDirty);

        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, Sakura);
        Assert.Equal(1f, loaded.Scale);
        Assert.Equal(Vector2.Zero, loaded.Offset);

        // At 100% a style change keeps any offset bit for bit, not just to within rounding.
        loaded.Offset = new Vector2(-8.63f, -6.57f);
        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, Bracket);
        Assert.Equal(new Vector2(-8.63f, -6.57f), loaded.Offset);
        Assert.Equal(CornerMask.TopLeft | CornerMask.BottomRight, loaded.Corners);
    }

    /// <summary>A resized ornament that changes style, in Basic, Advanced or with an Art Style, stays
    /// the same distance from the edge, though the new art is a different size.</summary>
    [Fact]
    public async Task ChangingStyle_KeepsTheDistanceFromTheEdge_InBothEditors()
    {
        var (document, ornament, _) = NewOrnament(Bracket);
        PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, 2f, BuiltInComponentCatalog.Instance);
        PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, 0f, BuiltInComponentCatalog.Instance);

        PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, Sakura, BuiltInComponentCatalog.Instance);
        Assert.Equal(0f, CornerOrnamentPlacement.EdgeDistance(document, ornament, BuiltInComponentCatalog.Find(Sakura)!), Precision);
        Assert.Equal(2f, ornament.Scale);
        Assert.All(CornerRects(document, ornament), rect => Assert.True(rect.Position.X >= -0.05f && rect.Position.Y >= -0.05f));

        Assert.True(PlateComponentEditor.SetDefinition(document, ornament.Id, BuiltInComponentCatalog.CornerOrnamentAstrolabePivot, BuiltInComponentCatalog.Instance));
        Assert.Equal(0f, CornerOrnamentPlacement.EdgeDistance(document, ornament, BuiltInComponentCatalog.Find(BuiltInComponentCatalog.CornerOrnamentAstrolabePivot)!), Precision);
        Assert.False(PlateComponentEditor.SetDefinition(document, ornament.Id, BuiltInComponentCatalog.CornerOrnamentAstrolabePivot, BuiltInComponentCatalog.Instance));
        Assert.False(PlateComponentEditor.SetDefinition(document, ornament.Id, BuiltInComponentCatalog.DividerDiamond, BuiltInComponentCatalog.Instance));

        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, Bracket);
        var slot = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.CornerOrnament)!;
        harness.Session.SetCornerOrnamentScale(slot.Id, 2f, continuous: false);
        harness.Session.SetCornerOrnamentEdgeDistance(slot.Id, 4f, continuous: false);
        harness.Basic.ApplyTheme(ProfileThemePresets.Find("af.style.celestial-sakura")!);
        Assert.Equal(Sakura, slot.DefinitionId);
        Assert.Equal(4f, CornerOrnamentPlacement.EdgeDistance(harness.Document, slot, BuiltInComponentCatalog.Find(Sakura)!), Precision);
    }

    /// <summary>A new ornament switched to any other style, in either editor, is still at the defaults.</summary>
    [Fact]
    public void ANewOrnament_StaysAtTheDefaults_WhicheverStyleItChangesTo()
    {
        var ids = BuiltInComponentCatalog.OfKind(PlateComponentKind.CornerOrnament).Where(d => !d.RequiresAsset).Select(d => d.Id).ToList();
        foreach (var from in ids)
        {
            foreach (var to in ids.Where(id => id != from))
            {
                var (basic, ornament, _) = NewOrnament(from);
                PlateComponentEditor.SetSlot(basic, PlateComponentKind.CornerOrnament, to, BuiltInComponentCatalog.Instance);
                Assert.True(CornerOrnamentPlacement.IsDefault(basic, ornament, BuiltInComponentCatalog.Find(to)!), $"Basic, {from} to {to}");

                var (advanced, added, _) = NewOrnament(from);
                PlateComponentEditor.SetDefinition(advanced, added.Id, to, BuiltInComponentCatalog.Instance);
                Assert.True(CornerOrnamentPlacement.IsDefault(advanced, added, BuiltInComponentCatalog.Find(to)!), $"Advanced, {from} to {to}");
            }
        }
    }

    [Fact]
    public async Task ApplyingAnArtStyle_GivesANewOrnamentTheDefaults_ButKeepsAnExistingOnesSizeAndDistance()
    {
        var style = ProfileThemePresets.Find("af.style.celestial-sakura")!;

        using (var fresh = await BasicHarness.NewClassicAsync())
        {
            fresh.Basic.ApplyTheme(style);
            var ornament = PlateComponentEditor.FindSlot(fresh.Document, PlateComponentKind.CornerOrnament)!;
            Assert.True(CornerOrnamentPlacement.IsDefault(fresh.Document, ornament, BuiltInComponentCatalog.Find(ornament.DefinitionId)!));
        }

        var document = ComponentDocuments.WithAnchors();
        var old = ComponentDocuments.Of(Bracket);
        old.Scale = 0.8f;
        old.Offset = new Vector2(3, 5);
        document.Components = [old];
        using var existing = await BasicHarness.OpenDocumentAsync(document);
        var kept = PlateComponentEditor.FindSlot(existing.Document, PlateComponentKind.CornerOrnament)!;
        var edges = CornerOrnamentPlacement.EdgeDistances(existing.Document, kept, BuiltInComponentCatalog.Find(Bracket)!);
        existing.Basic.ApplyTheme(style);
        Assert.Equal(Sakura, kept.DefinitionId);
        Assert.Equal(0.8f, kept.Scale);
        var after = CornerOrnamentPlacement.EdgeDistances(existing.Document, kept, BuiltInComponentCatalog.Find(Sakura)!);
        Assert.Equal(edges.X, after.X, Precision);
        Assert.Equal(edges.Y, after.Y, Precision);
    }

    // ---- Size --------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BasicOrnaments))]
    public void ChangingTheSize_GrowsFromTheCorner_KeepingTheDistanceFromTheEdge(string definitionId)
    {
        var (document, ornament, definition) = NewOrnament(definitionId);
        var before = CornerRects(document, ornament);

        Assert.True(PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, 2f, BuiltInComponentCatalog.Instance));

        Assert.Equal(2f, ornament.Scale);
        Assert.Equal(CornerOrnamentPlacement.DefaultEdgeDistance, CornerOrnamentPlacement.EdgeDistance(document, ornament, definition), Precision);
        var after = CornerRects(document, ornament);
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(after[i].Size.X > before[i].Size.X);
            var nearBefore = Vector2.Min(before[i].Position, canvas - (before[i].Position + before[i].Size));
            var nearAfter = Vector2.Min(after[i].Position, canvas - (after[i].Position + after[i].Size));
            Assert.Equal(nearBefore.X, nearAfter.X, 0.05f);
            Assert.Equal(nearBefore.Y, nearAfter.Y, 0.05f);
        }

        Assert.True(PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, 0.5f, BuiltInComponentCatalog.Instance));
        Assert.True(CornerRects(document, ornament)[0].Size.X < before[0].Size.X);
        Assert.Equal(CornerOrnamentPlacement.DefaultEdgeDistance, CornerOrnamentPlacement.EdgeDistance(document, ornament, definition), Precision);
    }

    [Fact]
    public void TheSize_IsBoundedByTheComponentLimits_AndAnUnchangedSizeChangesNothing()
    {
        var (document, ornament, _) = NewOrnament(Bracket);

        PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, 100f, BuiltInComponentCatalog.Instance);
        Assert.Equal(PlateComponentLimits.MaxScale, ornament.Scale);
        PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, float.NaN, BuiltInComponentCatalog.Instance);
        Assert.Equal(1f, ornament.Scale);
        Assert.False(PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, 1f, BuiltInComponentCatalog.Instance));
    }

    // ---- Distance ----------------------------------------------------------------------------------

    [Fact]
    public void TheDistance_MovesEveryCornerInOrOut_AndIsBounded()
    {
        var (document, ornament, definition) = NewOrnament(Sakura);
        var unit = ComponentPaintPlan.Unit(document);

        Assert.True(PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, 0f, BuiltInComponentCatalog.Instance));
        Assert.Equal(0f, CornerOrnamentPlacement.EdgeDistance(document, ornament, definition), Precision);
        var canvas = new Vector2(document.CanvasWidth, document.CanvasHeight);
        foreach (var rect in CornerRects(document, ornament))
        {
            var near = Vector2.Min(rect.Position, canvas - (rect.Position + rect.Size));
            Assert.Equal(0f, near.X, 0.05f);
            Assert.Equal(0f, near.Y, 0.05f);
        }

        PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, 40f, BuiltInComponentCatalog.Instance);
        Assert.Equal(40f * unit, CornerRects(document, ornament)[0].Position.X, 0.05f);

        PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, 10_000f, BuiltInComponentCatalog.Instance);
        Assert.Equal(CornerOrnamentPlacement.MaxEdgeDistance, CornerOrnamentPlacement.EdgeDistance(document, ornament, definition), Precision);
        PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, -10_000f, BuiltInComponentCatalog.Instance);
        Assert.Equal(CornerOrnamentPlacement.MinEdgeDistance, CornerOrnamentPlacement.EdgeDistance(document, ornament, definition), Precision);
        Assert.False(PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, CornerOrnamentPlacement.MinEdgeDistance, BuiltInComponentCatalog.Instance));
    }

    [Fact]
    public void TheDistance_KeepsAnAdvancedOneAxisNudge()
    {
        var (document, ornament, definition) = NewOrnament(Bracket);
        PlateComponentEditor.Update(document, ornament.Id, c => c.Offset += new Vector2(6, 0));
        var nudge = CornerOrnamentPlacement.EdgeDistances(document, ornament, definition);
        Assert.Equal(6f, nudge.X - nudge.Y, Precision);

        PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, 30f, BuiltInComponentCatalog.Instance);
        var moved = CornerOrnamentPlacement.EdgeDistances(document, ornament, definition);
        Assert.Equal(30f, (moved.X + moved.Y) / 2f, Precision);
        Assert.Equal(6f, moved.X - moved.Y, Precision);
    }

    // ---- Reset, other refinements, other kinds -----------------------------------------------------

    [Fact]
    public void Reset_ReturnsToTheDefaults_KeepingColorOpacityRotationAndCorners()
    {
        var (document, ornament, definition) = NewOrnament(Sakura);
        PlateComponentEditor.SetCorner(document, ornament.Id, CornerMask.TopRight, false);
        PlateComponentEditor.Update(document, ornament.Id, c =>
        {
            c.Color = new Vector4(0.2f, 0.4f, 0.6f, 1f);
            c.Opacity = 0.5f;
            c.RotationDegrees = 15f;
        });
        PlateComponentEditor.SetCornerOrnamentScale(document, ornament.Id, 2.2f, BuiltInComponentCatalog.Instance);
        PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, ornament.Id, 50f, BuiltInComponentCatalog.Instance);
        Assert.False(CornerOrnamentPlacement.IsDefault(document, ornament, definition));

        Assert.True(PlateComponentEditor.ResetCornerOrnamentPlacement(document, ornament.Id, BuiltInComponentCatalog.Instance));

        Assert.True(CornerOrnamentPlacement.IsDefault(document, ornament, definition));
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), ornament.Color);
        Assert.Equal(0.5f, ornament.Opacity);
        Assert.Equal(15f, ornament.RotationDegrees);
        Assert.Equal(CornerMask.All & ~CornerMask.TopRight, ornament.Corners);
        Assert.False(PlateComponentEditor.ResetCornerOrnamentPlacement(document, ornament.Id, BuiltInComponentCatalog.Instance));
    }

    [Fact]
    public void AdvancedReset_PutsAnOrnamentAtTheSameDefaults()
    {
        var (document, ornament, definition) = NewOrnament(Bracket);
        PlateComponentEditor.Update(document, ornament.Id, c =>
        {
            c.Scale = 3f;
            c.Offset = new Vector2(100, -40);
            c.RotationDegrees = 30f;
        });

        Assert.True(PlateComponentEditor.ResetTransform(document, ornament.Id));
        Assert.True(CornerOrnamentPlacement.IsDefault(document, ornament, definition));
        Assert.Equal(0f, ornament.RotationDegrees);
    }

    [Fact]
    public void OnlyCornerOrnamentsOfAKnownStyle_AreEdited()
    {
        var document = ComponentDocuments.WithAnchors();
        var divider = PlateComponentEditor.Add(document, BuiltInComponentCatalog.Find(BuiltInComponentCatalog.DividerDiamond)!);
        var unknown = new PlateComponent { Kind = PlateComponentKind.CornerOrnament, DefinitionId = "af.corner-ornament.from-a-newer-build", Scale = 1.5f, Offset = new Vector2(2, 2) };
        document.Components!.Add(unknown);

        Assert.False(PlateComponentEditor.SetCornerOrnamentScale(document, divider.Id, 2f, BuiltInComponentCatalog.Instance));
        Assert.False(PlateComponentEditor.SetCornerOrnamentEdgeDistance(document, divider.Id, 2f, BuiltInComponentCatalog.Instance));
        Assert.False(PlateComponentEditor.ResetCornerOrnamentPlacement(document, divider.Id, BuiltInComponentCatalog.Instance));
        Assert.False(PlateComponentEditor.SetCornerOrnamentScale(document, unknown.Id, 2f, BuiltInComponentCatalog.Instance));
        Assert.False(PlateComponentEditor.ResetCornerOrnamentPlacement(document, unknown.Id, BuiltInComponentCatalog.Instance));
        Assert.Equal(1f, divider.Scale);
        Assert.Equal(1.5f, unknown.Scale);
        Assert.Equal(new Vector2(2, 2), unknown.Offset);

        Assert.True(PlateComponentEditor.ResetTransform(document, unknown.Id));
        Assert.Equal(1f, unknown.Scale);
        Assert.Equal(Vector2.Zero, unknown.Offset);
    }

    // ---- Saving, reopening, the Advanced editor, undo ----------------------------------------------

    [Fact]
    public async Task TheChosenSizeAndDistance_SurviveSavingAndReopening_AndShowInAdvanced()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, Sakura);
        var ornament = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.CornerOrnament)!;
        var definition = BuiltInComponentCatalog.Find(Sakura)!;

        harness.Session.SetCornerOrnamentScale(ornament.Id, 1.8f, continuous: true);
        harness.Session.SetCornerOrnamentScale(ornament.Id, 2f, continuous: true);
        harness.Session.CommitPendingDocumentEdit();
        harness.Session.SetCornerOrnamentEdgeDistance(ornament.Id, 2f, continuous: false);
        var scale = ornament.Scale;
        var offset = ornament.Offset;
        var rects = CornerRects(harness.Document, ornament);
        Assert.True(await harness.Session.SaveProfileAsync());

        // Advanced shows the very same values: they are the component's own Scale and Offset.
        var reloaded = ComponentDocuments.RoundTrip(harness.Document);
        var again = PlateComponentEditor.FindSlot(reloaded, PlateComponentKind.CornerOrnament)!;
        Assert.Equal(scale, again.Scale);
        Assert.Equal(offset, again.Offset);
        Assert.Equal(2f, CornerOrnamentPlacement.EdgeDistance(reloaded, again, definition), Precision);
        Assert.Equal(rects, CornerRects(reloaded, again));

        using var reopened = await BasicHarness.OpenDocumentAsync(reloaded);
        reopened.SimulateBasicFrame();
        var shown = PlateComponentEditor.FindSlot(reopened.Document, PlateComponentKind.CornerOrnament)!;
        Assert.Equal(scale, shown.Scale);
        Assert.Equal(offset, shown.Offset);
        Assert.False(reopened.Session.IsDirty);
    }

    [Fact]
    public async Task ADrag_IsOneUndoStep()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.CornerOrnament, Bracket);
        var before = harness.Json();
        var id = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.CornerOrnament)!.Id;

        harness.Session.SetCornerOrnamentEdgeDistance(id, 20f, continuous: true);
        harness.Session.SetCornerOrnamentEdgeDistance(id, 30f, continuous: true);
        harness.Session.CommitPendingDocumentEdit();
        Assert.NotEqual(before, harness.Json());

        harness.Session.Undo();
        Assert.Equal(before, harness.Json());
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static ProfileDocument Canvas(float width, float height)
    {
        var document = ComponentDocuments.WithAnchors();
        document.CanvasWidth = width;
        document.CanvasHeight = height;
        return document;
    }

    private static (ProfileDocument Document, PlateComponent Ornament, ComponentDefinition Definition) NewOrnament(string definitionId)
    {
        var document = ComponentDocuments.WithAnchors();
        PlateComponentEditor.SetSlot(document, PlateComponentKind.CornerOrnament, definitionId, BuiltInComponentCatalog.Instance);
        return (document, PlateComponentEditor.FindSlot(document, PlateComponentKind.CornerOrnament)!, BuiltInComponentCatalog.Find(definitionId)!);
    }

    /// <summary>The ornament's drawn boxes, top-left, top-right, bottom-left, bottom-right (the selected ones).</summary>
    private static List<ElementRect> CornerRects(ProfileDocument document, PlateComponent ornament) =>
        ComponentDocuments.Plan(document).Where(s => ReferenceEquals(s.Component, ornament)).Select(s => s.Placement.Rect).ToList();
}
