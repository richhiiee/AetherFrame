using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Draws the visual content of a <see cref="ProfileDocument"/> (backdrop, background, then every
/// visible element in Z order) into an ImGui draw list at an arbitrary screen origin and uniform
/// scale.
///
/// This is purely a rendering service: it owns no editing state and knows nothing about
/// selection, dragging, resizing, snapping, hit testing, or undo/redo — those remain editor
/// concerns. The Advanced editor canvas, its Clean Preview, the Basic editor preview, and the
/// read-only Profile View all call into this one class, so they can never disagree about how a
/// profile looks. Anything editor-only (element bounds, placeholders) must be requested
/// explicitly through <see cref="ProfileRenderOptions"/>; the default is the finished profile.
///
/// Deliberately takes only a <see cref="ProfileDocument"/> and shared render resources — no
/// character/ownership identity — so it can later render a profile that didn't originate from the
/// locally logged-in character (e.g. one received over the network) without change.
/// </summary>
internal static class ProfileRenderer
{
    private static readonly Vector4 CanvasBackdropColor = new(0.09f, 0.09f, 0.09f, 1f);
    private static readonly Vector4 MissingAssetFillColor = new(0.35f, 0.12f, 0.12f, 0.6f);
    private static readonly Vector4 MissingAssetBorderColor = new(0.8f, 0.3f, 0.3f, 0.9f);
    private static readonly Vector4 UnknownElementFillColor = new(0.2f, 0.2f, 0.2f, 0.6f);
    private static readonly Vector4 UnknownElementBorderColor = new(0.5f, 0.5f, 0.5f, 0.8f);
    private static readonly Vector4 TextElementFillColor = new(0.15f, 0.15f, 0.15f, 0.6f);
    private static readonly Vector4 TextElementBorderColor = new(0.5f, 0.5f, 0.5f, 0.8f);

    // Reused every frame (render thread only) so painting never allocates a sorted copy.
    private static readonly List<ProfileElement> PaintOrderBuffer = new(ProfileDocument.MaxElementCount);

    /// <summary>
    /// Draws the full logical canvas starting at <paramref name="canvasOrigin"/> in screen space,
    /// uniformly scaled by <paramref name="scale"/> from the profile's own logical canvas size.
    /// </summary>
    internal static void Draw(
        ImDrawListPtr drawList,
        ProfileDocument profile,
        Vector2 canvasOrigin,
        float scale,
        ProfileRenderResources resources,
        in ProfileRenderOptions options)
    {
        // Cheap after the first call for a given profile instance (a single reference check) —
        // see ProfileFontService for why this keeps large text crisp instead of blurry-then-sharp.
        resources.Fonts.EnsurePrewarmed(profile);

        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * scale;

        drawList.AddRectFilled(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(CanvasBackdropColor));

        ProfileBackgroundRenderer.Draw(drawList, profile.Background, canvasOrigin, canvasScreenSize, scale, resources);

        // Hidden elements are skipped here, before any per-element work.
        ProfilePaintOrder.Fill(profile, PaintOrderBuffer, includeHidden: false);
        foreach (var element in PaintOrderBuffer)
        {
            if (!options.ShowEmptySectionHeadings && !BasicSections.IsDrawnInFinishedRendering(profile, element))
            {
                // A section heading with nothing under it (see BasicSections).
                continue;
            }

            DrawElement(drawList, element, canvasOrigin, scale, resources, options);
        }

        PaintOrderBuffer.Clear();
    }

    /// <summary>Draws a single element at its logical Position/Size, scaled from canvasOrigin.</summary>
    internal static void DrawElement(
        ImDrawListPtr drawList, ProfileElement element, Vector2 canvasOrigin, float scale, ProfileRenderResources resources, in ProfileRenderOptions options)
    {
        switch (element)
        {
            case TextProfileElement textElement:
            {
                var screenPos = canvasOrigin + (textElement.Position * scale);
                var screenSize = textElement.Size * scale;

                if (options.ShowElementBounds)
                {
                    // Editor-only placement guide — never part of the finished profile.
                    drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(TextElementFillColor));
                    drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(TextElementBorderColor));
                }

                var placeholder = options.PlaceholderProvider?.Invoke(textElement);
                ProfileTextRenderer.Draw(drawList, textElement, screenPos, screenSize, scale, resources.Fonts, placeholder);
                break;
            }

            case ImageProfileElement imageElement:
                // Images may be rotated around their own center (text never is), so they're
                // drawn from their rotated corners rather than a plain screenPos/screenSize rect.
                DrawImageElement(drawList, imageElement, canvasOrigin, scale, resources);
                break;

            default:
            {
                var screenPos = canvasOrigin + (element.Position * scale);
                var screenSize = element.Size * scale;
                drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(UnknownElementFillColor));
                drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(UnknownElementBorderColor));
                break;
            }
        }
    }

    private static void DrawImageElement(ImDrawListPtr drawList, ImageProfileElement imageElement, Vector2 canvasOrigin, float scale, ProfileRenderResources resources)
    {
        var opacity = Math.Clamp(imageElement.Opacity, 0f, 1f);
        var wrap = resources.Images.GetWrapOrNull(imageElement.AssetId);

        if (wrap is null)
        {
            // Missing, still loading, or failed to decode: a visible placeholder rather than
            // nothing, using the same rotated geometry so a broken element stays identifiable,
            // selectable, movable, resizable, rotatable, and replaceable.
            var corners = RotationGeometry.GetRotatedCorners(imageElement.Position, imageElement.Size, imageElement.RotationDegrees);
            var tl = canvasOrigin + (corners[0] * scale);
            var tr = canvasOrigin + (corners[1] * scale);
            var br = canvasOrigin + (corners[2] * scale);
            var bl = canvasOrigin + (corners[3] * scale);
            drawList.AddQuadFilled(tl, tr, br, bl, ImGui.GetColorU32(MissingAssetFillColor));
            drawList.AddQuad(tl, tr, br, bl, ImGui.GetColorU32(MissingAssetBorderColor));
            return;
        }

        if (opacity <= 0f)
        {
            return;
        }

        // Fit/Fill/Stretch and flips are resolved in the element's own unrotated local box, and
        // only then rotated around the element's center and projected to screen — so every mode
        // rotates identically, and the editor and Profile View go through this exact path.
        var layout = ImageFitLayout.Compute(
            imageElement.DisplayMode,
            imageElement.Size,
            new Vector2(wrap.Width, wrap.Height),
            ImageFitLayout.FullSource,
            imageElement.FlipX,
            imageElement.FlipY);

        var center = RotationGeometry.GetCenter(imageElement.Position, imageElement.Size);
        var rotation = imageElement.RotationDegrees;

        Vector2 Project(Vector2 local) =>
            canvasOrigin + (RotationGeometry.RotatePoint(imageElement.Position + local, center, rotation) * scale);

        drawList.AddImageQuad(
            wrap.Handle,
            Project(layout.DrawMin),
            Project(new Vector2(layout.DrawMax.X, layout.DrawMin.Y)),
            Project(layout.DrawMax),
            Project(new Vector2(layout.DrawMin.X, layout.DrawMax.Y)),
            layout.UvTopLeft,
            layout.UvTopRight,
            layout.UvBottomRight,
            layout.UvBottomLeft,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, opacity)));
    }
}
