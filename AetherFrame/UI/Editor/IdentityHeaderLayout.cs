using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Computes where the Identity Header's elements go for a curated <see cref="IdentityTitleLayout"/>:
/// pure geometry (no ImGui, no profile mutation), from the header region, each element's font
/// size, and — for the one-line layouts — each element's measured text width.
///
/// It only ever positions and sizes the three separate elements; it never composes their text.
/// Stacked layouts give every line the full region width (each element's own alignment then
/// places its text); inline layouts size each box to its text and place them side by side,
/// aligned as a group by the name's alignment, with their baselines lined up.
/// </summary>
internal static class IdentityHeaderLayout
{
    private const float Padding = TextProfileElement.LayoutPadding;

    // Line box height as a multiple of font size (room for descenders, italics, and a thin
    // outline) plus top/bottom padding; baseline as a fraction of font size from the line top.
    private const float LineHeightRatio = 1.2f;
    private const float BaselineRatio = 0.8f;

    // Vertical gap between stacked lines, and the visible gap between inline title and name,
    // both relative to the name's size.
    private const float LineGapRatio = 0.08f;
    private const float InlineGapRatio = 0.3f;

    // A little extra width on measured inline boxes so rounding can never clip the last glyph.
    private const float InlineSlack = 2f;

    /// <summary>One header line's inputs.</summary>
    /// <param name="Exists">Whether the element exists at all (absent elements get no rect).</param>
    /// <param name="Visible">Hidden elements are placed but take up no space.</param>
    /// <param name="FontSize">The element's font size.</param>
    /// <param name="TextWidth">Natural text width (needed only by the inline layouts).</param>
    internal readonly record struct Line(bool Exists, bool Visible, float FontSize, float TextWidth);

    internal readonly record struct Result(ElementRect? Name, ElementRect? Title, ElementRect? Tagline);

    internal static float BoxHeight(float fontSize) => MathF.Ceiling((fontSize * LineHeightRatio) + (2f * Padding));

    internal static Result Compute(
        IdentityTitleLayout layout, Vector2 regionPosition, float regionWidth, TextAlignment groupAlignment, Line name, Line title, Line tagline)
    {
        regionWidth = Math.Max(1f, regionWidth);
        var gap = MathF.Round(Math.Max(name.FontSize, title.FontSize) * LineGapRatio);

        ElementRect? nameRect = null, titleRect = null;
        float y;

        if (layout is IdentityTitleLayout.InlineBefore or IdentityTitleLayout.InlineAfter)
        {
            (nameRect, titleRect, y) = ComputeInline(layout, regionPosition, regionWidth, groupAlignment, name, title);
            y += gap;
        }
        else
        {
            // Classic puts the title above the name; every other stacked layout puts it below.
            y = regionPosition.Y;
            if (layout == IdentityTitleLayout.Classic)
            {
                titleRect = Stack(title, regionPosition.X, regionWidth, gap, ref y);
                nameRect = Stack(name, regionPosition.X, regionWidth, gap, ref y);
            }
            else
            {
                nameRect = Stack(name, regionPosition.X, regionWidth, gap, ref y);
                titleRect = Stack(title, regionPosition.X, regionWidth, gap, ref y);
            }
        }

        var taglineRect = Stack(tagline, regionPosition.X, regionWidth, gap, ref y);
        return new Result(nameRect, titleRect, taglineRect);
    }

    private static ElementRect? Stack(Line line, float x, float width, float gap, ref float y)
    {
        if (!line.Exists)
        {
            return null;
        }

        var height = BoxHeight(line.FontSize);
        var rect = new ElementRect(new Vector2(x, y), new Vector2(width, height));
        if (line.Visible)
        {
            y += height + gap;
        }

        return rect;
    }

    private static (ElementRect? Name, ElementRect? Title, float Bottom) ComputeInline(
        IdentityTitleLayout layout, Vector2 region, float regionWidth, TextAlignment groupAlignment, Line name, Line title)
    {
        var showTitle = title.Exists && title.Visible;
        var showName = name.Exists && name.Visible;

        var nameWidth = name.TextWidth + (2f * Padding) + InlineSlack;
        var titleWidth = title.TextWidth + (2f * Padding) + InlineSlack;
        var boxGap = Math.Max(0f, (InlineGapRatio * name.FontSize) - (2f * Padding));

        var total = (showName ? nameWidth : 0f) + (showTitle ? titleWidth : 0f) + (showName && showTitle ? boxGap : 0f);

        // Too wide for the region: shrink the boxes proportionally (the elements' auto fit then
        // shrinks their text to match) rather than overflowing the header.
        if (total > regionWidth && total > 0f)
        {
            var shrink = regionWidth / total;
            nameWidth *= shrink;
            titleWidth *= shrink;
            boxGap *= shrink;
            total = regionWidth;
        }

        var x = groupAlignment switch
        {
            TextAlignment.Center => region.X + ((regionWidth - total) / 2f),
            TextAlignment.Right => region.X + regionWidth - total,
            _ => region.X,
        };

        // Baselines line up: a smaller font's box starts lower by the difference in ascent.
        var lineFontSize = Math.Max(showName ? name.FontSize : 0f, showTitle ? title.FontSize : 0f);
        float BoxTop(float fontSize) => region.Y + ((lineFontSize - fontSize) * BaselineRatio);

        var titleFirst = layout == IdentityTitleLayout.InlineBefore;
        ElementRect? nameRect = null, titleRect = null;
        var bottom = region.Y;

        void Place(bool isTitle)
        {
            var line = isTitle ? title : name;
            if (!line.Exists)
            {
                return;
            }

            var width = isTitle ? titleWidth : nameWidth;
            var rect = new ElementRect(new Vector2(x, BoxTop(line.FontSize)), new Vector2(Math.Max(1f, width), BoxHeight(line.FontSize)));
            if (isTitle)
            {
                titleRect = rect;
            }
            else
            {
                nameRect = rect;
            }

            if (line.Visible)
            {
                x += width + boxGap;
                bottom = Math.Max(bottom, rect.Position.Y + rect.Size.Y);
            }
        }

        Place(isTitle: titleFirst);
        Place(isTitle: !titleFirst);
        return (nameRect, titleRect, bottom);
    }
}
