using System;
using System.Collections.Generic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>One Favorite Job as Basic shows it: its game data row id, full name, and standard abbreviation ("AST").</summary>
/// <param name="Id">The <c>ClassJob</c> row id (what a Plate stores).</param>
/// <param name="Name">The full name ("Astrologian").</param>
/// <param name="Abbreviation">The game's own abbreviation ("AST"), or empty when unknown.</param>
public sealed record FavoriteJob(uint Id, string Name, string Abbreviation);

/// <summary>
/// The Favorite Jobs rules: an ordered list of game job ids (the first is the primary favorite),
/// its heading (FAVORITE JOB for one, FAVORITE JOBS for more), and the one line of text the Plate
/// shows. The value stores the full names ("Astrologian, White Mage"); what it shows is derived
/// each time it's drawn (<see cref="DisplayText"/>): the full names while they fit the value's box
/// at its current font, size and width — measured, never by counting jobs — else the game's
/// abbreviations ("AST · WHM · RDM · DNC") in the same order; if even those don't fit, the value's
/// auto fit shrinks the text (the safe fitting every Basic value has). No job is ever dropped, and
/// the stored ids and their order never change for display. Pure: no game data, no fonts (the caller
/// provides both).
/// </summary>
public static class BasicFavoriteJobs
{
    /// <summary>How many Favorite Jobs a Plate can list.</summary>
    public const int MaxJobs = 8;

    public const string SingularHeading = "FAVORITE JOB";
    public const string PluralHeading = "FAVORITE JOBS";

    private const string NameSeparator = ", ";
    private const string AbbreviationSeparator = " · ";

    /// <summary>
    /// The Plate's Favorite Jobs, in order. A Plate saved before multiple Favorite Jobs has only its
    /// single <see cref="BasicPlateSettings.FavoriteJobId"/>: read as a one-job list, losslessly,
    /// without rewriting anything.
    /// </summary>
    public static List<uint> IdsOf(ProfileDocument profile)
    {
        if (profile.BasicPlate is not { } settings)
        {
            return new List<uint>();
        }

        if (settings.FavoriteJobIds.Count > 0)
        {
            return new List<uint>(settings.FavoriteJobIds);
        }

        return settings.FavoriteJobId > 0 ? new List<uint> { settings.FavoriteJobId } : new List<uint>();
    }

    /// <summary>The list with duplicates and empty ids removed (first occurrence kept) and at most <see cref="MaxJobs"/> entries.</summary>
    public static List<FavoriteJob> Normalize(IEnumerable<FavoriteJob> jobs)
    {
        var result = new List<FavoriteJob>();
        foreach (var job in jobs)
        {
            if (job.Id == 0 || result.Exists(j => j.Id == job.Id))
            {
                continue;
            }

            if (result.Count == MaxJobs)
            {
                break;
            }

            result.Add(job);
        }

        return result;
    }

    /// <summary>The section heading for <paramref name="count"/> jobs: FAVORITE JOBS for two or more, else FAVORITE JOB.</summary>
    public static string Heading(int count) => count >= 2 ? PluralHeading : SingularHeading;

    /// <summary>Whether a heading still shows one of Basic's own captions (so Basic may keep it in step with the count).</summary>
    public static bool IsDefaultHeading(string? text) => text is SingularHeading or PluralHeading;

    /// <summary>The full names, in order: "Astrologian, White Mage".</summary>
    public static string FullText(IReadOnlyList<FavoriteJob> jobs)
    {
        var names = new string[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            names[i] = jobs[i].Name.Trim();
        }

        return string.Join(NameSeparator, names);
    }

    /// <summary>The standard abbreviations, in order: "AST · WHM". A job without one keeps its name.</summary>
    public static string AbbreviatedText(IReadOnlyList<FavoriteJob> jobs)
    {
        var parts = new string[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            parts[i] = jobs[i].Abbreviation.Trim() is { Length: > 0 } abbreviation ? abbreviation : jobs[i].Name.Trim();
        }

        return string.Join(AbbreviationSeparator, parts);
    }

    /// <summary>
    /// What the Favorite Jobs value shows right now, in place of its stored text — or null to show
    /// the stored text as it is. Evaluated from the element's current state every time the Plate is
    /// drawn, so a change to anything that matters (the jobs or their order, font family, size, style,
    /// letter spacing, symbols, the box's width after a layout change or another editor's edit) is
    /// reflected immediately. Null when the value isn't Basic's full-names text for these jobs (its
    /// text was written in the Advanced editor, or in another language), when there are no jobs, when
    /// the full names fit, or when no font can measure yet (the stored full names then show, with
    /// auto fit). Otherwise the abbreviations, with the value's own prefix and suffix.
    /// </summary>
    /// <param name="profile">The Plate.</param>
    /// <param name="element">Its Favorite Jobs value element.</param>
    /// <param name="findJob">Game data: a job's name and abbreviation by row id.</param>
    /// <param name="measure">The rendered width of a display string in <paramref name="element"/>'s current style, or null while its font isn't ready.</param>
    public static string? DisplayText(ProfileDocument profile, TextProfileElement element, Func<uint, FavoriteJob?> findJob, Func<string, float?> measure)
    {
        if (element.Role != ProfileElementRole.BasicJob)
        {
            return null;
        }

        var ids = IdsOf(profile);
        if (ids.Count == 0)
        {
            return null;
        }

        var jobs = new List<FavoriteJob>(ids.Count);
        foreach (var id in ids)
        {
            if (findJob(id) is not { } job)
            {
                return null;
            }

            jobs.Add(job);
        }

        if (element.Text != FullText(jobs) || measure(element.GetDisplayText()) is not { } width)
        {
            return null;
        }

        var available = Math.Max(0f, element.Size.X - (2f * TextProfileElement.LayoutPadding));
        return width <= available + 0.01f ? null : string.Concat(element.Prefix, AbbreviatedText(jobs), element.Suffix);
    }
}
