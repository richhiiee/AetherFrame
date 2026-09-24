using System;
using System.Collections.Generic;
using System.Text;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// What the game currently reports about the logged-in character, for filling Basic sections.
/// A snapshot for explicit "use current" actions and new-Plate defaults only: it is never
/// persisted as-is and never overrides content already saved in a Plate. Any part may be
/// unknown (null / 0).
/// </summary>
/// <param name="Name">Character name.</param>
/// <param name="HomeWorld">Home World name.</param>
/// <param name="DataCenter">The Home World's Data Center name.</param>
/// <param name="JobId">Current class/job row id (0 when unknown).</param>
/// <param name="JobName">Current class/job display name.</param>
/// <param name="Level">Current class/job level (0 when unknown).</param>
/// <param name="FreeCompanyTag">The Free Company tag; "" when not in one; null when unknown.</param>
public sealed record BasicCharacterInfo(
    string? Name, string? HomeWorld, string? DataCenter, uint JobId, string? JobName, int Level, string? FreeCompanyTag);

/// <summary>
/// The text each structured Basic section displays. Pure formatting, shared by the editor, new-Plate
/// defaults, and tests. Uses only characters the curated fonts carry (Latin-1), so nothing renders
/// as a missing glyph.
/// </summary>
public static class BasicPlateText
{
    public const int MinLevel = 1;
    public const int MaxLevel = 100;

    /// <summary>Separator between playstyle entries and between parts of a line (U+00B7, in every curated font).</summary>
    public const string Separator = "  ·  ";

    /// <summary>Curated playstyle suggestions; any custom entry is allowed too.</summary>
    public static readonly string[] SuggestedPlaystyles =
    [
        "Casual", "Hardcore", "Roleplay", "Raiding", "Leveling", "Hunts", "Treasure Hunts", "PvP",
        "Crafting", "Gathering", "Glamour", "Housing", "Screenshots", "Gold Saucer", "Social", "Mentor",
        "Exploring", "New Player Friendly",
    ];

    /// <summary>"Phoenix [Light]", "Phoenix", or "" when the World is unknown.</summary>
    public static string World(string? homeWorld, string? dataCenter)
    {
        var world = homeWorld?.Trim() ?? string.Empty;
        var center = dataCenter?.Trim() ?? string.Empty;
        if (world.Length == 0)
        {
            return string.Empty;
        }

        return center.Length == 0 ? world : $"{world} [{center}]";
    }

    /// <summary>"Lv. 90", or "" for no level.</summary>
    public static string Level(int level) => level <= 0 ? string.Empty : $"Lv. {Math.Min(level, MaxLevel)}";

    /// <summary>A Free Company tag as the game shows it ("«TAG»"), or "" for none.</summary>
    public static string FreeCompanyTag(string? tag)
    {
        var value = tag?.Trim() ?? string.Empty;
        return value.Length == 0 ? string.Empty : $"«{value}»";
    }

    /// <summary>Sanitizes one playstyle entry: single line, trimmed, length-capped. "" means invalid.</summary>
    public static string NormalizePlaystyle(string? entry)
    {
        var value = (entry ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= BasicPlateSettings.MaxPlaystyleLength ? value : value[..BasicPlateSettings.MaxPlaystyleLength].TrimEnd();
    }

    /// <summary>The playstyle entries as one line: "Casual  ·  Raiding  ·  Glamour".</summary>
    public static string Playstyles(IReadOnlyList<string> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(Separator);
            }

            builder.Append(entry);
        }

        return builder.ToString();
    }

    /// <summary>"Weekends  ·  8 PM - 1 AM EST", or "" when nothing is set.</summary>
    public static string ActiveHours(BasicActiveHours? hours)
    {
        if (hours is null)
        {
            return string.Empty;
        }

        var days = Days(hours.Days);
        var time = TimeRange(hours);
        if (days.Length == 0)
        {
            return time;
        }

        return time.Length == 0 ? days : days + Separator + time;
    }

    /// <summary>"Every day", "Weekdays", "Weekends", or runs such as "Mon-Wed, Sat".</summary>
    public static string Days(BasicWeekdays days)
    {
        days &= BasicWeekdays.Everyday;
        switch (days)
        {
            case BasicWeekdays.None:
                return string.Empty;
            case BasicWeekdays.Everyday:
                return "Every day";
            case BasicWeekdays.Weekdays:
                return "Weekdays";
            case BasicWeekdays.Weekends:
                return "Weekends";
        }

        ReadOnlySpan<string> names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        var parts = new List<string>();
        var i = 0;
        while (i < 7)
        {
            if (!Has(days, i))
            {
                i++;
                continue;
            }

            var start = i;
            while (i + 1 < 7 && Has(days, i + 1))
            {
                i++;
            }

            parts.Add(i - start >= 2 ? $"{names[start]}-{names[i]}" : i > start ? $"{names[start]}, {names[i]}" : names[start]);
            i++;
        }

        return string.Join(", ", parts);

        static bool Has(BasicWeekdays set, int index) => ((int)set & (1 << index)) != 0;
    }

    /// <summary>"8 PM - 1 AM EST", "20:00 - 01:00", "All day", with the time zone label appended.</summary>
    public static string TimeRange(BasicActiveHours hours)
    {
        var start = BasicActiveHours.NormalizeMinutes(hours.StartMinutes);
        var end = BasicActiveHours.NormalizeMinutes(hours.EndMinutes);
        var range = start == end ? "All day" : $"{Time(start, hours.Use24HourClock)} - {Time(end, hours.Use24HourClock)}";

        var zone = hours.TimeZone.Trim();
        return zone.Length == 0 ? range : $"{range} {zone}";
    }

    /// <summary>A time of day: "8 PM", "8:30 PM", or "20:30".</summary>
    public static string Time(int minutes, bool use24HourClock)
    {
        minutes = BasicActiveHours.NormalizeMinutes(minutes);
        var hour = minutes / 60;
        var minute = minutes % 60;
        if (use24HourClock)
        {
            return $"{hour:00}:{minute:00}";
        }

        var suffix = hour < 12 ? "AM" : "PM";
        var hour12 = hour % 12 == 0 ? 12 : hour % 12;
        return minute == 0 ? $"{hour12} {suffix}" : $"{hour12}:{minute:00} {suffix}";
    }
}
