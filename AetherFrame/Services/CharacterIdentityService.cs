namespace AetherFrame.Services;

internal sealed class CharacterIdentityService
{
    internal bool IsCharacterLoggedIn => DalamudServices.PlayerState.IsLoaded;

    internal ulong? CurrentContentId => DalamudServices.PlayerState.IsLoaded
        ? DalamudServices.PlayerState.ContentId
        : null;
}
