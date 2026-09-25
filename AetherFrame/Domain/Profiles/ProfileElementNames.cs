using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Layer naming rules shared by every editor. Names are purely editor metadata: nothing here (or
/// <see cref="ProfileElement.Name"/> itself) is ever consulted when rendering a profile.
/// </summary>
public static class ProfileElementNames
{
    /// <summary>
    /// The name to show for an element: its own <see cref="ProfileElement.Name"/> if set, otherwise
    /// an automatic one — the Basic role's label for a reserved element (e.g. "Character Name"),
    /// or the element type ("Text", "Image").
    /// </summary>
    public static string GetDisplayName(ProfileElement element) =>
        string.IsNullOrWhiteSpace(element.Name) ? GetAutomaticName(element) : element.Name;

    /// <summary>The fallback name for an element with no name of its own.</summary>
    public static string GetAutomaticName(ProfileElement element) =>
        GetRoleLabel(element.Role) ?? GetTypeName(element);

    public static string GetTypeName(ProfileElement element) => element switch
    {
        TextProfileElement => "Text",
        ImageProfileElement => "Image",
        _ => "Element",
    };

    /// <summary>User-facing label for a Basic role, or null for <see cref="ProfileElementRole.None"/>.</summary>
    public static string? GetRoleLabel(ProfileElementRole role) => role switch
    {
        ProfileElementRole.BasicPortrait => "Portrait",
        ProfileElementRole.BasicName => "Character Name",
        ProfileElementRole.BasicTitle => "Title",
        ProfileElementRole.BasicMessage => "Message",
        ProfileElementRole.BasicTagline => "Tagline",
        ProfileElementRole.BasicWorld => "Home World",
        ProfileElementRole.BasicWorldHeading => "Home World Heading",
        ProfileElementRole.BasicJob => "Favorite Job",
        ProfileElementRole.BasicJobHeading => "Favorite Job Heading",
        ProfileElementRole.BasicLevel => "Level (retired, not shown)",
        ProfileElementRole.BasicFreeCompany => "Free Company",
        ProfileElementRole.BasicFreeCompanyHeading => "Free Company Heading",
        ProfileElementRole.BasicPlaystyle => "Playstyle",
        ProfileElementRole.BasicPlaystyleHeading => "Playstyle Heading",
        ProfileElementRole.BasicActiveHours => "Active Hours",
        ProfileElementRole.BasicActiveHoursHeading => "Active Hours Heading",
        ProfileElementRole.BasicMessageHeading => "Message Heading",
        _ => null,
    };

    /// <summary>
    /// The next sequential automatic name for a new element of <paramref name="element"/>'s type
    /// ("Text 1", "Text 2", ...): one past the highest number already used by an existing
    /// "Type N" name, so numbers are never reused while a later one still exists.
    /// </summary>
    public static string NextSequentialName(IEnumerable<ProfileElement> existing, ProfileElement element)
    {
        var typeName = GetTypeName(element);
        var prefix = typeName + " ";
        var highest = 0;

        foreach (var other in existing)
        {
            var name = other.Name;
            if (name.Length > prefix.Length
                && name.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(prefix.Length), out var number)
                && number > highest)
            {
                highest = number;
            }
        }

        return prefix + (highest + 1);
    }

    /// <summary>
    /// A name for a duplicate of <paramref name="source"/>: "Logo Copy", then "Logo Copy 2",
    /// "Logo Copy 3"... skipping any already in use. Duplicating a copy doesn't stack suffixes
    /// ("Logo Copy" duplicates to "Logo Copy 2", not "Logo Copy Copy").
    /// </summary>
    public static string MakeCopyName(IEnumerable<ProfileElement> existing, ProfileElement source)
    {
        var baseName = GetDisplayName(source).Trim();
        var copyIndex = baseName.LastIndexOf(" Copy", StringComparison.Ordinal);
        if (copyIndex > 0)
        {
            var suffix = baseName.AsSpan(copyIndex + " Copy".Length).Trim();
            if (suffix.IsEmpty || int.TryParse(suffix, out _))
            {
                baseName = baseName[..copyIndex];
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in existing)
        {
            used.Add(GetDisplayName(other));
        }

        var candidate = baseName + " Copy";
        for (var n = 2; used.Contains(candidate); n++)
        {
            candidate = $"{baseName} Copy {n}";
        }

        return Truncate(candidate);
    }

    /// <summary>Normalizes user input for <see cref="ProfileElement.Name"/>: trimmed, single line,
    /// length-capped. Empty means "automatic".</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return Truncate(name.Replace('\r', ' ').Replace('\n', ' ').Trim());
    }

    private static string Truncate(string name) =>
        name.Length <= ProfileElement.MaxNameLength ? name : name[..ProfileElement.MaxNameLength];
}
