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
    /// <summary>The Basic editor's Component slots, in display order. Background is one, so an Art
    /// Style's background artwork can be changed or taken away in Basic too.</summary>
    public static readonly PlateComponentKind[] BasicSlots =
    [
        PlateComponentKind.Background, PlateComponentKind.PlateFrame, PlateComponentKind.PortraitFrame, PlateComponentKind.PortraitOverlay, PlateComponentKind.NameBacking,
    ];

    /// <summary>The Basic editor's decoration slots, in display order.</summary>
    public static readonly PlateComponentKind[] BasicDecorations =
    [
        PlateComponentKind.CornerOrnament, PlateComponentKind.Divider, PlateComponentKind.SectionHeader,
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
    /// The Background artwork that hides the Plate's own background entirely, as it is drawn: visible,
    /// at full opacity, over every point of the canvas (every Background artwork is opaque). Null when
    /// any of the Plate's own background can show: no artwork, or artwork hidden, unresolvable,
    /// see-through, moved, shrunk or turned off the canvas, or fitted inside a canvas of another
    /// shape. The Basic editor shows the background's own settings (Pattern, Customize Background)
    /// only when this is null, since they change nothing the artwork covers.
    /// </summary>
    public static ComponentDefinition? CoveringBackground(ProfileDocument profile, IComponentCatalog catalog)
    {
        if (!HasAnyBackground(profile))
        {
            return null;
        }

        var plan = new List<PaintStep>();
        ComponentPaintPlan.Build(profile, Array.Empty<ProfileElement>(), catalog, plan);
        var canvas = new Vector2(Math.Max(0f, profile.CanvasWidth), Math.Max(0f, profile.CanvasHeight));
        var primitives = new List<ComponentPrimitive>();
        foreach (var step in plan)
        {
            if (step.Component is not { Kind: PlateComponentKind.Background } component || step.Definition is not { } definition)
            {
                continue;
            }

            primitives.Clear();
            ComponentGeometry.Build(profile, component, definition, step.Placement, primitives);
            foreach (var primitive in primitives)
            {
                if (primitive.Kind == ComponentPrimitiveKind.Art && primitive.Color.W >= 1f
                    && Contains(primitive, Vector2.Zero) && Contains(primitive, new Vector2(canvas.X, 0f))
                    && Contains(primitive, canvas) && Contains(primitive, new Vector2(0f, canvas.Y)))
                {
                    return definition;
                }
            }
        }

        return null;
    }

    private static bool HasAnyBackground(ProfileDocument profile)
    {
        foreach (var component in profile.Components ?? [])
        {
            if (component is { Kind: PlateComponentKind.Background })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="point"/> is inside or on the convex quad A-B-C-D, either
    /// winding (a mirrored quad winds the other way), within a hundredth of a pixel.</summary>
    private static bool Contains(ComponentPrimitive quad, Vector2 point)
    {
        const float Slack = 0.01f;
        var corners = new[] { quad.A, quad.B, quad.C, quad.D };
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var edge = b - a;
            var length = edge.Length();
            if (!(length > 0f))
            {
                return false;
            }

            // The point's signed distance from the edge's line.
            var distance = ((edge.X * (point.Y - a.Y)) - (edge.Y * (point.X - a.X))) / length;
            if (MathF.Abs(distance) <= Slack)
            {
                continue;
            }

            var side = distance > 0f ? 1 : -1;
            if (sign != 0 && side != sign)
            {
                return false;
            }

            sign = side;
        }

        return true;
    }

    /// <summary>
    /// Basic: chooses a slot's style, or empties the slot (null). An existing slot Component keeps
    /// its instance and every Advanced refinement (offset, scale, color...) and only changes style
    /// (a Corner Ornament keeps its distance from the edge, which at a size other than 100% can
    /// change its offset: <see cref="CornerOrnamentPlacement.ChangeDefinition"/>);
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

            SetDefinitionCore(profile, existing, definition, catalog);
            existing.AssetId = null;
            return true;
        }

        Add(profile, definition);
        return true;
    }

    /// <summary>
    /// Advanced: changes a Component's style to another of its kind. Everything else is kept, except
    /// that a Corner Ornament keeps its distance from the edge rather than its raw offset
    /// (<see cref="CornerOrnamentPlacement.ChangeDefinition"/>). False when nothing changed or the
    /// definition isn't one of the Component's kind.
    /// </summary>
    public static bool SetDefinition(ProfileDocument profile, Guid componentId, string definitionId, IComponentCatalog catalog)
    {
        if (Find(profile, componentId) is not { } component || component.DefinitionId == definitionId
            || catalog.Find(definitionId) is not { } definition || definition.Kind != component.Kind)
        {
            return false;
        }

        return Update(profile, componentId, c => SetDefinitionCore(profile, c, definition, catalog));
    }

    private static void SetDefinitionCore(ProfileDocument profile, PlateComponent component, ComponentDefinition definition, IComponentCatalog catalog)
    {
        if (component.Kind == PlateComponentKind.CornerOrnament && catalog.Find(component.DefinitionId) is { Kind: PlateComponentKind.CornerOrnament } previous)
        {
            CornerOrnamentPlacement.ChangeDefinition(profile, component, previous, definition);
        }
        else
        {
            component.DefinitionId = definition.Id;
        }
    }

    /// <summary>Adds a new Component of <paramref name="definition"/> with default placement, on top
    /// of its layer (a Corner Ornament at <see cref="CornerOrnamentPlacement"/>'s default size and
    /// distance from the edge). Throws when the Plate is at capacity.</summary>
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

        if (definition.Kind == PlateComponentKind.CornerOrnament)
        {
            CornerOrnamentPlacement.ApplyDefault(profile, component, definition);
        }

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

    /// <summary>Resets a Component's Advanced refinements (offset, scale, rotation, opacity, color) to its
    /// default placement: the one <see cref="Add"/> gives a new Component of its definition (for a
    /// Corner Ornament whose definition this build knows, <see cref="CornerOrnamentPlacement.ApplyDefault"/>).</summary>
    public static bool ResetTransform(ProfileDocument profile, Guid componentId) => Update(profile, componentId, component =>
    {
        component.Offset = default;
        component.Scale = 1f;
        component.RotationDegrees = 0f;
        component.Opacity = 1f;
        component.Color = null;
        if (component.Kind == PlateComponentKind.CornerOrnament && BuiltInComponentCatalog.Find(component.DefinitionId) is { Kind: PlateComponentKind.CornerOrnament } definition)
        {
            CornerOrnamentPlacement.ApplyDefault(profile, component, definition);
        }
    });

    /// <summary>Basic: sets a Corner Ornament's size, keeping its distance from the edge
    /// (<see cref="CornerOrnamentPlacement.SetScale"/>). False when it isn't a Corner Ornament of a known style, or nothing changed.</summary>
    public static bool SetCornerOrnamentScale(ProfileDocument profile, Guid componentId, float scale, IComponentCatalog catalog) =>
        EditCornerOrnament(profile, componentId, catalog, (component, definition) => CornerOrnamentPlacement.SetScale(profile, component, definition, scale));

    /// <summary>Basic: sets a Corner Ornament's distance from the Plate's edge, in reference pixels
    /// (<see cref="CornerOrnamentPlacement.SetEdgeDistance"/>). False as for <see cref="SetCornerOrnamentScale"/>.</summary>
    public static bool SetCornerOrnamentEdgeDistance(ProfileDocument profile, Guid componentId, float distance, IComponentCatalog catalog) =>
        EditCornerOrnament(profile, componentId, catalog, (component, definition) => CornerOrnamentPlacement.SetEdgeDistance(profile, component, definition, distance));

    /// <summary>Basic: puts a Corner Ornament back at the default size and distance, keeping its
    /// color, opacity, rotation and corners. False as for <see cref="SetCornerOrnamentScale"/>.</summary>
    public static bool ResetCornerOrnamentPlacement(ProfileDocument profile, Guid componentId, IComponentCatalog catalog) =>
        EditCornerOrnament(profile, componentId, catalog, (component, definition) => CornerOrnamentPlacement.ApplyDefault(profile, component, definition));

    private static bool EditCornerOrnament(ProfileDocument profile, Guid componentId, IComponentCatalog catalog, Action<PlateComponent, ComponentDefinition> edit)
    {
        if (Find(profile, componentId) is not { Kind: PlateComponentKind.CornerOrnament } component
            || catalog.Find(component.DefinitionId) is not { Kind: PlateComponentKind.CornerOrnament } definition)
        {
            return false;
        }

        var scale = component.Scale;
        var offset = component.Offset;
        Update(profile, componentId, c => edit(c, definition));
        return !component.Scale.Equals(scale) || component.Offset != offset;
    }

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
