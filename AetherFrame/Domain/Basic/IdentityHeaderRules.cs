using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// The Identity Header's pure rules — default styles, the Badge and Accent looks (and undoing
/// them), placement through <see cref="IdentityHeaderLayout"/>, and customization detection —
/// shared by the Basic editor's <c>BasicIdentitySession</c> and new-Plate defaults. Nothing here decides WHEN to place; that
/// stays with the session's rules (explicit layout actions always, Basic-managed headers on
/// content edits, customized headers never).
/// </summary>
internal static class IdentityHeaderRules
{
    // Sizes are defined for the legacy 1080-high canvas (as the header always was) and scaled by
    // the canvas height.
    private const float ReferenceCanvasHeight = 1080f;

    // The name is the header's anchor: largest, bold, with a subtle shadow; the title (half its
    // size, accent color) reads second. (The header no longer has a tagline: an older Plate's
    // tagline element is ordinary Advanced content that nothing here reads, places, or styles.)
    private const float NameFontSize = 64f;
    private const float NameShadowOffset = 3f;
    private const float NameShadowOpacity = BasicNameColor.DarkOutlineShadowOpacity;
    internal const float TitleSizeRatio = 0.5f;
    internal const float BadgeSizeRatio = 0.4f;
    private const float BadgeLetterSpacingRatio = 0.05f;
    private const float AccentLetterSpacingRatio = 0.03f;
    internal const float AutoFitMinimum = 10f;


    internal static TextProfileElement? Find(ProfileDocument profile, ProfileElementRole role) => BasicSections.FindText(profile, role);

    /// <summary>True when neither identity element (name, title) exists yet.</summary>
    internal static bool HasNoHeader(ProfileDocument profile) =>
        Find(profile, ProfileElementRole.BasicName) is null
        && Find(profile, ProfileElementRole.BasicTitle) is null;

    /// <summary>
    /// True when Basic mode isn't (or is no longer) managing the header's placement: it was never
    /// applied, or an element has since been moved/resized (e.g. in the Advanced editor).
    /// </summary>
    internal static bool IsCustomized(ProfileDocument profile)
    {
        if (HasNoHeader(profile))
        {
            return false;
        }

        if (profile.BasicIdentity?.AppliedLayout is not { } applied)
        {
            return true;
        }

        // Only the elements Basic places: a legacy tagline (and its old snapshot entry) is ignored.
        return !Matches(Find(profile, ProfileElementRole.BasicName), applied.Name)
            || !Matches(Find(profile, ProfileElementRole.BasicTitle), applied.Title);

        static bool Matches(TextProfileElement? element, ElementRect? rect) =>
            element is null || (rect is { } r && r.Matches(element.Position, element.Size));
    }

    internal static float CanvasScale(ProfileDocument profile) => profile.CanvasHeight / ReferenceCanvasHeight;

    /// <summary>The default character name size on this canvas.</summary>
    internal static float ScaledNameSize(ProfileDocument profile) => AdventurePlateClassicLayout.ClampFont(NameFontSize * CanvasScale(profile));

    /// <summary>New settings with the Adventure Plate Classic header region for the Plate's orientation.</summary>
    internal static BasicIdentityHeader CreateSettings(ProfileDocument profile)
    {
        var (position, width) = DefaultRegion(profile);
        return new BasicIdentityHeader { Layout = IdentityTitleLayout.Subtitle, RegionPosition = position, RegionWidth = width };
    }

    internal static (Vector2 Position, float Width) DefaultRegion(ProfileDocument profile) =>
        AdventurePlateClassicLayout.GetIdentityRegion(profile.BasicPlate?.Orientation ?? AdventurePlateOrientation.Normal, profile);

    /// <summary>
    /// A new, detached identity element with its default style (not placed yet). The name starts
    /// with <paramref name="characterName"/>; the title starts empty.
    /// </summary>
    internal static TextProfileElement Create(ProfileElementRole role, ProfileDocument profile, string? characterName)
    {
        var element = new TextProfileElement { Role = role };
        ApplyDefaultStyle(element, profile);
        if (role == ProfileElementRole.BasicName)
        {
            element.Text = characterName ?? string.Empty;
        }

        return element;
    }

    /// <summary>
    /// Restores an identity element's default look (font, size, theme color, auto fit, alignment
    /// following the name, and the title's look for the current layout). Never its text, prefix or
    /// suffix (user content), visibility, or placement.
    /// </summary>
    internal static void ApplyDefaultStyle(TextProfileElement element, ProfileDocument profile)
    {
        var theme = AdventurePlateClassicLayout.ResolveTheme(profile);
        var name = Find(profile, ProfileElementRole.BasicName);
        var nameSize = element.Role == ProfileElementRole.BasicName || name is null ? ScaledNameSize(profile) : name.FontSize;

        var defaults = new TextProfileElement();
        element.FontFamily = ProfileFontFamilies.AetherFrameSans;
        element.Wrap = false;
        element.AutoFitText = true;
        element.AutoFitMinimumSize = AutoFitMinimum;
        element.Alignment = element.Role != ProfileElementRole.BasicName && name is not null ? name.Alignment : TextAlignment.Left;
        element.VerticalAlignment = defaults.VerticalAlignment;
        element.Color = AdventurePlateClassicLayout.ThemeColorFor(element.Role, theme);
        element.Bold = false;
        element.Italic = false;
        element.Underline = false;
        element.Strikethrough = false;
        element.LetterSpacing = 0f;
        element.LineSpacing = defaults.LineSpacing;
        element.OutlineEnabled = false;
        element.ShadowEnabled = false;
        element.LayoutVersion = TextProfileElement.CurrentLayoutVersion;

        if (element.Role == ProfileElementRole.BasicName)
        {
            element.FontSize = nameSize;
            element.Bold = true;
            element.ShadowEnabled = true;
            element.ShadowColor = defaults.ShadowColor;
            element.ShadowOpacity = NameShadowOpacity;
            element.ShadowOffsetX = MathF.Round(NameShadowOffset * CanvasScale(profile), 1);
            element.ShadowOffsetY = element.ShadowOffsetX;

            // The theme's dedicated name treatment: display color and subtle contrasting outline.
            BasicNameColor.ApplyAutomatic(element, theme, profile);
            return;
        }

        // The title: its plain default, then the current layout's look on top (recorded, so
        // leaving the layout later can undo exactly that).
        element.FontSize = AdventurePlateClassicLayout.ClampFont(nameSize * TitleSizeRatio);
        if (profile.BasicIdentity is { } identity)
        {
            identity.LayoutStyle = null;
            ApplyLook(identity, element, identity.Layout, nameSize);
        }
    }

    /// <summary>
    /// The look a layout gives the title — Badge: a small, bold, spaced line; Accent: italic with a
    /// little extra spacing — or null for the plain layouts. Style only: no layout ever writes the
    /// title's text, prefix, or suffix.
    /// </summary>
    internal static TitleStyleValues? LayoutLook(IdentityTitleLayout layout, float nameSize) => layout switch
    {
        IdentityTitleLayout.Badge => new TitleStyleValues
        {
            FontSize = AdventurePlateClassicLayout.ClampFont(nameSize * BadgeSizeRatio),
            Bold = true,
            LetterSpacing = MathF.Round(Math.Max(1.5f, nameSize * BadgeLetterSpacingRatio), 1),
        },
        IdentityTitleLayout.Accent => new TitleStyleValues
        {
            Italic = true,
            LetterSpacing = MathF.Round(Math.Max(1f, nameSize * AccentLetterSpacingRatio), 1),
        },
        _ => null,
    };

    /// <summary>
    /// Switches the title from <paramref name="previous"/>'s look to <paramref name="next"/>'s: first
    /// undoes what the previous layout applied (each property only while it still holds the
    /// layout's value, so the user's own changes survive), then applies the next layout's look,
    /// recording what it replaced. Everything else about the title is untouched.
    /// </summary>
    internal static void ChangeLayoutLook(
        BasicIdentityHeader identity, TextProfileElement? title, IdentityTitleLayout previous, IdentityTitleLayout next, float nameSize)
    {
        if (title is not null)
        {
            var record = identity.LayoutStyle is { } recorded && recorded.Layout == previous ? recorded : LegacyRecord(previous, nameSize);
            record?.Applied.RevertOn(title, record.Previous);
        }

        identity.LayoutStyle = null;
        if (title is not null)
        {
            ApplyLook(identity, title, next, nameSize);
        }
    }

    private static void ApplyLook(BasicIdentityHeader identity, TextProfileElement title, IdentityTitleLayout layout, float nameSize)
    {
        if (LayoutLook(layout, nameSize) is not { } look)
        {
            return;
        }

        identity.LayoutStyle = new IdentityLayoutStyle { Layout = layout, Applied = look, Previous = look.CaptureFrom(title) };
        look.ApplyTo(title);
    }

    /// <summary>
    /// A header saved by a build that didn't record layout looks: what its Badge or Accent layout
    /// applied then, over the title's plain default — so leaving it behaves as it always did while
    /// the values are untouched, and keeps any value the user has changed. (Those builds also put
    /// decoration symbols in the title's prefix and suffix when choosing Accent; those can't be
    /// told apart from symbols the user picked, so they're never removed automatically.)
    /// </summary>
    private static IdentityLayoutStyle? LegacyRecord(IdentityTitleLayout layout, float nameSize) => layout switch
    {
        IdentityTitleLayout.Badge => new IdentityLayoutStyle
        {
            Layout = layout,
            Applied = new TitleStyleValues
            {
                FontSize = AdventurePlateClassicLayout.ClampFont(nameSize * BadgeSizeRatio),
                Bold = true,
                LetterSpacing = MathF.Round(Math.Max(1.5f, nameSize * BadgeLetterSpacingRatio), 1),
            },
            Previous = new TitleStyleValues { FontSize = AdventurePlateClassicLayout.ClampFont(nameSize * TitleSizeRatio), Bold = false, LetterSpacing = 0f },
        },
        IdentityTitleLayout.Accent => new IdentityLayoutStyle
        {
            Layout = layout,
            Applied = new TitleStyleValues { Italic = true },
            Previous = new TitleStyleValues { Italic = false },
        },
        _ => null,
    };

    /// <summary>
    /// The title decorations offered for its prefix and suffix: only characters every curated
    /// AetherFrame font can draw (its fonts carry Basic Latin and Latin-1), so a decoration can
    /// never render as "?" or a missing-glyph box. "" means none.
    /// </summary>
    internal static readonly string[] DecorationSymbols = ["", "·", "*", "~", "-", "|", "«", "»", "°", "§"];

    /// <summary>
    /// True when the Plate's fonts can draw every character of a decoration: Basic Latin and
    /// Latin-1 printable characters. False for symbols an earlier build offered (or wrote for
    /// Accent) such as "✦", which render as "?".
    /// </summary>
    internal static bool IsDrawableDecoration(string decoration)
    {
        foreach (var c in decoration)
        {
            if (c is < ' ' or (> '~' and < ' ') or > 'ÿ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// For a header Basic no longer places (customized): after a Basic edit, grows the name's box —
    /// never moving it, never shrinking it, staying on the canvas — until its text shows by the
    /// Basic name rule (<see cref="BasicNameFit"/>: full size, or at least 90% and wrapped) without
    /// being cut. So typing a longer name never leaves it crushed or clipped in a box sized for
    /// the old one. False when nothing changed (or the name can't be measured yet).
    /// </summary>
    internal static bool KeepNameReadable(
        ProfileDocument profile, Func<TextProfileElement, float?> measure, Func<TextProfileElement, float, float, int?>? countLines = null)
    {
        if (Find(profile, ProfileElementRole.BasicName) is not { } name || !BasicNameFit.Applies(name) || measure(name) is not { } natural)
        {
            return false;
        }

        var roomOnCanvas = Math.Max(1f, profile.CanvasWidth - name.Position.X);
        var width = Math.Max(name.Size.X, Math.Min(natural + (2f * TextProfileElement.LayoutPadding) + 2f, roomOnCanvas));
        Func<float, float, int>? lines = countLines is null ? null : (size, available) => countLines(name, size, available) ?? 1;
        var line = new IdentityHeaderLayout.Line(true, true, name.FontSize, natural, IsBasicName: true, name.LineSpacing, lines);
        var size = new Vector2(width, Math.Max(name.Size.Y, IdentityHeaderLayout.NameBoxHeight(line, width)));
        if (size == name.Size)
        {
            return false;
        }

        name.Size = size;
        return true;
    }

    /// <summary>
    /// Places the header's existing elements for its current layout within its region, and records
    /// where (<see cref="BasicIdentityHeader.AppliedLayout"/>). <paramref name="measure"/> gives an
    /// element's natural single-line text width (the name's, always; the title's, for the one-line
    /// layouts), or null when it can't be measured yet — then the width is estimated and this
    /// returns false, so the caller can re-place once it can. <paramref name="countLines"/> (optional)
    /// gives the lines a text wraps to at a size within a width, for a name too long for the region
    /// even at its Basic minimum (see <see cref="BasicNameFit"/>); without it they are estimated.
    /// Requires <see cref="ProfileDocument.BasicIdentity"/>.
    /// </summary>
    internal static bool Place(
        ProfileDocument profile, Func<TextProfileElement, float?> measure, Func<TextProfileElement, float, float, int?>? countLines = null)
    {
        var identity = profile.BasicIdentity ?? throw new InvalidOperationException("The Identity Header has no settings.");
        var name = Find(profile, ProfileElementRole.BasicName);
        var title = Find(profile, ProfileElementRole.BasicTitle);
        var exact = true;
        var inline = identity.Layout is IdentityTitleLayout.InlineBefore or IdentityTitleLayout.InlineAfter;
        var result = IdentityHeaderLayout.Compute(
            identity.Layout,
            identity.RegionPosition,
            identity.RegionWidth,
            name?.Alignment ?? TextAlignment.Left,
            LineFor(name, measureWidth: true, reserveWhenEmpty: true),
            LineFor(title, measureWidth: inline, reserveWhenEmpty: false));

        Assign(name, result.Name);
        Assign(title, result.Title);

        identity.AppliedLayout = new IdentityLayoutSnapshot
        {
            Name = result.Name,
            Title = result.Title,
            ExtensionData = ProfileElement.CopyExtensionData(identity.AppliedLayout?.ExtensionData),
        };
        return exact;

        // reserveWhenEmpty is false for the title: with no text yet (e.g. FFXIV Title chosen but no
        // title picked) it draws nothing in the finished profile, so it takes no space either.
        IdentityHeaderLayout.Line LineFor(TextProfileElement? element, bool measureWidth, bool reserveWhenEmpty)
        {
            if (element is null)
            {
                return default;
            }

            var takesSpace = element.Visible && (reserveWhenEmpty || element.Text.Length > 0);

            var width = 0f;
            if (measureWidth)
            {
                if (measure(element) is { } measured)
                {
                    width = measured;
                }
                else
                {
                    width = element.GetDisplayText().Length * element.FontSize * 0.5f;
                    exact = false;
                }
            }

            // Judged for the current text layout, which placing upgrades the element to (Assign).
            var isBasicName = element.Role == ProfileElementRole.BasicName && element.AutoFitText && !element.Wrap;
            Func<float, float, int>? lines = countLines is null
                ? null
                : (size, available) => countLines(element, size, available) ?? (int)MathF.Ceiling(width * (size / element.FontSize) / Math.Max(1f, available));
            return new IdentityHeaderLayout.Line(true, takesSpace, element.FontSize, width, isBasicName, element.LineSpacing, lines);
        }

        static void Assign(TextProfileElement? element, ElementRect? rect)
        {
            if (element is not null && rect is { } r)
            {
                element.Position = r.Position;
                element.Size = r.Size;

                // Placed by Basic (an explicit Identity action), so it uses the current text
                // layout the placement math assumes (canvas-unit padding, auto fit) — an element
                // from an earlier version is only ever upgraded here, never on load.
                element.LayoutVersion = TextProfileElement.CurrentLayoutVersion;
            }
        }
    }
}
