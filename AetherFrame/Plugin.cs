using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.UI;
using AetherFrame.UI.Editor;
using AetherFrame.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AetherFrame;

public sealed class Plugin : IAsyncDalamudPlugin, IAsyncDisposable
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IReliableFileStorage FileStorage { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/aetherframe";

    public PluginConfiguration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("AetherFrame");

    private readonly ProfileService profileService;
    private readonly MainWindow mainWindow;
    private readonly ProfileEditorWindow profileEditorWindow;

    public Plugin()
    {
        DalamudServices.Initialize(PluginInterface, CommandManager, ClientState, PlayerState, Framework, FileStorage, Log);

        Configuration = PluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();

        var characterIdentityService = new CharacterIdentityService();
        var bindingRepository = new CharacterBindingRepository();
        var profileRepository = new ProfileRepository();
        profileService = new ProfileService(bindingRepository, profileRepository, characterIdentityService);

        var editorSession = new EditorSession(profileService);

        mainWindow = new MainWindow(this, profileService);
        profileEditorWindow = new ProfileEditorWindow(profileService, editorSession);

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(profileEditorWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the AetherFrame main window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"===AetherFrame loaded ({PluginInterface.Manifest.Name})===");
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await profileService.LoadForCurrentCharacterAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        mainWindow.Dispose();
        profileEditorWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        return ValueTask.CompletedTask;
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    public void ToggleMainUi() => mainWindow.Toggle();

    public void ToggleProfileEditorUi() => profileEditorWindow.Toggle();
}
