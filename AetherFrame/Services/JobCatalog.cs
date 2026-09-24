using System;
using System.Collections.Generic;
using System.Globalization;
using Lumina.Excel.Sheets;

namespace AetherFrame.Services;

/// <summary>One choosable FFXIV job (or crafting/gathering class) from game data.</summary>
/// <param name="Id">The <c>ClassJob</c> sheet row id.</param>
/// <param name="Name">Display name, in the client's language.</param>
/// <param name="Abbreviation">Short name ("PLD").</param>
internal sealed record GameJob(uint Id, string Name, string Abbreviation);

/// <summary>
/// The Favorite Job choices, read from the live game data (the Lumina <c>ClassJob</c> sheet) rather
/// than a hardcoded list: every levelable class/job, except base classes that have a job (a
/// Gladiator is listed as its Paladin). Levels come from Dalamud's <c>IPlayerState</c> (managed, no
/// unsafe code). Loaded lazily; must be used from the main (framework/draw) thread.
/// </summary>
internal sealed class JobCatalog
{
    private IReadOnlyList<GameJob>? jobs;
    private Dictionary<uint, GameJob>? jobsById;

    internal IReadOnlyList<GameJob> Jobs
    {
        get
        {
            EnsureLoaded();
            return jobs!;
        }
    }

    internal GameJob? Find(uint jobId)
    {
        EnsureLoaded();
        return jobsById!.TryGetValue(jobId, out var job) ? job : null;
    }

    /// <summary>The logged-in character's level in that job, or null when unknown (none logged in, or not unlocked).</summary>
    internal int? GetCharacterLevel(uint jobId)
    {
        try
        {
            if (!DalamudServices.PlayerState.IsLoaded
                || !DalamudServices.DataManager.GetExcelSheet<ClassJob>().TryGetRow(jobId, out var row))
            {
                return null;
            }

            var level = DalamudServices.PlayerState.GetClassJobLevel(row);
            return level > 0 ? level : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Game data job names are lower case in some languages ("white mage"); shown title-cased.</summary>
    internal static string FormatName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 && trimmed == trimmed.ToLowerInvariant() && trimmed != trimmed.ToUpperInvariant()
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed)
            : trimmed;
    }

    private void EnsureLoaded()
    {
        if (jobs is not null)
        {
            return;
        }

        var list = new List<(GameJob Job, byte Priority)>();
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<ClassJob>();

            // Base classes that some job advances from (Gladiator -> Paladin) are listed as that job.
            var hasJob = new HashSet<uint>();
            foreach (var row in sheet)
            {
                if (row.JobIndex > 0 && row.ClassJobParent.RowId != row.RowId)
                {
                    hasJob.Add(row.ClassJobParent.RowId);
                }
            }

            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (row.RowId == 0 || row.ExpArrayIndex < 0 || hasJob.Contains(row.RowId) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add((new GameJob(row.RowId, FormatName(name), row.Abbreviation.ExtractText()), row.UIPriority));
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AetherFrame could not read the FFXIV job list from game data.");
        }

        list.Sort(static (a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Job.Id.CompareTo(b.Job.Id));

        var ordered = new List<GameJob>(list.Count);
        jobsById = new Dictionary<uint, GameJob>(list.Count);
        foreach (var (job, _) in list)
        {
            ordered.Add(job);
            jobsById[job.Id] = job;
        }

        jobs = ordered;
    }
}
