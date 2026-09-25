using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// The Adventure Plate Classic sections' edit rules (everything but the Identity Header, which
/// <c>BasicIdentitySession</c> owns), applied directly to one document. Pure: no ImGui, no history.
/// The Basic editor runs each call inside one <c>EditorSession</c> document edit, so every action
/// here is exactly one undo step and is seen by dirty state like any other edit.
///
/// <para><b>Nothing is created implicitly.</b> A section's elements are only created by a call
/// that needs them (typing a value, showing a section, a reset), and only the missing ones —
/// existing elements are always reused, so repeating an action never duplicates anything.</para>
///
/// <para><b>Basic-managed vs. customized.</b> Every placement Basic makes is recorded
/// (<see cref="BasicPlateSettings.Placements"/>). An element still exactly there follows the
/// layout; once moved or resized elsewhere (the Advanced editor) it is customized. Content and
/// style edits never move anything — except that a Basic-managed Favorite Job row reflows when the
/// level's text or visibility changes, so the job keeps its compact gap after the level. Otherwise
/// only <see cref="ApplyLayout"/>, <see cref="ResetSection"/>, <see cref="ResetLayout"/> (all
/// explicit), and an orientation change (managed elements only) move anything.</para>
/// </summary>
internal sealed class BasicPlateEditor
{
    private readonly Action<ProfileElement> addElement;
    private readonly Action<ProfileElement> removeElement;

    /// <param name="profile">The document to edit.</param>
    /// <param name="addElement">Adds a new element (assigning its Z order and enforcing capacity).</param>
    /// <param name="removeElement">Removes an element.</param>
    internal BasicPlateEditor(ProfileDocument profile, Action<ProfileElement> addElement, Action<ProfileElement> removeElement)
    {
        Profile = profile;
        this.addElement = addElement;
        this.removeElement = removeElement;
    }

    internal ProfileDocument Profile { get; }

    /// <summary>The settings, created (Normal orientation) by the first edit that needs them.</summary>
    internal BasicPlateSettings Settings => Profile.BasicPlate ??= new BasicPlateSettings();

    internal static AdventurePlateOrientation GetOrientation(ProfileDocument profile) =>
        profile.BasicPlate?.Orientation ?? AdventurePlateOrientation.Normal;

    // ---------------------------------------------------------------- queries

    /// <summary>True while the element sits exactly where Basic last placed it.</summary>
    internal static bool IsManaged(ProfileDocument profile, ProfileElement element) =>
        profile.BasicPlate?.GetPlacement(element.Role) is { } rect && rect.Matches(element.Position, element.Size);

    /// <summary>
    /// True when the section's layout group (see <see cref="BasicSections.LayoutGroups"/>) no longer
    /// follows the Basic layout: any existing element of it — heading, value, or, for Favorite Job
    /// and Level, either section's — was moved or resized elsewhere, or placed before Basic tracked
    /// placement. Judged per group, never per element, so a group is always either wholly Basic's
    /// or wholly the user's. A group with no elements isn't customized.
    /// </summary>
    internal static bool IsSectionCustomized(ProfileDocument profile, BasicSection section)
    {
        if (section == BasicSection.Identity)
        {
            return IdentityHeaderRules.IsCustomized(profile);
        }

        foreach (var element in GroupElements(profile, section))
        {
            if (!IsManaged(profile, element))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every customized layout group, as its primary section (Favorite Job and Level report once,
    /// as <see cref="BasicSection.Job"/>), in section order.
    /// </summary>
    internal static List<BasicSection> CustomizedSections(ProfileDocument profile)
    {
        var sections = new List<BasicSection>();
        foreach (var group in BasicSections.LayoutGroups)
        {
            if (IsSectionCustomized(profile, group[0]))
            {
                sections.Add(group[0]);
            }
        }

        return sections;
    }

    /// <summary>
    /// The existing elements of the section's whole layout group (not the Identity Header), without
    /// the retired Level (see <see cref="BasicSections.IsRetired"/>): it neither decides whether a
    /// group is customized nor is ever placed, styled or reset with it.
    /// </summary>
    internal static List<ProfileElement> GroupElements(ProfileDocument profile, BasicSection section)
    {
        var elements = new List<ProfileElement>();
        foreach (var member in BasicSections.LayoutGroupOf(section))
        {
            foreach (var role in RolesOf(member))
            {
                if (!BasicSections.IsRetired(role) && BasicSections.Find(profile, role) is { } element)
                {
                    elements.Add(element);
                }
            }
        }

        return elements;
    }

    /// <summary>
    /// The area a layout group currently covers on the Plate: the union of its visible elements
    /// (the Identity Header: name, title, tagline), wherever they are now — or null when none is
    /// shown.
    /// </summary>
    internal static ElementRect? CurrentGroupBounds(ProfileDocument profile, BasicSection section)
    {
        IEnumerable<ProfileElement> elements = section == BasicSection.Identity
            ? BasicSections.Get(BasicSection.Identity).Values.Select(role => BasicSections.Find(profile, role)).OfType<ProfileElement>()
            : GroupElements(profile, section);

        ElementRect? bounds = null;
        foreach (var element in elements)
        {
            if (element.Visible)
            {
                var rect = new ElementRect(element.Position, element.Size);
                bounds = bounds?.Union(rect) ?? rect;
            }
        }

        return bounds;
    }

    /// <summary>
    /// Layout groups whose current areas intersect, as (first, second) in group order. Basic's own
    /// layout never produces one; it happens when a customized group stays where the user placed
    /// it while the groups Basic owns follow an orientation change (or when the user drags one
    /// onto another). Detection only: resolving it is always the user's explicit choice.
    /// </summary>
    internal static List<(BasicSection First, BasicSection Second)> FindOverlaps(ProfileDocument profile)
    {
        var overlaps = new List<(BasicSection, BasicSection)>();
        var groups = BasicSections.LayoutGroups;
        for (var i = 0; i < groups.Length; i++)
        {
            if (CurrentGroupBounds(profile, groups[i][0]) is not { } a)
            {
                continue;
            }

            for (var j = i + 1; j < groups.Length; j++)
            {
                if (CurrentGroupBounds(profile, groups[j][0]) is { } b && a.Intersects(b))
                {
                    overlaps.Add((groups[i][0], groups[j][0]));
                }
            }
        }

        return overlaps;
    }

    /// <summary>The section's heading (if any) followed by its value roles.</summary>
    internal static IEnumerable<ProfileElementRole> RolesOf(BasicSection section)
    {
        var definition = BasicSections.Get(section);
        if (definition.Heading is { } heading)
        {
            yield return heading;
        }

        foreach (var role in definition.Values)
        {
            yield return role;
        }
    }

    // ---------------------------------------------------------------- section headings

    /// <summary>
    /// The standard section headings on the Plate (Home World, Favorite Job, Free Company, Playstyle,
    /// Active Hours, Message — whichever exist), in section order.
    /// </summary>
    internal static List<TextProfileElement> Headings(ProfileDocument profile)
    {
        var headings = new List<TextProfileElement>();
        foreach (var definition in BasicSections.All)
        {
            if (definition.Heading is { } role && BasicSections.FindText(profile, role) is { } heading)
            {
                headings.Add(heading);
            }
        }

        return headings;
    }

    /// <summary>The headings' shared size as Basic shows it (the first heading's), or null with no headings.</summary>
    internal static float? HeadingSize(ProfileDocument profile)
    {
        foreach (var definition in BasicSections.All)
        {
            if (definition.Heading is { } role && BasicSections.FindText(profile, role) is { } heading)
            {
                return heading.FontSize;
            }
        }

        return null;
    }

    /// <summary>True when the headings don't all have one size (e.g. one was resized in the Advanced Editor).</summary>
    internal static bool HeadingSizesDiffer(ProfileDocument profile)
    {
        var size = HeadingSize(profile);
        return Headings(profile).Exists(heading => !heading.FontSize.Equals(size));
    }

    /// <summary>
    /// Section heading size: every standard section heading at one size, together, clamped to what
    /// the layout shows at full size (<see cref="AdventurePlateClassicLayout.MaxHeadingFontSize"/>).
    /// Never their text or style, and never any value. A heading Basic still places has its box
    /// re-placed to fit the size (it grows or shrinks upward, see
    /// <see cref="AdventurePlateClassicLayout.MaxHeadingGrowth"/>; the value below never moves); a
    /// heading in a group customized in the Advanced Editor keeps its box. Headings created later
    /// start at the same size.
    /// </summary>
    internal void SetHeadingSize(float size)
    {
        var value = Math.Clamp(size, TextProfileElement.MinFontSize, AdventurePlateClassicLayout.MaxHeadingFontSize(Profile));

        // Judged before anything changes: which headings Basic still places.
        var headings = Headings(Profile);
        var placed = headings.FindAll(heading => BasicSections.SectionOf(heading.Role) is { } section && !IsSectionCustomized(Profile, section));

        foreach (var heading in headings)
        {
            heading.FontSize = value;
        }

        foreach (var heading in placed)
        {
            PlaceCore(heading);
        }
    }

    // ---------------------------------------------------------------- creation and content

    /// <summary>
    /// Creates whichever of the section's elements are missing, each at the layout's placement for
    /// the current orientation. Never the portrait (it needs an image), the Identity Header, or the
    /// Level (no longer part of Basic: a Plate that already shows one keeps it, but Basic never
    /// creates one).
    /// </summary>
    internal void EnsureSection(BasicSection section)
    {
        if (section is BasicSection.Portrait or BasicSection.Identity or BasicSection.Level)
        {
            return;
        }

        foreach (var role in RolesOf(section))
        {
            if (BasicSections.Find(Profile, role) is null)
            {
                Create(role);
            }
        }
    }

    /// <summary>The value element for a text role, creating its section if it has none.</summary>
    internal TextProfileElement EnsureText(ProfileElementRole role)
    {
        switch (BasicSections.Find(Profile, role))
        {
            case TextProfileElement existing:
                return existing;
            case { } other:
                throw new InvalidOperationException($"The {ProfileElementNames.GetDisplayName(other)} element isn't text.");
        }

        var section = BasicSections.SectionOf(role) ?? throw new ArgumentOutOfRangeException(nameof(role), role, "Not a Basic section role.");
        EnsureSection(section);
        return (TextProfileElement)BasicSections.Find(Profile, role)!;
    }

    /// <summary>Sets a section value's text (capped), creating the section if needed. Visibility is kept.</summary>
    internal void SetText(ProfileElementRole role, string? text) =>
        EnsureText(role).Text = Limit(text, TextProfileElement.MaxTextLength);

    /// <summary>
    /// Shows or hides every element of a section. Hiding never deletes anything. Showing a section
    /// that has no elements yet creates them (except the portrait, which needs an image first).
    /// </summary>
    internal void SetSectionVisible(BasicSection section, bool visible)
    {
        if (section == BasicSection.Identity)
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "The Identity Header has its own visibility controls.");
        }

        if (visible)
        {
            EnsureSection(section);
        }

        foreach (var role in RolesOf(section))
        {
            if (BasicSections.Find(Profile, role) is { } element)
            {
                element.Visible = visible;
            }
        }

    }

    /// <summary>
    /// Sets the Favorite Jobs, in order (the first is the primary favorite; duplicates dropped, at
    /// most <see cref="BasicFavoriteJobs.MaxJobs"/>): the stored ids (and the primary one older
    /// builds read), the value's stored text — always the full names, the canonical form; whether
    /// they're shown in full or abbreviated is decided whenever the Plate is drawn (see
    /// <see cref="BasicFavoriteJobs.DisplayText"/>) — and the heading, FAVORITE JOB or FAVORITE JOBS
    /// (unless it was given its own caption). Creates the section on the first job; never moves anything.
    /// </summary>
    internal void SetFavoriteJobs(IEnumerable<FavoriteJob> jobs)
    {
        var list = BasicFavoriteJobs.Normalize(jobs);
        Settings.FavoriteJobIds = list.ConvertAll(job => job.Id);
        Settings.FavoriteJobId = list.Count > 0 ? list[0].Id : 0;

        if (list.Count > 0 || BasicSections.FindText(Profile, ProfileElementRole.BasicJob) is not null)
        {
            EnsureText(ProfileElementRole.BasicJob).Text = Limit(BasicFavoriteJobs.FullText(list), TextProfileElement.MaxTextLength);
        }

        if (BasicSections.FindText(Profile, ProfileElementRole.BasicJobHeading) is { } heading && BasicFavoriteJobs.IsDefaultHeading(heading.Text))
        {
            heading.Text = BasicFavoriteJobs.Heading(list.Count);
        }
    }

    /// <summary>
    /// Replaces the playstyle entries: each sanitized, empties and case-insensitive duplicates
    /// dropped, at most <see cref="BasicPlateSettings.MaxPlaystyles"/> kept (in order).
    /// </summary>
    internal void SetPlaystyles(IEnumerable<string> entries)
    {
        var list = new List<string>();
        foreach (var entry in entries)
        {
            var value = BasicPlateText.NormalizePlaystyle(entry);
            if (value.Length == 0 || list.Exists(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (list.Count == BasicPlateSettings.MaxPlaystyles)
            {
                break;
            }

            list.Add(value);
        }

        Settings.Playstyles = list;
        SetText(ProfileElementRole.BasicPlaystyle, BasicPlateText.Playstyles(list));
    }

    /// <summary>Adds one entry; false (nothing changed) when full, empty, or already present.</summary>
    internal bool AddPlaystyle(string entry)
    {
        var current = Profile.BasicPlate?.Playstyles ?? new List<string>();
        var value = BasicPlateText.NormalizePlaystyle(entry);
        if (value.Length == 0 || current.Count >= BasicPlateSettings.MaxPlaystyles
            || current.Exists(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        SetPlaystyles([.. current, value]);
        return true;
    }

    internal void RemovePlaystyleAt(int index)
    {
        var current = Profile.BasicPlate?.Playstyles;
        if (current is null || index < 0 || index >= current.Count)
        {
            return;
        }

        var list = new List<string>(current);
        list.RemoveAt(index);
        SetPlaystyles(list);
    }

    /// <summary>Moves an entry one place earlier (<paramref name="offset"/> -1) or later (+1).</summary>
    internal void MovePlaystyle(int index, int offset)
    {
        var current = Profile.BasicPlate?.Playstyles;
        var target = index + offset;
        if (current is null || index < 0 || index >= current.Count || target < 0 || target >= current.Count)
        {
            return;
        }

        var list = new List<string>(current);
        (list[index], list[target]) = (list[target], list[index]);
        SetPlaystyles(list);
    }

    /// <summary>Sets (or with null, clears) the active hours and their displayed text.</summary>
    internal void SetActiveHours(BasicActiveHours? hours)
    {
        BasicActiveHours? value = null;
        if (hours is not null)
        {
            value = hours.Clone();
            value.Days &= BasicWeekdays.Everyday;
            value.StartMinutes = BasicActiveHours.NormalizeMinutes(value.StartMinutes);
            value.EndMinutes = BasicActiveHours.NormalizeMinutes(value.EndMinutes);
            value.TimeZone = Limit(value.TimeZone.Replace('\n', ' ').Trim(), BasicActiveHours.MaxTimeZoneLength);
        }

        Settings.ActiveHours = value;
        SetText(ProfileElementRole.BasicActiveHours, BasicPlateText.ActiveHours(value));
    }

    // ---------------------------------------------------------------- portrait

    /// <summary>Creates the portrait for an imported image asset, at the layout's placement.</summary>
    internal ImageProfileElement CreatePortrait(Guid assetId)
    {
        if (BasicSections.Find(Profile, ProfileElementRole.BasicPortrait) is not null)
        {
            throw new InvalidOperationException("This Plate already has a portrait.");
        }

        var portrait = (ImageProfileElement)Create(ProfileElementRole.BasicPortrait, image => image.AssetId = assetId);
        Settings.PortraitSource = BasicPortraitSource.ImportedImage;
        return portrait;
    }

    /// <summary>Removes the portrait element (its asset stays on disk, so undo can restore it).</summary>
    internal void RemovePortrait()
    {
        if (BasicSections.Find(Profile, ProfileElementRole.BasicPortrait) is { } portrait)
        {
            removeElement(portrait);
            Profile.BasicPlate?.RemovePlacement(ProfileElementRole.BasicPortrait);
        }
    }

    // ---------------------------------------------------------------- layout

    /// <summary>
    /// Places the section's whole layout group (for Favorite Job or Level: both) at the layout's
    /// placement, customized or not — the explicit way Basic reclaims it.
    /// </summary>
    internal void ApplyLayout(BasicSection section)
    {
        if (section == BasicSection.Identity)
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "The Identity Header is placed by BasicIdentitySession.");
        }

        foreach (var element in GroupElements(Profile, section))
        {
            Place(element);
        }
    }

    /// <summary>Places every existing section element (not the Identity Header). Creates nothing.</summary>
    internal void ApplyLayoutToAll()
    {
        foreach (var group in BasicSections.LayoutGroups)
        {
            if (group[0] != BasicSection.Identity)
            {
                ApplyLayout(group[0]);
            }
        }
    }

    /// <summary>
    /// Switches orientation. Each layout group moves as a whole — only when every element of it
    /// still follows the layout; a group with any customized element stays exactly where it is,
    /// all of it. Returns how many groups were left in place.
    /// </summary>
    internal int SetOrientation(AdventurePlateOrientation orientation)
    {
        // Judged before the switch, against the old orientation's placements.
        var moving = new List<ProfileElement>();
        var leftInPlace = 0;
        foreach (var group in BasicSections.LayoutGroups)
        {
            if (group[0] == BasicSection.Identity)
            {
                continue;
            }

            var elements = GroupElements(Profile, group[0]);
            if (elements.Count == 0)
            {
                continue;
            }

            if (IsSectionCustomized(Profile, group[0]))
            {
                leftInPlace++;
            }
            else
            {
                moving.AddRange(elements);
            }
        }

        Settings.Orientation = orientation;
        foreach (var element in moving)
        {
            Place(element);
        }

        return leftInPlace;
    }

    /// <summary>
    /// Restores a section's layout group (for Favorite Job or Level: both) to the Adventure Plate
    /// Classic defaults: creates any missing element (not the portrait), restores each one's
    /// default style (headings also their caption) and places them together. Content (text,
    /// portrait image) and visibility are kept; no other group is touched.
    /// </summary>
    internal void ResetSection(BasicSection section)
    {
        if (section == BasicSection.Identity)
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "The Identity Header is reset by BasicIdentitySession.");
        }

        foreach (var member in BasicSections.LayoutGroupOf(section))
        {
            EnsureSection(member);
        }

        foreach (var element in GroupElements(Profile, section))
        {
            AdventurePlateClassicLayout.ApplyDefaultStyle(element, Profile);
            Place(element);
        }
    }

    /// <summary>
    /// Reset Basic Layout (sections part): back to the Normal orientation, and every existing
    /// section element at its default placement. Content, styles, and visibility are kept; nothing
    /// is created or deleted; non-Basic elements are never touched.
    /// </summary>
    internal void ResetLayout()
    {
        Settings.Orientation = AdventurePlateOrientation.Normal;
        ApplyLayoutToAll();
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Applies a theme preset: the background's colors (an image background keeps showing its
    /// image), and the matching text color for every Basic text element (opacity kept) — except a
    /// character name with a custom color, which keeps it (see <see cref="BasicNameColor"/>). Only
    /// copies values — every one stays editable, and nothing references the preset afterwards.
    /// </summary>
    internal void ApplyTheme(ProfileThemePreset preset)
    {
        var previousTheme = AdventurePlateClassicLayout.ResolveTheme(Profile);
        Profile.NormalizeLegacyBackground();
        var background = Profile.Background!;
        var keepImage = background.HasImage;
        preset.ApplyTo(background);
        if (keepImage)
        {
            background.Mode = ProfileBackgroundMode.Image;
        }

        foreach (var element in Profile.Elements)
        {
            if (element is TextProfileElement text && BasicSections.SectionOf(text.Role) is not null)
            {
                if (text.Role == ProfileElementRole.BasicName)
                {
                    BasicNameColor.ApplyThemeChange(text, previousTheme, preset, Profile);
                }
                else
                {
                    text.Color = AdventurePlateClassicLayout.ThemeColorFor(text.Role, preset) with { W = text.Color.W };
                }
            }
        }

        Settings.ThemeId = preset.Id;
    }

    // ---------------------------------------------------------------- internals

    private ProfileElement Create(ProfileElementRole role, Action<ImageProfileElement>? configureImage = null)
    {
        // Judged before the new element exists: a new heading joins the others' shared size.
        var headingSize = BasicSections.IsHeading(role) ? HeadingSize(Profile) : null;

        var element = AdventurePlateClassicLayout.CreateElement(role, Profile);
        if (element is ImageProfileElement image)
        {
            configureImage?.Invoke(image);
        }
        else if (element is TextProfileElement text && headingSize is { } size)
        {
            text.FontSize = size;
        }

        addElement(element);
        if (headingSize is not null)
        {
            // Now that it exists at the shared size, its box is the one that size needs.
            PlaceCore(element);
        }

        Settings.SetPlacement(role, new ElementRect(element.Position, element.Size));
        return element;
    }

    private void Place(ProfileElement element) => PlaceCore(element);

    /// <summary>
    /// Loading: a Favorite Jobs value Basic still manages, placed by an earlier version of the
    /// layout (after a level, or after the old fixed level column), is re-placed to fill its whole
    /// cell — the Favorite Jobs are presented without the retired Level — and its placement
    /// recorded, so it keeps following the layout. The Level element itself is never touched; a
    /// customized (or never-tracked) value is untouched too. Returns whether anything changed.
    /// </summary>
    internal static bool UpgradeFavoriteJobRow(ProfileDocument profile)
    {
        if (profile.BasicPlate is not { } settings || IsSectionCustomized(profile, BasicSection.Job)
            || BasicSections.Find(profile, ProfileElementRole.BasicJob) is not { } job
            || AdventurePlateClassicLayout.GetRect(ProfileElementRole.BasicJob, GetOrientation(profile), profile) is not { } rect
            || rect.Matches(job.Position, job.Size))
        {
            return false;
        }

        job.Position = rect.Position;
        job.Size = rect.Size;
        settings.SetPlacement(ProfileElementRole.BasicJob, rect);
        return true;
    }

    private void PlaceCore(ProfileElement element)
    {
        if (AdventurePlateClassicLayout.GetRect(element.Role, GetOrientation(Profile), Profile) is not { } rect)
        {
            return;
        }

        element.Position = rect.Position;
        element.Size = rect.Size;
        Settings.SetPlacement(element.Role, rect);

        if (element is TextProfileElement text)
        {
            // Placed by Basic, so it uses the text layout the placement assumes (canvas-unit
            // padding, auto fit) — an older element is only ever upgraded here, never on load.
            text.LayoutVersion = TextProfileElement.CurrentLayoutVersion;
        }
    }

    private static string Limit(string? text, int maxLength)
    {
        var value = text ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

/// <summary>
/// Fills a brand-new Adventure Plate Classic so it's useful immediately: the Identity Header and
/// every section in place, typography from the layout, and the character's name, World, job,
/// level, and Free Company where the game reported them. Only ever applied at creation — never to
/// an existing Plate.
/// </summary>
internal static class AdventurePlateStarter
{
    /// <summary>The sections a new Plate starts with, in creation (and so paint) order. No Level: Basic doesn't show one.</summary>
    private static readonly BasicSection[] StarterSections =
    [
        BasicSection.World, BasicSection.FreeCompany, BasicSection.Job,
        BasicSection.ActiveHours, BasicSection.Playstyle, BasicSection.Message,
    ];

    internal static void Populate(ProfileDocument document, BasicCharacterInfo? character)
    {
        void Add(ProfileElement element)
        {
            element.ZIndex = document.Elements.Count == 0 ? 0 : document.Elements.Max(e => e.ZIndex) + 1;
            document.Elements.Add(element);
        }

        document.BasicPlate = new BasicPlateSettings { ThemeId = ProfileThemePresets.All[0].Id };

        // Identity Header: the character name, placed by the default (stacked) layout. Title and
        // tagline are created when first turned on, as in the Basic editor.
        document.BasicIdentity = IdentityHeaderRules.CreateSettings(document);
        Add(IdentityHeaderRules.Create(ProfileElementRole.BasicName, document, character?.Name));
        IdentityHeaderRules.Place(document, static _ => null);

        var editor = new BasicPlateEditor(document, Add, element => document.Elements.Remove(element));
        foreach (var section in StarterSections)
        {
            editor.EnsureSection(section);
        }

        if (character is null)
        {
            return;
        }

        editor.SetText(ProfileElementRole.BasicWorld, BasicPlateText.World(character.HomeWorld, character.DataCenter));
        if (character.JobId > 0 && !string.IsNullOrWhiteSpace(character.JobName))
        {
            // The character's current job as the one Favorite Job (its full name; one name fits).
            editor.SetFavoriteJobs([new FavoriteJob(character.JobId, character.JobName.Trim(), string.Empty)]);
        }

        editor.SetText(ProfileElementRole.BasicFreeCompany, BasicPlateText.FreeCompanyTag(character.FreeCompanyTag));
    }
}
