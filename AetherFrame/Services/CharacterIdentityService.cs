namespace AetherFrame.Services;

internal sealed class CharacterIdentityService
{
    internal bool IsCharacterLoggedIn => DalamudServices.PlayerState.IsLoaded;

    internal ulong? CurrentContentId => DalamudServices.PlayerState.IsLoaded
        ? DalamudServices.PlayerState.ContentId
        : null;

    /// <summary>The logged-in character's name, or null if none is logged in. Used only as a
    /// convenience default (e.g. pre-filling the Basic editor's name field) — never persisted
    /// as an identity check.</summary>
    internal string? CurrentCharacterName => DalamudServices.PlayerState.IsLoaded
        ? DalamudServices.PlayerState.CharacterName
        : null;
}
