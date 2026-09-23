using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Hosting;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Fonts;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using AetherFrame.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
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
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;

    private const string CommandName = "/aetherframe";

    public PluginConfiguration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("AetherFrame");

    private readonly PlateLibraryService plateLibrary;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileFontService fontService;
    private readonly ProceduralTextureCache proceduralTextureCache;
    private readonly PlateThumbnailService thumbnailService;
    private readonly PlateThumbnailTextures thumbnailTextures;
    private readonly EditorSurfaceCoordinator editorSurfaces;
    private readonly PlateLibraryWindow plateLibraryWindow;
    private readonly BasicProfileEditorWindow basicProfileEditorWindow;
    private readonly ProfileEditorWindow profileEditorWindow;
    private readonly ProfileViewWindow profileViewWindow;

    public Plugin()
    {
        DalamudServices.Initialize(PluginInterface, CommandManager, ClientState, PlayerState, Framework, FileStorage, Log, KeyState, TextureProvider, DataManager, UnlockState);

        Configuration = PluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();

        var log = new DalamudAetherFrameLog(Log);
        var paths = new PlateStoragePaths(PluginInterface.ConfigDirectory.FullName);

        // All Library persistence runs on the framework thread, as profile IO always has.
        plateLibrary = new PlateLibraryService(paths, new ReliablePlateFileStore(FileStorage), log, dispatch: work => Framework.Run(work));

        var characterIdentityService = new CharacterIdentityService();
        var profileService = new ProfileService(plateLibrary);

        var assetStorageService = new AssetStorageService(
            paths.AssetsDirectory, paths.AssetStagingDirectory, new AssetMetadataStore(paths.AssetMetadataDirectory, log), ImageFormatSupport.IsSupported, log);
        imageTextureCache = new ImageTextureCache(assetStorageService);
        fontService = new ProfileFontService();
        proceduralTextureCache = new ProceduralTextureCache();
        var renderResources = new ProfileRenderResources(imageTextureCache, fontService, proceduralTextureCache);
        var fileDialogManager = new FileDialogManager();
        var basicFileDialogManager = new FileDialogManager();

        // No thumbnail generator yet (no offscreen renderer exists): cards use their fallback.
        thumbnailService = new PlateThumbnailService(paths.ThumbnailsDirectory, generator: null, log);
        thumbnailTextures = new PlateThumbnailTextures(thumbnailService);
        plateLibrary.PlateSaved += thumbnailService.Invalidate;
        plateLibrary.PlateDeleted += thumbnailService.Remove;

        var editorSession = new EditorSession(profileService, assetStorageService, imageTextureCache);
        editorSurfaces = new EditorSurfaceCoordinator(() =>
        {
            editorSession.CommitPendingEdits();
            editorSession.EndInteraction();
        });
        var gameTitleCatalog = new GameTitleCatalog();
        var basicIdentitySession = new BasicIdentitySession(profileService, editorSession, characterIdentityService, fontService, gameTitleCatalog);
        var basicEditorSession = new BasicEditorSession(profileService, editorSession, assetStorageService, basicIdentitySession);
        keyboardShortcutService = new KeyboardShortcutService();

        basicProfileEditorWindow = new BasicProfileEditorWindow(
            profileService, editorSession, basicEditorSession, imageTextureCache, renderResources, basicFileDialogManager, OpenAdvancedEditor, OpenMyPlates, editorSurfaces);
        profileEditorWindow = new ProfileEditorWindow(
            profileService, editorSession, keyboardShortcutService, renderResources, fileDialogManager, ToggleOpenPlateInViewer, OpenBasicEditor, OpenMyPlates, editorSurfaces);
        editorSurfaces.Attach(basicProfileEditorWindow, profileEditorWindow);
        profileViewWindow = new ProfileViewWindow(profileService, plateLibrary, renderResources);
        plateLibraryWindow = new PlateLibraryWindow(
            plateLibrary, profileService, editorSession, characterIdentityService, thumbnailService, thumbnailTextures,
            OpenBasicEditor, OpenAdvancedEditor, profileViewWindow.ShowPlate);

        WindowSystem.AddWindow(plateLibraryWindow);
        WindowSystem.AddWindow(basicProfileEditorWindow);
        WindowSystem.AddWindow(profileEditorWindow);
        WindowSystem.AddWindow(profileViewWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens My Plates, your AetherFrame Plate collection."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information($"===AetherFrame loaded ({PluginInterface.Manifest.Name})===");
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await plateLibrary.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing on disk is touched by a failed load; My Plates says it couldn't load.
            Log.Error(ex, "AetherFrame could not load the Plate Library.");
            plateLibraryWindow.MarkLoadFailed();
        }
    }

    public ValueTask DisposeAsync()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        plateLibraryWindow.Dispose();
        basicProfileEditorWindow.Dispose();
        profileEditorWindow.Dispose();
        profileViewWindow.Dispose();
        keyboardShortcutService.Dispose();
        imageTextureCache.Clear();
        thumbnailTextures.Clear();
        thumbnailService.Dispose();
        proceduralTextureCache.Dispose();
        fontService.Dispose();

        CommandManager.RemoveHandler(CommandName);

        return ValueTask.CompletedTask;
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    /// <summary>The main entry point is My Plates.</summary>
    public void ToggleMainUi() => plateLibraryWindow.Toggle();

    private void OpenMyPlates() => plateLibraryWindow.IsOpen = true;

    /// <summary>Basic and Advanced are two surfaces over one editing session; only one is open at a time.</summary>
    private void OpenBasicEditor() => editorSurfaces.Show(EditorSurfaceKind.Basic);

    private void OpenAdvancedEditor() => editorSurfaces.Show(EditorSurfaceKind.Advanced);

    private void ToggleOpenPlateInViewer() => profileViewWindow.ToggleOpenPlate();
}
