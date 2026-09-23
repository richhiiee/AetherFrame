using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Fonts;
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
    /// <paramref name="scale"/> from the profile's own logical canvas size
    /// (<see cref="ProfileDocument.CanvasWidth"/>/<see cref="ProfileDocument.CanvasHeight"/>).
    /// <paramref name="showElementBounds"/> is purely editor chrome (the translucent box/border
    /// drawn behind each text element as a placement guide) — the read-only presentation window
    /// always passes false, since a finished profile must never show editing boxes.
    /// </summary>
    internal static void Draw(
        ImDrawListPtr drawList,
        ProfileDocument profile,
        Vector2 canvasOrigin,
        float scale,
        ImageTextureCache imageTextureCache,
        ProfileFontService fontService,
        bool showElementBounds)
    {
        // Cheap after the first call for a given profile instance (a single reference check) —
        // see ProfileFontService for why this keeps large text crisp instead of blurry-then-sharp.
        fontService.EnsurePrewarmed(profile);

        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * scale;

        drawList.AddRectFilled(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(CanvasBackdropColor));

        DrawBackground(drawList, profile, canvasOrigin, canvasScreenSize, imageTextureCache);

        // Paint order: ascending ZIndex, ties broken by list (insertion) order, so a later
        // element paints over an earlier one.
        var visibleElements = profile.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex);

        foreach (var element in visibleElements)
        {
            DrawElement(drawList, element, canvasOrigin, scale, imageTextureCache, fontService, showElementBounds);
        }
    }

    /// <summary>Draws a single element at its logical Position/Size, scaled from canvasOrigin.</summary>
    internal static void DrawElement(
        ImDrawListPtr drawList, ProfileElement element, Vector2 canvasOrigin, float scale, ImageTextureCache imageTextureCache, ProfileFontService fontService, bool showElementBounds)
    {
        switch (element)
        {
            case TextProfileElement textElement:
            {
                var screenPos = canvasOrigin + textElement.Position * scale;
                var screenSize = textElement.Size * scale;
                DrawTextElement(drawList, textElement, screenPos, screenSize, scale, fontService, showElementBounds);
                break;
            }

            case ImageProfileElement imageElement:
                // Images may be rotated around their own center (text never is), so they're
                // drawn from their rotated corners rather than a plain screenPos/screenSize rect.
                DrawImageElement(drawList, imageElement, canvasOrigin, scale, imageTextureCache);
                break;

            default:
            {
                var screenPos = canvasOrigin + element.Position * scale;
                var screenSize = element.Size * scale;
                drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(UnknownElementFillColor));
                drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(UnknownElementBorderColor));
                break;
            }
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

    // Underline/strikethrough are drawn as plain lines rather than read from real font metrics
    // (this ImGui binding doesn't expose per-font ascent/descent), positioned as a fraction of
    // the rendered font size down from the text's top edge — the usual approximation for this
    // when exact metrics aren't available, close enough to sit convincingly under/through glyphs
    // at any size.
    private const float UnderlineOffsetRatio = 0.88f;
    private const float StrikethroughOffsetRatio = 0.5f;
    private const float DecorationThicknessRatio = 0.06f;
    private const float MinDecorationThickness = 1f;

    private static void DrawTextElement(ImDrawListPtr drawList, TextProfileElement textElement, Vector2 screenPos, Vector2 screenSize, float scale, ProfileFontService fontService, bool showElementBounds)
    {
        if (showElementBounds)
        {
            // Editor-only placement guide — never part of the finished profile's visual content,
            // so the presentation window and Basic Editor's preview always pass showElementBounds
            // false and never see it.
            drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(TextElementFillColor));
            drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(TextElementBorderColor));
        }

        var text = string.IsNullOrEmpty(textElement.Text) ? "(empty)" : textElement.Text;

        // The element's own FontSize, scaled to actual screen pixels. Requesting a font handle
        // baked at (approximately) this exact pixel size — rather than always using ImGui's
        // small default UI font and stretching its glyphs up via the AddText size parameter — is
        // what keeps large profile text crisp instead of blurry.
        var renderedFontSize = Math.Max(1f, textElement.FontSize * scale);
        var fontHandle = fontService.GetHandle(textElement.FontFamily, renderedFontSize, textElement.Bold, textElement.Italic);

        using (fontHandle.Push())
        {
            var font = ImGui.GetFont();

            // The handle's font was baked at the nearest whole pixel to renderedFontSize (see
            // ProfileFontService), not necessarily that exact value, so CalcTextSize (which
            // measures at the pushed font's own baked size) needs the same tiny correction
            // AddText's explicit size parameter gets automatically. The correction is always
            // well under a pixel of visual difference.
            var bakedFontSize = ImGui.GetFontSize();
            var sizeScale = bakedFontSize > 0f ? renderedFontSize / bakedFontSize : 1f;
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
            DrawTextDecorations(drawList, textElement, textPos, textSize, renderedFontSize);
            drawList.PopClipRect();
        }
    }

    /// <summary>
    /// Underline/strikethrough for the single line of text just drawn at <paramref name="textPos"/>
    /// with measured extent <paramref name="textSize"/>. Uses the same color, and spans exactly
    /// the rendered text's width — nothing to draw for empty text, since <paramref name="textSize"/>
    /// is then ~0.
    /// </summary>
    private static void DrawTextDecorations(ImDrawListPtr drawList, TextProfileElement textElement, Vector2 textPos, Vector2 textSize, float renderedFontSize)
    {
        if ((!textElement.Underline && !textElement.Strikethrough) || textSize.X <= 0f)
        {
            return;
        }

        var color = ImGui.GetColorU32(textElement.Color);
        var thickness = Math.Max(MinDecorationThickness, renderedFontSize * DecorationThicknessRatio);
        var left = textPos.X;
        var right = textPos.X + textSize.X;

        if (textElement.Underline)
        {
            var y = textPos.Y + (renderedFontSize * UnderlineOffsetRatio);
            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), color, thickness);
        }

        if (textElement.Strikethrough)
        {
            var y = textPos.Y + (renderedFontSize * StrikethroughOffsetRatio);
            drawList.AddLine(new Vector2(left, y), new Vector2(right, y), color, thickness);
        }
    }

    private static readonly Vector2 QuadUvTopLeft = new(0f, 0f);
    private static readonly Vector2 QuadUvTopRight = new(1f, 0f);
    private static readonly Vector2 QuadUvBottomRight = new(1f, 1f);
    private static readonly Vector2 QuadUvBottomLeft = new(0f, 1f);

    private static void DrawImageElement(ImDrawListPtr drawList, ImageProfileElement imageElement, Vector2 canvasOrigin, float scale, ImageTextureCache imageTextureCache)
    {
        // Corners are computed in logical space (respecting rotation around the element's own
        // center) and only then projected to screen space, so rotation renders identically
        // regardless of the caller's canvas origin/zoom — the editor canvas and the read-only
        // presentation window both go through this exact path.
        var corners = RotationGeometry.GetRotatedCorners(imageElement.Position, imageElement.Size, imageElement.RotationDegrees);
        var topLeft = canvasOrigin + (corners[0] * scale);
        var topRight = canvasOrigin + (corners[1] * scale);
        var bottomRight = canvasOrigin + (corners[2] * scale);
        var bottomLeft = canvasOrigin + (corners[3] * scale);

        var wrap = imageTextureCache.GetWrapOrNull(imageElement.AssetId);

        if (wrap is null)
        {
            // Missing, still loading, or failed to decode: a visible placeholder rather than
            // nothing, using the same rotated geometry so a broken element stays identifiable,
            // selectable, movable, resizable, rotatable, and replaceable.
            drawList.AddQuadFilled(topLeft, topRight, bottomRight, bottomLeft, ImGui.GetColorU32(MissingAssetFillColor));
            drawList.AddQuad(topLeft, topRight, bottomRight, bottomLeft, ImGui.GetColorU32(MissingAssetBorderColor));
            return;
        }

        var tint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, Math.Clamp(imageElement.Opacity, 0f, 1f)));
        drawList.AddImageQuad(wrap.Handle, topLeft, topRight, bottomRight, bottomLeft, QuadUvTopLeft, QuadUvTopRight, QuadUvBottomRight, QuadUvBottomLeft, tint);
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
