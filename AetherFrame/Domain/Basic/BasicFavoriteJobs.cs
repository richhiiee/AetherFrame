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
/// shows. Full names come first ("Astrologian, White Mage"); only when they don't fit the value's
/// box — measured, never by counting jobs — does it switch to the game's abbreviations
/// ("AST · WHM · RDM · DNC"), in the same order. If even those don't fit, the value's auto fit
/// shrinks the text (the safe fitting every Basic value has); no job is ever dropped to make room.
/// Pure: no game data, no fonts (the caller measures).
/// </summary>
public static class BasicFavoriteJobs
{
    /// <summary>How many Favorite Jobs a Plate can list.</summary>
    public const int MaxJobs = 8;

    public const string SingularHeading = "FAVORITE JOB";
    public const string PluralHeading = "FAVORITE JOBS";

    private const string NameSeparator = ", ";
    private const string AbbreviationSeparator = " · ";

    // Only when no font is ready to measure with: a generous average advance per character, so an
    // estimate errs toward abbreviating rather than toward overflowing.
    private const float EstimatedAdvance = 0.6f;

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
    /// The text to show in a box <paramref name="availableWidth"/> wide: the full names when their
    /// measured width fits, otherwise the abbreviations (whose fit, if they still don't, is left to
    /// the value's auto fit). <paramref name="measure"/> gives a candidate's rendered width.
    /// </summary>
    public static string Choose(IReadOnlyList<FavoriteJob> jobs, Func<string, float> measure, float availableWidth)
    {
        if (jobs.Count == 0)
        {
            return string.Empty;
        }

        var full = FullText(jobs);
        return measure(full) <= availableWidth + 0.01f ? full : AbbreviatedText(jobs);
    }

    /// <summary>A conservative width estimate for <paramref name="text"/> in <paramref name="element"/>'s style, for when no font can measure it.</summary>
    public static float EstimateWidth(TextProfileElement element, string text) =>
        text.Length == 0 ? 0f : (text.Length * element.FontSize * EstimatedAdvance) + (Math.Max(0f, element.LetterSpacing) * (text.Length - 1));
}
