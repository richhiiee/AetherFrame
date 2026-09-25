using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>
/// The Component edits both editors make, as plain document mutations (the editor session wraps
/// each call in one undo step). Basic mode works through constrained slots — one Component per
/// kind, chosen from the built-in styles, placed by the layout — and Advanced mode edits the same
/// <see cref="PlateComponent"/> instances directly; there is only this one set of rules.
/// Every value written is bounded (see <see cref="PlateComponentLimits"/>).
/// </summary>
public static class PlateComponentEditor
{
    /// <summary>The Basic editor's Component slots, in display order.</summary>
    public static readonly PlateComponentKind[] BasicSlots =
    [
        PlateComponentKind.PlateFrame, PlateComponentKind.PortraitFrame, PlateComponentKind.PortraitOverlay, PlateComponentKind.NameBacking,
    ];

    /// <summary>The Basic editor's decoration slots, in display order.</summary>
    public static readonly PlateComponentKind[] BasicDecorations =
    [
        PlateComponentKind.CornerOrnament, PlateComponentKind.Divider, PlateComponentKind.SectionHeader,
    ];

    /// <summary>Kinds only the Advanced editor adds (no Basic slot), in display order, listed before the Basic ones.</summary>
    public static readonly PlateComponentKind[] AdvancedOnlyKinds =
    [
        PlateComponentKind.Background,
    ];

    /// <summary>
    /// Kinds whose placement follows text the player moves and edits freely in the Advanced editor
    /// (the name and title), and so can be fixed in place instead (<see cref="PlateComponent.FixedAnchorPosition"/>).
    /// Portrait Frames and Overlays keep following the portrait (a frame belongs to its picture);
    /// Section Headers decorate every heading at once; the rest are placed by the canvas already.
    /// </summary>
    public static bool CanFixAnchor(PlateComponentKind kind) => kind is PlateComponentKind.NameBacking or PlateComponentKind.Divider;

    /// <summary>
    /// Fixes a Name Backing's or Divider's anchor at <paramref name="anchor"/> (normally
    /// <see cref="ComponentPaintPlan.ContentAnchor"/>, so it doesn't move), or makes it follow the name
    /// and title again (null). Offset, Scale and Rotation are kept. False when nothing changed or the
    /// kind can't be fixed.
    /// </summary>
    public static bool SetFixedAnchor(ProfileDocument profile, Guid componentId, ElementRect? anchor)
    {
        if (Find(profile, componentId) is not { } component || !CanFixAnchor(component.Kind)
            || (component.FixedAnchorPosition == anchor?.Position && component.FixedAnchorSize == anchor?.Size))
        {
            return false;
        }

        return Update(profile, componentId, c =>
        {
            c.FixedAnchorPosition = anchor?.Position;
            c.FixedAnchorSize = anchor?.Size;
        });
    }

    public static string KindLabel(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.Background => "Background",
        PlateComponentKind.PlateFrame => "Plate Frame",
        PlateComponentKind.PortraitFrame => "Portrait Frame",
        PlateComponentKind.PortraitOverlay => "Portrait Overlay",
        PlateComponentKind.NameBacking => "Name Backing",
        PlateComponentKind.CornerOrnament => "Corner Ornament",
        PlateComponentKind.Divider => "Divider",
        PlateComponentKind.SectionHeader => "Section Header",
        _ => "Unknown Component",
    };

    /// <summary>The Component a Basic slot shows and edits: the first of that kind (consistently
    /// everywhere, like Basic sections), or null when the slot is empty.</summary>
    public static PlateComponent? FindSlot(ProfileDocument profile, PlateComponentKind kind)
    {
        if (profile.Components is { } components)
        {
            foreach (var component in components)
            {
                if (component.Kind == kind)
                {
                    return component;
                }
            }
        }

        return null;
    }

    public static PlateComponent? Find(ProfileDocument profile, Guid componentId) =>
        profile.Components?.Find(c => c.Id == componentId);

    /// <summary>How many Components the Plate holds, including ones this build couldn't read.</summary>
    public static int Count(ProfileDocument profile) => (profile.Components?.Count ?? 0) + (profile.UnrecognizedComponents?.Count ?? 0);

    public static bool HasCapacity(ProfileDocument profile) => Count(profile) < PlateComponentLimits.MaxComponentCount;

    /// <summary>
    /// Basic: chooses a slot's style, or empties the slot (null). An existing slot Component keeps
    /// its instance and every Advanced refinement (offset, scale, color...) and only changes style;
    /// an empty slot gets a new Component with default placement. Only built-in, image-free
    /// definitions of the slot's own kind are accepted. Returns false when nothing changed.
    /// </summary>
    public static bool SetSlot(ProfileDocument profile, PlateComponentKind kind, string? definitionId, IComponentCatalog catalog)
    {
        var existing = FindSlot(profile, kind);
        if (definitionId is null)
        {
            return existing is not null && profile.Components!.Remove(existing);
        }

        var definition = catalog.Find(definitionId);
        if (definition is null || definition.Kind != kind || definition.RequiresAsset)
        {
            throw new ArgumentException($"'{definitionId}' is not a {KindLabel(kind)} style.", nameof(definitionId));
        }

        if (existing is not null)
        {
            if (existing.DefinitionId == definition.Id)
            {
                return false;
            }

            existing.DefinitionId = definition.Id;
            existing.AssetId = null;
            return true;
        }

        Add(profile, definition);
        return true;
    }

    /// <summary>Adds a new Component of <paramref name="definition"/> with default placement, on top
    /// of its layer. Throws when the Plate is at capacity.</summary>
    public static PlateComponent Add(ProfileDocument profile, ComponentDefinition definition)
    {
        if (!HasCapacity(profile))
        {
            throw new InvalidOperationException($"A Plate can have at most {PlateComponentLimits.MaxComponentCount} Components.");
        }

        var components = profile.Components ??= new List<PlateComponent>();
        var component = new PlateComponent
        {
            Kind = definition.Kind,
            DefinitionId = definition.Id,
            LayerOrder = NextLayerOrder(components, definition.Kind),
        };

        components.Add(component);
        return component;
    }

    /// <summary>Removes a Component. Returns false if it doesn't exist.</summary>
    public static bool Remove(ProfileDocument profile, Guid componentId) =>
        profile.Components is { } components && components.RemoveAll(c => c.Id == componentId) > 0;

    /// <summary>Applies <paramref name="update"/> to a Component, then bounds every value it may
    /// have set. Kind and id can't be changed this way. Returns false if it doesn't exist.</summary>
    public static bool Update(ProfileDocument profile, Guid componentId, Action<PlateComponent> update)
    {
        if (Find(profile, componentId) is not { } component)
        {
            return false;
        }

        var id = component.Id;
        var kind = component.Kind;
        update(component);
        component.Id = id;
        component.Kind = kind;
        Bound(component);
        return true;
    }

    /// <summary>Moves a Component one step up (+1) or down (-1) among the Components of its own layer.</summary>
    public static bool MoveInLayer(ProfileDocument profile, Guid componentId, int direction)
    {
        if (Find(profile, componentId) is not { } component || direction == 0)
        {
            return false;
        }

        var layer = ComponentPaintPlan.LayerOf(component.Kind);
        var peers = new List<PlateComponent>();
        foreach (var candidate in profile.Components!)
        {
            if (ComponentPaintPlan.LayerOf(candidate.Kind) == layer)
            {
                peers.Add(candidate);
            }
        }

        // Current paint order within the layer: LayerOrder, ties by list order (stable sort).
        var ordered = new List<PlateComponent>(peers);
        StableSortByLayerOrder(ordered);
        var index = ordered.IndexOf(component);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= ordered.Count)
        {
            return false;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].LayerOrder = PlateComponentLimits.ClampLayerOrder(i);
        }

        return true;
    }

    /// <summary>
    /// Turns one corner of a Corner Ornament on or off, keeping the others (and any bits a newer build
    /// stored). The last drawn corner can't be turned off — "no corners" is choosing None (Basic) or
    /// removing the Component (Advanced). Returns false when nothing changed or the change isn't allowed.
    /// </summary>
    public static bool SetCorner(ProfileDocument profile, Guid componentId, CornerMask corner, bool enabled)
    {
        if (Find(profile, componentId) is not { Kind: PlateComponentKind.CornerOrnament } component
            || corner is not (CornerMask.TopLeft or CornerMask.TopRight or CornerMask.BottomLeft or CornerMask.BottomRight))
        {
            return false;
        }

        var current = component.Corners ?? CornerMask.All;
        var next = enabled ? current | corner : current & ~corner;
        if (next == current || (next & CornerMask.All) == CornerMask.None)
        {
            return false;
        }

        component.Corners = CornerMasks.Normalize(next);
        return true;
    }

    /// <summary>Resets a Component's Advanced refinements (offset, scale, rotation, opacity, color) to its default placement.</summary>
    public static bool ResetTransform(ProfileDocument profile, Guid componentId) => Update(profile, componentId, component =>
    {
        component.Offset = default;
        component.Scale = 1f;
        component.RotationDegrees = 0f;
        component.Opacity = 1f;
        component.Color = null;
    });

    /// <summary>Clamps every numeric value into its bounds (see <see cref="PlateComponentLimits"/>).</summary>
    public static void Bound(PlateComponent component)
    {
        component.Opacity = PlateComponentLimits.ClampOpacity(component.Opacity);
        component.Scale = PlateComponentLimits.ClampScale(component.Scale);
        component.RotationDegrees = PlateComponentLimits.ClampRotation(component.RotationDegrees);
        component.Offset = PlateComponentLimits.ClampOffset(component.Offset);
        component.LayerOrder = PlateComponentLimits.ClampLayerOrder(component.LayerOrder);
        component.DefinitionId ??= string.Empty;
        if (component.DefinitionId.Length > PlateComponentLimits.MaxDefinitionIdLength)
        {
            component.DefinitionId = component.DefinitionId[..PlateComponentLimits.MaxDefinitionIdLength];
        }

        if (component.Color is { } color)
        {
            component.Color = PlateComponentLimits.ClampColor(color);
        }

        // A fixed anchor is both halves or neither, on the canvas's coordinate range, never negative in size.
        if (component.FixedAnchorPosition is { } position && component.FixedAnchorSize is { } size)
        {
            component.FixedAnchorPosition = PlateComponentLimits.ClampOffset(position);
            component.FixedAnchorSize = Vector2.Max(Vector2.Zero, PlateComponentLimits.ClampOffset(size));
        }
        else
        {
            component.FixedAnchorPosition = null;
            component.FixedAnchorSize = null;
        }
    }

    private static int NextLayerOrder(List<PlateComponent> components, PlateComponentKind kind)
    {
        var layer = ComponentPaintPlan.LayerOf(kind);
        var max = -1;
        var any = false;
        foreach (var component in components)
        {
            if (ComponentPaintPlan.LayerOf(component.Kind) == layer)
            {
                any = true;
                max = Math.Max(max, component.LayerOrder);
            }
        }

        return any ? PlateComponentLimits.ClampLayerOrder(max + 1) : 0;
    }

    private static void StableSortByLayerOrder(List<PlateComponent> list)
    {
        for (var i = 1; i < list.Count; i++)
        {
            var current = list[i];
            var j = i - 1;
            while (j >= 0 && PlateComponentLimits.ClampLayerOrder(list[j].LayerOrder) > PlateComponentLimits.ClampLayerOrder(current.LayerOrder))
            {
                list[j + 1] = list[j];
                j--;
            }

            list[j + 1] = current;
        }
    }
}
