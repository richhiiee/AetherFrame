using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Draws one placement of a <see cref="PlateComponent"/> from its <see cref="ComponentGeometry"/>
/// primitives. Only ever called by <see cref="ProfileRenderer"/>, in the order
/// <see cref="ComponentPaintPlan"/> decides — so every surface that renders a Plate draws
/// Components identically.
/// </summary>
internal static class ComponentRenderer
{
    // Reused every frame (render thread only).
    private static readonly List<ComponentPrimitive> PrimitiveBuffer = new(64);

    internal static void Draw(ImDrawListPtr drawList, ProfileDocument profile, in PaintStep step, Vector2 canvasOrigin, float scale, ProfileRenderResources resources)
    {
        if (step.Component is not { } component || step.Definition is not { } definition)
        {
            return;
        }

        PrimitiveBuffer.Clear();
        ComponentGeometry.Build(profile, component, definition, step.Placement, PrimitiveBuffer);

        foreach (var primitive in PrimitiveBuffer)
        {
            var a = canvasOrigin + (primitive.A * scale);
            var b = canvasOrigin + (primitive.B * scale);
            var c = canvasOrigin + (primitive.C * scale);
            var d = canvasOrigin + (primitive.D * scale);
            var color = ImGui.GetColorU32(primitive.Color);

            switch (primitive.Kind)
            {
                case ComponentPrimitiveKind.Quad:
                    drawList.AddQuadFilled(a, b, c, d, color);
                    break;

                case ComponentPrimitiveKind.Triangle:
                    drawList.AddTriangleFilled(a, b, c, color);
                    break;

                case ComponentPrimitiveKind.Image:
                    // Missing, still loading, or undecodable: nothing — a Component is decoration,
                    // and the Plate stays fully readable without it.
                    if (component.AssetId is { } assetId && resources.Images.GetWrapOrNull(assetId) is { } wrap)
                    {
                        drawList.AddImageQuad(wrap.Handle, a, b, c, d, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), color);
                    }

                    break;

                case ComponentPrimitiveKind.Art:
                    // The level closest to (not smaller than) the on-screen size; the vertex color
                    // tints the white/greyscale artwork and carries the opacity.
                    var screenPixels = MathF.Max(Vector2.Distance(a, b), Vector2.Distance(a, d));
                    if (definition.Art is { } art && resources.Art.GetWrapOrNull(art, screenPixels) is { } artWrap)
                    {
                        drawList.AddImageQuad(artWrap.Handle, a, b, c, d, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), color);
                    }

                    break;
            }
        }

        PrimitiveBuffer.Clear();
    }
}
