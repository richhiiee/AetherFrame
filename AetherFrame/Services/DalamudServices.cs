using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AetherFrame.Services;

internal static class DalamudServices
{
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    internal static ICommandManager CommandManager { get; private set; } = null!;

    internal static IClientState ClientState { get; private set; } = null!;

    internal static IPlayerState PlayerState { get; private set; } = null!;

    internal static IFramework Framework { get; private set; } = null!;

    internal static IReliableFileStorage FileStorage { get; private set; } = null!;

    internal static IPluginLog Log { get; private set; } = null!;

    internal static IKeyState KeyState { get; private set; } = null!;

    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    internal static IDataManager DataManager { get; private set; } = null!;

    internal static IUnlockState UnlockState { get; private set; } = null!;

    internal static IObjectTable ObjectTable { get; private set; } = null!;

    internal static void Initialize(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IPlayerState playerState,
        IFramework framework,
        IReliableFileStorage fileStorage,
        IPluginLog log,
        IKeyState keyState,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        IUnlockState unlockState,
        IObjectTable objectTable)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        ClientState = clientState;
        PlayerState = playerState;
        Framework = framework;
        FileStorage = fileStorage;
        Log = log;
        KeyState = keyState;
        TextureProvider = textureProvider;
        DataManager = dataManager;
        UnlockState = unlockState;
        ObjectTable = objectTable;
    }
}
