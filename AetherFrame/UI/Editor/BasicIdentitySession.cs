using System;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The Basic editor's Identity Header: Character Name and Title — two separate role-tagged
/// <see cref="TextProfileElement"/>s plus the profile's
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
/// a layout, presses Apply Layout, or resets.</para>
///
/// <para><b>History.</b> Every action goes through <see cref="EditorSession.ApplyDocumentEdit"/>
/// (discrete) or <see cref="EditorSession.BeginOrContinueDocumentEdit"/> (sliders, colors,
/// typing: one entry per drag/typing run), so each is exactly one undo step no matter how many
/// elements it touched, and dirty state/save/revert see it like any other edit.</para>
///
/// The pure rules (default styles, placement math, customization) live in
/// <see cref="IdentityHeaderRules"/>; this class decides when they apply.
/// </summary>
internal sealed class BasicIdentitySession
{
    /// <summary>The decoration symbols offered for the title (all drawable by the Plate's fonts; "" = none).</summary>
    internal static readonly string[] DecorationSymbols = IdentityHeaderRules.DecorationSymbols;

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly ICharacterInfoSource characterInfo;
    private readonly IIdentityTextMeasurer measurer;
    private readonly IGameTitleSource titles;

    // An inline layout was last computed with estimated widths because a font face wasn't built
    // yet; RefineLayout re-measures and folds the exact placement into that same undo entry.
    private bool refineNeeded;

    internal BasicIdentitySession(
        ProfileService profileService,
        EditorSession editorSession,
        ICharacterInfoSource characterInfo,
        IIdentityTextMeasurer measurer,
        IGameTitleSource titles)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.characterInfo = characterInfo;
        this.measurer = measurer;
        this.titles = titles;
    }

    internal string? CharacterName => characterInfo.CurrentInfo?.Name is { Length: > 0 } name ? name : null;

    // ---------------------------------------------------------------- read-only state

    internal static TextProfileElement? Find(ProfileDocument profile, ProfileElementRole role) => IdentityHeaderRules.Find(profile, role);

    /// <summary>True when neither identity element (name, title) exists yet (a fresh profile).</summary>
    internal static bool HasNoHeader(ProfileDocument profile) => IdentityHeaderRules.HasNoHeader(profile);

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
    internal static bool IsCustomized(ProfileDocument profile) => IdentityHeaderRules.IsCustomized(profile);

    /// <summary>
    /// For an FFXIV title: true when the title element's text no longer matches the chosen game
    /// title (it was edited in the Advanced editor). Shown as a note; never "corrected".
    /// </summary>
    internal bool IsGameTitleTextEdited(ProfileDocument profile) =>
        profile.BasicIdentity is { TitleSource: IdentityTitleSource.GameTitle, GameTitleId: > 0 } identity
        && titles.Find(identity.GameTitleId) is { } title
        && Find(profile, ProfileElementRole.BasicTitle) is { } element
        && element.Text != title.Masculine && element.Text != title.Feminine;

    // ---------------------------------------------------------------- header lifecycle

    /// <summary>
    /// Creates the header for a profile that has none: the Character Name, filled from the
    /// logged-in character, placed by the default layout. The title is created later,
    /// when first turned on. One undo step.
    /// </summary>
    internal void CreateHeader() => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicName);
        ctx.RequestLayout(force: true);
    });

    /// <summary>
    /// Explicitly re-places the header in the Adventure Plate layout's header region (for the
    /// current orientation) with its current title layout — the one way, besides a reset, that a
    /// customized header moves. Style and content are kept.
    /// </summary>
    internal void ApplyLayout() => Edit(ctx =>
    {
        if (!HasNoHeader(ctx.Profile))
        {
            ctx.ResetRegion();
            ctx.RequestLayout(force: true);
        }
    });

    /// <summary>
    /// Reset Section: the header back to the Adventure Plate Classic defaults — the layout's
    /// region, default typography and colors for each identity element — then placed with its
    /// current title layout. Creates the name if missing; keeps every text, the title source and
    /// layout choice, and visibility. One undo step.
    /// </summary>
    internal void ResetSection() => Edit(ctx =>
    {
        ctx.EnsureElement(ProfileElementRole.BasicName);

        // The name first: the title follows its size and alignment. (A legacy tagline is Advanced
        // content now, and is left exactly as it is.)
        foreach (var role in (ReadOnlySpan<ProfileElementRole>)[ProfileElementRole.BasicName, ProfileElementRole.BasicTitle])
        {
            if (Find(ctx.Profile, role) is { } element)
            {
                IdentityHeaderRules.ApplyDefaultStyle(element, ctx.Profile);
            }
        }

        ctx.ResetRegion();
        ctx.RequestLayout(force: true);
    });

    /// <summary>
    /// A plate-wide Basic layout action (Apply Layout, Reset Basic Layout, an orientation change)
    /// as ONE undo step: <paramref name="plateEdit"/> on the sections, then — if a header exists —
    /// the header moved to the layout's region for the resulting orientation and re-placed.
    /// <paramref name="onlyIfManaged"/> leaves a customized header exactly where it is.
    /// </summary>
    internal void EditPlateLayout(Action<BasicPlateEditor> plateEdit, bool onlyIfManaged) => Edit(ctx =>
    {
        plateEdit(BasicEditorSession.CreatePlateEditor(profileService, ctx.Profile));
        if (!HasNoHeader(ctx.Profile) && !(onlyIfManaged && ctx.WasCustomized))
        {
            ctx.ResetRegion();
            ctx.RequestLayout(force: true);
        }
    });

    /// <summary>
    /// Chooses a curated layout and places the header (explicit, so always). The title swaps the
    /// previous layout's look for this one's (see <see cref="IdentityHeaderRules.ChangeLayoutLook"/>);
    /// its text, prefix, and suffix are never touched.
    /// </summary>
    internal void SetLayout(IdentityTitleLayout layout) => Edit(ctx =>
    {
        var identity = ctx.Identity();
        var previous = identity.Layout;
        identity.Layout = layout;

        IdentityHeaderRules.ChangeLayoutLook(
            identity,
            Find(ctx.Profile, ProfileElementRole.BasicTitle),
            previous,
            layout,
            Find(ctx.Profile, ProfileElementRole.BasicName)?.FontSize ?? IdentityHeaderRules.ScaledNameSize(ctx.Profile));

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
        title.Text = gameTitle.GetText(titles.FeminineForms);
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

    /// <summary>Removes both title decorations (one undo step). An explicit choice: nothing removes them on its own.</summary>
    internal void ClearDecoration() => Edit(ctx =>
    {
        if (Find(ctx.Profile, ProfileElementRole.BasicTitle) is { } title)
        {
            title.Prefix = string.Empty;
            title.Suffix = string.Empty;
            ctx.RequestLayout(force: false);
        }
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
        titleId > 0 && titles.Find(titleId) is { } title ? title.GetText(titles.FeminineForms) : null;

    private bool TryMeasureAll(ProfileDocument profile)
    {
        foreach (var role in (ReadOnlySpan<ProfileElementRole>)[ProfileElementRole.BasicName, ProfileElementRole.BasicTitle])
        {
            if (Find(profile, role) is { } element && !measurer.TryMeasureNaturalWidth(element, out _))
            {
                return false;
            }
        }

        return true;
    }

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
        private readonly bool hadNoHeader;
        private bool layoutRequested;
        private bool forceLayout;
        private bool createdElement;

        internal EditContext(BasicIdentitySession owner, ProfileDocument profile)
        {
            this.owner = owner;
            Profile = profile;

            // Judged BEFORE this edit touches anything, so its own changes can't read as customization.
            WasCustomized = IsCustomized(profile);
            hadNoHeader = HasNoHeader(profile);
        }

        internal ProfileDocument Profile { get; }

        /// <summary>Whether the header was customized when this edit began.</summary>
        internal bool WasCustomized { get; }

        internal BasicIdentityHeader Identity()
        {
            owner.profileService.UpdateBasicIdentity(CreateIdentity, static _ => { });
            return Profile.BasicIdentity!;
        }

        /// <summary>Moves the header region to the Adventure Plate layout's (for the current orientation).</summary>
        internal void ResetRegion()
        {
            var identity = Identity();
            (identity.RegionPosition, identity.RegionWidth) = IdentityHeaderRules.DefaultRegion(Profile);
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
            var element = IdentityHeaderRules.Create(role, Profile, owner.CharacterName);

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
            var place = forceLayout || hadNoHeader || (!WasCustomized && Profile.BasicIdentity?.AppliedLayout is not null);
            if (place && !HasNoHeader(Profile))
            {
                Identity();
                var exact = IdentityHeaderRules.Place(Profile, Measure, CountLines);
                if (!exact)
                {
                    // Font not built yet: estimated now, re-measured once it is (RefineLayout).
                    owner.refineNeeded = true;
                }
            }
            else if (!HasNoHeader(Profile))
            {
                // A customized header stays where the player put it, but its name is never left in
                // a box too small for it (e.g. after typing a longer name here).
                IdentityHeaderRules.KeepNameReadable(Profile, Measure, CountLines);
            }
        }

        private float? Measure(TextProfileElement element) =>
            owner.measurer.TryMeasureNaturalWidth(element, out var width) ? width : null;

        private int? CountLines(TextProfileElement element, float fontSize, float maxWidth) =>
            owner.measurer.TryCountLines(element, fontSize, maxWidth, out var lines) ? lines : null;

        private void PlaceBelowExisting(TextProfileElement element)
        {
            var identity = Profile.BasicIdentity!;
            var x = identity.RegionPosition.X;
            var width = identity.RegionWidth;
            var y = identity.RegionPosition.Y;

            foreach (var role in (ReadOnlySpan<ProfileElementRole>)[ProfileElementRole.BasicName, ProfileElementRole.BasicTitle])
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
        /// nothing jumps), otherwise the Adventure Plate layout's region.
        /// </summary>
        private BasicIdentityHeader CreateIdentity()
        {
            var identity = IdentityHeaderRules.CreateSettings(Profile);

            // Legacy header: bind to where it already is, and keep its existing title visible.
            var existingName = Find(Profile, ProfileElementRole.BasicName);
            var existingTitle = Find(Profile, ProfileElementRole.BasicTitle);
            var anchor = existingName ?? existingTitle;
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
