using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Advanced placement of Name Backings and Dividers is independent of the name (the Celestial
/// Sakura showcase acceptance failure: moving the name dragged its nameplate along). The Advanced
/// editor fixes their anchor where the name is when they are added; Basic slots keep following it.
/// </summary>
public class NameBackingPlacementTests
{
    public static IEnumerable<object[]> FixableIds() =>
    [
        [BuiltInComponentCatalog.NameBackingBar],
        [BuiltInComponentCatalog.NameBackingCelestialSakura],
        [BuiltInComponentCatalog.DividerLine],
        [BuiltInComponentCatalog.DividerCelestialSakuraOrnate],
    ];

    [Fact]
    public async Task AdvancedAdd_StartsCenteredOnTheMeasuredName_AsTheRendererWouldFollowIt()
    {
        using var harness = await AdvancedHarnessAsync();
        var name = Name(harness);

        var id = harness.Session.AddComponent(BuiltInComponentCatalog.NameBackingBar)!.Value;
        var component = PlateComponentEditor.Find(harness.Document, id)!;

        var anchor = ComponentPaintPlan.FixedAnchorOf(component);
        Assert.NotNull(anchor);
        Assert.Equal(ComponentPaintPlan.ContentAnchor(harness.Document, Drawn(harness.Document), PlateComponentKind.NameBacking, Measure(harness)), anchor);

        // Exactly where a following backing is drawn at this moment: no jump.
        var rect = Placement(harness, id);
        var follower = ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar);
        harness.Document.Components!.Add(follower);
        Assert.Equal(Placement(harness, follower.Id), rect);

        // Around the name's text, narrower than its box (measured, like the renderer).
        Assert.True(rect.Position.Y < name.Position.Y && rect.Position.Y + rect.Size.Y > name.Position.Y + name.Size.Y);
        Assert.True(rect.Size.X < name.Size.X);
    }

    [Theory]
    [MemberData(nameof(FixableIds))]
    public async Task MovingOrRetypingTheName_DoesNotMoveTheAdvancedDecoration(string definitionId)
    {
        using var harness = await AdvancedHarnessAsync();
        var id = harness.Session.AddComponent(definitionId)!.Value;
        var before = Placement(harness, id);
        var name = Name(harness);

        harness.Session.BeginOrContinueEdit(name.Id, e =>
        {
            e.Position += new Vector2(120, 40);
            e.Size += new Vector2(-100, 10);
        });
        harness.Session.CommitPendingEdit();
        harness.Session.BeginOrContinueEdit(name.Id, e => ((TextProfileElement)e).Text = "A Much Longer Character Name");
        harness.Session.CommitPendingEdit();

        Assert.Equal(before, Placement(harness, id));
    }

    [Theory]
    [MemberData(nameof(FixableIds))]
    public async Task MovingTheDecoration_DoesNotMoveTheName(string definitionId)
    {
        using var harness = await AdvancedHarnessAsync();
        var id = harness.Session.AddComponent(definitionId)!.Value;
        var name = Name(harness);
        var nameBefore = (name.Position, name.Size);
        var before = Placement(harness, id);

        harness.Session.EditComponent(id, c => c.Offset = new Vector2(-30, 25), continuous: false);

        Assert.Equal(nameBefore, (name.Position, name.Size));
        var after = Placement(harness, id);
        Assert.Equal(before.Position + new Vector2(-30, 25), after.Position);
        Assert.Equal(before.Size, after.Size);
    }

    [Fact]
    public async Task SaveAndReopen_KeepsBothIndependentPlacements()
    {
        using var harness = await AdvancedHarnessAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.NameBackingCelestialSakura)!.Value;
        var name = Name(harness);
        harness.Session.BeginOrContinueEdit(name.Id, e => e.Position += new Vector2(60, 12));
        harness.Session.CommitPendingEdit();
        harness.Session.EditComponent(id, c => c.Offset = new Vector2(0, -6), continuous: false);
        var backing = Placement(harness, id);
        var namePosition = name.Position;

        Assert.True(await harness.Session.SaveProfileAsync());
        var reopened = harness.Library.OpenDocumentForEditing(harness.PlateId);

        Assert.True(PlateComponent.ListsEqual(harness.Document.Components, reopened.Components));
        Assert.Equal(namePosition, reopened.Elements.Single(e => e.Role == ProfileElementRole.BasicName).Position);
        Assert.Equal(backing, Placement(reopened, id, Measure(harness)));
        var saved = JsonNode.Parse(harness.Fixture.ReadPlateJson(harness.PlateId))!["Components"]![0]!;
        Assert.NotNull(saved["FixedAnchorPosition"]);
        Assert.NotNull(saved["FixedAnchorSize"]);
    }

    [Fact]
    public async Task BasicSlot_StillFollowsTheName()
    {
        using var harness = await AdvancedHarnessAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.NameBacking, BuiltInComponentCatalog.NameBackingBar);
        var component = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.NameBacking)!;
        Assert.Null(component.FixedAnchorPosition);
        Assert.Null(component.FixedAnchorSize);

        var before = Placement(harness, component.Id);
        harness.Session.BeginOrContinueEdit(Name(harness).Id, e => e.Position += new Vector2(0, 30));
        harness.Session.CommitPendingEdit();

        Assert.NotEqual(before, Placement(harness, component.Id));

        // Its JSON is exactly what earlier builds wrote.
        var json = ComponentDocuments.ComponentJson(harness.Document, 0);
        Assert.False(json.ContainsKey("FixedAnchorPosition"));
        Assert.False(json.ContainsKey("FixedAnchorSize"));
    }

    [Fact]
    public async Task FollowsTheName_Toggle_FixesWithoutAJump_AndFollowsAgain_OneUndoStepEach()
    {
        using var harness = await AdvancedHarnessAsync();
        harness.Session.SetComponentSlot(PlateComponentKind.NameBacking, BuiltInComponentCatalog.NameBackingCelestialSakura);
        var id = PlateComponentEditor.FindSlot(harness.Document, PlateComponentKind.NameBacking)!.Id;
        var followed = Placement(harness, id);

        harness.Session.SetComponentFollowsContent(id, follows: false);
        Assert.Equal(followed, Placement(harness, id));
        Assert.NotNull(ComponentPaintPlan.FixedAnchorOf(PlateComponentEditor.Find(harness.Document, id)!));

        harness.Session.BeginOrContinueEdit(Name(harness).Id, e => e.Position += new Vector2(0, 50));
        harness.Session.CommitPendingEdit();
        Assert.Equal(followed, Placement(harness, id));

        harness.Session.SetComponentFollowsContent(id, follows: true);
        Assert.NotEqual(followed, Placement(harness, id)); // back on the (moved) name
        Assert.Null(ComponentPaintPlan.FixedAnchorOf(PlateComponentEditor.Find(harness.Document, id)!));

        harness.Session.Undo();
        Assert.NotNull(ComponentPaintPlan.FixedAnchorOf(PlateComponentEditor.Find(harness.Document, id)!));
    }

    [Fact]
    public async Task PortraitFrames_KeepFollowingThePortrait()
    {
        using var harness = await AdvancedHarnessAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.PortraitFrameCelestialSakura)!.Value;

        Assert.Null(PlateComponentEditor.Find(harness.Document, id)!.FixedAnchorPosition);
        Assert.False(PlateComponentEditor.SetFixedAnchor(harness.Document, id, new ElementRect(Vector2.Zero, new Vector2(10, 10))));
        Assert.False(PlateComponentEditor.CanFixAnchor(PlateComponentKind.PortraitFrame));
        Assert.Equal([PlateComponentKind.NameBacking, PlateComponentKind.Divider], Enum.GetValues<PlateComponentKind>().Where(PlateComponentEditor.CanFixAnchor));
    }

    [Fact]
    public async Task ProportionalCanvasResize_ScalesTheFixedAnchorWithTheElements_AndUndoes()
    {
        using var harness = await AdvancedHarnessAsync();
        var id = harness.Session.AddComponent(BuiltInComponentCatalog.NameBackingBar)!.Value;
        var component = PlateComponentEditor.Find(harness.Document, id)!;
        var (position, size) = (component.FixedAnchorPosition!.Value, component.FixedAnchorSize!.Value);

        harness.Session.ApplyCanvasResize(1600, 900, scaleContentsProportionally: true);
        Assert.Equal(position * 1.25f, component.FixedAnchorPosition);
        Assert.Equal(size * 1.25f, component.FixedAnchorSize);

        harness.Session.Undo();
        Assert.Equal(position, component.FixedAnchorPosition);
        Assert.Equal(size, component.FixedAnchorSize);
    }

    [Fact]
    public void AnUnusableFixedAnchor_Follows_AndBoundingNormalizesHalves()
    {
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar);
        document.Components = [component];
        var following = Assert.Single(ComponentDocuments.Plan(document), s => !s.IsElement).Placement.Rect;

        foreach (var (position, size) in new (Vector2?, Vector2?)[] { (new Vector2(5, 5), null), (new Vector2(5, 5), Vector2.Zero), (new Vector2(float.NaN, 5), new Vector2(10, 10)) })
        {
            component.FixedAnchorPosition = position;
            component.FixedAnchorSize = size;
            Assert.Equal(following, Assert.Single(ComponentDocuments.Plan(document), s => !s.IsElement).Placement.Rect);
        }

        component.FixedAnchorPosition = new Vector2(5, 5);
        component.FixedAnchorSize = null;
        PlateComponentEditor.Bound(component);
        Assert.Null(component.FixedAnchorPosition);

        component.FixedAnchorPosition = new Vector2(1e9f, -1e9f);
        component.FixedAnchorSize = new Vector2(-20, 40);
        PlateComponentEditor.Bound(component);
        Assert.Equal(new Vector2(PlateComponentLimits.MaxOffset, -PlateComponentLimits.MaxOffset), component.FixedAnchorPosition);
        Assert.Equal(new Vector2(0, 40), component.FixedAnchorSize);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static async Task<BasicHarness> AdvancedHarnessAsync()
    {
        var harness = await BasicHarness.NewClassicAsync();
        harness.Session.IdentityMeasurer = harness.Measurer; // as the plugin wires it
        return harness;
    }

    private static TextProfileElement Name(BasicHarness harness) =>
        (TextProfileElement)harness.Document.Elements.Single(e => e.Role == ProfileElementRole.BasicName);

    private static Func<TextProfileElement, float?> Measure(BasicHarness harness) =>
        element => harness.Measurer.TryMeasureNaturalWidth(element, out var width) ? width : null;

    private static List<ProfileElement> Drawn(ProfileDocument document)
    {
        var drawn = new List<ProfileElement>();
        ProfilePaintOrder.Fill(document, drawn, includeHidden: false);
        return drawn;
    }

    /// <summary>The component's placement as the renderer paints it (measured text, like ProfileRenderer).</summary>
    private static ElementRect Placement(BasicHarness harness, Guid componentId) => Placement(harness.Document, componentId, Measure(harness));

    private static ElementRect Placement(ProfileDocument document, Guid componentId, Func<TextProfileElement, float?> measure)
    {
        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, Drawn(document), BuiltInComponentCatalog.Instance, steps, measure);
        return steps.Single(s => s.Component?.Id == componentId).Placement.Rect;
    }
}
