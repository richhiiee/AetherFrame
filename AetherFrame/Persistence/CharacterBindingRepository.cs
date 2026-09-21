using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Characters;
using AetherFrame.Services;
using Dalamud.Plugin.Services;

namespace AetherFrame.Persistence;

internal sealed class CharacterBindingRepository
{
    private readonly IReliableFileStorage fileStorage;
    private readonly string directory;

    internal CharacterBindingRepository()
    {
        fileStorage = DalamudServices.FileStorage;
        directory = Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, "Characters");
    }

    internal async Task<CharacterBinding> LoadOrCreateAsync(ulong contentId)
    {
        var path = GetPath(contentId);

        if (!fileStorage.Exists(path))
        {
            return new CharacterBinding { ContentId = contentId };
        }

        CharacterBinding? binding = null;

        await fileStorage.ReadAllTextAsync(path, json =>
        {
            binding = JsonSerializer.Deserialize<CharacterBinding>(json, JsonOptions.Default)
                ?? throw new JsonException("Character binding file deserialized to null.");
        }).ConfigureAwait(false);

        return binding ?? new CharacterBinding { ContentId = contentId };
    }

    internal async Task SaveAsync(CharacterBinding binding)
    {
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(binding, JsonOptions.Default);
        await fileStorage.WriteAllTextAsync(GetPath(binding.ContentId), json).ConfigureAwait(false);
    }

    private string GetPath(ulong contentId) => Path.Combine(directory, $"{contentId}.json");
}
