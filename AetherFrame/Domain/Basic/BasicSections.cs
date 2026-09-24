using System;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>The semantic sections of an Adventure Plate, as the Basic editor presents them.</summary>
public enum BasicSection
{
    Portrait,
    Identity,
    World,
    Job,
    Level,
    FreeCompany,
    Playstyle,
    ActiveHours,
    Message,
}

/// <summary>
/// One section: an optional heading (the small caption above it) and the value element(s) holding
/// its content. Every element is an ordinary role-tagged element in the document.
/// </summary>
/// <param name="Section">The section.</param>
/// <param name="Title">Its name in the Basic editor.</param>
/// <param name="Heading">Its heading's role, or null for a section without one.</param>
/// <param name="HeadingText">The heading's default text.</param>
/// <param name="Values">Its value roles (the portrait image, the identity texts, or one text).</param>
public sealed record BasicSectionDefinition(
    BasicSection Section, string Title, ProfileElementRole? Heading, string HeadingText, ProfileElementRole[] Values);

/// <summary>
/// How semantic roles group into sections, and the section-level queries both editors and the
/// renderer share. Pure lookups over the document: nothing here ever changes it.
/// </summary>
public static class BasicSections
{
    public static readonly BasicSectionDefinition[] All =
    [
        new(BasicSection.Portrait, "Portrait", null, string.Empty, [ProfileElementRole.BasicPortrait]),
        new(BasicSection.Identity, "Identity", null, string.Empty,
            [ProfileElementRole.BasicName, ProfileElementRole.BasicTitle]),
        new(BasicSection.World, "Home World", ProfileElementRole.BasicWorldHeading, "HOME WORLD", [ProfileElementRole.BasicWorld]),
        new(BasicSection.Job, "Favorite Job", ProfileElementRole.BasicJobHeading, "FAVORITE JOB", [ProfileElementRole.BasicJob]),
        new(BasicSection.Level, "Level", null, string.Empty, [ProfileElementRole.BasicLevel]),
        new(BasicSection.FreeCompany, "Free Company", ProfileElementRole.BasicFreeCompanyHeading, "FREE COMPANY", [ProfileElementRole.BasicFreeCompany]),
        new(BasicSection.Playstyle, "Playstyle", ProfileElementRole.BasicPlaystyleHeading, "PLAYSTYLE", [ProfileElementRole.BasicPlaystyle]),
        new(BasicSection.ActiveHours, "Active Hours", ProfileElementRole.BasicActiveHoursHeading, "ACTIVE HOURS", [ProfileElementRole.BasicActiveHours]),
        new(BasicSection.Message, "Message", ProfileElementRole.BasicMessageHeading, "MESSAGE", [ProfileElementRole.BasicMessage]),
    ];

    /// <summary>The sections whose placement Basic tracks per element (all but the Identity Header,
    /// which is placed as one header by <see cref="IdentityHeaderLayout"/>).</summary>
    public static readonly BasicSection[] ElementSections =
    [
        BasicSection.Portrait, BasicSection.World, BasicSection.Job, BasicSection.Level,
        BasicSection.FreeCompany, BasicSection.Playstyle, BasicSection.ActiveHours, BasicSection.Message,
    ];

    /// <summary>
    /// The units Basic lays out atomically. Every element of a group — each section's heading and
    /// values, and for Favorite Job and Level both sections — follows the layout together or is
    /// customized together: if any one of them has been moved or resized elsewhere, the whole group
    /// counts as customized and nothing in it moves automatically (an orientation change never
    /// splits a group). Only explicit actions (Apply Layout, Reset Section, Reset Basic Layout)
    /// reclaim a group, and they always place all of it. The sections stay separate semantically
    /// (own content, own visibility); only placement ownership is shared.
    /// </summary>
    public static readonly BasicSection[][] LayoutGroups =
    [
        [BasicSection.Portrait],
        [BasicSection.Identity],
        [BasicSection.World],
        [BasicSection.Job, BasicSection.Level],
        [BasicSection.FreeCompany],
        [BasicSection.Playstyle],
        [BasicSection.ActiveHours],
        [BasicSection.Message],
    ];

    /// <summary>The layout group containing <paramref name="section"/> (its first entry is the group's primary section).</summary>
    public static BasicSection[] LayoutGroupOf(BasicSection section)
    {
        foreach (var group in LayoutGroups)
        {
            if (Array.IndexOf(group, section) >= 0)
            {
                return group;
            }
        }

        return [section];
    }

    public static BasicSectionDefinition Get(BasicSection section) => All[(int)section];

    /// <summary>The section a role belongs to, or null for <see cref="ProfileElementRole.None"/> (and unknown roles).</summary>
    public static BasicSection? SectionOf(ProfileElementRole role)
    {
        foreach (var definition in All)
        {
            if (definition.Heading == role || Array.IndexOf(definition.Values, role) >= 0)
            {
                return definition.Section;
            }
        }

        return null;
    }

    /// <summary>True for a section heading role.</summary>
    public static bool IsHeading(ProfileElementRole role)
    {
        foreach (var definition in All)
        {
            if (definition.Heading == role)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The heading's default text for a heading role, or null for any other role.</summary>
    public static string? DefaultHeadingText(ProfileElementRole role)
    {
        foreach (var definition in All)
        {
            if (definition.Heading == role)
            {
                return definition.HeadingText;
            }
        }

        return null;
    }

    /// <summary>
    /// The element holding <paramref name="role"/>, or null. If a document somehow holds several
    /// (e.g. edited by hand), the first is the one Basic binds to — consistently everywhere.
    /// </summary>
    public static ProfileElement? Find(ProfileDocument profile, ProfileElementRole role)
    {
        if (role == ProfileElementRole.None)
        {
            return null;
        }

        foreach (var element in profile.Elements)
        {
            if (element.Role == role)
            {
                return element;
            }
        }

        return null;
    }

    public static TextProfileElement? FindText(ProfileDocument profile, ProfileElementRole role) => Find(profile, role) as TextProfileElement;

    /// <summary>True when any element of the section exists.</summary>
    public static bool Exists(ProfileDocument profile, BasicSection section)
    {
        var definition = Get(section);
        if (definition.Heading is { } heading && Find(profile, heading) is not null)
        {
            return true;
        }

        foreach (var role in definition.Values)
        {
            if (Find(profile, role) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the section is shown: a value element exists and at least one is visible.
    /// (For the Identity Header, the character name's own visibility.)</summary>
    public static bool IsVisible(ProfileDocument profile, BasicSection section)
    {
        if (section == BasicSection.Identity)
        {
            return Find(profile, ProfileElementRole.BasicName) is { Visible: true };
        }

        foreach (var role in Get(section).Values)
        {
            if (Find(profile, role) is { Visible: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the section would draw something in a finished Plate: a visible value
    /// element with content (text, or an image).</summary>
    public static bool HasVisibleContent(ProfileDocument profile, BasicSection section)
    {
        foreach (var role in Get(section).Values)
        {
            switch (Find(profile, role))
            {
                case TextProfileElement { Visible: true } text when text.Text.Length > 0:
                case ImageProfileElement { Visible: true }:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finished rendering (Plate Viewer, Clean Preview, the Basic preview) skips a section heading
    /// whose section has nothing to show, so an empty or hidden section never leaves a stray caption
    /// behind. Every other element — including all non-Basic elements — is unaffected. Editor
    /// canvases draw headings regardless, so they stay findable and selectable.
    /// </summary>
    public static bool IsDrawnInFinishedRendering(ProfileDocument profile, ProfileElement element)
    {
        if (element.Role == ProfileElementRole.None || !IsHeading(element.Role) || SectionOf(element.Role) is not { } section)
        {
            return true;
        }

        return HasVisibleContent(profile, section);
    }
}
