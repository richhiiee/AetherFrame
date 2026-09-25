using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Fonts;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Lays out and draws a <see cref="TextProfileElement"/>: explicit line breaks, word wrapping,
/// letter and line spacing, horizontal/vertical alignment, auto fit, outline, shadow, and
/// underline/strikethrough.
///
/// <para><b>Layout is computed in logical canvas units and cached per element</b>, keyed by every
/// input that affects it (text, font, size, box, spacing, wrap, auto fit). Consequences:</para>
/// <list type="bullet">
/// <item>Line breaks and the auto-fit size are computed once and then reused by every view — the
/// editor at any zoom, Clean Preview, and Profile View all draw the identical lines, rather than
/// each re-wrapping against whichever font size tier its own scale happens to rasterize at.</item>
/// <item>A frame where nothing changed does no measuring at all; only drawing.</item>
/// </list>
/// Advances are measured from the pushed font and normalized to its baked size, so any tier of the
/// family yields the same logical metrics. Text is never measured or drawn with the transient
/// cold-start fallback font (see <see cref="ProfileFontService"/>): until the family's own face is
/// built, the element is simply not drawn, so it never appears blurry first.
///
/// <para><b>Legacy layout.</b> Elements saved before <see cref="TextProfileElement.LayoutVersion"/>
/// existed render exactly as the old renderer did — no wrapping, 4 screen-pixel padding, and the
/// whole block aligned by its widest line — until the user explicitly opts in to the current
/// layout (see <see cref="TextProfileElement.LegacyLayoutVersion"/>).</para>
///
/// <para><b>Outline</b> is the classic dilation technique: the glyphs are drawn several times in
/// the outline color, offset around rings out to the outline thickness, then the fill is drawn on
/// top. <b>Shadow</b> draws the same (outlined) shape once more, offset, underneath. Both are
/// crisp by construction and work with every font face, style, alignment, and wrap mode.</para>
/// </summary>
internal static class ProfileTextRenderer
{
    /// <summary>Inset between the element box and its text, in logical pixels.</summary>
    internal const float PaddingLogical = TextProfileElement.LayoutPadding;

    /// <summary>
    /// The legacy layout's inset, in SCREEN pixels at every zoom — what every text element used
    /// before layout versions existed (see <see cref="TextProfileElement.LegacyLayoutVersion"/>).
    /// </summary>
    private const float LegacyPaddingScreenPixels = 4f;

    // Underline/strikethrough are drawn as plain lines rather than read from real font metrics,
    // positioned as a fraction of the rendered font size down from the line's top edge — the
    // usual approximation when exact metrics aren't used, close enough to sit convincingly
    // under/through glyphs at any size.
    private const float UnderlineOffsetRatio = 0.88f;
    private const float StrikethroughOffsetRatio = 0.5f;
    private const float DecorationThicknessRatio = 0.06f;
    private const float MinDecorationThickness = 1f;

    // Outline visual floor: a non-zero outline never vanishes entirely when heavily zoomed out.
    private const float MinOutlineScreenThickness = 1f;
    private const int MaxOutlineOffsets = 64;

    private const int AutoFitIterations = 12;
    private const int MaxCachedLayouts = 512;

    private const float PlaceholderAlpha = 0.35f;

    // Smallest size tier: measuring only needs normalized metrics, so any already-built tier works.
    private const float MeasureRequestPixels = 1f;

    private static readonly Dictionary<Guid, LayoutEntry> Layouts = new();
    private static readonly List<LineSpan> MeasureLines = new();
    private static readonly Dictionary<int, Vector2[]> OutlineOffsetsByThickness = new();
    private static readonly Vector2[] ZeroOffset = [Vector2.Zero];

    /// <summary>
    /// Draws <paramref name="element"/> into its screen box. When the element has no text,
    /// <paramref name="placeholder"/> (editor-only; always null for finished rendering) is drawn
    /// dimmed instead, without effects; with no placeholder, nothing is drawn.
    /// </summary>
    internal static void Draw(
        ImDrawListPtr drawList,
        TextProfileElement element,
        Vector2 screenPos,
        Vector2 screenSize,
        float scale,
        ProfileFontService fonts,
        string? placeholder,
        string? displayOverride = null)
    {
        // Prefix/Suffix decorations are composed here, at draw time (cached, no per-frame
        // allocation); the stored Text itself never contains them. A derived display (the Favorite
        // Jobs' abbreviations) replaces the composed text — a stable cached string, so the layout
        // cache below still recognizes it frame to frame.
        var isPlaceholder = string.IsNullOrEmpty(element.Text);
        var content = isPlaceholder ? placeholder : displayOverride ?? element.GetDisplayText();
        if (string.IsNullOrEmpty(content) || scale <= 0f)
        {
            return;
        }

        var textAlpha = Math.Clamp(element.Color.W, 0f, 1f) * (isPlaceholder ? PlaceholderAlpha : 1f);
        if (textAlpha <= 0f)
        {
            return;
        }

        // Measure with the element's nominal size tier; the layout itself is size-independent.
        // Hard readiness: text is never drawn (or measured) through a font that isn't this
        // family's own, fully built face — see IsReady. It simply appears a frame or two later,
        // crisp, instead of first appearing blurry.
        var measureHandle = fonts.GetHandle(element.FontFamily, Math.Max(1f, element.FontSize * scale), element.Bold, element.Italic, out var measureIsFallback);
        if (!IsReady(measureHandle, measureIsFallback))
        {
            return;
        }

        LayoutEntry layout;
        using (measureHandle.Push())
        {
            layout = GetOrBuildLayout(element, content);
        }

        var renderedFontSize = Math.Max(1f, layout.EffectiveFontSize * scale);
        var renderHandle = measureHandle;
        if (!layout.EffectiveFontSize.Equals(element.FontSize))
        {
            renderHandle = fonts.GetHandle(element.FontFamily, renderedFontSize, element.Bold, element.Italic, out var renderIsFallback);
            if (!IsReady(renderHandle, renderIsFallback))
            {
                return;
            }
        }

        using (renderHandle.Push())
        {
            DrawLayout(drawList, element, content, layout, screenPos, screenSize, scale, renderedFontSize, textAlpha, isPlaceholder);
        }
    }

    /// <summary>
    /// True only for a built handle of the requested family (the ideal size tier, or a larger
    /// already-built one, which only ever downscales). The cold-start fallback — Dalamud's global
    /// default font, which would be stretched up to size and look blurry — is never drawn with.
    /// </summary>
    private static bool IsReady(Dalamud.Interface.ManagedFontAtlas.IFontHandle handle, bool isColdStartFallback) =>
        !isColdStartFallback && handle.Available;

    /// <summary>
    /// The font size <paramref name="element"/> actually renders at: its own FontSize, or the
    /// reduced auto-fit size if one has been computed. Editor display only (e.g. the Inspector's
    /// "fitted to N px" hint); never affects stored data.
    /// </summary>
    internal static float? GetCachedEffectiveFontSize(TextProfileElement element) =>
        Layouts.TryGetValue(element.Id, out var entry) && ReferenceEquals(entry.Text, element.GetDisplayText()) ? entry.EffectiveFontSize : null;

    /// <summary>
    /// Measures the natural (unwrapped) width of <paramref name="element"/>'s display text at its
    /// own FontSize and letter spacing, in logical canvas pixels (the widest explicit line) — the
    /// exact metrics the renderer itself lays text out with, so a layout built from this lines up
    /// with what's drawn. Excludes padding. Returns false (width 0) if no built face of the
    /// element's font is available yet, rather than measuring with a stand-in font.
    /// </summary>
    internal static bool TryMeasureNaturalWidth(TextProfileElement element, ProfileFontService fonts, out float width) =>
        TryMeasureNaturalWidth(element, element.GetDisplayText(), fonts, out width);

    /// <summary>
    /// <see cref="TryMeasureNaturalWidth(TextProfileElement, ProfileFontService, out float)"/> for any
    /// <paramref name="text"/> in <paramref name="element"/>'s style (font, size, bold, italic, letter spacing).
    /// </summary>
    internal static bool TryMeasureNaturalWidth(TextProfileElement element, string text, ProfileFontService fonts, out float width)
    {
        width = 0f;
        if (text.Length == 0)
        {
            return true;
        }

        // Metrics are normalized to the face's baked size, so any built tier measures the same;
        // asking for the smallest tier lets GetHandle hand back whichever tier is already built.
        var handle = fonts.GetHandle(element.FontFamily, MeasureRequestPixels, element.Bold, element.Italic, out var isFallback);
        if (!IsReady(handle, isFallback))
        {
            return false;
        }

        using (handle.Push())
        {
            var metrics = new FontMetrics(ImGui.GetFont(), ImGui.GetFontSize());
            BuildLines(MeasureLines, text, metrics, Math.Max(1f, element.FontSize), element.LetterSpacing, float.PositiveInfinity, keepTrailingSpaces: false);
        }

        foreach (var line in MeasureLines)
        {
            width = Math.Max(width, line.Width);
        }

        MeasureLines.Clear();
        return true;
    }

    /// <summary>
    /// The number of lines <paramref name="element"/>'s display text word-wraps to at
    /// <paramref name="fontSize"/> within <paramref name="maxWidth"/> (logical canvas pixels, text
    /// padding excluded), with the renderer's own line breaking. False (0) while no built face of
    /// the element's font is available.
    /// </summary>
    internal static bool TryCountLines(TextProfileElement element, float fontSize, float maxWidth, ProfileFontService fonts, out int lines)
    {
        lines = 0;
        var text = element.GetDisplayText();
        if (text.Length == 0)
        {
            lines = 1;
            return true;
        }

        var handle = fonts.GetHandle(element.FontFamily, MeasureRequestPixels, element.Bold, element.Italic, out var isFallback);
        if (!IsReady(handle, isFallback))
        {
            return false;
        }

        using (handle.Push())
        {
            var metrics = new FontMetrics(ImGui.GetFont(), ImGui.GetFontSize());
            BuildLines(MeasureLines, text, metrics, Math.Max(1f, fontSize), element.LetterSpacing, Math.Max(1f, maxWidth), keepTrailingSpaces: false);
        }

        lines = MeasureLines.Count;
        MeasureLines.Clear();
        return true;
    }

    private static LayoutEntry GetOrBuildLayout(TextProfileElement element, string content)
    {
        if (Layouts.TryGetValue(element.Id, out var cached) && cached.Matches(element, content))
        {
            return cached;
        }

        if (cached is null)
        {
            if (Layouts.Count >= MaxCachedLayouts)
            {
                Layouts.Clear();
            }

            cached = new LayoutEntry();
            Layouts[element.Id] = cached;
        }

        cached.CaptureInputs(element, content);

        var metrics = new FontMetrics(ImGui.GetFont(), ImGui.GetFontSize());
        var box = element.Size;
        var available = new Vector2(Math.Max(1f, box.X - (2f * PaddingLogical)), Math.Max(1f, box.Y - (2f * PaddingLogical)));

        var size = Math.Max(1f, element.FontSize);
        var wrapWidth = element.EffectiveWrap ? available.X : float.PositiveInfinity;
        if (BasicNameFit.Applies(element))
        {
            // The Basic character name: one authoritative size (BasicNameFit, the rule its layout
            // was built with) instead of the generic auto fit, which would shrink it toward its
            // tiny auto-fit minimum whenever the box is narrower than the text.
            BuildLines(cached.Lines, content, metrics, size, element.LetterSpacing, float.PositiveInfinity, keepTrailingSpaces: false);
            var natural = 0f;
            foreach (var line in cached.Lines)
            {
                natural = Math.Max(natural, line.Width);
            }

            (size, var wrap) = BasicNameFit.Resolve(size, natural, available.X);
            if (wrap)
            {
                wrapWidth = available.X;
            }
        }
        else if (element.EffectiveAutoFit && !Fits(cached, content, metrics, size, available, element))
        {
            var min = Math.Clamp(element.AutoFitMinimumSize, 1f, size);
            if (!Fits(cached, content, metrics, min, available, element))
            {
                size = min;
            }
            else
            {
                // Largest size in [min, FontSize) that still fits.
                var low = min;
                var high = size;
                for (var i = 0; i < AutoFitIterations; i++)
                {
                    var mid = (low + high) / 2f;
                    if (Fits(cached, content, metrics, mid, available, element))
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                size = low;
            }
        }

        cached.EffectiveFontSize = size;
        BuildLines(cached.Lines, content, metrics, size, element.LetterSpacing, wrapWidth, element.UsesLegacyLayout);

        // Legacy layout aligns the whole block (its widest line) as one unit.
        cached.BlockWidth = 0f;
        foreach (var line in cached.Lines)
        {
            cached.BlockWidth = Math.Max(cached.BlockWidth, line.Width);
        }

        return cached;
    }

    private static bool Fits(LayoutEntry scratch, string content, FontMetrics metrics, float size, Vector2 available, TextProfileElement element)
    {
        BuildLines(scratch.Lines, content, metrics, size, element.LetterSpacing, element.EffectiveWrap ? available.X : float.PositiveInfinity, keepTrailingSpaces: false);

        var lineCount = scratch.Lines.Count;
        var blockHeight = ((lineCount - 1) * size * element.LineSpacing) + size;
        if (blockHeight > available.Y + 0.01f)
        {
            return false;
        }

        foreach (var line in scratch.Lines)
        {
            if (line.Width > available.X + 0.01f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into lines at explicit newlines and — when
    /// <paramref name="maxWidth"/> is finite — greedy word wrap, breaking inside a word only when
    /// the word alone is wider than a line. Widths are logical pixels at <paramref name="size"/>,
    /// excluding trailing spaces (unless <paramref name="keepTrailingSpaces"/>, which the legacy
    /// layout uses to measure exactly as ImGui's CalcTextSize did).
    /// </summary>
    private static void BuildLines(List<LineSpan> lines, string text, FontMetrics metrics, float size, float letterSpacing, float maxWidth, bool keepTrailingSpaces)
    {
        lines.Clear();

        var paragraphStart = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', paragraphStart);
            var paragraphEnd = newline < 0 ? text.Length : newline;
            var contentEnd = paragraphEnd > paragraphStart && text[paragraphEnd - 1] == '\r' ? paragraphEnd - 1 : paragraphEnd;

            LayoutParagraph(lines, text, paragraphStart, contentEnd, metrics, size, letterSpacing, maxWidth, keepTrailingSpaces);

            if (newline < 0)
            {
                break;
            }

            paragraphStart = newline + 1;
        }
    }

    private static void LayoutParagraph(List<LineSpan> lines, string text, int start, int end, FontMetrics metrics, float size, float letterSpacing, float maxWidth, bool keepTrailingSpaces)
    {
        if (start >= end)
        {
            lines.Add(new LineSpan(start, 0, 0f));
            return;
        }

        if (float.IsPositiveInfinity(maxWidth))
        {
            lines.Add(MakeLine(text, start, end, metrics, size, letterSpacing, keepTrailingSpaces));
            return;
        }

        var lineStart = start;
        while (lineStart < end)
        {
            var width = 0f;
            var lastSpace = -1;
            var i = lineStart;
            var previous = '\0';

            for (; i < end; i++)
            {
                var c = text[i];
                var advance = metrics.Advance(c, size);
                if (i > lineStart)
                {
                    advance += metrics.Kerning(previous, c, size) + letterSpacing;
                }

                if (c == ' ')
                {
                    lastSpace = i;
                }
                else if (width + advance > maxWidth && i > lineStart)
                {
                    break;
                }

                width += advance;
                previous = c;
            }

            if (i >= end)
            {
                lines.Add(MakeLine(text, lineStart, end, metrics, size, letterSpacing, keepTrailingSpaces: false));
                break;
            }

            // Overflowed at i: break after the last space if this line has one, else mid-word.
            var lineEnd = lastSpace > lineStart ? lastSpace : i;
            lines.Add(MakeLine(text, lineStart, lineEnd, metrics, size, letterSpacing, keepTrailingSpaces: false));

            // Continuation lines never start with the spaces a wrap consumed.
            lineStart = lineEnd;
            while (lineStart < end && text[lineStart] == ' ')
            {
                lineStart++;
            }
        }
    }

    private static LineSpan MakeLine(string text, int start, int end, FontMetrics metrics, float size, float letterSpacing, bool keepTrailingSpaces)
    {
        var visibleEnd = end;
        while (!keepTrailingSpaces && visibleEnd > start && text[visibleEnd - 1] == ' ')
        {
            visibleEnd--;
        }

        var width = 0f;
        var previous = '\0';
        for (var i = start; i < visibleEnd; i++)
        {
            var c = text[i];
            width += metrics.Advance(c, size);
            if (i > start)
            {
                width += metrics.Kerning(previous, c, size) + letterSpacing;
            }

            previous = c;
        }

        return new LineSpan(start, end - start, width);
    }

    private static void DrawLayout(
        ImDrawListPtr drawList,
        TextProfileElement element,
        string content,
        LayoutEntry layout,
        Vector2 screenPos,
        Vector2 screenSize,
        float scale,
        float renderedFontSize,
        float textAlpha,
        bool isPlaceholder)
    {
        var font = ImGui.GetFont();
        var bakedFontSize = ImGui.GetFontSize();
        var padding = element.UsesLegacyLayout ? LegacyPaddingScreenPixels : PaddingLogical * scale;
        var lineHeight = renderedFontSize * element.LineSpacing;
        var lines = layout.Lines;
        var blockHeight = ((lines.Count - 1) * lineHeight) + renderedFontSize;

        var top = element.VerticalAlignment switch
        {
            TextVerticalAlignment.Middle => screenPos.Y + ((screenSize.Y - blockHeight) / 2f),
            TextVerticalAlignment.Bottom => screenPos.Y + screenSize.Y - padding - blockHeight,
            _ => screenPos.Y + padding,
        };

        var drawEffects = !isPlaceholder;
        var outlineThickness = drawEffects && element.OutlineEnabled && element.OutlineThickness > 0f
            ? Math.Max(MinOutlineScreenThickness, Math.Min(element.OutlineThickness, TextProfileElement.MaxOutlineThickness) * scale)
            : 0f;
        var shadowOffset = drawEffects && element.ShadowEnabled
            ? new Vector2(element.ShadowOffsetX, element.ShadowOffsetY) * scale
            : (Vector2?)null;

        // Clip to the element box, grown by whatever the effects extend past the glyphs, so an
        // outline or shadow on text touching the box edge isn't sliced off.
        var effectMargin = outlineThickness + (shadowOffset is { } s ? Math.Max(Math.Abs(s.X), Math.Abs(s.Y)) : 0f);
        drawList.PushClipRect(screenPos - new Vector2(effectMargin), screenPos + screenSize + new Vector2(effectMargin), true);

        var context = new DrawContext(drawList, font, element, content, lines, screenPos, screenSize, scale, renderedFontSize, bakedFontSize, padding, lineHeight, top, layout.BlockWidth * scale);
        var outlineOffsets = outlineThickness > 0f ? GetOutlineOffsets(outlineThickness) : null;

        if (shadowOffset is { } shadow)
        {
            var shadowAlpha = textAlpha * Math.Clamp(element.ShadowOpacity, 0f, 1f);
            DrawPassGroup(ref context, shadow, outlineOffsets ?? ZeroOffset, element.ShadowColor, shadowAlpha, outlineThickness, includeCenter: true);
        }

        if (outlineOffsets is not null)
        {
            var outlineAlpha = textAlpha * Math.Clamp(element.OutlineOpacity, 0f, 1f);
            DrawPassGroup(ref context, Vector2.Zero, outlineOffsets, element.OutlineColor, outlineAlpha, outlineThickness, includeCenter: false);
        }

        var fillColor = ImGui.GetColorU32(element.Color with { W = textAlpha });
        DrawGlyphs(ref context, Vector2.Zero, fillColor);
        DrawDecorations(ref context, Vector2.Zero, fillColor, 0f);

        drawList.PopClipRect();
    }

    /// <summary>
    /// Draws the glyphs once per offset (plus optionally the unshifted shape), then the line
    /// decorations once, thickened to match. Overlapping passes would otherwise compound a
    /// translucent color toward opaque, so each pass's alpha is reduced such that the typical
    /// overlap still lands near the requested opacity; fully opaque colors are unaffected.
    /// </summary>
    private static void DrawPassGroup(ref DrawContext context, Vector2 baseOffset, Vector2[] offsets, Vector4 rgb, float alpha, float outlineThickness, bool includeCenter)
    {
        if (alpha <= 0f)
        {
            return;
        }

        var passCount = offsets.Length + (includeCenter ? 1 : 0);
        var typicalOverlap = Math.Max(1f, passCount / 3f);
        var passAlpha = alpha >= 0.999f ? 1f : 1f - MathF.Pow(1f - alpha, 1f / typicalOverlap);
        var passColor = ImGui.GetColorU32(rgb with { W = passAlpha });

        if (includeCenter && offsets != ZeroOffset)
        {
            DrawGlyphs(ref context, baseOffset, passColor);
        }

        foreach (var offset in offsets)
        {
            DrawGlyphs(ref context, baseOffset + offset, passColor);
        }

        DrawDecorations(ref context, baseOffset, ImGui.GetColorU32(rgb with { W = alpha }), outlineThickness * 2f);
    }

    private static void DrawGlyphs(ref DrawContext c, Vector2 offset, uint color)
    {
        var letterSpacing = c.Element.LetterSpacing * c.Scale;
        var glyphScale = c.BakedFontSize > 0f ? c.RenderedFontSize / c.BakedFontSize : 1f;

        for (var index = 0; index < c.Lines.Count; index++)
        {
            var line = c.Lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            var origin = new Vector2(LineX(ref c, line), c.Top + (index * c.LineHeight)) + offset;

            if (letterSpacing == 0f)
            {
                // Whole line in one call: ImGui's own glyph placement, exactly like legacy text.
                c.DrawList.AddText(c.Font, c.RenderedFontSize, origin, color, c.Content.AsSpan(line.Start, line.Length));
                continue;
            }

            // Letter-spaced: place glyphs individually, each through the same AddText path as
            // unspaced text (one-character spans: no allocation), so glyphs render identically.
            var x = origin.X;
            var previous = '\0';
            var end = line.Start + line.Length;
            for (var i = line.Start; i < end; i++)
            {
                var ch = c.Content[i];
                if (i > line.Start)
                {
                    x += (c.Font.GetDistanceAdjustmentForPair(previous, ch) * glyphScale) + letterSpacing;
                }

                // A surrogate pair is one character: draw both halves together.
                var length = char.IsHighSurrogate(ch) && i + 1 < end ? 2 : 1;

                if (ch != ' ')
                {
                    c.DrawList.AddText(c.Font, c.RenderedFontSize, new Vector2(x, origin.Y), color, c.Content.AsSpan(i, length));
                }

                x += c.Font.GetCharAdvance(ch) * glyphScale;
                previous = ch;
                i += length - 1;
            }
        }
    }

    private static void DrawDecorations(ref DrawContext c, Vector2 offset, uint color, float extraThickness)
    {
        if (!c.Element.Underline && !c.Element.Strikethrough)
        {
            return;
        }

        var thickness = Math.Max(MinDecorationThickness, c.RenderedFontSize * DecorationThicknessRatio) + extraThickness;
        var grow = extraThickness / 2f;

        for (var index = 0; index < c.Lines.Count; index++)
        {
            var line = c.Lines[index];
            if (line.Width <= 0f)
            {
                continue;
            }

            var left = LineX(ref c, line) + offset.X;
            var right = left + (line.Width * c.Scale);
            var lineTop = c.Top + (index * c.LineHeight) + offset.Y;

            if (c.Element.Underline)
            {
                var y = lineTop + (c.RenderedFontSize * UnderlineOffsetRatio);
                c.DrawList.AddLine(new Vector2(left - grow, y), new Vector2(right + grow, y), color, thickness);
            }

            if (c.Element.Strikethrough)
            {
                var y = lineTop + (c.RenderedFontSize * StrikethroughOffsetRatio);
                c.DrawList.AddLine(new Vector2(left - grow, y), new Vector2(right + grow, y), color, thickness);
            }
        }
    }

    /// <summary>
    /// Left edge of a line for the element's horizontal alignment. A line wider than the box falls
    /// back to the left edge rather than overflowing left. The legacy layout aligns the whole block
    /// by its widest line (every line then starts at the same x), exactly as the old renderer's
    /// single AddText of the full text did.
    /// </summary>
    private static float LineX(ref DrawContext c, LineSpan line)
    {
        var width = c.Element.UsesLegacyLayout ? c.LegacyBlockWidth : line.Width * c.Scale;
        return c.Element.Alignment switch
        {
            TextAlignment.Center => c.BoxPos.X + Math.Max(0f, (c.BoxSize.X - width) / 2f),
            TextAlignment.Right => c.BoxPos.X + Math.Max(0f, c.BoxSize.X - width - c.Padding),
            _ => c.BoxPos.X + c.Padding,
        };
    }

    /// <summary>
    /// Integer pixel offsets (glyph positions are pixel-snapped by ImGui anyway) on concentric
    /// rings out to <paramref name="thickness"/>, dense enough that the union of shifted glyphs
    /// forms a solid outline without gaps, cached per rounded thickness.
    /// </summary>
    private static Vector2[] GetOutlineOffsets(float thickness)
    {
        var radius = Math.Max(1, (int)MathF.Round(thickness));
        if (OutlineOffsetsByThickness.TryGetValue(radius, out var cached))
        {
            return cached;
        }

        var offsets = new List<Vector2>();
        var seen = new HashSet<(int, int)>();
        var ringStep = Math.Max(1.5f, radius / 4f);
        var sampleSpacing = Math.Max(1.25f, radius / 8f);

        for (var r = (float)radius; r > 0.5f; r -= ringStep)
        {
            var samples = Math.Clamp((int)MathF.Ceiling(2f * MathF.PI * r / sampleSpacing), 8, 32);
            for (var k = 0; k < samples; k++)
            {
                var angle = 2f * MathF.PI * k / samples;
                var dx = (int)MathF.Round(r * MathF.Cos(angle));
                var dy = (int)MathF.Round(r * MathF.Sin(angle));
                if ((dx != 0 || dy != 0) && seen.Add((dx, dy)))
                {
                    offsets.Add(new Vector2(dx, dy));
                }
            }
        }

        if (offsets.Count > MaxOutlineOffsets)
        {
            // Outer ring first in the list, so an even subsample keeps the silhouette intact.
            var reduced = new List<Vector2>(MaxOutlineOffsets);
            for (var i = 0; i < MaxOutlineOffsets; i++)
            {
                reduced.Add(offsets[i * offsets.Count / MaxOutlineOffsets]);
            }

            offsets = reduced;
        }

        var result = offsets.ToArray();
        OutlineOffsetsByThickness[radius] = result;
        return result;
    }

    private readonly record struct LineSpan(int Start, int Length, float Width);

    /// <summary>Normalized (logical, size-independent) glyph metrics from the pushed font.</summary>
    private readonly struct FontMetrics
    {
        private readonly ImFontPtr font;
        private readonly float inverseBakedSize;

        internal FontMetrics(ImFontPtr font, float bakedSize)
        {
            this.font = font;
            inverseBakedSize = bakedSize > 0f ? 1f / bakedSize : 0f;
        }

        internal float Advance(char c, float size) => font.GetCharAdvance(c) * inverseBakedSize * size;

        internal float Kerning(char left, char right, float size) =>
            left == '\0' ? 0f : font.GetDistanceAdjustmentForPair(left, right) * inverseBakedSize * size;
    }

    private ref struct DrawContext
    {
        internal readonly ImDrawListPtr DrawList;
        internal readonly ImFontPtr Font;
        internal readonly TextProfileElement Element;
        internal readonly string Content;
        internal readonly List<LineSpan> Lines;
        internal readonly Vector2 BoxPos;
        internal readonly Vector2 BoxSize;
        internal readonly float Scale;
        internal readonly float RenderedFontSize;
        internal readonly float BakedFontSize;
        internal readonly float Padding;
        internal readonly float LineHeight;
        internal readonly float Top;
        internal readonly float LegacyBlockWidth;

        internal DrawContext(
            ImDrawListPtr drawList,
            ImFontPtr font,
            TextProfileElement element,
            string content,
            List<LineSpan> lines,
            Vector2 boxPos,
            Vector2 boxSize,
            float scale,
            float renderedFontSize,
            float bakedFontSize,
            float padding,
            float lineHeight,
            float top,
            float legacyBlockWidth)
        {
            DrawList = drawList;
            Font = font;
            Element = element;
            Content = content;
            Lines = lines;
            BoxPos = boxPos;
            BoxSize = boxSize;
            Scale = scale;
            RenderedFontSize = renderedFontSize;
            BakedFontSize = bakedFontSize;
            Padding = padding;
            LineHeight = lineHeight;
            Top = top;
            LegacyBlockWidth = legacyBlockWidth;
        }
    }

    /// <summary>A cached layout plus the exact inputs it was computed from.</summary>
    private sealed class LayoutEntry
    {
        internal readonly List<LineSpan> Lines = new();

        internal float EffectiveFontSize;
        internal float BlockWidth;

        internal string Text = string.Empty;
        private string fontFamily = string.Empty;
        private bool bold;
        private bool italic;
        private bool wrap;
        private bool autoFit;
        private int layoutVersion;
        private float fontSize;
        private float autoFitMinimum;
        private float letterSpacing;
        private float lineSpacing;
        private Vector2 boxSize;

        internal bool Matches(TextProfileElement e, string content) =>
            (ReferenceEquals(Text, content) || Text == content)
            && fontFamily == e.FontFamily
            && bold == e.Bold
            && italic == e.Italic
            && wrap == e.EffectiveWrap
            && autoFit == e.EffectiveAutoFit
            && layoutVersion == e.LayoutVersion
            && fontSize.Equals(e.FontSize)
            && autoFitMinimum.Equals(e.AutoFitMinimumSize)
            && letterSpacing.Equals(e.LetterSpacing)
            && lineSpacing.Equals(e.LineSpacing)
            && boxSize == e.Size;

        internal void CaptureInputs(TextProfileElement e, string content)
        {
            Text = content;
            fontFamily = e.FontFamily;
            bold = e.Bold;
            italic = e.Italic;
            wrap = e.EffectiveWrap;
            autoFit = e.EffectiveAutoFit;
            layoutVersion = e.LayoutVersion;
            fontSize = e.FontSize;
            autoFitMinimum = e.AutoFitMinimumSize;
            letterSpacing = e.LetterSpacing;
            lineSpacing = e.LineSpacing;
            boxSize = e.Size;
        }
    }
}
