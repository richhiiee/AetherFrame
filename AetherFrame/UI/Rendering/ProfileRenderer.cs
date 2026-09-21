using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Draws the visual content of a <see cref="ProfileDocument"/> (background, then every visible
/// element in Z order) into an ImGui draw list at an arbitrary screen origin and uniform scale.
///
/// This is purely a rendering service: it owns no editing state and knows nothing about
/// selection, dragging, resizing, hit testing, or undo/redo — those remain editor concerns in
/// <c>ProfileEditorWindow</c>. Both the editor canvas and the read-only presentation window
/// (<c>ProfileViewWindow</c>) call into this class so the two never duplicate paint logic.
///
/// Deliberately takes only a <see cref="ProfileDocument"/> and an <see cref="ImageTextureCache"/>
/// — no character/ownership identity — so it can later render a profile that didn't originate
/// from the locally logged-in character (e.g. one received over the network) without change.
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

    /// <summary>
    /// Draws the full logical canvas (backdrop, background image, every visible element in Z
    /// order) starting at <paramref name="canvasOrigin"/> in screen space, uniformly scaled by
    /// <paramref name="scale"/> from the profile's logical 1920x1080 canvas.
    /// </summary>
    internal static void Draw(
        ImDrawListPtr drawList,
        ProfileDocument profile,
        Vector2 canvasOrigin,
        float scale,
        ImageTextureCache imageTextureCache)
    {
        var canvasScreenSize = new Vector2(ProfileDocument.CanvasWidth, ProfileDocument.CanvasHeight) * scale;

        drawList.AddRectFilled(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(CanvasBackdropColor));

        DrawBackground(drawList, profile, canvasOrigin, canvasScreenSize, imageTextureCache);

        // Paint order: ascending ZIndex, ties broken by list (insertion) order, so a later
        // element paints over an earlier one.
        var visibleElements = profile.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex);

        foreach (var element in visibleElements)
        {
            DrawElement(drawList, element, canvasOrigin, scale, imageTextureCache);
        }
    }

    /// <summary>Draws a single element at its logical Position/Size, scaled from canvasOrigin.</summary>
    internal static void DrawElement(
        ImDrawListPtr drawList, ProfileElement element, Vector2 canvasOrigin, float scale, ImageTextureCache imageTextureCache)
    {
        var screenPos = canvasOrigin + element.Position * scale;
        var screenSize = element.Size * scale;

        switch (element)
        {
            case TextProfileElement textElement:
                DrawTextElement(drawList, textElement, screenPos, screenSize, scale);
                break;
            case ImageProfileElement imageElement:
                DrawImageElement(drawList, imageElement, screenPos, screenSize, imageTextureCache);
                break;
            default:
                drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(UnknownElementFillColor));
                drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(UnknownElementBorderColor));
                break;
        }
    }

    internal static void DrawBackground(
        ImDrawListPtr drawList, ProfileDocument profile, Vector2 canvasOrigin, Vector2 canvasScreenSize, ImageTextureCache imageTextureCache)
    {
        if (profile.BackgroundAssetId is not { } assetId)
        {
            return;
        }

        var wrap = imageTextureCache.GetWrapOrNull(assetId);
        if (wrap is null)
        {
            drawList.AddRectFilled(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(new Vector4(0.35f, 0.12f, 0.12f, 0.5f)));
            return;
        }

        var (drawPos, drawSize, uvMin, uvMax) = ComputeBackgroundLayout(
            profile.BackgroundFitMode, wrap.Width, wrap.Height, canvasOrigin, canvasScreenSize);

        var tint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, Math.Clamp(profile.BackgroundOpacity, 0f, 1f)));
        drawList.AddImage(wrap.Handle, drawPos, drawPos + drawSize, uvMin, uvMax, tint);
    }

    private static void DrawTextElement(ImDrawListPtr drawList, TextProfileElement textElement, Vector2 screenPos, Vector2 screenSize, float scale)
    {
        drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(TextElementFillColor));
        drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(TextElementBorderColor));

        var text = string.IsNullOrEmpty(textElement.Text) ? "(empty)" : textElement.Text;

        // Render at the element's own FontSize (scaled), not the current default ImGui font
        // size, so the Font Size control actually changes what's drawn. There's no
        // CalcTextSizeA(font, size, ...) in this binding, so approximate the rendered extent by
        // scaling the default-size measurement — glyph metrics scale linearly.
        var font = ImGui.GetFont();
        var renderedFontSize = Math.Max(1f, textElement.FontSize * scale);
        var sizeScale = renderedFontSize / ImGui.GetFontSize();
        var textSize = ImGui.CalcTextSize(text) * sizeScale;
        var textPos = screenPos + new Vector2(4f, 4f);

        if (textElement.Alignment == TextAlignment.Center)
        {
            textPos.X = screenPos.X + Math.Max(0f, (screenSize.X - textSize.X) / 2f);
        }
        else if (textElement.Alignment == TextAlignment.Right)
        {
            textPos.X = screenPos.X + Math.Max(0f, screenSize.X - textSize.X - 4f);
        }

        drawList.PushClipRect(screenPos, screenPos + screenSize, true);
        drawList.AddText(font, renderedFontSize, textPos, ImGui.GetColorU32(textElement.Color), text);
        drawList.PopClipRect();
    }

    private static void DrawImageElement(ImDrawListPtr drawList, ImageProfileElement imageElement, Vector2 screenPos, Vector2 screenSize, ImageTextureCache imageTextureCache)
    {
        var wrap = imageTextureCache.GetWrapOrNull(imageElement.AssetId);

        if (wrap is null)
        {
            // Missing, still loading, or failed to decode: a visible placeholder rather than
            // nothing, so a broken image element is still identifiable.
            drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(MissingAssetFillColor));
            drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(MissingAssetBorderColor));
            return;
        }

        var tint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, Math.Clamp(imageElement.Opacity, 0f, 1f)));
        drawList.AddImage(wrap.Handle, screenPos, screenPos + screenSize, Vector2.Zero, Vector2.One, tint);
    }

    /// <summary>
    /// Computes the screen-space rect and UV window to draw a background texture with, for the
    /// given fit mode. Cover crops via UV (drawn rect always fills the canvas); Contain shrinks
    /// the drawn rect instead (letterboxed, full UV); Stretch fills the canvas with full UV,
    /// ignoring aspect ratio.
    /// </summary>
    private static (Vector2 Position, Vector2 Size, Vector2 UvMin, Vector2 UvMax) ComputeBackgroundLayout(
        BackgroundFitMode fitMode, int textureWidth, int textureHeight, Vector2 canvasOrigin, Vector2 canvasSize)
    {
        if (textureWidth <= 0 || textureHeight <= 0 || fitMode == BackgroundFitMode.Stretch || canvasSize.X <= 0f || canvasSize.Y <= 0f)
        {
            return (canvasOrigin, canvasSize, Vector2.Zero, Vector2.One);
        }

        var textureAspect = textureWidth / (float)textureHeight;
        var canvasAspect = canvasSize.X / canvasSize.Y;

        if (fitMode == BackgroundFitMode.Cover)
        {
            Vector2 uvMin, uvMax;
            if (textureAspect > canvasAspect)
            {
                var visibleFraction = canvasAspect / textureAspect;
                var margin = (1f - visibleFraction) / 2f;
                uvMin = new Vector2(margin, 0f);
                uvMax = new Vector2(1f - margin, 1f);
            }
            else
            {
                var visibleFraction = textureAspect / canvasAspect;
                var margin = (1f - visibleFraction) / 2f;
                uvMin = new Vector2(0f, margin);
                uvMax = new Vector2(1f, 1f - margin);
            }

            return (canvasOrigin, canvasSize, uvMin, uvMax);
        }

        // Contain: shrink the drawn rect to fit fully inside the canvas, centered.
        var drawSize = textureAspect > canvasAspect
            ? new Vector2(canvasSize.X, canvasSize.X / textureAspect)
            : new Vector2(canvasSize.Y * textureAspect, canvasSize.Y);

        var offset = (canvasSize - drawSize) / 2f;
        return (canvasOrigin + offset, drawSize, Vector2.Zero, Vector2.One);
    }
}
