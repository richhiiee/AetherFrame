using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The Basic editor's Identity Header: Character Name, Title, and optional Tagline — three
/// separate role-tagged <see cref="TextProfileElement"/>s plus the profile's
/// <see cref="BasicIdentityHeader"/> settings (title source, curated layout, header region).
///
/// <para><b>Binding, never resetting.</b> Role elements that already exist are always bound
/// exactly as they are. Nothing here runs on open: elements and settings are only created by an
/// explicit Identity edit, and existing placement only changes through the rules below.</para>
///
/// <para><b>Basic-managed vs. customized.</b> After Basic places the header it remembers where
/// (<see cref="BasicIdentityHeader.AppliedLayout"/>). While the elements are still there, content
/// and style edits reflow the header (e.g. a longer title pushes an inline name along). Once any
/// has been moved or resized elsewhere — the Advanced editor — the header is customized: Basic
/// edits still change text and style, but placement changes only when the user explicitly picks
/// a layout or presses Apply Layout.</para>
///
/// <para><b>History.</b> Every action goes through <see cref="EditorSession.ApplyDocumentEdit"/>
/// (discrete) or <see cref="EditorSession.BeginOrContinueDocumentEdit"/> (sliders, colors,
/// typing: one entry per drag/typing run), so each is exactly one undo step no matter how many
/// elements it touched, and dirty state/save/revert see it like any other edit.</para>
/// </summary>
internal sealed class BasicIdentitySession
{
    // Default header geometry and sizes are defined for the legacy 1920x1080 canvas and scaled to
    // the actual canvas (the Adventure Plate default is 1280x720).
    private const float ReferenceCanvasWidth = 1920f;
    private const float ReferenceCanvasHeight = 1080f;
    private static readonly Vector2 DefaultRegionPosition = new(740f, 80f);
    private const float DefaultRegionWidth = 1120f;
    private const float DefaultNameFontSize = 56f;
    private const float DefaultTaglineFontSize = 22f;
    private const float TitleSizeRatio = 0.5f;
    private const float BadgeSizeRatio = 0.4f;
    private const float BadgeLetterSpacingRatio = 0.05f;
    private const float AutoFitMinimum = 10f;

    internal const string AccentDecoration = "✦";

    /// <summary>Curated decoration symbols (Unicode text; "" = none).</summary>
    internal static readonly string[] DecorationSymbols = ["", "♥", "♡", "★", "☆", "✦", "✧", "◆", "◇", "•", "♪"];

    internal static readonly Vector4 DefaultNameColor = new(0.96f, 0.96f, 0.97f, 1f);
    internal static readonly Vector4 DefaultTitleColor = new(0.85f, 0.68f, 0.25f, 1f);
    internal static readonly Vector4 DefaultTaglineColor = new(0.78f, 0.78f, 0.82f, 1f);

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly CharacterIdentityService characterIdentity;
    private readonly ProfileFontService fonts;

    // An inline layout was last computed with estimated widths because a font face wasn't built
    // yet; RefineLayout re-measures and folds the exact placement into that same undo entry.
    private bool refineNeeded;

    internal BasicIdentitySession(
        ProfileService profileService, EditorSession editorSession, CharacterIdentityService characterIdentity, ProfileFontService fonts, GameTitleCatalog titles)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.characterIdentity = characterIdentity;
        this.fonts = fonts;
        Titles = titles;
    }

    internal GameTitleCatalog Titles { get; }

    internal string? CharacterName => characterIdentity.CurrentCharacterName;

    // ---------------------------------------------------------------- read-only state

    internal static TextProfileElement? Find(ProfileDocument profile, ProfileElementRole role) =>
        profile.Elements.Find(e => e.Role == role) as TextProfileElement;

    /// <summary>True when none of the three identity elements exist yet (a fresh profile).</summary>
    internal static bool HasNoHeader(ProfileDocument profile) =>
        Find(profile, ProfileElementRole.BasicName) is null
        && Find(profile, ProfileElementRole.BasicTitle) is null
        && Find(profile, ProfileElementRole.BasicTagline) is null;

    /// <summary>
    /// The title source to show: the stored one, or — for a profile whose header predates these
    /// settings — inferred from the existing title element (visible text: Custom; otherwise None).
    /// Inference is display-only; nothing is written until the user changes something.
    /// </summary>
    internal static IdentityTitleSource GetTitleSource(ProfileDocument profile)
    {
        if (profile.BasicIdentity is { } identity)
        {
            return identity.TitleSource;
        }

        return Find(profile, ProfileElementRole.BasicTitle) is { Visible: true, Text.Length: > 0 }
            ? IdentityTitleSource.Custom
            : IdentityTitleSource.None;
    }

    internal static IdentityTitleLayout GetLayout(ProfileDocument profile) =>
        profile.BasicIdentity?.Layout ?? IdentityTitleLayout.Subtitle;

    /// <summary>The custom title text: the title element's actual text when it exists (so edits made
    /// in the Advanced editor show here), otherwise the stored custom title.</summary>
    internal static string GetCustomTitle(ProfileDocument profile) =>
        GetTitleSource(profile) == IdentityTitleSource.Custom && Find(profile, ProfileElementRole.BasicTitle) is { } title
            ? title.Text
            : profile.BasicIdentity?.CustomTitle ?? string.Empty;

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

        return !Matches(Find(profile, ProfileElementRole.BasicName), applied.Name)
            || !Matches(Find(profile, ProfileElementRole.BasicTitle), applied.Title)
            || !Matches(Find(profile, ProfileElementRole.BasicTagline), applied.Tagline);

        static bool Matches(TextProfileElement? element, ElementRect? rect) =>
            element is null || (rect is { } r && r.Matches(element.Position, element.Size));
    }

    /// <summary>
    /// For an FFXIV title: true when the title element's text no longer matches the chosen game
    /// title (it was edited in the Advanced editor). Shown as a note; never "corrected".
    /// </summary>
    internal bool IsGameTitleTextEdited(ProfileDocument profile) =>
        profile.BasicIdentity is { TitleSource: IdentityTitleSource.GameTitle, GameTitleId: > 0 } identity
        && Titles.Find(identity.GameTitleId) is { } title
        && Find(profile, ProfileElementRole.BasicTitle) is { } element
        && element.Text != title.Masculine && element.Text != title.Feminine;

    // ---------------------------------------------------------------- header lifecycle

    /// <summary>
    /// Creates the header for a profile that has none: the Character Name, filled from the
    /// logged-in character, placed by the default layout. Title and Tagline are created later,
    /// when first turned on. One undo step.
    /// </summary>
    internal void CreateHeader() => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicName);
        ctx.RequestLayout(force: true);
    });

    /// <summary>Explicitly (re)places the header with its current layout — the one way a customized header moves.</summary>
    internal void ApplyLayout() => Edit(ctx => ctx.RequestLayout(force: true));

    /// <summary>
    /// The explicit Reset Basic Layout: runs <paramref name="alsoReset"/> (the other Basic
    /// elements) and, if a header exists, moves it back to the default region and re-places it —
    /// all as one undo step.
    /// </summary>
    internal void ResetLayout(Action<ProfileDocument> alsoReset) => Edit(ctx =>
    {
        alsoReset(ctx.Profile);
        if (!HasNoHeader(ctx.Profile))
        {
            ctx.ResetRegion();
            ctx.RequestLayout(force: true);
        }
    });

    /// <summary>Chooses a curated layout: applies its title style defaults and places the header (explicit, so always).</summary>
    internal void SetLayout(IdentityTitleLayout layout) => Edit(ctx =>
    {
        var identity = ctx.Identity();
        var previous = identity.Layout;
        identity.Layout = layout;

        if (Find(ctx.Profile, ProfileElementRole.BasicTitle) is { } title)
        {
            ApplyLayoutStyle(title, previous, layout, Find(ctx.Profile, ProfileElementRole.BasicName)?.FontSize ?? ctx.ScaledNameSize);
        }

        ctx.RequestLayout(force: true);
    });

    /// <summary>Uses the chosen game title's own placement: Classic for titles shown before the name, Subtitle for after.</summary>
    internal void UseGamePlacement()
    {
        var profile = profileService.CurrentProfile;
        if (profile?.BasicIdentity is { TitleSource: IdentityTitleSource.GameTitle, GameTitleId: > 0 } identity)
        {
            SetLayout(identity.GameTitleIsPrefix ? IdentityTitleLayout.Classic : IdentityTitleLayout.Subtitle);
        }
    }

    // ---------------------------------------------------------------- name

    internal void SetNameVisible(bool visible) => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicName).Visible = visible;
        ctx.RequestLayout(force: false);
    });

    /// <summary>Live name typing; commit with <see cref="Commit"/>.</summary>
    internal void SetNameText(string text) => EditContinuous(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicName).Text = Limit(text, TextProfileElement.MaxTextLength);
        ctx.RequestLayout(force: false);
    });

    /// <summary>Fills the name from the logged-in character (explicit; never done silently).</summary>
    internal void UseCharacterName()
    {
        if (CharacterName is { Length: > 0 } characterName)
        {
            Edit(ctx =>
            {
                ctx.EnsureElement(ProfileElementRole.BasicName).Text = characterName;
                ctx.RequestLayout(force: false);
            });
        }
    }

    // ---------------------------------------------------------------- title

    /// <summary>
    /// None hides the title element (keeping its text); FFXIV Title and Custom show it with that
    /// source's text. Each source's own value (chosen title, custom text) is kept while another
    /// source is active, so switching back restores it.
    /// </summary>
    internal void SetTitleSource(IdentityTitleSource source) => Edit(ctx =>
    {
        var identity = ctx.Identity();

        // Keep an existing custom title (e.g. from an earlier version) before switching away.
        if (identity.TitleSource != source && GetTitleSource(ctx.Profile) == IdentityTitleSource.Custom
            && Find(ctx.Profile, ProfileElementRole.BasicTitle) is { Text.Length: > 0 } existing)
        {
            identity.CustomTitle = Limit(existing.Text, BasicIdentityHeader.MaxCustomTitleLength);
        }

        identity.TitleSource = source;

        if (source == IdentityTitleSource.None)
        {
            if (Find(ctx.Profile, ProfileElementRole.BasicTitle) is { } hidden)
            {
                hidden.Visible = false;
            }
        }
        else
        {
            var title = ctx.EnsureElement(ProfileElementRole.BasicTitle);
            title.Visible = true;
            title.Text = source == IdentityTitleSource.Custom
                ? identity.CustomTitle
                : ResolveGameTitleText(identity.GameTitleId) ?? string.Empty;
        }

        ctx.RequestLayout(force: false);
    });

    /// <summary>Chooses an FFXIV title (and records its before/after-name placement metadata).</summary>
    internal void SelectGameTitle(GameTitle gameTitle) => Edit(ctx =>
    {
        var identity = ctx.Identity();
        identity.TitleSource = IdentityTitleSource.GameTitle;
        identity.GameTitleId = gameTitle.Id;
        identity.GameTitleIsPrefix = gameTitle.IsPrefix;

        var title = ctx.EnsureElement(ProfileElementRole.BasicTitle);
        title.Visible = true;
        title.Text = gameTitle.GetText(GameTitleCatalog.UseFeminineForms);
        ctx.RequestLayout(force: false);
    });

    /// <summary>Live custom-title typing (capped at <see cref="BasicIdentityHeader.MaxCustomTitleLength"/>); commit with <see cref="Commit"/>.</summary>
    internal void SetCustomTitle(string text) => EditContinuous(ctx =>
    {
        var value = Limit(text.Replace('\n', ' '), BasicIdentityHeader.MaxCustomTitleLength);
        var identity = ctx.Identity();
        identity.TitleSource = IdentityTitleSource.Custom;
        identity.CustomTitle = value;

        var title = ctx.EnsureElement(ProfileElementRole.BasicTitle);
        title.Visible = true;
        title.Text = value;
        ctx.RequestLayout(force: false);
    });

    internal void SetPrefix(string symbol) => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicTitle).Prefix = Limit(symbol, TextProfileElement.MaxAffixLength);
        ctx.RequestLayout(force: false);
    });

    internal void SetSuffix(string symbol) => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicTitle).Suffix = Limit(symbol, TextProfileElement.MaxAffixLength);
        ctx.RequestLayout(force: false);
    });

    // ---------------------------------------------------------------- tagline

    internal void SetTaglineVisible(bool visible) => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicTagline).Visible = visible;
        ctx.RequestLayout(force: false);
    });

    /// <summary>Live tagline typing; commit with <see cref="Commit"/>.</summary>
    internal void SetTaglineText(string text) => EditContinuous(ctx =>
    {
        var tagline = ctx.EnsureElement(ProfileElementRole.BasicTagline);
        tagline.Text = Limit(text.Replace('\n', ' '), BasicIdentityHeader.MaxTaglineLength);
        tagline.Visible = true;
        ctx.RequestLayout(force: false);
    });

    // ---------------------------------------------------------------- styling (shared typography)

    /// <summary>
    /// Edits one identity element's style through the shared text properties (font, size, color,
    /// outline, shadow, alignment...) — the same fields and renderer every other text uses.
    /// <paramref name="continuous"/> coalesces a slider/color drag into one undo step (commit with
    /// <see cref="Commit"/>). A no-op if that element doesn't exist.
    /// </summary>
    internal void EditStyle(ProfileElementRole role, Action<TextProfileElement> apply, bool continuous)
    {
        void Change(EditContext ctx)
        {
            if (Find(ctx.Profile, role) is { } element)
            {
                apply(element);
                ctx.RequestLayout(force: false);
            }
        }

        if (continuous)
        {
            EditContinuous(Change);
        }
        else
        {
            Edit(Change);
        }
    }

    /// <summary>
    /// Populates the name/title/tagline colors from a shared theme preset (primary text, accent,
    /// soft text). Only copies values: every color stays individually editable afterwards.
    /// </summary>
    internal void ApplyThemeColors(ProfileThemePreset preset) => Edit(ctx =>
    {
        SetColor(Find(ctx.Profile, ProfileElementRole.BasicName), preset.TextColor);
        SetColor(Find(ctx.Profile, ProfileElementRole.BasicTitle), preset.AccentTextColor);
        SetColor(Find(ctx.Profile, ProfileElementRole.BasicTagline), preset.SoftTextColor);

        // Opacity (the color's alpha) is kept; only the hue changes.
        static void SetColor(TextProfileElement? element, Vector4 color)
        {
            if (element is not null)
            {
                element.Color = color with { W = element.Color.W };
            }
        }
    });

    /// <summary>Ends the current continuous (slider/color/typing) edit, recording its single undo step.</summary>
    internal void Commit() => editorSession.CommitPendingDocumentEdit();

    /// <summary>
    /// Call once per frame from the Basic editor. If an inline layout had to be placed from
    /// estimated widths (a font face wasn't built yet), re-measures once the font is ready and
    /// folds the exact placement into that same undo step. Changes nothing otherwise.
    /// </summary>
    internal void RefineLayout()
    {
        if (!refineNeeded || editorSession.HasPendingDocumentEdit || profileService.CurrentProfile is not { } profile)
        {
            return;
        }

        if (IsCustomized(profile))
        {
            // Moved elsewhere since: Basic no longer manages its placement, so nothing to refine.
            refineNeeded = false;
            return;
        }

        if (!TryMeasureAll(profile))
        {
            return; // Still waiting for the font; try again next frame.
        }

        refineNeeded = false;
        editorSession.AmendLastDocumentEdit(() =>
        {
            var context = new EditContext(this, profile);
            context.RequestLayout(force: false);
            context.Finish();
        });
    }

    // ---------------------------------------------------------------- internals

    private void Edit(Action<EditContext> change) =>
        editorSession.ApplyDocumentEdit(() => RunEdit(change));

    private void EditContinuous(Action<EditContext> change) =>
        editorSession.BeginOrContinueDocumentEdit(() => RunEdit(change));

    private void RunEdit(Action<EditContext> change)
    {
        // Validates that the profile is editable (character, busy state) before touching it.
        profileService.RequireEditableProfile();
        var profile = profileService.CurrentProfile ?? throw new InvalidOperationException("No Plate is open.");

        var context = new EditContext(this, profile);
        change(context);
        context.Finish();
    }

    private string? ResolveGameTitleText(uint titleId) =>
        titleId > 0 && Titles.Find(titleId) is { } title ? title.GetText(GameTitleCatalog.UseFeminineForms) : null;

    private bool TryMeasureAll(ProfileDocument profile)
    {
        foreach (var role in (ReadOnlySpan<ProfileElementRole>)[ProfileElementRole.BasicName, ProfileElementRole.BasicTitle])
        {
            if (Find(profile, role) is { } element && !ProfileTextRenderer.TryMeasureNaturalWidth(element, fonts, out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Style defaults that define the Badge and Accent looks, applied only on entering/leaving
    /// those layouts (so switching between the plain layouts never disturbs the user's styling).
    /// </summary>
    private static void ApplyLayoutStyle(TextProfileElement title, IdentityTitleLayout previous, IdentityTitleLayout next, float nameSize)
    {
        if (next == IdentityTitleLayout.Badge && previous != IdentityTitleLayout.Badge)
        {
            title.FontSize = ClampFont(nameSize * BadgeSizeRatio);
            title.Bold = true;
            title.LetterSpacing = MathF.Round(Math.Max(1.5f, nameSize * BadgeLetterSpacingRatio), 1);
        }
        else if (previous == IdentityTitleLayout.Badge && next != IdentityTitleLayout.Badge)
        {
            title.FontSize = ClampFont(nameSize * TitleSizeRatio);
            title.Bold = false;
            title.LetterSpacing = 0f;
        }

        if (next == IdentityTitleLayout.Accent && previous != IdentityTitleLayout.Accent)
        {
            title.Italic = true;
            if (title.Prefix.Length == 0 && title.Suffix.Length == 0)
            {
                title.Prefix = AccentDecoration;
                title.Suffix = AccentDecoration;
            }
        }
        else if (previous == IdentityTitleLayout.Accent && next != IdentityTitleLayout.Accent)
        {
            title.Italic = false;
        }
    }

    private static float ClampFont(float size) =>
        Math.Clamp(MathF.Round(size), TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize);

    private static string Limit(string? text, int maxLength)
    {
        var value = text ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>
    /// One Identity edit in progress: creates missing pieces on demand, and at the end places the
    /// header if the edit asked for it and the rules allow (see the type docs).
    /// </summary>
    private sealed class EditContext
    {
        private readonly BasicIdentitySession owner;
        private readonly bool wasCustomized;
        private readonly bool hadNoHeader;
        private bool layoutRequested;
        private bool forceLayout;
        private bool createdElement;

        internal EditContext(BasicIdentitySession owner, ProfileDocument profile)
        {
            this.owner = owner;
            Profile = profile;

            // Judged BEFORE this edit touches anything, so its own changes can't read as customization.
            wasCustomized = IsCustomized(profile);
            hadNoHeader = HasNoHeader(profile);
        }

        internal ProfileDocument Profile { get; }

        private float CanvasScale => Profile.CanvasHeight / ReferenceCanvasHeight;

        internal float ScaledNameSize => ClampFont(DefaultNameFontSize * CanvasScale);

        internal BasicIdentityHeader Identity()
        {
            owner.profileService.UpdateBasicIdentity(CreateIdentity, static _ => { });
            return Profile.BasicIdentity!;
        }

        /// <summary>Moves the header region back to the default (scaled to the canvas).</summary>
        internal void ResetRegion()
        {
            var identity = Identity();
            var scaleX = Profile.CanvasWidth / ReferenceCanvasWidth;
            identity.RegionPosition = DefaultRegionPosition * new Vector2(scaleX, CanvasScale);
            identity.RegionWidth = DefaultRegionWidth * scaleX;
        }

        internal void RequestLayout(bool force)
        {
            layoutRequested = true;
            forceLayout |= force;
        }

        /// <summary>The element for <paramref name="role"/>, created with Identity defaults if missing.</summary>
        internal TextProfileElement EnsureElement(ProfileElementRole role)
        {
            if (Find(Profile, role) is { } existing)
            {
                return existing;
            }

            Identity();

            var nameSize = Find(Profile, ProfileElementRole.BasicName)?.FontSize ?? ScaledNameSize;
            var element = new TextProfileElement
            {
                Role = role,
                FontFamily = ProfileFontFamilies.AetherFrameSans,
                Wrap = false,
                AutoFitText = true,
                AutoFitMinimumSize = AutoFitMinimum,
                Alignment = Find(Profile, ProfileElementRole.BasicName)?.Alignment ?? TextAlignment.Left,
            };

            switch (role)
            {
                case ProfileElementRole.BasicName:
                    element.Text = owner.CharacterName ?? string.Empty;
                    element.FontSize = nameSize;
                    element.Color = DefaultNameColor;
                    break;
                case ProfileElementRole.BasicTitle:
                    var layout = Profile.BasicIdentity!.Layout;
                    element.FontSize = ClampFont(nameSize * (layout == IdentityTitleLayout.Badge ? BadgeSizeRatio : TitleSizeRatio));
                    element.Color = DefaultTitleColor;
                    ApplyLayoutStyle(element, IdentityTitleLayout.Subtitle, layout, nameSize);
                    break;
                default:
                    element.FontSize = ClampFont(DefaultTaglineFontSize * CanvasScale);
                    element.Color = DefaultTaglineColor;
                    element.Italic = true;
                    break;
            }

            // A reasonable spot for a piece added to a header Basic isn't managing (customized or
            // from an earlier version): just below the existing identity elements, without moving
            // any of them. A managed or brand-new header is placed by the layout at Finish.
            PlaceBelowExisting(element);

            owner.profileService.AddElement(element);
            createdElement = true;
            return element;
        }

        internal void Finish()
        {
            if (!layoutRequested && !createdElement)
            {
                return;
            }

            // Placement rules: explicit layout actions always place; a brand-new header gets its
            // first placement; a Basic-managed header reflows; a customized one never moves.
            var place = forceLayout || hadNoHeader || (!wasCustomized && Profile.BasicIdentity?.AppliedLayout is not null);
            if (place)
            {
                ApplyLayout();
            }
        }

        private void ApplyLayout()
        {
            var identity = Identity();
            var name = Find(Profile, ProfileElementRole.BasicName);
            var title = Find(Profile, ProfileElementRole.BasicTitle);
            var tagline = Find(Profile, ProfileElementRole.BasicTagline);

            var inline = identity.Layout is IdentityTitleLayout.InlineBefore or IdentityTitleLayout.InlineAfter;
            var result = IdentityHeaderLayout.Compute(
                identity.Layout,
                identity.RegionPosition,
                identity.RegionWidth,
                name?.Alignment ?? TextAlignment.Left,
                LineFor(name, inline, reserveWhenEmpty: true),
                LineFor(title, inline, reserveWhenEmpty: false),
                LineFor(tagline, inline: false, reserveWhenEmpty: false));

            Assign(name, result.Name);
            Assign(title, result.Title);
            Assign(tagline, result.Tagline);

            identity.AppliedLayout = new IdentityLayoutSnapshot { Name = result.Name, Title = result.Title, Tagline = result.Tagline };
        }

        /// <param name="reserveWhenEmpty">
        /// False for the title and tagline: with no text yet (e.g. FFXIV Title chosen but no title
        /// picked) they draw nothing in the finished profile, so they take no space either — only
        /// their editor placeholder shows where they'll go.
        /// </param>
        private IdentityHeaderLayout.Line LineFor(TextProfileElement? element, bool inline, bool reserveWhenEmpty)
        {
            if (element is null)
            {
                return default;
            }

            var takesSpace = element.Visible && (reserveWhenEmpty || element.Text.Length > 0);

            var width = 0f;
            if (inline && !ProfileTextRenderer.TryMeasureNaturalWidth(element, owner.fonts, out width))
            {
                // Font not built yet: estimate now, re-measure once it is (RefineLayout).
                width = element.GetDisplayText().Length * element.FontSize * 0.5f;
                owner.refineNeeded = true;
            }

            return new IdentityHeaderLayout.Line(true, takesSpace, element.FontSize, width);
        }

        private static void Assign(TextProfileElement? element, ElementRect? rect)
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

        private void PlaceBelowExisting(TextProfileElement element)
        {
            var identity = Profile.BasicIdentity!;
            var x = identity.RegionPosition.X;
            var width = identity.RegionWidth;
            var y = identity.RegionPosition.Y;

            foreach (var role in (ReadOnlySpan<ProfileElementRole>)[ProfileElementRole.BasicName, ProfileElementRole.BasicTitle, ProfileElementRole.BasicTagline])
            {
                if (Find(Profile, role) is { } other)
                {
                    y = Math.Max(y, other.Position.Y + other.Size.Y);
                }
            }

            var height = IdentityHeaderLayout.BoxHeight(element.FontSize);
            y = Math.Min(y, Math.Max(0f, Profile.CanvasHeight - height));
            element.Position = new Vector2(x, y);
            element.Size = new Vector2(Math.Max(EditorSession.MinElementWidth, width), height);
        }

        /// <summary>
        /// New settings: the header region follows an existing name element if there is one (so
        /// nothing jumps), otherwise the default region scaled to the canvas.
        /// </summary>
        private BasicIdentityHeader CreateIdentity()
        {
            var scaleX = Profile.CanvasWidth / ReferenceCanvasWidth;
            var identity = new BasicIdentityHeader
            {
                Layout = IdentityTitleLayout.Subtitle,
                RegionPosition = DefaultRegionPosition * new Vector2(scaleX, CanvasScale),
                RegionWidth = DefaultRegionWidth * scaleX,
            };

            // Legacy header: bind to where it already is, and keep its existing title visible.
            var existingName = Find(Profile, ProfileElementRole.BasicName);
            var existingTitle = Find(Profile, ProfileElementRole.BasicTitle);
            var anchor = existingName ?? existingTitle ?? Find(Profile, ProfileElementRole.BasicTagline);
            if (anchor is not null)
            {
                identity.RegionPosition = anchor.Position;
                identity.RegionWidth = anchor.Size.X;
            }

            if (existingTitle is { Visible: true, Text.Length: > 0 })
            {
                identity.TitleSource = IdentityTitleSource.Custom;
                identity.CustomTitle = Limit(existingTitle.Text, BasicIdentityHeader.MaxCustomTitleLength);
            }

            return identity;
        }
    }
}
