using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>
/// The explicit paint layers of a Plate, bottom to top. Values are spaced so a future layer can be
/// inserted between two existing ones without renumbering; nothing persists them.
/// </summary>
public enum PlateLayer
{
    /// <summary>The canvas backdrop behind everything.</summary>
    Backdrop = 0,

    /// <summary>The Plate background (solid, gradient, image).</summary>
    Background = 100,

    /// <summary>The background's procedural Pattern (drawn with the background).</summary>
    Pattern = 200,

    /// <summary>The Basic portrait element.</summary>
    Portrait = 300,

    PortraitFrame = 400,
    PortraitOverlay = 500,
    NameBacking = 600,

    /// <summary>Identity and text: every element other than the portrait.</summary>
    Identity = 700,

    /// <summary>Corner Ornaments, Dividers and Section Headers.</summary>
    Decorations = 800,

    PlateFrame = 900,

    /// <summary>Reserved for future foreground effects; nothing paints here in V1.</summary>
    Foreground = 1000,
}

/// <summary>Where one component instance is drawn: a logical canvas rectangle (already including
/// the component's own offset and scale), its rotation around the rectangle's center, and whether
/// the shape is mirrored (Corner Ornaments are drawn once per corner).</summary>
public readonly record struct ComponentPlacement(ElementRect Rect, float RotationDegrees, bool MirrorX, bool MirrorY);

/// <summary>One step of a Plate's paint sequence: an element, or one placement of a component.</summary>
public readonly record struct PaintStep(PlateLayer Layer, ProfileElement? Element, PlateComponent? Component, ComponentDefinition? Definition, ComponentPlacement Placement)
{
    public bool IsElement => Element is not null;
}

/// <summary>Why a component is or isn't drawn.</summary>
public enum ComponentStatus
{
    Ready,

    /// <summary>A kind this build doesn't know (a newer build's), kept but never drawn.</summary>
    UnknownKind,

    /// <summary>A definition id no catalog here provides, kept but never drawn.</summary>
    MissingDefinition,

    /// <summary>The definition is for a different kind than the component: treated as missing, never re-interpreted.</summary>
    KindMismatch,

    /// <summary>An image definition with no image chosen.</summary>
    MissingImage,
}

/// <summary>
/// Turns a Plate's elements and components into one deterministic paint sequence. Pure logic
/// (no rendering), shared by every surface that draws a Plate through <c>ProfileRenderer</c>.
///
/// <para><b>Ordering.</b> Elements keep exactly the order <c>ProfilePaintOrder</c> gives them
/// (ZIndex, ties by list order), so a Plate without components paints exactly as before. Components
/// are placed by their <see cref="PlateLayer"/> relative to what they decorate:
/// Portrait Frames then Portrait Overlays immediately after the portrait element; Name Backings
/// immediately before the first identity element (name or title); then, after every element,
/// Decorations, then Plate Frames. Within one layer: ascending <see cref="PlateComponent.LayerOrder"/>,
/// ties by list order. When the anchor element doesn't exist at all, the component uses the
/// Adventure Plate Classic layout's placement and paints at the bottom of the element stack (in
/// layer order); when the anchor exists but is hidden, the component is hidden with it.</para>
///
/// <para><b>Failure isolation.</b> A component that can't be resolved (see <see cref="ComponentStatus"/>)
/// is skipped on its own; nothing about it can stop the rest of the Plate from painting.</para>
/// </summary>
public static class ComponentPaintPlan
{
    /// <summary>Plate Frame inset from the canvas edge, in reference pixels.</summary>
    public const float PlateFrameInset = 14f;

    /// <summary>Corner Ornament size and inset, in reference pixels.</summary>
    public const float CornerSize = 40f;
    public const float CornerInset = 22f;

    /// <summary>Name Backing padding around the name and title, in reference pixels.</summary>
    public const float NameBackingPadX = 16f;
    public const float NameBackingPadY = 6f;

    /// <summary>Divider gap below the identity header and its height, in reference pixels.</summary>
    public const float DividerGap = 4f;
    public const float DividerHeight = 24f;

    public static PlateLayer LayerOf(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.PlateFrame => PlateLayer.PlateFrame,
        PlateComponentKind.PortraitFrame => PlateLayer.PortraitFrame,
        PlateComponentKind.PortraitOverlay => PlateLayer.PortraitOverlay,
        PlateComponentKind.NameBacking => PlateLayer.NameBacking,
        PlateComponentKind.CornerOrnament or PlateComponentKind.Divider or PlateComponentKind.SectionHeader => PlateLayer.Decorations,
        _ => PlateLayer.Foreground,
    };

    /// <summary>True for kinds this build can place and draw.</summary>
    public static bool IsKnownKind(PlateComponentKind kind) => kind is >= PlateComponentKind.PlateFrame and <= PlateComponentKind.SectionHeader;

    /// <summary>Resolves a component's definition, or says why it can't be drawn.</summary>
    public static ComponentStatus Resolve(PlateComponent component, IComponentCatalog catalog, out ComponentDefinition? definition)
    {
        definition = null;
        if (!IsKnownKind(component.Kind))
        {
            return ComponentStatus.UnknownKind;
        }

        var found = catalog.Find(component.DefinitionId);
        if (found is null)
        {
            return ComponentStatus.MissingDefinition;
        }

        if (found.Kind != component.Kind)
        {
            return ComponentStatus.KindMismatch;
        }

        definition = found;
        return found.RequiresAsset && (component.AssetId is not { } asset || asset == Guid.Empty)
            ? ComponentStatus.MissingImage
            : ComponentStatus.Ready;
    }

    /// <summary>
    /// Fills <paramref name="output"/> (cleared first) with the paint sequence for
    /// <paramref name="drawnElements"/> — the elements that will actually be painted, already in
    /// paint order — plus <paramref name="profile"/>'s drawable components.
    /// <paramref name="measureText"/> (optional) gives a text element's natural single-line text
    /// width, or null when it can't be measured; with it the Name Backing tracks the name and
    /// title's actual text instead of their whole boxes (see <see cref="TextExtent"/>).
    /// </summary>
    public static void Build(
        ProfileDocument profile, IReadOnlyList<ProfileElement> drawnElements, IComponentCatalog catalog, List<PaintStep> output,
        Func<TextProfileElement, float?>? measureText = null)
    {
        output.Clear();

        var components = profile.Components;
        if (components is null || components.Count == 0)
        {
            foreach (var element in drawnElements)
            {
                output.Add(ElementStep(element));
            }

            return;
        }

        var portraitBand = new List<(PlateComponent Component, ComponentDefinition Definition, int Index)>();
        var nameBand = new List<(PlateComponent Component, ComponentDefinition Definition, int Index)>();
        var decorationBand = new List<(PlateComponent Component, ComponentDefinition Definition, int Index)>();
        var frameBand = new List<(PlateComponent Component, ComponentDefinition Definition, int Index)>();

        for (var i = 0; i < components.Count && i < PlateComponentLimits.MaxComponentCount; i++)
        {
            var component = components[i];
            if (component is null || !component.Visible || Resolve(component, catalog, out var definition) != ComponentStatus.Ready)
            {
                continue;
            }

            var entry = (component, definition!, i);
            switch (LayerOf(component.Kind))
            {
                case PlateLayer.PortraitFrame or PlateLayer.PortraitOverlay:
                    portraitBand.Add(entry);
                    break;
                case PlateLayer.NameBacking:
                    nameBand.Add(entry);
                    break;
                case PlateLayer.Decorations:
                    decorationBand.Add(entry);
                    break;
                case PlateLayer.PlateFrame:
                    frameBand.Add(entry);
                    break;
            }
        }

        SortBand(portraitBand);
        SortBand(nameBand);
        SortBand(decorationBand);
        SortBand(frameBand);

        var unit = Unit(profile);

        // Anchors: the portrait element, and the identity elements (name, title).
        var portraitElement = BasicSections.Find(profile, ProfileElementRole.BasicPortrait);
        var hasIdentityElements = BasicSections.Find(profile, ProfileElementRole.BasicName) is not null
            || BasicSections.Find(profile, ProfileElementRole.BasicTitle) is not null;

        ElementRect? drawnIdentity = null;
        ProfileElement? firstIdentity = null;
        foreach (var element in drawnElements)
        {
            if (element.Role is ProfileElementRole.BasicName or ProfileElementRole.BasicTitle)
            {
                firstIdentity ??= element;
                var rect = TextExtent(element, measureText);
                drawnIdentity = drawnIdentity?.Union(rect) ?? rect;
            }
        }

        var orientation = profile.BasicPlate?.Orientation ?? AdventurePlateOrientation.Normal;

        // Missing anchors: layout placement, at the bottom of the element stack.
        if (portraitElement is null && portraitBand.Count > 0)
        {
            var fallback = AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicPortrait, orientation, profile) ?? CanvasRect(profile);
            AddBand(output, portraitBand, fallback, 0f);
        }

        if (!hasIdentityElements && nameBand.Count > 0)
        {
            var region = AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Identity, orientation, profile);
            AddBand(output, nameBand, Pad(region, unit), 0f);
        }

        foreach (var element in drawnElements)
        {
            if (ReferenceEquals(element, firstIdentity) && nameBand.Count > 0 && drawnIdentity is { } identityRect)
            {
                AddBand(output, nameBand, Pad(identityRect, unit), 0f);
            }

            output.Add(ElementStep(element));

            if (ReferenceEquals(element, portraitElement) && portraitBand.Count > 0)
            {
                AddBand(output, portraitBand, new ElementRect(element.Position, element.Size), RotationGeometry.GetRotationDegrees(element));
            }
        }

        foreach (var (component, definition, _) in decorationBand)
        {
            switch (component.Kind)
            {
                case PlateComponentKind.CornerOrnament:
                    AddCorners(output, profile, component, definition, unit);
                    break;

                case PlateComponentKind.Divider:
                    var identity = drawnIdentity ?? AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Identity, orientation, profile);
                    var divider = new ElementRect(
                        new Vector2(identity.Position.X, identity.Position.Y + identity.Size.Y + (DividerGap * unit)),
                        new Vector2(identity.Size.X, DividerHeight * unit));
                    output.Add(ComponentStep(component, definition, divider, 0f, false, false));
                    break;

                case PlateComponentKind.SectionHeader:
                    foreach (var element in drawnElements)
                    {
                        if (element is TextProfileElement && BasicSections.IsHeading(element.Role))
                        {
                            output.Add(ComponentStep(component, definition, new ElementRect(element.Position, element.Size), 0f, false, false));
                        }
                    }

                    break;
            }
        }

        foreach (var (component, definition, _) in frameBand)
        {
            var inset = PlateFrameInset * unit;
            var canvas = CanvasRect(profile);
            var rect = new ElementRect(canvas.Position + new Vector2(inset), Vector2.Max(Vector2.Zero, canvas.Size - new Vector2(2f * inset)));
            output.Add(ComponentStep(component, definition, rect, 0f, false, false));
        }
    }

    /// <summary>Reference-canvas pixels to this Plate's logical pixels (by height, like Basic fonts).</summary>
    public static float Unit(ProfileDocument profile) =>
        profile.CanvasHeight > 0f && float.IsFinite(profile.CanvasHeight) ? profile.CanvasHeight / AdventurePlateClassicLayout.ReferenceHeight : 1f;

    private static PaintStep ElementStep(ProfileElement element) =>
        new(element.Role == ProfileElementRole.BasicPortrait ? PlateLayer.Portrait : PlateLayer.Identity, element, null, null, default);

    private static void AddBand(List<PaintStep> output, List<(PlateComponent Component, ComponentDefinition Definition, int Index)> band, ElementRect anchor, float anchorRotation)
    {
        foreach (var (component, definition, _) in band)
        {
            output.Add(ComponentStep(component, definition, anchor, anchorRotation, false, false));
        }
    }

    private static void AddCorners(List<PaintStep> output, ProfileDocument profile, PlateComponent component, ComponentDefinition definition, float unit)
    {
        var size = CornerSize * unit * CornerSizeFactor(definition);
        var inset = CornerInset * unit;
        var canvas = CanvasRect(profile);
        var right = canvas.Size.X - inset - size;
        var bottom = canvas.Size.Y - inset - size;
        var box = new Vector2(size);
        var corners = CornerMasks.Effective(component);

        // Only the selected corners, always in this order: top-left, top-right, bottom-left,
        // bottom-right. The offset mirrors per corner, so one Offset moves every ornament inward or
        // outward symmetrically. The shape mirrors with it, except for artwork placed by rotation
        // (clockwise, around the square box's center), which keeps its details' handedness.
        var rotate = definition.Art is { CornerPlacement: CornerArtPlacement.Rotate };
        AddCorner(CornerMask.TopLeft, new Vector2(inset, inset), 0f, false, false);
        AddCorner(CornerMask.TopRight, new Vector2(right, inset), 90f, true, false);
        AddCorner(CornerMask.BottomLeft, new Vector2(inset, bottom), 270f, false, true);
        AddCorner(CornerMask.BottomRight, new Vector2(right, bottom), 180f, true, true);

        void AddCorner(CornerMask corner, Vector2 position, float artRotation, bool flipX, bool flipY)
        {
            if ((corners & corner) != 0)
            {
                output.Add(ComponentStep(component, definition, new ElementRect(position, box), rotate ? artRotation : 0f, flipX, flipY, mirrorShape: !rotate));
            }
        }
    }

    /// <summary>
    /// The logical-canvas bounds of everything <paramref name="component"/> paints in
    /// <paramref name="plan"/> (from <see cref="Build"/>): the union of its placements, each rotated
    /// around its own center. Components aren't clipped to the Plate, so this may extend past the
    /// canvas. Only placements actually in the plan count — an unselected corner, a hidden or
    /// unresolvable component contributes nothing (null when nothing is painted).
    /// </summary>
    public static (Vector2 Min, Vector2 Max)? GetVisualBounds(IReadOnlyList<PaintStep> plan, PlateComponent component)
    {
        (Vector2 Min, Vector2 Max)? bounds = null;
        foreach (var step in plan)
        {
            if (!ReferenceEquals(step.Component, component))
            {
                continue;
            }

            var (min, max) = RotationGeometry.GetVisualBounds(step.Placement.Rect.Position, step.Placement.Rect.Size, step.Placement.RotationDegrees);
            bounds = bounds is { } union ? (Vector2.Min(union.Min, min), Vector2.Max(union.Max, max)) : (min, max);
        }

        return bounds;
    }

    /// <summary>A Corner Ornament's box size relative to <see cref="CornerSize"/>: 1 for procedural
    /// marks, the artwork's bounded <see cref="BuiltInArtAsset.SizeFactor"/> for bundled art.</summary>
    public static float CornerSizeFactor(ComponentDefinition definition) =>
        definition.Art is { SizeFactor: var factor } && float.IsFinite(factor) ? Math.Clamp(factor, 0.25f, 4f) : 1f;

    /// <summary>Applies the component's own (bounded) scale and offset to its anchored placement.</summary>
    private static PaintStep ComponentStep(PlateComponent component, ComponentDefinition definition, ElementRect anchor, float anchorRotation, bool mirrorX, bool mirrorY) =>
        ComponentStep(component, definition, anchor, anchorRotation, mirrorX, mirrorY, mirrorShape: true);

    /// <summary>As above; <paramref name="mirrorX"/>/<paramref name="mirrorY"/> always flip the
    /// offset, and flip the shape only when <paramref name="mirrorShape"/>.</summary>
    private static PaintStep ComponentStep(PlateComponent component, ComponentDefinition definition, ElementRect anchor, float anchorRotation, bool mirrorX, bool mirrorY, bool mirrorShape)
    {
        var scale = PlateComponentLimits.ClampScale(component.Scale);
        var offset = PlateComponentLimits.ClampOffset(component.Offset);
        if (mirrorX)
        {
            offset.X = -offset.X;
        }

        if (mirrorY)
        {
            offset.Y = -offset.Y;
        }

        var center = anchor.Position + (anchor.Size / 2f) + offset;
        var size = anchor.Size * scale;
        var rect = new ElementRect(center - (size / 2f), size);
        var rotation = anchorRotation + PlateComponentLimits.ClampRotation(component.RotationDegrees);

        return new PaintStep(LayerOf(component.Kind), null, component, definition, new ComponentPlacement(rect, rotation, mirrorShape && mirrorX, mirrorShape && mirrorY));
    }

    /// <summary>
    /// The part of an identity element's box its text occupies: a single-line text box narrowed to
    /// the measured text (plus the text padding and a rounding slack), placed by its alignment, so a
    /// short name gets a compact backing and a long one a wide backing. The whole box when the text
    /// can't be measured, wraps, or uses the legacy text layout.
    /// </summary>
    internal static ElementRect TextExtent(ProfileElement element, Func<TextProfileElement, float?>? measureText)
    {
        var box = new ElementRect(element.Position, element.Size);
        if (measureText is null || element is not TextProfileElement text || text.EffectiveWrap || text.UsesLegacyLayout
            || measureText(text) is not { } measured || !float.IsFinite(measured) || measured < 0f)
        {
            return box;
        }

        var width = Math.Min(box.Size.X, measured + (2f * TextProfileElement.LayoutPadding) + TextExtentSlack);
        var x = text.Alignment switch
        {
            TextAlignment.Center => box.Position.X + ((box.Size.X - width) / 2f),
            TextAlignment.Right => box.Position.X + box.Size.X - width,
            _ => box.Position.X,
        };

        return new ElementRect(new Vector2(x, box.Position.Y), new Vector2(width, box.Size.Y));
    }

    private const float TextExtentSlack = 2f;

    private static ElementRect Pad(ElementRect rect, float unit)
    {
        var pad = new Vector2(NameBackingPadX, NameBackingPadY) * unit;
        return new ElementRect(rect.Position - pad, rect.Size + (2f * pad));
    }

    private static ElementRect CanvasRect(ProfileDocument profile) =>
        new(Vector2.Zero, new Vector2(Math.Max(0f, profile.CanvasWidth), Math.Max(0f, profile.CanvasHeight)));

    /// <summary>Stable sort: ascending LayerOrder, ties by list index.</summary>
    private static void SortBand(List<(PlateComponent Component, ComponentDefinition Definition, int Index)> band)
    {
        band.Sort(static (a, b) =>
        {
            var byLayer = LayerOf(a.Component.Kind).CompareTo(LayerOf(b.Component.Kind));
            if (byLayer != 0)
            {
                return byLayer;
            }

            var byOrder = PlateComponentLimits.ClampLayerOrder(a.Component.LayerOrder).CompareTo(PlateComponentLimits.ClampLayerOrder(b.Component.LayerOrder));
            return byOrder != 0 ? byOrder : a.Index.CompareTo(b.Index);
        });
    }
}
