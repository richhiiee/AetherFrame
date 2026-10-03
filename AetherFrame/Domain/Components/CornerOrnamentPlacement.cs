using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Components;

/// <summary>
/// The Basic editor's view of a Corner Ornament's placement: its size and its distance from the
/// Plate's edge, both written through the same <see cref="PlateComponent.Scale"/> and
/// <see cref="PlateComponent.Offset"/> the Advanced editor edits, so the two editors always agree and
/// nothing new is stored.
///
/// The distance is measured in reference pixels (<see cref="ComponentPaintPlan.Unit"/>) from the
/// Plate's edge to the outer edge of an ornament's box, the same for every corner because the offset
/// mirrors per corner (<see cref="ComponentPaintPlan"/>). Changing the size keeps that distance, so an
/// ornament grows from its corner toward the middle instead of around its center.
///
/// New Corner Ornaments start at <see cref="DefaultScale"/> and <see cref="DefaultEdgeDistance"/>
/// (<see cref="ApplyDefault"/>, called by <see cref="PlateComponentEditor.Add"/>): larger and closer
/// to the edge than the layout's own placement (<see cref="ComponentPaintPlan.CornerSize"/> at
/// <see cref="ComponentPaintPlan.CornerInset"/>), which Kim's first-user walkthrough found too small
/// and too far inside. Ornaments already on a Plate keep the values they were saved with.
/// </summary>
public static class CornerOrnamentPlacement
{
    /// <summary>Size of a new Corner Ornament (Scale): a quarter larger than the layout's box.</summary>
    public const float DefaultScale = 1.25f;

    /// <summary>Distance of a new Corner Ornament from the Plate's edge, in reference pixels.</summary>
    public const float DefaultEdgeDistance = 8f;

    /// <summary>The Basic Size slider's range (Scale). Advanced keeps <see cref="PlateComponentLimits"/>'s wider one.</summary>
    public const float MinBasicScale = 0.5f;
    public const float MaxBasicScale = 2.5f;

    /// <summary>The Basic distance slider's range, in reference pixels: a little past the edge to well inside it.</summary>
    public const float MinEdgeDistance = -20f;
    public const float MaxEdgeDistance = 120f;

    /// <summary>
    /// How far the ornament's box sits from the Plate's left and top edges (the same as from the
    /// right and bottom ones, since the offset mirrors per corner), in reference pixels, for its
    /// current bounded scale and offset.
    /// </summary>
    public static Vector2 EdgeDistances(ProfileDocument profile, PlateComponent component, ComponentDefinition definition)
    {
        var unit = ComponentPaintPlan.Unit(profile);
        var scale = PlateComponentLimits.ClampScale(component.Scale);
        var offset = PlateComponentLimits.ClampOffset(component.Offset);
        return new Vector2(Inside(definition, scale)) + (offset / unit);
    }

    /// <summary>The single distance Basic shows: the mean of <see cref="EdgeDistances"/>' two axes
    /// (equal unless the Advanced editor moved the ornament along one axis only).</summary>
    public static float EdgeDistance(ProfileDocument profile, PlateComponent component, ComponentDefinition definition)
    {
        var distances = EdgeDistances(profile, component, definition);
        return (distances.X + distances.Y) / 2f;
    }

    /// <summary>
    /// Sets the size, keeping each axis's distance from the edge (the offset follows the scale).
    /// The scale is bounded by <see cref="PlateComponentLimits"/> only, so a value the Advanced
    /// editor chose outside Basic's slider range is still kept exactly when it is set back.
    /// </summary>
    public static void SetScale(ProfileDocument profile, PlateComponent component, ComponentDefinition definition, float scale)
    {
        var distances = EdgeDistances(profile, component, definition);
        component.Scale = PlateComponentLimits.ClampScale(scale);
        component.Offset = OffsetFor(profile, definition, component.Scale, distances);
    }

    /// <summary>
    /// Moves the ornament to <paramref name="distance"/> reference pixels from the edge (bounded by
    /// <see cref="MinEdgeDistance"/> and <see cref="MaxEdgeDistance"/>). Both axes move by the same
    /// amount, so a difference between them the Advanced editor made is kept.
    /// </summary>
    public static void SetEdgeDistance(ProfileDocument profile, PlateComponent component, ComponentDefinition definition, float distance)
    {
        var target = float.IsFinite(distance) ? Math.Clamp(distance, MinEdgeDistance, MaxEdgeDistance) : DefaultEdgeDistance;
        var delta = target - EdgeDistance(profile, component, definition);
        var distances = EdgeDistances(profile, component, definition) + new Vector2(delta);
        component.Offset = OffsetFor(profile, definition, PlateComponentLimits.ClampScale(component.Scale), distances);
    }

    /// <summary>
    /// Changes the ornament's style to <paramref name="to"/>, keeping its distance from the edge. A
    /// style's artwork can be larger or smaller (<see cref="ComponentPaintPlan.ArtSizeFactor"/>), and
    /// at any size but 100% the same offset would then put it elsewhere, even past the edge. At 100%
    /// the offset is unchanged, so an ornament no one resized keeps exactly its saved values.
    /// </summary>
    public static void ChangeDefinition(ProfileDocument profile, PlateComponent component, ComponentDefinition from, ComponentDefinition to)
    {
        // Shifted by the difference in how far each style's box sits inside at this scale (zero at
        // 100%), rather than converted to a distance and back, so an unresized offset stays exact.
        var scale = PlateComponentLimits.ClampScale(component.Scale);
        var shift = (Inside(from, scale) - Inside(to, scale)) * ComponentPaintPlan.Unit(profile);
        component.DefinitionId = to.Id;
        component.Offset = PlateComponentLimits.ClampOffset(PlateComponentLimits.ClampOffset(component.Offset) + new Vector2(shift));
    }

    /// <summary>Puts the ornament at <see cref="DefaultScale"/> and <see cref="DefaultEdgeDistance"/>.
    /// Rotation, opacity, color and the chosen corners are not touched.</summary>
    public static void ApplyDefault(ProfileDocument profile, PlateComponent component, ComponentDefinition definition)
    {
        component.Scale = DefaultScale;
        component.Offset = OffsetFor(profile, definition, DefaultScale, new Vector2(DefaultEdgeDistance));
    }

    /// <summary>True when the ornament is at its default size and distance (Basic's reset is then idle).</summary>
    public static bool IsDefault(ProfileDocument profile, PlateComponent component, ComponentDefinition definition)
    {
        const float Tolerance = 0.05f;
        var distances = EdgeDistances(profile, component, definition);
        return MathF.Abs(component.Scale - DefaultScale) < 0.001f
            && MathF.Abs(distances.X - DefaultEdgeDistance) < Tolerance
            && MathF.Abs(distances.Y - DefaultEdgeDistance) < Tolerance;
    }

    /// <summary>The distance from the edge, in reference pixels, of an unmoved ornament at <paramref name="scale"/>:
    /// the layout's inset, plus half of what the box shrinks by (it scales around its center).</summary>
    private static float Inside(ComponentDefinition definition, float scale) =>
        ComponentPaintPlan.CornerInset + (ComponentPaintPlan.CornerSize * ComponentPaintPlan.ArtSizeFactor(definition) * (1f - scale) / 2f);

    private static Vector2 OffsetFor(ProfileDocument profile, ComponentDefinition definition, float scale, Vector2 distances) =>
        PlateComponentLimits.ClampOffset((distances - new Vector2(Inside(definition, scale))) * ComponentPaintPlan.Unit(profile));
}
