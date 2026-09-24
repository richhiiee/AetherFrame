using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// Computes where the Identity Header's elements go for a curated <see cref="IdentityTitleLayout"/>:
/// pure geometry (no ImGui, no profile mutation), from the header region, each element's font
/// size, and each element's measured text width.
///
/// The header is the Character Name and the Title (the Tagline is no longer part of Basic mode;
/// an older Plate's tagline is ordinary Advanced content and is never placed here). It only ever
/// positions and sizes those two separate elements; it never composes their text.
/// Stacked layouts give every line the full region width (each element's own alignment then
/// places its text); inline layouts size each box to its text and place them side by side,
/// aligned as a group by the name's alignment, with their baselines lined up.
///
/// <para><b>The name comes first.</b> The name is the primary identity element, so its size is
/// never traded for the title's placement:</para>
/// <list type="number">
/// <item>The name shows at its font size, in a box its measured text width plus padding
/// (stacked: the whole region width).</item>
/// <item>An inline title sits <see cref="InlineGapRatio"/> after (or before) the name while both
/// fit the region at their full sizes.</item>
/// <item>Otherwise the title reflows to its own line under the name, aligned with it — the name
/// keeps the primary line and its full size. The title yields before the name: only a title
/// wider than the whole region on its own line is fitted smaller (its generic auto fit).</item>
/// <item>Only a name wider than the whole region on its own is reduced, by the one Basic name
/// rule (<see cref="BasicNameFit"/>): at most to 90% of its size, then word-wrapped at that size
/// in a taller box — never tiny, never cut.</item>
/// </list>
/// </summary>
internal static class IdentityHeaderLayout
{
    private const float Padding = TextProfileElement.LayoutPadding;

    // Line box height as a multiple of font size (room for descenders, italics, and a thin
    // outline) plus top/bottom padding; baseline as a fraction of font size from the line top.
    private const float LineHeightRatio = 1.2f;
    private const float BaselineRatio = 0.8f;

    // Vertical gap between stacked (or reflowed) name and title lines (enough air that the title
    // reads as its own line under the bold name), and the visible gap between an inline title and
    // name, both relative to the larger font size.
    private const float LineGapRatio = 0.2f;
    private const float InlineGapRatio = 0.3f;

    // A little extra width on measured boxes so rounding can never clip the last glyph.
    private const float InlineSlack = 2f;

    // Padding on both sides plus the rounding slack: box width = text width + BoxInset.
    private const float BoxInset = (2f * Padding) + InlineSlack;

    /// <summary>One header line's inputs.</summary>
    /// <param name="Exists">Whether the element exists at all (absent elements get no rect).</param>
    /// <param name="Visible">Hidden elements are placed but take up no space.</param>
    /// <param name="FontSize">The element's font size.</param>
    /// <param name="TextWidth">Natural single-line text width at <paramref name="FontSize"/>.</param>
    /// <param name="IsBasicName">Sized by <see cref="BasicNameFit"/> (the Basic name with auto fit on, wrap off).</param>
    /// <param name="LineSpacing">The element's line spacing (for a wrapped name's height).</param>
    /// <param name="CountLines">Lines the text wraps to at a size within a width, or null to estimate from <paramref name="TextWidth"/>.</param>
    internal readonly record struct Line(
        bool Exists, bool Visible, float FontSize, float TextWidth, bool IsBasicName = false, float LineSpacing = 1f, Func<float, float, int>? CountLines = null)
    {
        /// <summary>The box width that shows the text on one line at <see cref="FontSize"/>.</summary>
        internal float NaturalBoxWidth => TextWidth + BoxInset;
    }

    internal readonly record struct Result(ElementRect? Name, ElementRect? Title);

    internal static float BoxHeight(float fontSize) => MathF.Ceiling((fontSize * LineHeightRatio) + (2f * Padding));

    /// <summary>The space between an inline name's box and the title's (the visible gap is this plus both paddings).</summary>
    internal static float InlineBoxGap(float nameFontSize) => Math.Max(0f, (InlineGapRatio * nameFontSize) - (2f * Padding));

    /// <summary>
    /// True when an inline header keeps its title on the name's line: both shown, and both fit the
    /// region at their full sizes with the gap between. Otherwise the title reflows under the name.
    /// </summary>
    internal static bool FitsInline(float regionWidth, Line name, Line title) =>
        name.NaturalBoxWidth + InlineBoxGap(name.FontSize) + title.NaturalBoxWidth <= regionWidth;

    /// <summary>The name's box height in a box <paramref name="boxWidth"/> wide: one line at its size, or the wrapped lines at the Basic minimum.</summary>
    internal static float NameBoxHeight(Line name, float boxWidth)
    {
        if (!name.IsBasicName)
        {
            return BoxHeight(name.FontSize);
        }

        var available = Math.Max(0f, boxWidth - (2f * Padding));
        var (size, wrap) = BasicNameFit.Resolve(name.FontSize, name.TextWidth, available);
        if (!wrap)
        {
            return BoxHeight(name.FontSize);
        }

        var lines = Math.Max(2, name.CountLines?.Invoke(size, available)
            ?? (int)MathF.Ceiling(name.TextWidth * (size / name.FontSize) / Math.Max(1f, available)));
        return MathF.Ceiling((size * LineHeightRatio) + ((lines - 1) * size * name.LineSpacing) + (2f * Padding));
    }

    internal static Result Compute(
        IdentityTitleLayout layout, Vector2 regionPosition, float regionWidth, TextAlignment groupAlignment, Line name, Line title)
    {
        regionWidth = Math.Max(1f, regionWidth);
        var gap = MathF.Round(Math.Max(name.FontSize, title.FontSize) * LineGapRatio);

        if (layout is IdentityTitleLayout.InlineBefore or IdentityTitleLayout.InlineAfter)
        {
            var showName = name.Exists && name.Visible;
            var showTitle = title.Exists && title.Visible;
            if (!showName || !showTitle || FitsInline(regionWidth, name, title))
            {
                return ComputeInline(layout, regionPosition, regionWidth, groupAlignment, name, title);
            }

            // Not enough room for both at full size: the name keeps the primary line, the title
            // moves under it — neither is shrunk to share the line.
            var reflowY = regionPosition.Y;
            var reflowedName = Stack(name, Aligned(name.NaturalBoxWidth), gap, ref reflowY, isName: true);
            var reflowedTitle = Stack(title, Aligned(title.NaturalBoxWidth), gap, ref reflowY, isName: false);
            return new Result(reflowedName, reflowedTitle);
        }

        // Stacked: every line has the full region width. Classic puts the title above the name;
        // every other stacked layout puts it below.
        var y = regionPosition.Y;
        var full = (regionPosition.X, regionWidth);
        if (layout == IdentityTitleLayout.Classic)
        {
            var classicTitle = Stack(title, full, gap, ref y, isName: false);
            var classicName = Stack(name, full, gap, ref y, isName: true);
            return new Result(classicName, classicTitle);
        }

        var stackedName = Stack(name, full, gap, ref y, isName: true);
        var stackedTitle = Stack(title, full, gap, ref y, isName: false);
        return new Result(stackedName, stackedTitle);

        // A box its natural width (at most the region's), placed by the group alignment.
        (float X, float Width) Aligned(float naturalWidth)
        {
            var width = Math.Min(naturalWidth, regionWidth);
            var x = groupAlignment switch
            {
                TextAlignment.Center => regionPosition.X + ((regionWidth - width) / 2f),
                TextAlignment.Right => regionPosition.X + regionWidth - width,
                _ => regionPosition.X,
            };
            return (x, width);
        }
    }

    private static ElementRect? Stack(Line line, (float X, float Width) column, float gap, ref float y, bool isName)
    {
        if (!line.Exists)
        {
            return null;
        }

        var height = isName ? NameBoxHeight(line, column.Width) : BoxHeight(line.FontSize);
        var rect = new ElementRect(new Vector2(column.X, y), new Vector2(Math.Max(1f, column.Width), height));
        if (line.Visible)
        {
            y += height + gap;
        }

        return rect;
    }

    // One line: the shown boxes at their natural widths (the name at most the region's width),
    // side by side and aligned as a group, baselines lined up. Only called when they fit.
    private static Result ComputeInline(
        IdentityTitleLayout layout, Vector2 region, float regionWidth, TextAlignment groupAlignment, Line name, Line title)
    {
        var showTitle = title.Exists && title.Visible;
        var showName = name.Exists && name.Visible;

        var nameWidth = Math.Min(name.NaturalBoxWidth, regionWidth);
        var titleWidth = Math.Min(title.NaturalBoxWidth, regionWidth);
        var boxGap = InlineBoxGap(name.FontSize);

        var total = (showName ? nameWidth : 0f) + (showTitle ? titleWidth : 0f) + (showName && showTitle ? boxGap : 0f);

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

        void Place(bool isTitle)
        {
            var line = isTitle ? title : name;
            if (!line.Exists)
            {
                return;
            }

            var width = isTitle ? titleWidth : nameWidth;
            var height = isTitle ? BoxHeight(line.FontSize) : NameBoxHeight(line, width);
            var rect = new ElementRect(new Vector2(x, BoxTop(line.FontSize)), new Vector2(Math.Max(1f, width), height));
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
            }
        }

        Place(isTitle: titleFirst);
        Place(isTitle: !titleFirst);
        return new Result(nameRect, titleRect);
    }
}
