using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Hosting;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Commands;
using AetherFrame.Services.Fonts;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using AetherFrame.Windows;
using Dalamud.Bindings.ImGui;
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
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    public PluginConfiguration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("AetherFrame");

    private readonly PlateLibraryService plateLibrary;
    private readonly TemplateLibraryService templateLibrary;
    private readonly CharacterIdentityService characterIdentityService;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileFontService fontService;
    private readonly ProceduralTextureCache proceduralTextureCache;
    private readonly BuiltInArtTextureCache builtInArtTextureCache;
    private readonly PlateThumbnailService thumbnailService;
    private readonly PlateThumbnailTextures thumbnailTextures;
    private readonly PlateThumbnailService templateThumbnailService;
    private readonly PlateThumbnailTextures templateThumbnailTextures;
    private readonly EditorSurfaceCoordinator editorSurfaces;
    private readonly PlateLibraryWindow plateLibraryWindow;
    private readonly BasicProfileEditorWindow basicProfileEditorWindow;
    private readonly ProfileEditorWindow profileEditorWindow;
    private readonly ProfileViewWindow profileViewWindow;
    private readonly PackageImportWindow packageImportWindow;
    private readonly PlatePackageService packageService;
    private readonly BasicGuidance basicGuidance;

    public Plugin()
    {
        DalamudServices.Initialize(PluginInterface, CommandManager, ClientState, PlayerState, Framework, FileStorage, Log, KeyState, TextureProvider, DataManager, UnlockState, ObjectTable);

        var savedConfiguration = PluginInterface.GetPluginConfig() as PluginConfiguration;
        Configuration = savedConfiguration ?? new PluginConfiguration();

        // The one-time Basic suggestion: decided now from the configuration alone (a current one's
        // stored flag always wins), so it's ready before any window can ask for Advanced.
        basicGuidance = new BasicGuidance(new ConfigurationGuidanceStore(Configuration));
        basicGuidance.Resolve(BasicGuidance.OriginOf(savedConfiguration is not null, Configuration.Version, PluginConfiguration.CurrentVersion));

        var log = new DalamudAetherFrameLog(Log);
        var paths = new PlateStoragePaths(PluginInterface.ConfigDirectory.FullName);

        // All Library persistence runs on the framework thread, as profile IO always has.
        plateLibrary = new PlateLibraryService(paths, new ReliablePlateFileStore(FileStorage), log, dispatch: work => Framework.Run(work));
        templateLibrary = new TemplateLibraryService(paths, new ReliablePlateFileStore(FileStorage), plateLibrary, log, dispatch: work => Framework.Run(work));

        var jobCatalog = new JobCatalog();
        characterIdentityService = new CharacterIdentityService(jobCatalog);

        // Character details refresh on their own every half second; a login or logout also
        // refreshes them immediately.
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;
        var profileService = new ProfileService(plateLibrary);

        var assetStorageService = new AssetStorageService(
            paths.AssetsDirectory, paths.AssetStagingDirectory, new AssetMetadataStore(paths.AssetMetadataDirectory, log), ImageFormatSupport.IsSupported, log);
        imageTextureCache = new ImageTextureCache(assetStorageService);
        fontService = new ProfileFontService();
        proceduralTextureCache = new ProceduralTextureCache();
        builtInArtTextureCache = new BuiltInArtTextureCache();
        var renderResources = new ProfileRenderResources(imageTextureCache, fontService, proceduralTextureCache, builtInArtTextureCache, jobCatalog);
        var fileDialogManager = new FileDialogManager();
        var basicFileDialogManager = new FileDialogManager();

        // No thumbnail generator yet (no offscreen renderer exists): cards use their fallback.
        thumbnailService = new PlateThumbnailService(paths.ThumbnailsDirectory, generator: null, log);
        thumbnailTextures = new PlateThumbnailTextures(thumbnailService);
        plateLibrary.PlateSaved += thumbnailService.Invalidate;
        plateLibrary.PlateDeleted += thumbnailService.Remove;

        // Same (currently inert) thumbnail pipeline as Plates, kept in a separate directory only
        // to avoid a Guid-collision surface between a Template id and a Plate id.
        templateThumbnailService = new PlateThumbnailService(paths.TemplateThumbnailsDirectory, generator: null, log);
        templateThumbnailTextures = new PlateThumbnailTextures(templateThumbnailService);

        var editorSession = new EditorSession(profileService, assetStorageService, imageTextureCache, log, () => ImGui.GetFrameCount());
        editorSurfaces = new EditorSurfaceCoordinator(() =>
        {
            editorSession.CommitPendingEdits();
            editorSession.EndInteraction();
        });
        var gameTitleCatalog = new GameTitleCatalog();
        var basicIdentitySession = new BasicIdentitySession(
            profileService, editorSession, characterIdentityService, new ProfileTextMeasurer(fontService), gameTitleCatalog);
        var basicEditorSession = new BasicEditorSession(
            profileService, editorSession, assetStorageService, basicIdentitySession, characterIdentityService, jobCatalog);
        keyboardShortcutService = new KeyboardShortcutService();

        // Undo, Redo, Save and Revert as both editors' shared action bar offers them.
        var documentCommands = new EditorDocumentCommands(profileService, editorSession);

        basicProfileEditorWindow = new BasicProfileEditorWindow(
            profileService, editorSession, basicEditorSession, imageTextureCache, renderResources, basicFileDialogManager, gameTitleCatalog, jobCatalog,
            OpenAdvancedEditor, OpenMyPlates, editorSurfaces, documentCommands, keyboardShortcutService);
        profileEditorWindow = new ProfileEditorWindow(
            profileService, editorSession, keyboardShortcutService, renderResources, fileDialogManager, OpenBasicEditor, OpenMyPlates, editorSurfaces, documentCommands);
        editorSurfaces.Attach(basicProfileEditorWindow, profileEditorWindow);
        // The one place "this character's Active Plate" is resolved (the viewer's default request).
        var activePlates = new ActivePlateResolver(plateLibrary, () => characterIdentityService.CurrentCharacter);
        profileViewWindow = new ProfileViewWindow(profileService, plateLibrary, activePlates, renderResources, OpenMyPlates);

        // .aetherframe export/import: local files only, chosen by the player; nothing networked.
        packageService = new PlatePackageService(
            plateLibrary, assetStorageService, paths, $"AetherFrame {PluginInterface.Manifest.AssemblyVersion}", ImageFormatSupport.IsSupported, log);
        packageImportWindow = new PackageImportWindow(packageService, renderResources, (plateId, name) => plateLibraryWindow!.OnPlateImported(plateId, name));
        plateLibraryWindow = new PlateLibraryWindow(
            plateLibrary, templateLibrary, profileService, editorSession, characterIdentityService, thumbnailService, thumbnailTextures,
            templateThumbnailService, templateThumbnailTextures, renderResources, OpenBasicEditor, OpenAdvancedEditor, () => editorSurfaces.ActiveSurface,
            basicGuidance, profileViewWindow.ShowPlate, profileViewWindow.ShowDocument,
            packageService, new FileDialogManager(), packageImportWindow.Begin);

        WindowSystem.AddWindow(plateLibraryWindow);
        WindowSystem.AddWindow(basicProfileEditorWindow);
        WindowSystem.AddWindow(profileEditorWindow);
        WindowSystem.AddWindow(profileViewWindow);
        WindowSystem.AddWindow(packageImportWindow);

        CommandManager.AddHandler(AetherFrameCommand.Name, new CommandInfo(OnCommand)
        {
            HelpMessage = AetherFrameCommand.HelpMessage
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information($"===AetherFrame loaded ({PluginInterface.Manifest.Name})===");
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Temporary files from an import interrupted by the game closing; only AetherFrame's own.
        packageService.SweepStaging();

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

        try
        {
            await templateLibrary.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing on disk is touched by a failed load; Templates says it couldn't load,
            // exactly like My Plates does above.
            Log.Error(ex, "AetherFrame could not load the Template Library.");
        }
    }

    public ValueTask DisposeAsync()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        WindowSystem.RemoveAllWindows();

        plateLibraryWindow.Dispose();
        basicProfileEditorWindow.Dispose();
        profileEditorWindow.Dispose();
        profileViewWindow.Dispose();
        packageImportWindow.Dispose();
        keyboardShortcutService.Dispose();
        imageTextureCache.Clear();
        thumbnailTextures.Clear();
        thumbnailService.Dispose();
        templateThumbnailTextures.Clear();
        templateThumbnailService.Dispose();
        proceduralTextureCache.Dispose();
        builtInArtTextureCache.Dispose();
        fontService.Dispose();

        CommandManager.RemoveHandler(AetherFrameCommand.Name);

        return ValueTask.CompletedTask;
    }

    private void OnCommand(string command, string args)
    {
        switch (AetherFrameCommand.Parse(args))
        {
            case AetherFrameCommandAction.ViewActivePlate:
                profileViewWindow.ShowActivePlate();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    private void OnLogin() => characterIdentityService.InvalidateCharacterInfo();

    private void OnLogout(int type, int code) => characterIdentityService.InvalidateCharacterInfo();

    /// <summary>The main entry point is My Plates.</summary>
    public void ToggleMainUi() => plateLibraryWindow.Toggle();

    /// <summary>
    /// The editors' My Plates button: opens My Plates, or — when it's already open, perhaps behind
    /// the editor — brings it to the front. Never closes it.
    /// </summary>
    private void OpenMyPlates()
    {
        plateLibraryWindow.IsOpen = true;
        plateLibraryWindow.BringToFront();
    }

    /// <summary>
    /// Basic and Advanced are two surfaces over one editing session; only one is open at a time.
    /// Every way into the Basic editor comes through here, and having opened it means the one-time
    /// Basic suggestion is no longer needed.
    /// </summary>
    private void OpenBasicEditor()
    {
        basicGuidance.MarkHandled();
        editorSurfaces.Show(EditorSurfaceKind.Basic);
    }

    private void OpenAdvancedEditor() => editorSurfaces.Show(EditorSurfaceKind.Advanced);

    /// <summary>The guidance flag, persisted in the plugin configuration.</summary>
    private sealed class ConfigurationGuidanceStore(PluginConfiguration configuration) : IBasicGuidanceStore
    {
        public bool BasicGuidanceHandled
        {
            get => configuration.BasicGuidanceHandled;
            set => configuration.BasicGuidanceHandled = value;
        }

        public void Save()
        {
            configuration.Version = PluginConfiguration.CurrentVersion;
            PluginInterface.SavePluginConfig(configuration);
        }
    }
}
