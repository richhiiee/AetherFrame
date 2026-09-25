using System;
using System.Collections.Generic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>
/// What the Basic editor's Theme browser shows: one collection of every theme, narrowed by a search
/// and an optional family filter (from the catalog's own <see cref="ThemeFamily"/> metadata — the
/// families that actually have themes, in <see cref="ProfileThemePresets.FamilyOrder"/>). Pure: it
/// only picks and orders themes; applying one is unchanged (by its stable <see cref="ProfileThemePreset.Id"/>),
/// and nothing here reads or writes a Plate beyond telling which theme it uses.
/// </summary>
internal static class ThemeBrowser
{
    /// <summary>
    /// The themes matching <paramref name="search"/> and <paramref name="family"/>, in catalog order.
    /// The search is case-insensitive and ignores surrounding spaces; every word of it must appear in
    /// a theme's name, description, or family. An empty search and no family (All) give the whole catalog.
    /// </summary>
    internal static List<ProfileThemePreset> Filter(IEnumerable<ProfileThemePreset> themes, string? search, ThemeFamily? family)
    {
        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<ProfileThemePreset>();
        foreach (var theme in themes)
        {
            if (family is { } wanted && theme.Family != wanted)
            {
                continue;
            }

            if (Array.TrueForAll(words, word => Matches(theme, word)))
            {
                matches.Add(theme);
            }
        }

        return matches;
    }

    /// <summary>
    /// <see cref="Filter"/>'s results grouped by family: the families in the catalog's family order
    /// (<see cref="ProfileThemePresets.FamilyOrder"/>, then any family it doesn't list), each with its
    /// matching themes in catalog order. A family with no match is left out, so a search keeps its
    /// results under their families instead of mixing them together.
    /// </summary>
    internal static List<(ThemeFamily Family, List<ProfileThemePreset> Themes)> Group(IEnumerable<ProfileThemePreset> themes, string? search, ThemeFamily? family)
    {
        var matches = Filter(themes, search, family);
        var order = new List<ThemeFamily>(ProfileThemePresets.FamilyOrder);
        foreach (var theme in matches)
        {
            if (!order.Contains(theme.Family))
            {
                order.Add(theme.Family);
            }
        }

        var groups = new List<(ThemeFamily, List<ProfileThemePreset>)>();
        foreach (var candidate in order)
        {
            var members = matches.FindAll(theme => theme.Family == candidate);
            if (members.Count > 0)
            {
                groups.Add((candidate, members));
            }
        }

        return groups;
    }

    /// <summary>The families that have at least one theme, in the catalog's family order: the only filters worth offering.</summary>
    internal static List<(ThemeFamily Family, int Count)> Families(IReadOnlyCollection<ProfileThemePreset> themes)
    {
        var families = new List<(ThemeFamily, int)>();
        foreach (var family in ProfileThemePresets.FamilyOrder)
        {
            var count = 0;
            foreach (var theme in themes)
            {
                if (theme.Family == family)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                families.Add((family, count));
            }
        }

        return families;
    }

    /// <summary>The theme the Plate uses (its stored Basic theme id), or null — whatever the browser is showing.</summary>
    internal static ProfileThemePreset? Current(ProfileDocument profile) => ProfileThemePresets.Find(profile.BasicPlate?.ThemeId);

    /// <summary>How many theme cards fit side by side in <paramref name="width"/> (always at least one).</summary>
    internal static int Columns(float width, float cardWidth, float spacing) =>
        Math.Max(1, (int)((width + spacing) / Math.Max(1f, cardWidth + spacing)));

    private static bool Matches(ProfileThemePreset theme, string word) =>
        theme.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || theme.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
        || theme.Family.ToString().Contains(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The Theme browser's view state (search text and family filter): editor-only, never part of a Plate.</summary>
internal sealed class ThemeBrowserState
{
    internal const int MaxSearchLength = 64;

    internal string Search { get; set; } = string.Empty;

    /// <summary>The family filter; null is All (the default).</summary>
    internal ThemeFamily? Family { get; set; }

    /// <summary>The Plate the browser last showed, and whether its current theme still needs scrolling into view.</summary>
    internal Guid PlateId { get; set; }

    internal bool ScrollToCurrent { get; set; }

    internal bool IsFiltered => Family is not null || Search.Trim().Length > 0;

    internal void Clear()
    {
        Search = string.Empty;
        Family = null;
    }
}
