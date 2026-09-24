using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Basic mode's Adventure Plate settings: what the section elements themselves can't express.
/// Every section's content still lives in ordinary role-tagged elements (see
/// <see cref="ProfileElementRole"/>), fully editable in the Advanced editor; this only holds the
/// structured data behind some of them (playstyles, active hours, the chosen job) and Basic's own
/// layout bookkeeping.
///
/// Null on <see cref="ProfileDocument.BasicPlate"/> (every profile before this existed) means
/// "not configured": Basic mode binds to whatever section elements already exist, exactly where
/// they are, and writes nothing until the user makes an explicit Basic edit.
/// </summary>
public sealed class BasicPlateSettings
{
    public const int MaxPlaystyles = 6;
    public const int MaxPlaystyleLength = 24;

    public AdventurePlateOrientation Orientation { get; set; } = AdventurePlateOrientation.Normal;

    /// <summary>Where the portrait comes from. Only <see cref="BasicPortraitSource.ImportedImage"/> works today.</summary>
    public BasicPortraitSource PortraitSource { get; set; } = BasicPortraitSource.ImportedImage;

    /// <summary>
    /// Where Basic mode last placed each section element (not the Identity Header, which keeps its
    /// own record in <see cref="BasicIdentityHeader.AppliedLayout"/>). An element still sitting
    /// exactly there follows the Basic layout; once it has been moved or resized elsewhere (the
    /// Advanced editor) it counts as customized, and Basic never moves it again on its own. A list
    /// keyed by the numeric role rather than a dictionary, so a role this build doesn't know can
    /// never make the document fail to load.
    /// </summary>
    public List<BasicPlacement> Placements { get; set; } = new();

    /// <summary>Up to <see cref="MaxPlaystyles"/> entries, in display order.</summary>
    public List<string> Playstyles { get; set; } = new();

    /// <summary>Structured Active Hours, or null when never set.</summary>
    public BasicActiveHours? ActiveHours { get; set; }

    /// <summary>Row id of the Favorite Job (game data), or 0 when none was chosen from the list.</summary>
    public uint FavoriteJobId { get; set; }

    /// <summary>The shown level, or 0 for none.</summary>
    public int Level { get; set; }

    /// <summary>
    /// Stable <see cref="ProfileThemePreset.Id"/> of the theme last applied in Basic mode ("" for
    /// none). Only a soft default for Reset Section's colors: nothing references the preset, and
    /// every color stays editable. The JSON property name (<c>ThemeName</c>) predates the Id/Name
    /// split and is kept as-is so no existing Plate needs rewriting; every value already stored
    /// there is a theme's Id (Id was defined equal to Name for every theme that shipped before the
    /// split existed).
    /// </summary>
    [JsonPropertyName("ThemeName")]
    public string ThemeId { get; set; } = string.Empty;

    /// <summary>Properties this build doesn't know, kept through clone and save unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public ElementRect? GetPlacement(ProfileElementRole role)
    {
        foreach (var placement in Placements)
        {
            if (placement.Role == role)
            {
                return placement.Rect;
            }
        }

        return null;
    }

    public void SetPlacement(ProfileElementRole role, ElementRect rect)
    {
        foreach (var placement in Placements)
        {
            if (placement.Role == role)
            {
                placement.Rect = rect;
                return;
            }
        }

        Placements.Add(new BasicPlacement { Role = role, Rect = rect });
    }

    public void RemovePlacement(ProfileElementRole role) => Placements.RemoveAll(p => p.Role == role);

    public BasicPlateSettings Clone()
    {
        var clone = new BasicPlateSettings
        {
            Orientation = Orientation,
            PortraitSource = PortraitSource,
            Playstyles = new List<string>(Playstyles),
            ActiveHours = ActiveHours?.Clone(),
            FavoriteJobId = FavoriteJobId,
            Level = Level,
            ThemeId = ThemeId,
            ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
        };

        foreach (var placement in Placements)
        {
            clone.Placements.Add(placement.Clone());
        }

        return clone;
    }

    public bool ContentEquals(BasicPlateSettings? other)
    {
        if (other is null
            || Orientation != other.Orientation
            || PortraitSource != other.PortraitSource
            || FavoriteJobId != other.FavoriteJobId
            || Level != other.Level
            || ThemeId != other.ThemeId
            || (ActiveHours is null ? other.ActiveHours is not null : !ActiveHours.ContentEquals(other.ActiveHours))
            || Placements.Count != other.Placements.Count
            || Playstyles.Count != other.Playstyles.Count)
        {
            return false;
        }

        for (var i = 0; i < Playstyles.Count; i++)
        {
            if (Playstyles[i] != other.Playstyles[i])
            {
                return false;
            }
        }

        for (var i = 0; i < Placements.Count; i++)
        {
            if (Placements[i].Role != other.Placements[i].Role || Placements[i].Rect != other.Placements[i].Rect)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Where Basic mode last placed one section element.</summary>
public sealed class BasicPlacement
{
    public ProfileElementRole Role { get; set; }

    public ElementRect Rect { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public BasicPlacement Clone() => new() { Role = Role, Rect = Rect, ExtensionData = ProfileElement.CopyExtensionData(ExtensionData) };
}

/// <summary>
/// When the character is usually around, as a Plate shows it: which days, a start and end time of
/// day, and an optional time zone label. Purely descriptive local data — never connected to online
/// status, scheduling, or location.
/// </summary>
public sealed class BasicActiveHours
{
    public const int MinutesPerDay = 24 * 60;
    public const int MaxTimeZoneLength = 16;

    public BasicWeekdays Days { get; set; } = BasicWeekdays.None;

    /// <summary>Minutes after midnight, [0, 1440).</summary>
    public int StartMinutes { get; set; } = 20 * 60;

    /// <summary>Minutes after midnight, [0, 1440). Earlier than the start means "past midnight";
    /// equal to the start means "all day".</summary>
    public int EndMinutes { get; set; } = 23 * 60;

    public bool Use24HourClock { get; set; }

    /// <summary>Free text such as "EST" or "Server Time", up to <see cref="MaxTimeZoneLength"/>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public BasicActiveHours Clone() => new()
    {
        Days = Days,
        StartMinutes = StartMinutes,
        EndMinutes = EndMinutes,
        Use24HourClock = Use24HourClock,
        TimeZone = TimeZone,
        ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
    };

    public bool ContentEquals(BasicActiveHours? other) =>
        other is not null
        && Days == other.Days
        && StartMinutes == other.StartMinutes
        && EndMinutes == other.EndMinutes
        && Use24HourClock == other.Use24HourClock
        && TimeZone == other.TimeZone;

    internal static int NormalizeMinutes(int minutes) => ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
}

/// <summary>Days of the week. Persisted numerically.</summary>
[Flags]
public enum BasicWeekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekends = Saturday | Sunday,
    Everyday = Weekdays | Weekends,
}

/// <summary>Adventure Plate Classic orientation. Persisted numerically; append only.</summary>
public enum AdventurePlateOrientation
{
    /// <summary>Portrait on the left, details on the right.</summary>
    Normal = 0,

    /// <summary>Portrait on the right, details on the left.</summary>
    Mirrored = 1,
}

/// <summary>Where the Basic portrait comes from. Persisted numerically; append only.</summary>
public enum BasicPortraitSource
{
    /// <summary>An image file imported into AetherFrame's managed assets.</summary>
    ImportedImage = 0,

    /// <summary>Reserved: the character's in-game portrait. Not available yet.</summary>
    CurrentPortrait = 1,

    /// <summary>Reserved: a scene composed in AetherFrame. Not available yet.</summary>
    AetherFrameScene = 2,
}
