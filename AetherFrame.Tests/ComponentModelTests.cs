using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>Documents with Components, for the pure model, persistence and paint-plan tests.</summary>
internal static class ComponentDocuments
{
    internal static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A portrait (Z 0), a name (Z 1), a title (Z 2), a heading (Z 3) and a free caption (Z 4).</summary>
    internal static ProfileDocument WithAnchors()
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Components", Now);
        document.Elements.Add(new ImageProfileElement { Role = ProfileElementRole.BasicPortrait, AssetId = Guid.NewGuid(), Position = new Vector2(40, 40), Size = new Vector2(400, 640), ZIndex = 0 });
        document.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicName, Text = "Name", Position = new Vector2(480, 60), Size = new Vector2(700, 60), ZIndex = 1 });
        document.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicTitle, Text = "Title", Position = new Vector2(480, 125), Size = new Vector2(700, 30), ZIndex = 2 });
        document.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicWorldHeading, Text = "HOME WORLD", Position = new Vector2(480, 196), Size = new Vector2(360, 24), ZIndex = 3 });
        document.Elements.Add(new TextProfileElement { Role = ProfileElementRole.BasicWorld, Text = "Odin", Position = new Vector2(480, 220), Size = new Vector2(360, 34), ZIndex = 4 });
        document.Elements.Add(new TextProfileElement { Text = "Caption", Position = new Vector2(900, 600), Size = new Vector2(200, 40), ZIndex = 5 });
        return document;
    }

    internal static PlateComponent Of(string definitionId, int layerOrder = 0)
    {
        var definition = BuiltInComponentCatalog.Find(definitionId)!;
        return new PlateComponent { Kind = definition.Kind, DefinitionId = definition.Id, LayerOrder = layerOrder };
    }

    /// <summary>One Component of every V1 type.</summary>
    internal static List<PlateComponent> OneOfEach() =>
    [
        Of(BuiltInComponentCatalog.PlateFrameDouble),
        Of(BuiltInComponentCatalog.SectionHeaderUnderline),
        Of(BuiltInComponentCatalog.PortraitOverlayFade),
        Of(BuiltInComponentCatalog.DividerDiamond),
        Of(BuiltInComponentCatalog.NameBackingRibbon),
        Of(BuiltInComponentCatalog.CornerOrnamentBracket),
        Of(BuiltInComponentCatalog.PortraitFrameBrackets),
    ];

    internal static List<PaintStep> Plan(ProfileDocument document)
    {
        var drawn = new List<ProfileElement>();
        foreach (var element in document.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex))
        {
            drawn.Add(element);
        }

        var steps = new List<PaintStep>();
        ComponentPaintPlan.Build(document, drawn, BuiltInComponentCatalog.Instance, steps);
        return steps;
    }

    internal static ProfileDocument RoundTrip(ProfileDocument document)
    {
        var json = PlateDocuments.ToJson(document).ToJsonString();
        var reloaded = PlateDocuments.Materialize(JsonNode.Parse(json)!.AsObject());
        return reloaded;
    }

    internal static JsonObject ComponentJson(ProfileDocument document, int index) =>
        (JsonObject)PlateDocuments.ToJson(document)["Components"]![index]!;
}

public class ComponentCatalogTests
{
    /// <summary>Frozen forever: a shipped id may never change meaning or disappear.</summary>
    private static readonly (string Id, PlateComponentKind Kind)[] Shipped =
    [
        ("af.plate-frame.line", PlateComponentKind.PlateFrame),
        ("af.plate-frame.double", PlateComponentKind.PlateFrame),
        ("af.plate-frame.notched", PlateComponentKind.PlateFrame),
        ("af.portrait-frame.line", PlateComponentKind.PortraitFrame),
        ("af.portrait-frame.double", PlateComponentKind.PortraitFrame),
        ("af.portrait-frame.brackets", PlateComponentKind.PortraitFrame),
        ("af.portrait-overlay.fade", PlateComponentKind.PortraitOverlay),
        ("af.portrait-overlay.vignette", PlateComponentKind.PortraitOverlay),
        ("af.portrait-overlay.image", PlateComponentKind.PortraitOverlay),
        ("af.name-backing.bar", PlateComponentKind.NameBacking),
        ("af.name-backing.ribbon", PlateComponentKind.NameBacking),
        ("af.name-backing.fade", PlateComponentKind.NameBacking),
        ("af.corner-ornament.bracket", PlateComponentKind.CornerOrnament),
        ("af.corner-ornament.diamond", PlateComponentKind.CornerOrnament),
        ("af.corner-ornament.astrolabe-pivot", PlateComponentKind.CornerOrnament),
        ("af.divider.line", PlateComponentKind.Divider),
        ("af.divider.diamond", PlateComponentKind.Divider),
        ("af.section-header.underline", PlateComponentKind.SectionHeader),
        ("af.section-header.tick", PlateComponentKind.SectionHeader),
        ("af.background.celestial-sakura", PlateComponentKind.Background),
        ("af.plate-frame.celestial-sakura", PlateComponentKind.PlateFrame),
        ("af.portrait-frame.celestial-sakura", PlateComponentKind.PortraitFrame),
        ("af.name-backing.celestial-sakura", PlateComponentKind.NameBacking),
        ("af.divider.celestial-sakura-ornate", PlateComponentKind.Divider),
        ("af.divider.celestial-sakura-slim", PlateComponentKind.Divider),
        ("af.corner-ornament.celestial-sakura", PlateComponentKind.CornerOrnament),
    ];

    [Fact]
    public void ShippedIds_AreStable_AndKeepTheirKind()
    {
        foreach (var (id, kind) in Shipped)
        {
            var definition = BuiltInComponentCatalog.Find(id);
            Assert.NotNull(definition);
            Assert.Equal(kind, definition!.Kind);
        }

        Assert.Equal(Shipped.Length, BuiltInComponentCatalog.All.Count);
    }

    [Fact]
    public void KindValues_AreFrozen()
    {
        Assert.Equal(0, (int)PlateComponentKind.Unknown);
        Assert.Equal(1, (int)PlateComponentKind.PlateFrame);
        Assert.Equal(2, (int)PlateComponentKind.PortraitFrame);
        Assert.Equal(3, (int)PlateComponentKind.PortraitOverlay);
        Assert.Equal(4, (int)PlateComponentKind.NameBacking);
        Assert.Equal(5, (int)PlateComponentKind.CornerOrnament);
        Assert.Equal(6, (int)PlateComponentKind.Divider);
        Assert.Equal(7, (int)PlateComponentKind.SectionHeader);
        Assert.Equal(8, (int)PlateComponentKind.Background);
    }

    [Fact]
    public void Ids_AreUnique_WellFormed_AndBounded()
    {
        var pattern = new Regex("^af\\.[a-z-]+\\.[a-z-]+$");
        Assert.Equal(BuiltInComponentCatalog.All.Count, BuiltInComponentCatalog.All.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var definition in BuiltInComponentCatalog.All)
        {
            Assert.Matches(pattern, definition.Id);
            Assert.True(definition.Id.Length <= PlateComponentLimits.MaxDefinitionIdLength);
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
        }
    }

    [Fact]
    public void EveryV1Type_HasABasicSelectableBuiltIn()
    {
        foreach (var kind in PlateComponentEditor.BasicSlots.Concat(PlateComponentEditor.BasicDecorations))
        {
            Assert.Contains(BuiltInComponentCatalog.OfKind(kind), d => !d.RequiresAsset);
        }

        Assert.Equal(7, PlateComponentEditor.BasicSlots.Length + PlateComponentEditor.BasicDecorations.Length);
    }

    [Fact]
    public void Lookup_IsExactAndCaseSensitive_NeverByDisplayName()
    {
        Assert.Null(BuiltInComponentCatalog.Find("AF.PLATE-FRAME.LINE"));
        Assert.Null(BuiltInComponentCatalog.Find("Line"));
        Assert.Null(BuiltInComponentCatalog.Find(""));
        Assert.Null(BuiltInComponentCatalog.Find(null));
        Assert.Null(BuiltInComponentCatalog.Find(" af.plate-frame.line"));
    }

    [Fact]
    public void Rendering_DependsOnShape_NotDisplayName()
    {
        var document = ComponentDocuments.WithAnchors();
        var original = BuiltInComponentCatalog.Find(BuiltInComponentCatalog.PlateFrameNotched)!;
        var renamed = original with { Name = "Something Else", Description = "Different text" };
        var component = ComponentDocuments.Of(original.Id);
        var placement = new ComponentPlacement(new ElementRect(new Vector2(10, 10), new Vector2(500, 300)), 0f, false, false);

        var a = new List<ComponentPrimitive>();
        var b = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, component, original, placement, a);
        ComponentGeometry.Build(document, component, renamed, placement, b);

        Assert.NotEmpty(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void EveryDefinition_DrawsSomething_WithFiniteGeometry()
    {
        var document = ComponentDocuments.WithAnchors();
        var placement = new ComponentPlacement(new ElementRect(new Vector2(20, 20), new Vector2(400, 200)), 15f, true, false);
        foreach (var definition in BuiltInComponentCatalog.All)
        {
            var component = ComponentDocuments.Of(definition.Id);
            component.AssetId = definition.RequiresAsset ? Guid.NewGuid() : null;
            var output = new List<ComponentPrimitive>();
            ComponentGeometry.Build(document, component, definition, placement, output);

            Assert.NotEmpty(output);
            Assert.True(output.Count <= 64, definition.Id);
            Assert.All(output, p => Assert.True(Finite(p.A) && Finite(p.B) && Finite(p.C) && Finite(p.D) && p.Color.W > 0f, definition.Id));
        }
    }

    [Fact]
    public void DefaultColors_FollowTheTheme()
    {
        var document = ComponentDocuments.WithAnchors();
        var definition = BuiltInComponentCatalog.Find(BuiltInComponentCatalog.PlateFrameLine)!;
        document.BasicPlate = new BasicPlateSettings { ThemeId = ProfileThemePresets.All[0].Id };
        var first = definition.DefaultColor(document);
        document.BasicPlate.ThemeId = ProfileThemePresets.All[5].Id;
        var second = definition.DefaultColor(document);

        Assert.Equal(ProfileThemePresets.All[5].AccentTextColor with { W = definition.DefaultAlpha }, second);
        Assert.NotEqual(first, second);
    }

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}

public class ComponentSerializationTests
{
    [Fact]
    public void PlateWithoutComponents_WritesNoComponentsProperty()
    {
        var document = ComponentDocuments.WithAnchors();
        var json = PlateDocuments.ToJson(document);
        Assert.False(json.ContainsKey("Components"));

        var reloaded = ComponentDocuments.RoundTrip(document);
        Assert.Null(reloaded.Components);
    }

    [Fact]
    public void Components_RoundTrip_EveryValue()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();
        var custom = document.Components[0];
        custom.Color = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
        custom.Opacity = 0.5f;
        custom.Offset = new Vector2(-12.5f, 7f);
        custom.Scale = 1.25f;
        custom.RotationDegrees = -30f;
        custom.LayerOrder = 3;
        custom.Visible = false;
        document.Components.Add(new PlateComponent { Kind = PlateComponentKind.PortraitOverlay, DefinitionId = BuiltInComponentCatalog.PortraitOverlayImage, AssetId = Guid.NewGuid() });

        var reloaded = ComponentDocuments.RoundTrip(document);

        Assert.True(PlateComponent.ListsEqual(document.Components, reloaded.Components));
        Assert.Null(reloaded.UnrecognizedComponents);
    }

    [Fact]
    public void Json_UsesStableNames_AndNumericKind()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar)];
        var json = ComponentDocuments.ComponentJson(document, 0);

        Assert.Equal(4, json["Kind"]!.GetValue<int>());
        Assert.Equal("af.name-backing.bar", json["DefinitionId"]!.GetValue<string>());
        foreach (var name in new[] { "Id", "Visible", "Color", "Opacity", "Offset", "Scale", "RotationDegrees", "LayerOrder", "AssetId" })
        {
            Assert.True(json.ContainsKey(name), name);
        }
    }

    [Fact]
    public void Json_HoldsNoPathsOrIdentity()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();
        var text = PlateDocuments.ToJson(document)["Components"]!.ToJsonString();

        Assert.DoesNotContain("\\\\", text);
        Assert.DoesNotContain(":/", text);
        Assert.DoesNotContain("ContentId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComponentIds_AreRepaired_WhenMissingOrDuplicated_WithoutReordering()
    {
        var shared = Guid.NewGuid();
        var document = ComponentDocuments.WithAnchors();
        document.Components =
        [
            new PlateComponent { Id = shared, Kind = PlateComponentKind.PlateFrame, DefinitionId = BuiltInComponentCatalog.PlateFrameLine },
            new PlateComponent { Id = shared, Kind = PlateComponentKind.Divider, DefinitionId = BuiltInComponentCatalog.DividerLine },
            new PlateComponent { Id = Guid.Empty, Kind = PlateComponentKind.NameBacking, DefinitionId = BuiltInComponentCatalog.NameBackingBar },
        ];

        var reloaded = ComponentDocuments.RoundTrip(document);

        Assert.Equal(shared, reloaded.Components![0].Id);
        Assert.Equal(3, reloaded.Components.Select(c => c.Id).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, reloaded.Components.Select(c => c.Id));
        Assert.Equal([PlateComponentKind.PlateFrame, PlateComponentKind.Divider, PlateComponentKind.NameBacking], reloaded.Components.Select(c => c.Kind));
    }
}

public class ComponentForwardCompatibilityTests
{
    private static JsonObject PlateWithComponents(params JsonNode?[] components)
    {
        var json = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        var array = new JsonArray();
        foreach (var component in components)
        {
            array.Add(component);
        }

        json["Components"] = array;
        return json;
    }

    private static JsonObject Valid(string definitionId) =>
        JsonSerializer.SerializeToNode(ComponentDocuments.Of(definitionId), JsonOptions.Default)!.AsObject();

    [Fact]
    public void UnknownFields_ArePreserved_ThroughEditAndSave()
    {
        var component = Valid(BuiltInComponentCatalog.PlateFrameLine);
        component["FutureGlow"] = JsonNode.Parse("""{ "Strength": 0.4, "Mode": "soft" }""");
        var document = PlateDocuments.Materialize(PlateWithComponents(component));

        PlateComponentEditor.Update(document, document.Components![0].Id, c => c.Opacity = 0.3f);
        var saved = ComponentDocuments.ComponentJson(document, 0);

        Assert.Equal("soft", saved["FutureGlow"]!["Mode"]!.GetValue<string>());
        Assert.Equal(0.3f, saved["Opacity"]!.GetValue<float>());
    }

    [Fact]
    public void UnknownKind_IsKept_NotDrawn_AndDoesNotAffectOthers()
    {
        var future = Valid(BuiltInComponentCatalog.PlateFrameLine);
        future["Kind"] = 42;
        future["DefinitionId"] = "af.hologram.sparkle";
        var document = PlateDocuments.Materialize(PlateWithComponents(future, Valid(BuiltInComponentCatalog.PlateFrameLine)));

        Assert.Equal(ComponentStatus.UnknownKind, ComponentPaintPlan.Resolve(document.Components![0], BuiltInComponentCatalog.Instance, out _));
        var plan = ComponentDocuments.Plan(document);
        Assert.DoesNotContain(plan, s => ReferenceEquals(s.Component, document.Components[0]));
        Assert.Contains(plan, s => ReferenceEquals(s.Component, document.Components[1]));

        var saved = ComponentDocuments.ComponentJson(document, 0);
        Assert.Equal(42, saved["Kind"]!.GetValue<int>());
        Assert.Equal("af.hologram.sparkle", saved["DefinitionId"]!.GetValue<string>());
    }

    [Fact]
    public void MissingDefinition_IsKept_NotDrawn_AndThePlateStillDraws()
    {
        var missing = Valid(BuiltInComponentCatalog.PortraitFrameLine);
        missing["DefinitionId"] = "af.portrait-frame.from-the-future";
        var document = PlateDocuments.Materialize(PlateWithComponents(missing));

        Assert.Equal(ComponentStatus.MissingDefinition, ComponentPaintPlan.Resolve(document.Components![0], BuiltInComponentCatalog.Instance, out _));
        var plan = ComponentDocuments.Plan(document);
        Assert.Equal(document.Elements.Count, plan.Count);
        Assert.All(plan, s => Assert.True(s.IsElement));
        Assert.Equal("af.portrait-frame.from-the-future", ComponentDocuments.ComponentJson(document, 0)["DefinitionId"]!.GetValue<string>());
    }

    [Fact]
    public void DefinitionOfAnotherKind_IsNeverReinterpreted()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [new PlateComponent { Kind = PlateComponentKind.NameBacking, DefinitionId = BuiltInComponentCatalog.PlateFrameLine }];

        Assert.Equal(ComponentStatus.KindMismatch, ComponentPaintPlan.Resolve(document.Components[0], BuiltInComponentCatalog.Instance, out _));
        Assert.All(ComponentDocuments.Plan(document), s => Assert.True(s.IsElement));
    }

    [Fact]
    public void ImageComponent_WithoutImage_IsNotDrawn()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayImage)];

        Assert.Equal(ComponentStatus.MissingImage, ComponentPaintPlan.Resolve(document.Components[0], BuiltInComponentCatalog.Instance, out _));
        Assert.All(ComponentDocuments.Plan(document), s => Assert.True(s.IsElement));
    }

    public static IEnumerable<object[]> MalformedComponents() =>
    [
        ["""{ "Kind": 1, "DefinitionId": "af.plate-frame.line", "Opacity": "very" }"""],
        ["""{ "Kind": "PlateFrame", "DefinitionId": "af.plate-frame.line" }"""],
        ["""{ "Kind": 1, "DefinitionId": null }"""],
        ["""{ "Kind": 1, "DefinitionId": 17 }"""],
        ["""{ "Kind": 1, "DefinitionId": "af.plate-frame.line", "Offset": [1, 2] }"""],
        ["""{ "Kind": 1, "DefinitionId": "af.plate-frame.line", "Id": "not-a-guid" }"""],
        ["""{ "Kind": 1, "DefinitionId": "af.plate-frame.line", "LayerOrder": 1e40 }"""],
        ["""{ "Kind": 1, "DefinitionId": "af.plate-frame.line", "Visible": "yes" }"""],
        ["""{ "Kind": 1, "DefinitionId": "af.plate-frame.line", "AssetId": 5 }"""],
        ["\"just a string\""],
        ["12"],
        ["[]"],
        ["null"],
        ["true"],
    ];

    [Theory]
    [MemberData(nameof(MalformedComponents))]
    public void MalformedComponent_IsIsolated_AndKeptVerbatim(string malformedJson)
    {
        var good = Valid(BuiltInComponentCatalog.DividerLine);
        var raw = PlateWithComponents(good, JsonNode.Parse(malformedJson), Valid(BuiltInComponentCatalog.PlateFrameLine));

        var document = PlateDocuments.Materialize(raw);

        // Everything else loads: the elements, and both good Components, in order.
        Assert.Equal(6, document.Elements.Count);
        Assert.Equal([BuiltInComponentCatalog.DividerLine, BuiltInComponentCatalog.PlateFrameLine], document.Components!.Select(c => c.DefinitionId));
        Assert.Single(document.UnrecognizedComponents!);

        // Written back unchanged on save.
        var saved = PlateDocuments.ToJson(document)["Components"]!.AsArray();
        Assert.Equal(3, saved.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(malformedJson), saved[2]));

        // And survives a second load/save cycle as well.
        var again = PlateDocuments.ToJson(PlateDocuments.Materialize(PlateDocuments.ToJson(document)))["Components"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(malformedJson), again[2]));
    }

    [Fact]
    public void NonArrayComponents_IsKept_UntilAComponentIsAdded()
    {
        var raw = PlateDocuments.ToJson(ComponentDocuments.WithAnchors());
        raw["Components"] = JsonNode.Parse("""{ "from": "the future" }""");

        var document = PlateDocuments.Materialize(raw);
        Assert.Null(document.Components);
        Assert.True(JsonNode.DeepEquals(raw["Components"], PlateDocuments.ToJson(document)["Components"]));

        PlateComponentEditor.SetSlot(document, PlateComponentKind.PlateFrame, BuiltInComponentCatalog.PlateFrameLine, BuiltInComponentCatalog.Instance);
        var saved = PlateDocuments.ToJson(document)["Components"];
        Assert.IsType<JsonArray>(saved);
        Assert.Single(saved!.AsArray());
    }

    [Fact]
    public void OlderBuildPlate_LoadsWithNoComponents()
    {
        var plateId = Guid.NewGuid();
        var document = PlateDocuments.Materialize(JsonNode.Parse(LegacyData.VersionOneDocument(plateId, 0))!.AsObject());

        Assert.Null(document.Components);
        Assert.All(ComponentDocuments.Plan(document), s => Assert.True(s.IsElement));
    }

    [Fact]
    public void ComponentsProperty_IsPreservedByOlderBuilds_AsTopLevelExtensionData()
    {
        // What an older AetherFrame (no Components) does: the whole property lands in the
        // document's extension data and is written back verbatim.
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();
        var json = PlateDocuments.ToJson(document);

        var asOlderBuild = JsonSerializer.Deserialize<OlderBuildDocument>(json.ToJsonString(), JsonOptions.Default)!;
        var rewritten = JsonSerializer.SerializeToNode(asOlderBuild, JsonOptions.Default)!.AsObject();

        Assert.True(JsonNode.DeepEquals(json["Components"], rewritten["Components"]));
    }

    [Fact]
    public void AdversarialMutations_NeverFailTheDocument()
    {
        var random = new Random(1234);
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();
        var baseJson = PlateDocuments.ToJson(document);
        JsonNode?[] junk = [null, JsonValue.Create("x"), JsonValue.Create(-1e30), JsonValue.Create(true), new JsonArray(), new JsonObject(), JsonValue.Create(int.MaxValue)];

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var raw = (JsonObject)baseJson.DeepClone();
            var components = raw["Components"]!.AsArray();
            var target = components[random.Next(components.Count)]!.AsObject();
            var keys = target.Select(p => p.Key).ToArray();
            var key = keys[random.Next(keys.Length)];
            target[key] = junk[random.Next(junk.Length)]?.DeepClone();

            var loaded = PlateDocuments.Materialize(raw);
            Assert.Equal(document.Elements.Count, loaded.Elements.Count);
            Assert.Equal(components.Count, (loaded.Components?.Count ?? 0) + (loaded.UnrecognizedComponents?.Count ?? 0));

            // Whatever loaded plans and draws without throwing, with finite geometry.
            foreach (var step in ComponentDocuments.Plan(loaded).Where(s => !s.IsElement))
            {
                var primitives = new List<ComponentPrimitive>();
                ComponentGeometry.Build(loaded, step.Component!, step.Definition!, step.Placement, primitives);
                Assert.All(primitives, p => Assert.True(float.IsFinite(p.A.X) && float.IsFinite(p.C.Y) && float.IsFinite(p.Color.W)));
            }

            Assert.Equal(components.Count, PlateDocuments.ToJson(loaded)["Components"]!.AsArray().Count);
        }
    }

    /// <summary>The shape of a Plate document as a build before Components saw it.</summary>
    private sealed class OlderBuildDocument
    {
        public int Version { get; set; }

        public string Name { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}

public class ComponentBoundsTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1e30f)]
    [InlineData(-1e30f)]
    public void Bound_ClampsEveryValue(float wild)
    {
        var component = new PlateComponent
        {
            Kind = PlateComponentKind.PlateFrame,
            DefinitionId = new string('a', 500),
            Opacity = wild,
            Scale = wild,
            RotationDegrees = wild,
            Offset = new Vector2(wild, wild),
            LayerOrder = int.MinValue,
            Color = new Vector4(wild, wild, wild, wild),
        };

        PlateComponentEditor.Bound(component);

        Assert.InRange(component.Opacity, 0f, 1f);
        Assert.InRange(component.Scale, PlateComponentLimits.MinScale, PlateComponentLimits.MaxScale);
        Assert.InRange(component.RotationDegrees, PlateComponentLimits.MinRotation, PlateComponentLimits.MaxRotation);
        Assert.InRange(component.Offset.X, -PlateComponentLimits.MaxOffset, PlateComponentLimits.MaxOffset);
        Assert.InRange(component.Offset.Y, -PlateComponentLimits.MaxOffset, PlateComponentLimits.MaxOffset);
        Assert.Equal(PlateComponentLimits.MinLayerOrder, component.LayerOrder);
        Assert.Equal(PlateComponentLimits.MaxDefinitionIdLength, component.DefinitionId.Length);
        Assert.InRange(component.Color!.Value.X, 0f, 1f);
        Assert.InRange(component.Color!.Value.W, 0f, 1f);
    }

    [Fact]
    public void Update_BoundsValues_AndCannotChangeIdentityOrKind()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine)];
        var id = document.Components[0].Id;

        PlateComponentEditor.Update(document, id, c =>
        {
            c.Scale = 99f;
            c.Id = Guid.NewGuid();
            c.Kind = PlateComponentKind.Divider;
        });

        Assert.Equal(id, document.Components[0].Id);
        Assert.Equal(PlateComponentKind.PlateFrame, document.Components[0].Kind);
        Assert.Equal(PlateComponentLimits.MaxScale, document.Components[0].Scale);
    }

    [Fact]
    public void RendererSide_UnboundedStoredValues_AreStillBounded()
    {
        // A hand-edited file may hold anything; the paint plan never trusts stored values.
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine);
        component.Scale = 1000f;
        component.Offset = new Vector2(float.NaN, 1e20f);
        component.RotationDegrees = float.PositiveInfinity;
        document.Components = [component];

        var step = ComponentDocuments.Plan(document).Single(s => !s.IsElement);
        var portrait = document.Elements[0];

        Assert.True(step.Placement.Rect.Size.X <= portrait.Size.X * PlateComponentLimits.MaxScale + 0.01f);
        Assert.True(float.IsFinite(step.Placement.Rect.Position.X) && float.IsFinite(step.Placement.Rect.Position.Y));
        Assert.True(float.IsFinite(step.Placement.RotationDegrees));
    }

    [Fact]
    public void Capacity_IsEnforced_CountingUnreadableComponents()
    {
        var document = ComponentDocuments.WithAnchors();
        document.UnrecognizedComponents = [JsonDocument.Parse("{}").RootElement.Clone()];
        var definition = BuiltInComponentCatalog.Find(BuiltInComponentCatalog.DividerLine)!;
        for (var i = 0; i < PlateComponentLimits.MaxComponentCount - 1; i++)
        {
            PlateComponentEditor.Add(document, definition);
        }

        Assert.False(PlateComponentEditor.HasCapacity(document));
        Assert.Throws<InvalidOperationException>(() => PlateComponentEditor.Add(document, definition));
    }

    [Fact]
    public void Plan_DrawsAtMostTheCapacity_EvenFromAnOversizedFile()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = Enumerable.Range(0, 100).Select(_ => ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine)).ToList();

        Assert.Equal(PlateComponentLimits.MaxComponentCount, ComponentDocuments.Plan(document).Count(s => !s.IsElement));
    }
}

public class ComponentLayerOrderTests
{
    private static List<PlateLayer> Layers(List<PaintStep> plan) => plan.Select(s => s.Layer).ToList();

    [Fact]
    public void LayerValues_AreExplicitAndAscending()
    {
        PlateLayer[] expected =
        [
            PlateLayer.Backdrop, PlateLayer.Background, PlateLayer.Pattern, PlateLayer.Portrait, PlateLayer.PortraitFrame,
            PlateLayer.PortraitOverlay, PlateLayer.NameBacking, PlateLayer.Identity, PlateLayer.Decorations, PlateLayer.PlateFrame, PlateLayer.Foreground,
        ];

        for (var i = 1; i < expected.Length; i++)
        {
            Assert.True((int)expected[i - 1] < (int)expected[i], $"{expected[i - 1]} < {expected[i]}");
        }
    }

    [Fact]
    public void WithoutComponents_ThePlanIsExactlyTheElementOrder()
    {
        var document = ComponentDocuments.WithAnchors();
        var plan = ComponentDocuments.Plan(document);

        Assert.Equal(document.Elements.OrderBy(e => e.ZIndex).Select(e => e.Id), plan.Select(s => s.Element!.Id));
    }

    [Fact]
    public void DefaultPlate_PaintsInTheSpecifiedLayerOrder()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();

        var layers = Layers(ComponentDocuments.Plan(document));

        PlateLayer[] expected =
        [
            PlateLayer.Portrait, PlateLayer.PortraitFrame, PlateLayer.PortraitOverlay, PlateLayer.NameBacking,
            PlateLayer.Identity, PlateLayer.Identity, PlateLayer.Identity, PlateLayer.Identity, PlateLayer.Identity,
        ];
        Assert.Equal(expected, layers.Take(expected.Length));
        Assert.All(layers.Skip(expected.Length).Take(layers.Count - expected.Length - 1), l => Assert.Equal(PlateLayer.Decorations, l));
        Assert.Equal(PlateLayer.PlateFrame, layers[^1]);

        // Non-decreasing: nothing ever paints below an earlier layer in the default layout.
        for (var i = 1; i < layers.Count; i++)
        {
            Assert.True(layers[i - 1] <= layers[i], $"step {i}: {layers[i - 1]} then {layers[i]}");
        }
    }

    [Fact]
    public void Order_DoesNotDependOnInsertionOrder()
    {
        var reference = ComponentDocuments.WithAnchors();
        reference.Components = ComponentDocuments.OneOfEach();
        var expected = Layers(ComponentDocuments.Plan(reference));

        var random = new Random(7);
        for (var i = 0; i < 25; i++)
        {
            var shuffled = ComponentDocuments.WithAnchors();
            shuffled.Components = ComponentDocuments.OneOfEach().OrderBy(_ => random.Next()).ToList();
            Assert.Equal(expected, Layers(ComponentDocuments.Plan(shuffled)));
        }
    }

    [Fact]
    public void WithinALayer_LayerOrderWins_ThenListOrder()
    {
        var document = ComponentDocuments.WithAnchors();
        var a = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine, layerOrder: 5);
        var b = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameDouble, layerOrder: -1);
        var c = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameBrackets, layerOrder: 5);
        document.Components = [a, b, c];

        var components = ComponentDocuments.Plan(document).Where(s => !s.IsElement).Select(s => s.Component).ToList();

        Assert.Equal([b, a, c], components);
    }

    [Fact]
    public void LayerOrder_NeverMovesAComponentOutOfItsLayer()
    {
        var document = ComponentDocuments.WithAnchors();
        var overlay = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitOverlayFade, layerOrder: PlateComponentLimits.MinLayerOrder);
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine, layerOrder: PlateComponentLimits.MaxLayerOrder);
        document.Components = [overlay, frame];

        var components = ComponentDocuments.Plan(document).Where(s => !s.IsElement).Select(s => s.Component).ToList();

        Assert.Equal([frame, overlay], components);
    }

    [Fact]
    public void PortraitComponents_FollowThePortrait_InElementOrder()
    {
        // An Advanced user put a free element underneath the portrait: that order is kept, and the
        // portrait's frame still paints directly over the portrait.
        var document = ComponentDocuments.WithAnchors();
        var glow = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(20, 20), Size = new Vector2(440, 680), ZIndex = -1 };
        document.Elements.Add(glow);
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine)];

        var plan = ComponentDocuments.Plan(document);

        Assert.Same(glow, plan[0].Element);
        Assert.Equal(ProfileElementRole.BasicPortrait, plan[1].Element!.Role);
        Assert.Equal(PlateLayer.PortraitFrame, plan[2].Layer);
        Assert.Equal(new ElementRect(document.Elements[0].Position, document.Elements[0].Size), plan[2].Placement.Rect);
    }

    [Fact]
    public void PortraitComponents_RotateWithThePortrait()
    {
        var document = ComponentDocuments.WithAnchors();
        ((ImageProfileElement)document.Elements[0]).RotationDegrees = 20f;
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine);
        frame.RotationDegrees = 5f;
        document.Components = [frame];

        Assert.Equal(25f, ComponentDocuments.Plan(document).Single(s => !s.IsElement).Placement.RotationDegrees);
    }

    [Fact]
    public void HiddenAnchor_HidesItsComponents()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Elements[0].Visible = false;
        document.Elements[1].Visible = false;
        document.Elements[2].Visible = false;
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine), ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar)];

        Assert.All(ComponentDocuments.Plan(document), s => Assert.True(s.IsElement));
    }

    [Fact]
    public void MissingAnchor_UsesTheLayoutPlacement_AtTheBottomOfTheElementStack()
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Empty", ComponentDocuments.Now);
        document.Elements.Add(new TextProfileElement { Text = "Free", ZIndex = 0 });
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar), ComponentDocuments.Of(BuiltInComponentCatalog.PortraitFrameLine)];

        var plan = ComponentDocuments.Plan(document);

        Assert.Equal([PlateLayer.PortraitFrame, PlateLayer.NameBacking, PlateLayer.Identity], Layers(plan));
        var expected = Domain.Basic.AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicPortrait, AdventurePlateOrientation.Normal, document)!.Value;
        Assert.Equal(expected, plan[0].Placement.Rect);
    }

    [Fact]
    public void NameBacking_PaintsBeforeTheFirstIdentityElement_AndCoversNameAndTitle()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.NameBackingBar)];

        var plan = ComponentDocuments.Plan(document);
        var index = plan.FindIndex(s => !s.IsElement);

        Assert.Equal(ProfileElementRole.BasicName, plan[index + 1].Element!.Role);
        var rect = plan[index].Placement.Rect;
        Assert.True(rect.Position.Y < 60f && rect.Position.Y + rect.Size.Y > 155f);
    }

    [Fact]
    public void CornerOrnament_DrawsFourMirroredCorners_AndSectionHeader_OnePerHeading()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(BuiltInComponentCatalog.CornerOrnamentDiamond), ComponentDocuments.Of(BuiltInComponentCatalog.SectionHeaderTick)];

        var steps = ComponentDocuments.Plan(document).Where(s => !s.IsElement).ToList();
        var corners = steps.Where(s => s.Component!.Kind == PlateComponentKind.CornerOrnament).ToList();
        var headers = steps.Where(s => s.Component!.Kind == PlateComponentKind.SectionHeader).ToList();

        Assert.Equal(4, corners.Count);
        Assert.Equal(4, corners.Select(c => (c.Placement.MirrorX, c.Placement.MirrorY)).Distinct().Count());
        Assert.Single(headers);
        Assert.Equal(document.Elements[3].Position, headers[0].Placement.Rect.Position);
    }

    [Fact]
    public void InvisibleComponent_IsNotDrawn()
    {
        var document = ComponentDocuments.WithAnchors();
        var frame = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        frame.Visible = false;
        document.Components = [frame];

        Assert.All(ComponentDocuments.Plan(document), s => Assert.True(s.IsElement));
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = ComponentDocuments.OneOfEach();

        Assert.Equal(ComponentDocuments.Plan(document), ComponentDocuments.Plan(document));
    }
}

public class ComponentAssetReferenceTests
{
    [Fact]
    public void ComponentImage_IsAReference()
    {
        var asset = Guid.NewGuid();
        var document = ComponentDocuments.WithAnchors();
        document.Components = [new PlateComponent { Kind = PlateComponentKind.PortraitOverlay, DefinitionId = BuiltInComponentCatalog.PortraitOverlayImage, AssetId = asset }];

        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(document, found);

        Assert.Contains(asset, found);
    }

    [Fact]
    public void ReferencesInUnknownComponentData_AreConservativelyKept()
    {
        var inExtension = Guid.NewGuid();
        var inUnreadable = Guid.NewGuid();
        var inMalformedWhole = Guid.NewGuid();
        var document = ComponentDocuments.WithAnchors();
        var component = ComponentDocuments.Of(BuiltInComponentCatalog.PlateFrameLine);
        component.ExtensionData = new Dictionary<string, JsonElement> { ["FutureTexture"] = JsonSerializer.SerializeToElement(inExtension.ToString()) };
        document.Components = [component];
        document.UnrecognizedComponents = [JsonDocument.Parse($$"""{ "Kind": "x", "Image": "{{inUnreadable}}" }""").RootElement.Clone()];

        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(document, found);
        Assert.Contains(inExtension, found);
        Assert.Contains(inUnreadable, found);

        var whole = ComponentDocuments.WithAnchors();
        whole.MalformedComponentsValue = JsonDocument.Parse($$"""{ "Image": "{{inMalformedWhole}}" }""").RootElement.Clone();
        var foundWhole = new HashSet<Guid>();
        AssetReferenceScanner.Collect(whole, foundWhole);
        Assert.Contains(inMalformedWhole, foundWhole);
    }

    [Fact]
    public void RemovedComponent_NoLongerReferencesItsImage()
    {
        var asset = Guid.NewGuid();
        var document = ComponentDocuments.WithAnchors();
        var component = new PlateComponent { Kind = PlateComponentKind.PortraitOverlay, DefinitionId = BuiltInComponentCatalog.PortraitOverlayImage, AssetId = asset };
        document.Components = [component];
        PlateComponentEditor.Remove(document, component.Id);

        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(document, found);
        Assert.DoesNotContain(asset, found);
    }
}
