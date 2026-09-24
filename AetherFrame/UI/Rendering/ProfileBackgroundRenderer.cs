using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Paints a <see cref="ProfileBackground"/> over the canvas rectangle. Every mode costs a handful
/// of draw-list primitives per frame and allocates nothing: a solid fill is one rect, a gradient
/// is one four-color rect, a textured fill is one rect plus a bounded number of tiled quads of a
/// cached mask texture, and an image is one quad.
/// </summary>
internal static class ProfileBackgroundRenderer
{
    private static readonly Vector4 MissingImageColor = new(0.35f, 0.12f, 0.12f, 0.5f);

    // Beyond this many tile quads the pattern is sub-pixel anyway (extreme zoom-out); it's drawn
    // as its flat average tint instead of thousands of invisible quads.
    private const int MaxTileQuads = 4096;
    private const float MinTileScreenSize = 12f;

    internal static void Draw(ImDrawListPtr drawList, ProfileBackground? background, Vector2 canvasOrigin, Vector2 canvasScreenSize, float scale, ProfileRenderResources resources)
    {
        if (background is null || background.Mode == ProfileBackgroundMode.None)
        {
            return;
        }

        var opacity = Math.Clamp(background.Opacity, 0f, 1f);
        if (opacity <= 0f)
        {
            return;
        }

        var canvasMax = canvasOrigin + canvasScreenSize;

        // The base: what Mode draws, on its own, exactly as if no Pattern were ever involved.
        // TexturedFill is kept only for backward compatibility with already-saved Plates — its
        // base was always identical to SolidColor's (a flat PrimaryColor fill); new documents
        // never need to select it, since a Pattern no longer requires any particular Mode.
        switch (background.Mode)
        {
            case ProfileBackgroundMode.SolidColor:
            case ProfileBackgroundMode.TexturedFill:
                drawList.AddRectFilled(canvasOrigin, canvasMax, ToU32(background.PrimaryColor, opacity));
                break;

            case ProfileBackgroundMode.LinearGradient:
                DrawLinearGradient(drawList, background, canvasOrigin, canvasScreenSize, opacity);
                break;

            case ProfileBackgroundMode.Image:
                DrawImage(drawList, background, canvasOrigin, canvasScreenSize, opacity, resources);
                break;
        }

        // The Pattern overlay: independent of Mode — composed on top of whatever base was just
        // drawn above (solid, gradient, or image alike), never a Mode of its own to switch into.
        if (background.Texture != ProfileBackgroundTexture.None)
        {
            DrawTexture(drawList, background, canvasOrigin, canvasScreenSize, scale, opacity, resources.Textures);
        }
    }

    /// <summary>
    /// CSS-style linear gradient at any angle, as one four-color rectangle — exact, not an
    /// approximation; see <see cref="LinearGradientLayout"/> for the proof and its one
    /// precondition (a uniform alpha, enforced here by using opaque endpoint colors and applying
    /// transparency only through the background's single Opacity).
    /// </summary>
    private static void DrawLinearGradient(ImDrawListPtr drawList, ProfileBackground background, Vector2 origin, Vector2 size, float opacity)
    {
        var primary = background.PrimaryColor with { W = 1f };
        var secondary = background.SecondaryColor with { W = 1f };
        var (tl, tr, br, bl) = LinearGradientLayout.ComputeCornerT(size, background.GradientAngle);

        uint ColorAt(float t) => ToU32(Vector4.Lerp(primary, secondary, t), opacity);

        drawList.AddRectFilledMultiColor(origin, origin + size, ColorAt(tl), ColorAt(tr), ColorAt(br), ColorAt(bl));
    }

    /// <summary>
    /// Tiles the cached mask texture across the canvas (clipped to it), rotated around the canvas
    /// center, tinted with the secondary color at the configured intensity. The tile grid is laid
    /// out in logical canvas space, so the pattern sits identically in the editor and Profile View.
    /// </summary>
    private static void DrawTexture(ImDrawListPtr drawList, ProfileBackground background, Vector2 origin, Vector2 size, float scale, float opacity, ProceduralTextureCache textures)
    {
        var tile = textures.GetTile(background.Texture, out var meanCoverage);
        if (tile is null)
        {
            return;
        }

        var intensity = Math.Clamp(background.TextureIntensity, 0f, 1f);
        var alpha = Math.Clamp(background.SecondaryColor.W, 0f, 1f) * intensity * opacity;
        if (alpha <= 0f)
        {
            return;
        }

        var patternScale = Math.Clamp(background.TextureScale, ProfileBackground.MinTextureScale, ProfileBackground.MaxTextureScale);
        var tileSize = patternScale * ProceduralTextureCache.PeriodsPerTile * scale;
        var tint = ToU32(background.SecondaryColor with { W = 1f }, alpha);

        var rotation = ProfileBackground.SupportsRotation(background.Texture) ? background.TextureRotation : 0f;
        var radians = rotation * (MathF.PI / 180f);
        var axisU = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var axisV = new Vector2(-axisU.Y, axisU.X);

        // The canvas's extent measured along the (rotated) tile axes, from its center.
        var halfExtentU = (MathF.Abs(size.X * axisU.X) + MathF.Abs(size.Y * axisU.Y)) / 2f;
        var halfExtentV = (MathF.Abs(size.X * axisV.X) + MathF.Abs(size.Y * axisV.Y)) / 2f;

        var firstU = (int)MathF.Floor(-halfExtentU / tileSize);
        var lastU = (int)MathF.Ceiling(halfExtentU / tileSize);
        var firstV = (int)MathF.Floor(-halfExtentV / tileSize);
        var lastV = (int)MathF.Ceiling(halfExtentV / tileSize);
        var quadCount = (long)(lastU - firstU) * (lastV - firstV);

        if (tileSize / ProceduralTextureCache.PeriodsPerTile < 1.5f || tileSize < MinTileScreenSize || quadCount > MaxTileQuads)
        {
            drawList.AddRectFilled(origin, origin + size, ToU32(background.SecondaryColor with { W = 1f }, alpha * meanCoverage));
            return;
        }

        var center = origin + (size / 2f);
        var stepU = axisU * tileSize;
        var stepV = axisV * tileSize;

        drawList.PushClipRect(origin, origin + size, true);
        for (var v = firstV; v < lastV; v++)
        {
            for (var u = firstU; u < lastU; u++)
            {
                var p0 = center + (stepU * u) + (stepV * v);
                drawList.AddImageQuad(
                    tile.Handle,
                    p0,
                    p0 + stepU,
                    p0 + stepU + stepV,
                    p0 + stepV,
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f),
                    tint);
            }
        }

        drawList.PopClipRect();
    }

    private static void DrawImage(ImDrawListPtr drawList, ProfileBackground background, Vector2 origin, Vector2 size, float opacity, ProfileRenderResources resources)
    {
        if (background.ImageAssetId is not { } assetId)
        {
            return;
        }

        var wrap = resources.Images.GetWrapOrNull(assetId);
        if (wrap is null)
        {
            drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(MissingImageColor));
            return;
        }

        var layout = ImageFitLayout.Compute(
            background.ImageFit, size, new Vector2(wrap.Width, wrap.Height), ImageFitLayout.FullSource, background.ImageFlipX, background.ImageFlipY);

        drawList.AddImage(wrap.Handle, origin + layout.DrawMin, origin + layout.DrawMax, layout.UvMin, layout.UvMax, ToU32(Vector4.One, opacity));
    }

    private static uint ToU32(Vector4 color, float alphaMultiplier) =>
        ImGui.GetColorU32(color with { W = Math.Clamp(color.W * alphaMultiplier, 0f, 1f) });
}
