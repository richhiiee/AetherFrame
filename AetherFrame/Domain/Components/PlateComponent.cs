using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>
/// One reusable visual piece placed on a Plate (a frame, a name backing, a divider...): a reference
/// to an immutable <see cref="ComponentDefinition"/> by its stable <see cref="DefinitionId"/>, plus
/// the per-Plate configuration needed to reproduce its appearance. Stored in
/// <see cref="ProfileDocument.Components"/>, so Templates, duplication, packages, undo and dirty
/// state carry it exactly like every other part of the Plate's creative state.
///
/// Where it goes is decided by its <see cref="Kind"/> (which anchor it follows and which
/// <see cref="PlateLayer"/> it paints in — see <see cref="ComponentPaintPlan"/>); the Basic editor
/// only ever changes the definition, while <see cref="Offset"/>, <see cref="Scale"/>,
/// <see cref="RotationDegrees"/>, <see cref="Opacity"/> and <see cref="LayerOrder"/> are the
/// Advanced refinements on top of that placement. Every value is bounded when rendered or edited
/// (see <see cref="PlateComponentLimits"/>); nothing here is ever resolved by display name.
/// </summary>
public sealed class PlateComponent
{
    /// <summary>Stable instance identity within the Plate (editor selection, duplicate detection).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What this component is. Stored (not derived from the definition) so a component whose
    /// definition is missing still keeps its slot, layer and Basic ownership. Unknown values (a newer
    /// build's kinds) are kept verbatim and never rendered.</summary>
    public PlateComponentKind Kind { get; set; }

    /// <summary>Stable catalog id of the definition (see <see cref="BuiltInComponentCatalog"/>).</summary>
    public string DefinitionId { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    /// <summary>Color override; null follows the definition's theme-derived default color.</summary>
    public Vector4? Color { get; set; }

    /// <summary>[0, 1], multiplied with the color's own alpha.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Logical canvas offset from the anchored placement.</summary>
    public Vector2 Offset { get; set; }

    /// <summary>Uniform scale around the placement's center (1 = the default placement).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Clockwise rotation around the placement's center, in degrees.</summary>
    public float RotationDegrees { get; set; }

    /// <summary>Order among components in the same layer band: higher paints later (on top). Ties
    /// keep list order. Never moves a component out of its band.</summary>
    public int LayerOrder { get; set; }

    /// <summary>Managed image asset, for definitions drawn from an image (see
    /// <see cref="ComponentDefinition.RequiresAsset"/>). Null for procedural definitions.</summary>
    public Guid? AssetId { get; set; }

    /// <summary>Properties this build doesn't know, kept through clone and save unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public PlateComponent Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        DefinitionId = DefinitionId,
        Visible = Visible,
        Color = Color,
        Opacity = Opacity,
        Offset = Offset,
        Scale = Scale,
        RotationDegrees = RotationDegrees,
        LayerOrder = LayerOrder,
        AssetId = AssetId,
        ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
    };

    /// <summary>Value equality over every editable property (the dirty-state check). Extension data
    /// is not editable, so, as for elements, it is not compared.</summary>
    public bool ContentEquals(PlateComponent? other) =>
        other is not null
        && Id == other.Id
        && Kind == other.Kind
        && DefinitionId == other.DefinitionId
        && Visible == other.Visible
        && Color == other.Color
        && Opacity.Equals(other.Opacity)
        && Offset == other.Offset
        && Scale.Equals(other.Scale)
        && RotationDegrees.Equals(other.RotationDegrees)
        && LayerOrder == other.LayerOrder
        && AssetId == other.AssetId;

    /// <summary>Element-wise <see cref="ContentEquals(PlateComponent?)"/> of two lists (null and empty are equal: both mean "no components").</summary>
    public static bool ListsEqual(IReadOnlyList<PlateComponent>? a, IReadOnlyList<PlateComponent>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        for (var i = 0; i < countA; i++)
        {
            if (!a![i].ContentEquals(b![i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Independent deep copy of a component list (null stays null).</summary>
    public static List<PlateComponent>? CloneList(IReadOnlyList<PlateComponent>? source)
    {
        if (source is null)
        {
            return null;
        }

        var copy = new List<PlateComponent>(source.Count);
        foreach (var component in source)
        {
            copy.Add(component.Clone());
        }

        return copy;
    }
}

/// <summary>
/// The Component types. Persisted as the numeric value: values are explicit and only ever
/// appended, so a newer build's kinds load as undefined values here and are preserved untouched.
/// </summary>
public enum PlateComponentKind
{
    /// <summary>Never valid for a real component; the value an absent "Kind" reads as.</summary>
    Unknown = 0,
    PlateFrame = 1,
    PortraitFrame = 2,
    PortraitOverlay = 3,
    NameBacking = 4,
    CornerOrnament = 5,
    Divider = 6,
    SectionHeader = 7,
}

/// <summary>Bounds for every numeric component value, shared by the editors, the renderer and import validation.</summary>
public static class PlateComponentLimits
{
    /// <summary>Most components one Plate may hold (known plus preserved unknown ones).</summary>
    public const int MaxComponentCount = 32;

    public const int MaxDefinitionIdLength = 64;

    public const float MinScale = 0.25f;
    public const float MaxScale = 4f;
    public const float MaxOffset = 4096f;
    public const float MinRotation = -360f;
    public const float MaxRotation = 360f;
    public const int MinLayerOrder = -100;
    public const int MaxLayerOrder = 100;

    public static float ClampOpacity(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;

    public static float ClampScale(float value) => float.IsFinite(value) ? Math.Clamp(value, MinScale, MaxScale) : 1f;

    public static float ClampRotation(float value) => float.IsFinite(value) ? Math.Clamp(value, MinRotation, MaxRotation) : 0f;

    public static int ClampLayerOrder(int value) => Math.Clamp(value, MinLayerOrder, MaxLayerOrder);

    public static Vector2 ClampOffset(Vector2 value) => new(ClampOffsetAxis(value.X), ClampOffsetAxis(value.Y));

    public static Vector4 ClampColor(Vector4 value) => new(ClampUnit(value.X), ClampUnit(value.Y), ClampUnit(value.Z), ClampUnit(value.W));

    private static float ClampOffsetAxis(float value) => float.IsFinite(value) ? Math.Clamp(value, -MaxOffset, MaxOffset) : 0f;

    private static float ClampUnit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
}
