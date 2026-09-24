using System;
using AetherFrame.Domain.Basic;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;

namespace AetherFrame.Services;

internal sealed class CharacterIdentityService : ICharacterInfoSource
{
    private readonly JobCatalog jobs;
    private readonly CharacterInfoCache infoCache;
    private bool loggedReadFailure;

    internal CharacterIdentityService(JobCatalog jobs)
    {
        this.jobs = jobs;
        infoCache = new CharacterInfoCache(ReadInfo, () => Environment.TickCount64);
    }

    internal bool IsCharacterLoggedIn => DalamudServices.PlayerState.IsLoaded;

    internal ulong? CurrentContentId => DalamudServices.PlayerState.IsLoaded
        ? DalamudServices.PlayerState.ContentId
        : null;

    /// <summary>The logged-in character's name, or null if none is logged in. Used only as a
    /// convenience default (e.g. pre-filling the Basic editor's name field) and as descriptive
    /// metadata — never persisted as an identity check.</summary>
    internal string? CurrentCharacterName => DalamudServices.PlayerState.IsLoaded
        ? DalamudServices.PlayerState.CharacterName
        : null;

    /// <summary>
    /// The logged-in character for the Plate Library, or null when none is logged in (never a
    /// fabricated stand-in). The ContentId is only the local binding key; the name and home
    /// World are descriptive.
    /// </summary>
    internal CharacterContext? CurrentCharacter
    {
        get
        {
            var player = DalamudServices.PlayerState;
            if (!player.IsLoaded || player.ContentId == 0)
            {
                return null;
            }

            string? homeWorld = null;
            if (player.HomeWorld.ValueNullable is { } world)
            {
                homeWorld = world.Name.ExtractText();
            }

            return new CharacterContext(player.ContentId, player.CharacterName, string.IsNullOrWhiteSpace(homeWorld) ? null : homeWorld);
        }
    }

    /// <summary>
    /// What Basic mode can fill in from the logged-in character — name, Home World and Data
    /// Center, current job and level, Free Company tag — or null when none is logged in. Read only
    /// through Dalamud's managed APIs (<c>IPlayerState</c>, game data sheets, the local player's
    /// object) and refreshed every half second (see <see cref="CharacterInfoCache"/>), so it
    /// follows logins, logouts, and job changes while the editor is open. Anything not reliably
    /// available is left unknown rather than guessed. A snapshot for explicit "use current"
    /// actions and new-Plate defaults, never written on its own.
    /// </summary>
    public BasicCharacterInfo? CurrentInfo => infoCache.Current;

    /// <summary>Forces the next <see cref="CurrentInfo"/> to read fresh (login, logout).</summary>
    internal void InvalidateCharacterInfo() => infoCache.Invalidate();

    private BasicCharacterInfo? ReadInfo()
    {
        var player = DalamudServices.PlayerState;
        if (!player.IsLoaded)
        {
            return null;
        }

        // The name is the one required piece; everything else is read on its own, so one
        // unavailable detail can never hide the rest (or the character itself).
        var name = Read(() => player.CharacterName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var world = Read(() => player.HomeWorld.ValueNullable?.Name.ExtractText());
        var dataCenter = Read(() => player.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText());
        var jobId = Read(() => player.ClassJob.RowId);
        var jobName = Read(() => jobs.Find(player.ClassJob.RowId)?.Name
            ?? (player.ClassJob.ValueNullable is { } job ? JobCatalog.FormatName(job.Name.ExtractText()) : null));
        var level = Read(() => (int)player.Level);

        // The tag is on the local player's object; "" when the character isn't in a Free Company.
        var freeCompanyTag = Read(() => DalamudServices.ObjectTable.LocalPlayer?.CompanyTag.TextValue);

        return new BasicCharacterInfo(
            name,
            string.IsNullOrWhiteSpace(world) ? null : world,
            string.IsNullOrWhiteSpace(dataCenter) ? null : dataCenter,
            jobId,
            string.IsNullOrWhiteSpace(jobName) ? null : jobName,
            level,
            freeCompanyTag);
    }

    private T? Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            if (!loggedReadFailure)
            {
                loggedReadFailure = true;
                DalamudServices.Log.Warning(ex, "AetherFrame could not read one of the current character's details.");
            }

            return default;
        }
    }
}
