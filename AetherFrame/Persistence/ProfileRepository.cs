using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using Dalamud.Plugin.Services;

namespace AetherFrame.Persistence;

internal sealed class ProfileRepository
{
    private readonly IReliableFileStorage fileStorage;
    private readonly string directory;

    internal ProfileRepository()
    {
        fileStorage = DalamudServices.FileStorage;
        directory = Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, "Profiles");
    }

    internal async Task<ProfileDocument?> LoadAsync(Guid profileId)
    {
        var path = GetPath(profileId);

        if (!fileStorage.Exists(path))
        {
            return null;
        }

        ProfileDocument? profile = null;

        await fileStorage.ReadAllTextAsync(path, json =>
        {
            profile = JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions.Default)
                ?? throw new JsonException("Profile document deserialized to null.");
        }).ConfigureAwait(false);

        return profile;
    }

    internal async Task SaveAsync(ProfileDocument profile)
    {
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(profile, JsonOptions.Default);
        await fileStorage.WriteAllTextAsync(GetPath(profile.ProfileId), json).ConfigureAwait(false);
    }

    private string GetPath(Guid profileId) => Path.Combine(directory, $"{profileId}.json");
}
