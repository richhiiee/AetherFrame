using System;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.UI;

internal sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ProfileService profileService;

    internal MainWindow(Plugin plugin, ProfileService profileService)
        : base("AetherFrame##AetherFrameMainWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        this.profileService = profileService;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!DalamudServices.PlayerState.IsLoaded)
        {
            ImGui.TextUnformatted("No character is currently logged in.");
            return;
        }

        ImGui.TextUnformatted($"Character content ID: {DalamudServices.PlayerState.ContentId}");

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile loaded.");

            using (ImRaii.Disabled(profileService.IsBusy))
            {
                if (ImGui.Button("Load"))
                {
                    _ = LoadSafelyAsync();
                }
            }

            return;
        }

        ImGui.TextUnformatted($"Profile: {profile.Name}");
        ImGui.TextUnformatted($"Elements: {profile.Elements.Count}/{ProfileDocument.MaxElementCount}");

        if (ImGui.Button("Open Editor"))
        {
            plugin.ToggleProfileEditorUi();
        }

        ImGui.SameLine();
        if (ImGui.Button("View Profile"))
        {
            plugin.ToggleProfileViewUi();
        }

        if (profileService.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("(busy)");
        }
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await profileService.LoadForCurrentCharacterAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "AetherFrame failed to load the current character's profile.");
        }
    }
}
