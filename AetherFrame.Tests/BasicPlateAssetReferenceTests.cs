using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Assets;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Conservative asset discovery in the Basic Plate settings and the Identity Header: every
/// preserved unknown field, nested ones included, counts toward asset liveness.
/// </summary>
public class BasicPlateAssetReferenceTests
{
    private static JsonObject ClassicJson()
    {
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Basic", ComponentDocuments.Now, new PlateStarterContent(null));
        document.BasicPlate ??= new BasicPlateSettings();
        document.BasicPlate.Placements.Add(new BasicPlacement { Role = ProfileElementRole.BasicWorld, Rect = new ElementRect(new Vector2(1, 2), new Vector2(3, 4)) });
        document.BasicPlate.ActiveHours = new BasicActiveHours { Days = BasicWeekdays.Weekends };
        document.BasicIdentity = new BasicIdentityHeader
        {
            AppliedLayout = new IdentityLayoutSnapshot(),
            LayoutStyle = new IdentityLayoutStyle(),
        };
        return PlateDocuments.ToJson(document);
    }

    private static HashSet<Guid> Scan(JsonObject raw)
    {
        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(PlateDocuments.Materialize(raw), found);
        return found;
    }

    private static JsonObject At(JsonObject raw, string path)
    {
        JsonNode node = raw;
        foreach (var part in path.Split('.'))
        {
            node = int.TryParse(part, out var index) ? node[index]! : node[part]!;
        }

        return node.AsObject();
    }

    public static IEnumerable<object[]> BasicBags() =>
    [
        ["BasicPlate"],
        ["BasicPlate.ActiveHours"],
        ["BasicPlate.Placements.0"],
        ["BasicIdentity"],
        ["BasicIdentity.AppliedLayout"],
        ["BasicIdentity.LayoutStyle"],
        ["BasicIdentity.LayoutStyle.Applied"],
        ["BasicIdentity.LayoutStyle.Previous"],
    ];

    [Theory]
    [MemberData(nameof(BasicBags))]
    public void FutureField_HoldingAnAssetId_IsLive_AndSurvivesSave(string bag)
    {
        var direct = Guid.NewGuid();
        var nested = Guid.NewGuid();
        var inArray = Guid.NewGuid();
        var raw = ClassicJson();
        var target = At(raw, bag);
        target["FutureImage"] = direct.ToString();
        target["FutureDecor"] = new JsonObject { ["Layers"] = new JsonArray(new JsonObject { ["Asset"] = nested.ToString("N") }) };
        target["FutureGallery"] = new JsonArray(inArray.ToString("B"), "caption");

        var found = Scan(raw);
        Assert.Contains(direct, found);
        Assert.Contains(nested, found);
        Assert.Contains(inArray, found);

        // Preserved through the editor's clone-and-save path, and still live after reloading.
        var saved = PlateDocuments.ToJson(PlateDocuments.Materialize(raw));
        var afterSave = Scan(saved);
        Assert.Contains(direct, afterSave);
        Assert.Contains(nested, afterSave);
        Assert.Contains(inArray, afterSave);
    }

    [Fact]
    public void KnownBasicPlateAssetRefs_AreLive()
    {
        // The Basic portrait is an ordinary image element with a role; Basic settings alongside it
        // never hide it.
        var portraitAsset = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Basic", ComponentDocuments.Now, new PlateStarterContent(null));
        document.BasicPlate ??= new BasicPlateSettings();
        document.Elements.Add(new ImageProfileElement { Role = ProfileElementRole.BasicPortrait, AssetId = portraitAsset, Size = new Vector2(400, 640) });

        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(document, found);

        Assert.Contains(portraitAsset, found);
    }

    [Fact]
    public void MalformedValues_AreIgnoredSafely()
    {
        var raw = ClassicJson();
        var plate = At(raw, "BasicPlate");
        plate["FutureNumber"] = 12345;
        plate["FutureFloat"] = 1.5e300;
        plate["FutureBool"] = true;
        plate["FutureNull"] = null;
        plate["FutureText"] = "not-a-guid";
        plate["FutureEmpty"] = Guid.Empty.ToString();
        plate["FutureTruncated"] = Guid.NewGuid().ToString()[..30];
        plate["FuturePadded"] = Guid.NewGuid() + "x";
        plate["FutureEmptyObject"] = new JsonObject();
        plate["FutureEmptyArray"] = new JsonArray();

        JsonNode deep = new JsonArray("deep-but-not-a-guid");
        for (var i = 0; i < 50; i++)
        {
            deep = new JsonObject { ["Next"] = deep };
        }

        plate["FutureDeep"] = deep;
        At(raw, "BasicPlate.ActiveHours")["FutureOdd"] = new JsonArray(null, 1, false, "{}");

        var baseline = Scan(ClassicJson());
        var found = Scan(raw);

        Assert.DoesNotContain(Guid.Empty, found);
        Assert.Equal(baseline.OrderBy(g => g), found.OrderBy(g => g));
    }

    [Fact]
    public void KnownNonAssetValues_AreNotReferences()
    {
        // Known Basic text fields are user content, never asset references — even GUID-shaped text.
        var guidShaped = Guid.NewGuid();
        var document = PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Basic", ComponentDocuments.Now, new PlateStarterContent(null));
        document.BasicPlate ??= new BasicPlateSettings();
        document.BasicPlate.Playstyles.Add(guidShaped.ToString());
        document.BasicPlate.ActiveHours = new BasicActiveHours { TimeZone = guidShaped.ToString("N")[..16] };
        document.BasicIdentity = new BasicIdentityHeader { CustomTitle = guidShaped.ToString() };

        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(document, found);

        Assert.DoesNotContain(guidShaped, found);
    }

    [Fact]
    public async Task PlateHoldingAFutureBasicRef_KeepsTheAssetLive()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var raw = ClassicJson();
        raw["ProfileId"] = plateId;
        At(raw, "BasicPlate")["FuturePortraitSource"] = new JsonObject { ["SceneAsset"] = asset.ToString() };
        fixture.WritePlateJson(plateId, raw.ToJsonString());
        var library = await fixture.LoadAsync();

        var scan = await library.ScanAssetReferencesAsync();

        Assert.True(scan.IsComplete);
        Assert.Contains(asset, scan.ReferencedAssetIds);
    }

    [Fact]
    public async Task TemplateHoldingAFutureBasicRef_KeepsTheAssetLive_AfterThePlateDropsIt()
    {
        using var fixture = new TemplateLibraryFixture();
        var plateId = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var raw = ClassicJson();
        raw["ProfileId"] = plateId;
        At(raw, "BasicPlate.Placements.0")["FutureBadge"] = asset.ToString();
        fixture.WritePlateJson(plateId, raw.ToJsonString());
        var templates = await fixture.LoadAsync();

        var templateId = await templates.SaveAsTemplateAsync(plateId, "Future Basic");
        Assert.Contains(asset.ToString(), fixture.ReadTemplateJson(templateId), StringComparison.OrdinalIgnoreCase);

        // The source Plate no longer carries it: only the Template keeps the asset alive.
        var document = fixture.PlateLibrary.OpenDocumentForEditing(plateId);
        document.BasicPlate!.Placements[0].ExtensionData = null;
        await fixture.PlateLibrary.SavePlateDocumentAsync(document);
        Assert.DoesNotContain(asset, (await fixture.PlateLibrary.ScanAssetReferencesAsync()).ReferencedAssetIds);

        var live = await LiveAssetReferences.ComputeAsync(fixture.PlateLibrary, templates);
        Assert.True(live.IsComplete);
        Assert.Contains(asset, live.ReferencedAssetIds);

        // And a Plate made from that Template carries the reference too.
        var created = await templates.InstantiateAsync(templateId, null);
        var fromTemplate = new HashSet<Guid>();
        AssetReferenceScanner.Collect(fixture.PlateLibrary.OpenDocumentForEditing(created.PlateId), fromTemplate);
        Assert.Contains(asset, fromTemplate);
    }

    [Fact]
    public void EveryPreservedExtensionBagInADocument_IsScanned()
    {
        // Guard: a new [JsonExtensionData] type reachable from a Plate document must be added to
        // this list AND to AssetReferenceScanner, or this test fails.
        var expected = new HashSet<Type>
        {
            typeof(ProfileDocument), typeof(ProfileElement), typeof(ProfileBackground),
            typeof(BasicIdentityHeader), typeof(IdentityLayoutSnapshot), typeof(IdentityLayoutStyle), typeof(TitleStyleValues),
            typeof(BasicPlateSettings), typeof(BasicPlacement), typeof(BasicActiveHours),
            typeof(PlateComponent),
        };

        Assert.Equal(expected.Select(t => t.Name).OrderBy(n => n), ReachableBagTypes().Select(t => t.Name).OrderBy(n => n));

        // Every one of those bags, populated at once, reports its own GUID.
        var ids = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray();
        static Dictionary<string, JsonElement> Bag(Guid id) => new() { ["Future"] = JsonSerializer.SerializeToElement(id.ToString()) };
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "All", ComponentDocuments.Now);
        document.ExtensionData = Bag(ids[0]);
        document.Elements.Add(new TextProfileElement { ExtensionData = Bag(ids[1]) });
        document.Background!.ExtensionData = Bag(ids[2]);
        document.BasicIdentity = new BasicIdentityHeader
        {
            ExtensionData = Bag(ids[3]),
            AppliedLayout = new IdentityLayoutSnapshot { ExtensionData = Bag(ids[4]) },
            LayoutStyle = new IdentityLayoutStyle
            {
                ExtensionData = Bag(ids[5]),
                Applied = new TitleStyleValues { ExtensionData = Bag(ids[6]) },
                Previous = new TitleStyleValues(),
            },
        };
        document.BasicPlate = new BasicPlateSettings
        {
            ExtensionData = Bag(ids[7]),
            ActiveHours = new BasicActiveHours { ExtensionData = Bag(ids[8]) },
        };
        document.BasicPlate.Placements.Add(new BasicPlacement { ExtensionData = Bag(ids[9]) });
        document.Components = [new PlateComponent { Kind = PlateComponentKind.Divider, DefinitionId = BuiltInComponentCatalog.DividerLine, ExtensionData = Bag(ids[10]) }];

        var found = new HashSet<Guid>();
        AssetReferenceScanner.Collect(document, found);

        Assert.All(ids, id => Assert.Contains(id, found));
    }

    /// <summary>Types with a [JsonExtensionData] bag reachable from <see cref="ProfileDocument"/>'s persisted properties.</summary>
    private static List<Type> ReachableBagTypes()
    {
        var result = new List<Type>();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(typeof(ProfileDocument));
        queue.Enqueue(typeof(TextProfileElement));
        queue.Enqueue(typeof(ImageProfileElement));

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type) || type.Namespace?.StartsWith("AetherFrame", StringComparison.Ordinal) != true)
            {
                continue;
            }

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var ownBag = properties.Any(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null && p.DeclaringType == type);
            if (ownBag)
            {
                result.Add(type);
            }

            foreach (var property in properties)
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                {
                    continue;
                }

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (propertyType.IsGenericType)
                {
                    foreach (var argument in propertyType.GetGenericArguments())
                    {
                        queue.Enqueue(argument);
                    }
                }
                else
                {
                    queue.Enqueue(propertyType);
                }
            }
        }

        return result;
    }
}
