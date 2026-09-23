using AetherFrame.Services.Plates;

namespace AetherFrame.Services;

internal sealed class CharacterIdentityService
{
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
}
