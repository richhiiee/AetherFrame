using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// Adventure Plate Classic: the first structured Basic layout. Its structure follows the FFXIV
/// Adventurer Plate — a tall portrait beside a details panel (the Identity Header, then labelled
/// fields, then the message) — without copying the game's UI.
///
/// Geometry is defined on the 1280x720 Adventure Plate reference canvas and scaled to the Plate's
/// actual canvas (positions per axis, font sizes by height), so it works on any canvas without
/// ever resizing one. The two orientations are one layout: Mirrored swaps the portrait and the
/// details panel as blocks, keeping the panel's own reading order.
///
/// Pure data and math: nothing here touches a document except to style or create a detached
/// element for a caller that asked for one.
/// </summary>
public static class AdventurePlateClassicLayout
{
    public const float ReferenceWidth = 1280f;
    public const float ReferenceHeight = 720f;

    // Blocks (reference canvas, Normal orientation). Mirrored places the details panel where the
    // portrait was and the portrait at the right edge; nothing inside the panel moves relative to it.
    private const float Margin = 40f;
    private const float PortraitWidth = 400f;
    private const float PortraitHeight = 640f;
    private const float PanelWidth = 760f;
    private const float PanelGap = 40f;

    // The details grid: the Identity Header on top, then two two-column rows, then two full-width
    // rows. Every group owns one cell; cells are separated by a clear gutter so no heading or value
    // can run into its neighbor's.
    //
    //   Row 1  | Home World              | Free Company   |
    //   Row 2  | Favorite Job & Level    | Active Hours   |
    //   Row 3  | Playstyle (full width)                   |
    //   Row 4  | Message (full width)                     |
    private const float ColumnGap = 40f;
    private const float ColumnWidth = (PanelWidth - ColumnGap) / 2f;
    private const float SecondColumn = ColumnWidth + ColumnGap;

    private const float HeaderTop = 44f;

    /// <summary>
    /// Height reserved for the Identity Header (name and title) above the grid: its tallest curated
    /// layout fits, and the gap below it keeps the header well clear of the first details row.
    /// </summary>
    private const float HeaderHeight = 120f;

    private const float Row1Top = 196f;
    private const float Row2Top = 276f;
    private const float Row3Top = 356f;
    private const float Row4Top = 460f;
    private const float PanelBottom = Margin + PortraitHeight;

    private const float HeadingHeight = 24f;

    /// <summary>
    /// How far (reference pixels) a section heading's box may grow upward into the gutter above its
    /// row when its text is larger than the row holds. The smallest such gutter — between one
    /// row's value and the next row's heading — is 22 reference pixels, so the grown box still
    /// keeps clear of everything above it; its bottom edge, and so the value below, never moves.
    /// </summary>
    public const float MaxHeadingGrowth = 16f;
    private const float ValueHeight = 34f;
    private const float PlaystyleHeight = 58f;

    // Typography (reference canvas pixels; scaled with the canvas height).

    /// <summary>
    /// The section headings' default size ("HOME WORLD", "MESSAGE"...), in reference pixels: large
    /// enough to read at a glance next to the 20 px values, and exactly what the heading row holds
    /// (its height less the text padding). A larger heading's box grows upward to hold it (see
    /// <see cref="MaxHeadingGrowth"/>). Applied when a heading is created and by Reset Section;
    /// existing Plates keep their own sizes.
    /// </summary>
    public const float DefaultHeadingFontSize = 16f;
    private const float HeadingLetterSpacing = 1.5f;
    private const float ValueFontSize = 20f;
    private const float PlaystyleFontSize = 18f;
    private const float MessageFontSize = 18f;
    private const float AutoFitMinimum = 10f;

    /// <summary>Panel-relative rectangles (x from the details panel's left edge).</summary>
    private static readonly Dictionary<ProfileElementRole, (float X, float Y, float W, float H)> PanelRects = new()
    {
        [ProfileElementRole.BasicWorldHeading] = (0f, Row1Top, ColumnWidth, HeadingHeight),
        [ProfileElementRole.BasicWorld] = (0f, Row1Top + HeadingHeight, ColumnWidth, ValueHeight),
        [ProfileElementRole.BasicFreeCompanyHeading] = (SecondColumn, Row1Top, ColumnWidth, HeadingHeight),
        [ProfileElementRole.BasicFreeCompany] = (SecondColumn, Row1Top + HeadingHeight, ColumnWidth, ValueHeight),
        [ProfileElementRole.BasicJobHeading] = (0f, Row2Top, ColumnWidth, HeadingHeight),
        [ProfileElementRole.BasicJob] = (0f, Row2Top + HeadingHeight, ColumnWidth, ValueHeight),
        [ProfileElementRole.BasicActiveHoursHeading] = (SecondColumn, Row2Top, ColumnWidth, HeadingHeight),
        [ProfileElementRole.BasicActiveHours] = (SecondColumn, Row2Top + HeadingHeight, ColumnWidth, ValueHeight),
        [ProfileElementRole.BasicPlaystyleHeading] = (0f, Row3Top, PanelWidth, HeadingHeight),
        [ProfileElementRole.BasicPlaystyle] = (0f, Row3Top + HeadingHeight, PanelWidth, PlaystyleHeight),
        [ProfileElementRole.BasicMessageHeading] = (0f, Row4Top, PanelWidth, HeadingHeight),
        [ProfileElementRole.BasicMessage] = (0f, Row4Top + HeadingHeight, PanelWidth, PanelBottom - Row4Top - HeadingHeight),
    };

    /// <summary>Scale from the reference canvas to the Plate's canvas, per axis.</summary>
    public static Vector2 CanvasScale(ProfileDocument profile) =>
        new(profile.CanvasWidth / ReferenceWidth, profile.CanvasHeight / ReferenceHeight);

    /// <summary>Font size scale (by canvas height, like every other Basic font default).</summary>
    public static float FontScale(ProfileDocument profile) => profile.CanvasHeight / ReferenceHeight;

    private static float PanelLeft(AdventurePlateOrientation orientation) =>
        orientation == AdventurePlateOrientation.Mirrored ? Margin : Margin + PortraitWidth + PanelGap;

    private static float PortraitLeft(AdventurePlateOrientation orientation) =>
        orientation == AdventurePlateOrientation.Mirrored ? ReferenceWidth - Margin - PortraitWidth : Margin;

    /// <summary>
    /// Where the layout places the element with <paramref name="role"/>, on this Plate's canvas; null
    /// for roles the layout doesn't place individually (the Identity Header's name and title,
    /// placed as one header within <see cref="GetIdentityRegion"/>, the retired Level, and
    /// non-Basic elements). Favorite Jobs has its whole cell: nothing shares its row any more.
    /// </summary>
    public static ElementRect? GetRect(ProfileElementRole role, AdventurePlateOrientation orientation, ProfileDocument profile)
    {
        Vector2 position, size;
        if (role == ProfileElementRole.BasicPortrait)
        {
            position = new Vector2(PortraitLeft(orientation), Margin);
            size = new Vector2(PortraitWidth, PortraitHeight);
        }
        else if (PanelRects.TryGetValue(role, out var rect))
        {
            position = new Vector2(PanelLeft(orientation) + rect.X, rect.Y);
            size = new Vector2(rect.W, rect.H);

            // A heading larger than its row holds grows upward (bottom edge fixed, above its value).
            var growth = HeadingGrowth(role, profile);
            position.Y -= growth;
            size.Y += growth;
        }
        else
        {
            return null;
        }

        var scale = CanvasScale(profile);
        return new ElementRect(position * scale, size * scale);
    }

    /// <summary>
    /// How much taller than the row (reference pixels, up to <see cref="MaxHeadingGrowth"/>) a
    /// heading's box must be for its own text size to fit without auto fit shrinking it: 0 for a
    /// heading at or below the default size, for a heading that doesn't exist yet, and for every
    /// other role.
    /// </summary>
    public static float HeadingGrowth(ProfileElementRole role, ProfileDocument profile)
    {
        var scaleY = FontScale(profile);
        if (!BasicSections.IsHeading(role) || BasicSections.FindText(profile, role) is not { } heading || !(scaleY > 0f) || !float.IsFinite(scaleY))
        {
            return 0f;
        }

        var needed = (heading.FontSize + (2f * TextProfileElement.LayoutPadding)) / scaleY;
        return Math.Clamp(MathF.Ceiling(needed - HeadingHeight), 0f, MaxHeadingGrowth);
    }

    /// <summary>
    /// The largest section heading size (canvas units) the layout can show at full size on this
    /// Plate: the tallest heading box less its text padding (32 px on the reference canvas).
    /// </summary>
    public static float MaxHeadingFontSize(ProfileDocument profile)
    {
        var tallest = (HeadingHeight + MaxHeadingGrowth) * FontScale(profile);
        return Math.Clamp(MathF.Floor(tallest - (2f * TextProfileElement.LayoutPadding)), TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize);
    }

    /// <summary>The region the Identity Header fills: top of the details panel, full panel width.</summary>
    public static (Vector2 Position, float Width) GetIdentityRegion(AdventurePlateOrientation orientation, ProfileDocument profile)
    {
        var scale = CanvasScale(profile);
        return (new Vector2(PanelLeft(orientation), HeaderTop) * scale, PanelWidth * scale.X);
    }

    /// <summary>
    /// The whole area a layout group (see <see cref="BasicSections.LayoutGroups"/>) occupies in this
    /// layout: the union of its elements' cells (heading, level, job...), or for the Identity Header
    /// its reserved header area. Groups are laid out so these never intersect, in either
    /// orientation, on any canvas.
    /// </summary>
    public static ElementRect GetGroupBounds(BasicSection section, AdventurePlateOrientation orientation, ProfileDocument profile)
    {
        if (section == BasicSection.Identity)
        {
            var (position, width) = GetIdentityRegion(orientation, profile);
            return new ElementRect(position, new Vector2(width, HeaderHeight * CanvasScale(profile).Y));
        }

        ElementRect? bounds = null;
        foreach (var member in BasicSections.LayoutGroupOf(section))
        {
            foreach (var role in BasicPlateEditor.RolesOf(member))
            {
                if (GetRect(role, orientation, profile) is { } rect)
                {
                    bounds = bounds?.Union(rect) ?? rect;
                }
            }
        }

        return bounds ?? throw new ArgumentOutOfRangeException(nameof(section), section, "Not a Basic layout group.");
    }

    /// <summary>The theme a Plate's Basic defaults use: its last applied Basic theme (by stable id),
    /// else the first preset.</summary>
    public static ProfileThemePreset ResolveTheme(ProfileDocument profile) =>
        ProfileThemePresets.Find(profile.BasicPlate?.ThemeId) ?? ProfileThemePresets.All[0];

    /// <summary>The theme text color a section role uses (name: the theme's name color; headings: accent; level: soft; values: primary).</summary>
    public static Vector4 ThemeColorFor(ProfileElementRole role, ProfileThemePreset theme) => role switch
    {
        ProfileElementRole.BasicName => BasicNameColor.Automatic(theme),
        ProfileElementRole.BasicTitle => theme.AccentTextColor,
        _ when BasicSections.IsHeading(role) => theme.AccentTextColor,
        _ => theme.TextColor,
    };

    /// <summary>
    /// A new, detached element for a section role (not the Identity Header, which
    /// <c>BasicIdentitySession</c> creates), with the layout's default style and placement. Text
    /// content is empty except a heading's default caption.
    /// </summary>
    public static ProfileElement CreateElement(ProfileElementRole role, ProfileDocument profile)
    {
        var orientation = profile.BasicPlate?.Orientation ?? AdventurePlateOrientation.Normal;
        var rect = GetRect(role, orientation, profile) ?? throw new ArgumentOutOfRangeException(nameof(role), role, "Not a Basic section element.");

        ProfileElement element;
        if (role == ProfileElementRole.BasicPortrait)
        {
            element = new ImageProfileElement { Role = role };
            ApplyDefaultStyle(element, profile);
        }
        else
        {
            var text = new TextProfileElement { Role = role, Text = HeadingCaption(role, profile) ?? string.Empty };
            ApplyDefaultStyle(text, profile);
            element = text;
        }

        element.Position = rect.Position;
        element.Size = rect.Size;
        return element;
    }

    /// <summary>
    /// Restores a section element's default look: typography and colors for text (a heading also
    /// gets its default caption back), fit and transform for the portrait. Never its placement,
    /// visibility, or content (text, image) — callers own those.
    /// </summary>
    public static void ApplyDefaultStyle(ProfileElement element, ProfileDocument profile)
    {
        switch (element)
        {
            case ImageProfileElement image:
                image.DisplayMode = ProfileImageFit.Fill;
                image.Opacity = 1f;
                image.RotationDegrees = 0f;
                image.FlipX = false;
                image.FlipY = false;
                image.PreserveAspectRatio = true;
                break;

            case TextProfileElement text:
                ApplyDefaultTextStyle(text, profile);
                break;
        }
    }

    private static void ApplyDefaultTextStyle(TextProfileElement text, ProfileDocument profile)
    {
        var scale = FontScale(profile);
        var role = text.Role;
        var theme = ResolveTheme(profile);
        var heading = BasicSections.IsHeading(role);

        var fontSize = heading ? DefaultHeadingFontSize
            : role == ProfileElementRole.BasicPlaystyle ? PlaystyleFontSize
            : role == ProfileElementRole.BasicMessage ? MessageFontSize
            : ValueFontSize;

        var multiline = role is ProfileElementRole.BasicPlaystyle or ProfileElementRole.BasicMessage;

        // Every text property is reset (to the TextProfileElement defaults unless the layout says
        // otherwise), so a reset section looks exactly like a freshly created one.
        var defaults = new TextProfileElement();
        text.Prefix = defaults.Prefix;
        text.Suffix = defaults.Suffix;
        text.FontFamily = ProfileFontFamilies.AetherFrameSans;
        text.FontSize = ClampFont(fontSize * scale);
        text.Color = ThemeColorFor(role, theme);
        text.Alignment = TextAlignment.Left;
        text.VerticalAlignment = heading ? TextVerticalAlignment.Bottom : TextVerticalAlignment.Top;
        text.Wrap = multiline;
        text.Bold = heading;
        text.Italic = false;
        text.Underline = false;
        text.Strikethrough = false;
        text.LetterSpacing = heading ? MathF.Round(HeadingLetterSpacing * scale, 1) : 0f;
        text.LineSpacing = defaults.LineSpacing;
        text.AutoFitText = true;
        text.AutoFitMinimumSize = ClampFont(AutoFitMinimum * Math.Min(1f, scale));
        text.OutlineEnabled = false;
        text.OutlineColor = defaults.OutlineColor;
        text.OutlineThickness = defaults.OutlineThickness;
        text.OutlineOpacity = defaults.OutlineOpacity;
        text.ShadowEnabled = false;
        text.ShadowColor = defaults.ShadowColor;
        text.ShadowOpacity = defaults.ShadowOpacity;
        text.ShadowOffsetX = defaults.ShadowOffsetX;
        text.ShadowOffsetY = defaults.ShadowOffsetY;
        text.LayoutVersion = TextProfileElement.CurrentLayoutVersion;

        if (heading && HeadingCaption(role, profile) is { } caption)
        {
            text.Text = caption;
        }
    }

    /// <summary>
    /// A heading's default caption on this Plate (null for any other role): the section's own, except
    /// Favorite Jobs', which follows how many jobs are chosen (FAVORITE JOB / FAVORITE JOBS).
    /// </summary>
    public static string? HeadingCaption(ProfileElementRole role, ProfileDocument profile) =>
        role == ProfileElementRole.BasicJobHeading
            ? BasicFavoriteJobs.Heading(BasicFavoriteJobs.IdsOf(profile).Count)
            : BasicSections.DefaultHeadingText(role);

    internal static float ClampFont(float size) =>
        Math.Clamp(MathF.Round(size), TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize);
}
