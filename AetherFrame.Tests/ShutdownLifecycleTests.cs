using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Persistence;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Unloading while file work is in flight: a write that has begun is allowed to finish before
/// anything it uses is disposed, nothing new starts once unloading begins, and unloading never
/// waits forever.
/// </summary>
public class ShutdownLifecycleTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NoOperationsRunning_ShutdownCompletesImmediately()
    {
        var operations = new OwnedOperations();

        var shutdown = operations.ShutdownAsync(Generous);

        Assert.True(shutdown.IsCompleted);
        Assert.True(await shutdown);
        Assert.True(operations.IsShuttingDown);
        Assert.True(operations.Stopping.IsCancellationRequested);
    }

    [Fact]
    public async Task NoOperationsRunning_ServicesAreDisposedAtOnce()
    {
        var disposedUi = false;
        var disposedUsed = false;
        var log = new TestLog();

        var shutdown = PluginShutdown.RunAsync(new OwnedOperations(), Generous, () => { disposedUi = true; return Task.CompletedTask; }, () => disposedUsed = true, log);

        Assert.True(shutdown.IsCompleted);
        await shutdown;
        Assert.True(disposedUi);
        Assert.True(disposedUsed);
        Assert.Empty(log.Messages);
    }

    [Fact]
    public async Task RunningSave_IsDrained_AndLandsWhole()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(store);
        var library = await LoadAsync(fixture, operations);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var document = library.OpenDocumentForEditing(created.PlateId);
        document.Revision = 42;

        store.Hold();
        var save = library.SavePlateDocumentAsync(document);
        await store.WriteStarted.WaitAsync(Generous);

        var shutdown = operations.ShutdownAsync(Generous);
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(1, operations.RunningCount);

        store.Release();
        await save;

        Assert.True(await shutdown);
        Assert.Equal(0, operations.RunningCount);
        using var saved = System.Text.Json.JsonDocument.Parse(fixture.ReadPlateJson(created.PlateId));
        Assert.Equal(42, saved.RootElement.GetProperty("Revision").GetInt32());
    }

    [Fact]
    public async Task ServicesAreDisposed_OnlyAfterTheRunningWriteFinishes()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(store);
        var library = await LoadAsync(fixture, operations);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        store.Hold();
        var rename = library.RenamePlateAsync(created.PlateId, "Renamed Before Dispose");
        await store.WriteStarted.WaitAsync(Generous);

        string? plateAtDisposal = null;
        var shutdown = PluginShutdown.RunAsync(operations, Generous, () => Task.CompletedTask, () => plateAtDisposal = fixture.ReadPlateJson(created.PlateId), new TestLog());
        await Task.Delay(50);
        Assert.Null(plateAtDisposal);

        store.Release();
        await rename;
        await shutdown;

        Assert.NotNull(plateAtDisposal);
        Assert.Contains("Renamed Before Dispose", plateAtDisposal);
    }

    [Fact]
    public async Task OnceShutdownBegins_NewWritesAreRefused_AndNothingIsWritten()
    {
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var library = await LoadAsync(fixture, operations);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var before = fixture.ReadPlateJson(created.PlateId);
        var plateFiles = Directory.GetFiles(fixture.Paths.PlatesDirectory).Length;

        Assert.True(await operations.ShutdownAsync(Generous));

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(created.PlateId, "Too Late"));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.DuplicatePlateAsync(created.PlateId));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Bob, created.PlateId));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.DeletePlateAsync(created.PlateId));

        Assert.Equal(before, fixture.ReadPlateJson(created.PlateId));
        Assert.Equal(plateFiles, Directory.GetFiles(fixture.Paths.PlatesDirectory).Length);
        Assert.False(File.Exists(fixture.Paths.GetBindingPath(Characters.Bob.ContentId)));
        Assert.Equal(0, operations.RunningCount);
    }

    [Fact]
    public async Task OnceShutdownBegins_TemplateWritesAreRefused()
    {
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var plates = await LoadAsync(fixture, operations);
        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, plates, fixture.Log, () => fixture.Clock.Now, operations: operations);
        await templates.InitializeAsync();
        var plate = await plates.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        await operations.ShutdownAsync(Generous);

        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.SaveAsTemplateAsync(plate.PlateId, "Too Late"));
        Assert.False(Directory.Exists(fixture.Paths.TemplatesDirectory) && Directory.GetFiles(fixture.Paths.TemplatesDirectory).Length > 0);
    }

    [Fact]
    public async Task OnceShutdownBegins_PackageExportAndImportAreRefused()
    {
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var library = await LoadAsync(fixture, operations);
        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var assets = new AetherFrame.Services.AssetStorageService(
            fixture.Paths.AssetsDirectory, fixture.Paths.AssetStagingDirectory, new AetherFrame.Services.Assets.AssetMetadataStore(fixture.Paths.AssetMetadataDirectory));
        var packages = new PlatePackageService(library, assets, fixture.Paths, "AetherFrame test", operations: operations);
        var exportPath = Path.Combine(fixture.Paths.PlatesDirectory, "..", "export.aetherframe");

        var exported = packages.Export(plate.PlateId, exportPath, overwrite: false);
        Assert.True(exported.Succeeded);
        using var staged = packages.Inspect(exportPath);
        var platesBefore = library.GetOrderedPlates().Count;
        File.Delete(exportPath);

        await operations.ShutdownAsync(Generous);

        Assert.False(packages.Export(plate.PlateId, exportPath, overwrite: false).Succeeded);
        Assert.False(File.Exists(exportPath));
        Assert.False((await packages.ImportAsync(staged)).Succeeded);
        Assert.Equal(platesBefore, library.GetOrderedPlates().Count);
    }

    [Fact]
    public async Task OperationWaitingItsTurn_AtShutdown_NeverStarts()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(store);
        var library = await LoadAsync(fixture, operations);
        var first = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var second = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var secondBefore = fixture.ReadPlateJson(second.PlateId);

        store.Hold();
        var running = library.RenamePlateAsync(first.PlateId, "Running");
        await store.WriteStarted.WaitAsync(Generous);
        var queued = library.RenamePlateAsync(second.PlateId, "Queued");

        var shutdown = operations.ShutdownAsync(Generous);

        // The queued rename gives up at once, while the running one is still held.
        await Assert.ThrowsAsync<PlateLibraryException>(() => queued.WaitAsync(Generous));
        Assert.False(running.IsCompleted);

        store.Release();
        await running;
        Assert.True(await shutdown);
        Assert.Contains("Running", fixture.ReadPlateJson(first.PlateId));
        Assert.Equal(secondBefore, fixture.ReadPlateJson(second.PlateId));
    }

    [Fact]
    public async Task FailingRunningWrite_DoesNotDeadlockShutdown()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(store);
        var library = await LoadAsync(fixture, operations);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var before = fixture.ReadPlateJson(created.PlateId);

        store.Hold(failOnRelease: true);
        var rename = library.RenamePlateAsync(created.PlateId, "Never Written");
        await store.WriteStarted.WaitAsync(Generous);

        var disposed = false;
        var shutdown = PluginShutdown.RunAsync(operations, Generous, () => Task.CompletedTask, () => disposed = true, new TestLog());
        store.Release();

        await Assert.ThrowsAsync<IOException>(() => rename);
        await shutdown.WaitAsync(Generous);
        Assert.True(disposed);
        Assert.Equal(0, operations.RunningCount);
        Assert.Equal(before, fixture.ReadPlateJson(created.PlateId));
    }

    [Fact]
    public async Task WriteOutlastingTheTimeout_ShutdownReturns_ButWhatTheWriteUsesIsKeptUntilItEnds()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(new ShutdownGuardedFileStore(store, operations));
        var library = await LoadAsync(fixture, operations);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        store.Hold();
        var rename = library.RenamePlateAsync(created.PlateId, "Outlasted The Timeout");
        await store.WriteStarted.WaitAsync(Generous);

        var disposedUi = false;
        var usedDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new TestLog();

        // Unloading is bounded: it returns after the timeout although the write is still running…
        await PluginShutdown.RunAsync(
            operations, TimeSpan.FromMilliseconds(100), () => { disposedUi = true; return Task.CompletedTask; }, () => usedDisposed.TrySetResult(), log)
            .WaitAsync(Generous);

        Assert.True(disposedUi);
        Assert.True(operations.IsAbandoned);
        Assert.Contains(log.Messages, m => m.Contains("stopped waiting"));

        // …but nothing the write still uses has been disposed underneath it.
        await Task.Delay(100);
        Assert.False(usedDisposed.Task.IsCompleted);
        Assert.Equal(1, operations.RunningCount);

        // The step that was under way completes whole; only then is the rest disposed.
        store.Release();
        await rename;
        await usedDisposed.Task.WaitAsync(Generous);
        Assert.Equal(0, operations.RunningCount);
        Assert.Contains("Outlasted The Timeout", fixture.ReadPlateJson(created.PlateId));
    }

    [Fact]
    public async Task AbandonedMultiFileOperation_StopsBetweenItsAtomicFiles_AndTheNextLoadRecovers()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(new ShutdownGuardedFileStore(store, operations));
        var library = await LoadAsync(fixture, operations);
        var first = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bindingBefore = fixture.ReadBindingJson(Characters.Alice.ContentId);
        var orderBefore = fixture.ReadLibraryOrder();

        // Creating a Plate writes its document, then the character's binding, then the index.
        store.Hold();
        var create = library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await store.WriteStarted.WaitAsync(Generous);

        Assert.False(await operations.ShutdownAsync(TimeSpan.FromMilliseconds(100)));
        store.Release();

        // The document write under way completes; the binding and index writes never start.
        await Assert.ThrowsAsync<OperationAbandonedException>(() => create.WaitAsync(Generous));
        await operations.Drained.WaitAsync(Generous);
        Assert.Equal(bindingBefore, fixture.ReadBindingJson(Characters.Alice.ContentId));
        Assert.Equal(orderBefore, fixture.ReadLibraryOrder());

        var plateFiles = Directory.GetFiles(fixture.Paths.PlatesDirectory, "*.json");
        Assert.Equal(2, plateFiles.Length);
        Assert.All(plateFiles, file => System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).Dispose());

        // Exactly the interruption the write ordering is built for: the next load lists both Plates.
        var reloaded = new PlateLibraryService(fixture.Paths, new SystemFileStore(), fixture.Log, () => fixture.Clock.Now);
        await reloaded.InitializeAsync();
        Assert.Equal(2, reloaded.GetOrderedPlates().Count);
        Assert.Contains(reloaded.GetOrderedPlates(), p => p.PlateId == first.PlateId);
    }

    [Fact]
    public async Task OperationDispatchedButNotStarted_WhenUnloadingStopsWaiting_NeverStarts()
    {
        // A framework thread that only runs queued work when told to.
        var framework = new ManualFramework();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var library = new PlateLibraryService(fixture.Paths, new ShutdownGuardedFileStore(fixture.Store, operations), fixture.Log, () => fixture.Clock.Now, framework.Dispatch, operations);
        var loaded = library.InitializeAsync();
        framework.RunQueued();
        await loaded;
        var creating = library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        framework.RunQueued();
        var created = await creating;
        var before = fixture.ReadPlateJson(created.PlateId);

        // Dispatched, but the framework thread never gets to it before the timeout.
        var rename = library.RenamePlateAsync(created.PlateId, "Late Start");
        Assert.False(await operations.ShutdownAsync(TimeSpan.FromMilliseconds(100)));

        framework.RunQueued();

        await Assert.ThrowsAsync<OperationAbandonedException>(() => rename.WaitAsync(Generous));
        await operations.Drained.WaitAsync(Generous);
        Assert.Equal(before, fixture.ReadPlateJson(created.PlateId));
    }

    [Fact]
    public async Task WriteThatNeverEnds_NeverDeadlocksUnloading()
    {
        var store = new GatedStore();
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture(new ShutdownGuardedFileStore(store, operations));
        var library = await LoadAsync(fixture, operations);
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        store.Hold();
        var rename = library.RenamePlateAsync(created.PlateId, "Never Released");
        await store.WriteStarted.WaitAsync(Generous);

        var usedDisposed = false;
        await PluginShutdown.RunAsync(operations, TimeSpan.FromMilliseconds(50), () => Task.CompletedTask, () => usedDisposed = true, new TestLog())
            .WaitAsync(Generous);

        Assert.False(usedDisposed);
        Assert.False(rename.IsCompleted);

        store.Release();
        await rename.WaitAsync(Generous);
    }

    [Fact]
    public void GuardedStore_RefusesEveryFileStep_OnceAbandoned_AndNothingBefore()
    {
        using var directory = new TempDirectory();
        var operations = new OwnedOperations();
        var store = new ShutdownGuardedFileStore(new SystemFileStore(), operations);
        var path = Path.Combine(directory.Path, "a.json");

        store.WriteTextAsync(path, "{}").GetAwaiter().GetResult();
        Assert.True(store.FileExists(path));

        Assert.True(operations.TryBegin(out var lease));
        Assert.False(operations.ShutdownAsync(TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult());

        // Refused before anything starts: thrown synchronously, no task is ever created.
        Assert.Throws<OperationAbandonedException>(() => { _ = store.WriteTextAsync(path, "{\"changed\":1}"); });
        Assert.Throws<OperationAbandonedException>(() => { _ = store.ReadTextAsync(path, _ => { }); });
        Assert.Throws<OperationAbandonedException>(() => store.MoveFile(path, path + ".moved"));
        Assert.Throws<OperationAbandonedException>(() => store.CopyFile(path, path + ".copy"));
        Assert.Throws<OperationAbandonedException>(() => store.DeleteFile(path));
        Assert.Throws<OperationAbandonedException>(() => store.FileExists(path));
        Assert.Throws<OperationAbandonedException>(() => store.ListFiles(directory.Path, "*.json"));
        Assert.Equal("{}", File.ReadAllText(path).Trim());
        lease.Dispose();
    }

    [Fact]
    public async Task CanceledLoad_StopsBeforeWritingAnything()
    {
        using var fixture = new LibraryFixture();
        var library = fixture.CreateService();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.InitializeAsync(canceled.Token));

        Assert.False(library.IsLoaded);
        Assert.False(File.Exists(fixture.Paths.LibraryFile));

        // An uncanceled load afterwards is unaffected.
        await library.InitializeAsync();
        Assert.True(library.IsLoaded);
        Assert.True(File.Exists(fixture.Paths.LibraryFile));
    }

    [Fact]
    public async Task LoadDuringShutdown_StopsBeforeWritingAnything()
    {
        var operations = new OwnedOperations();
        using var fixture = new LibraryFixture();
        var library = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, operations: operations);
        var templates = new TemplateLibraryService(fixture.Paths, fixture.Store, library, fixture.Log, () => fixture.Clock.Now, operations: operations);

        await operations.ShutdownAsync(Generous);

        await Assert.ThrowsAsync<PlateLibraryException>(() => library.InitializeAsync());
        await Assert.ThrowsAsync<TemplateLibraryException>(() => templates.InitializeAsync());
        Assert.False(File.Exists(fixture.Paths.LibraryFile));
    }

    [Fact]
    public void Lease_EndsItsOperationOnce_EvenIfDisposedTwice()
    {
        var operations = new OwnedOperations();
        Assert.True(operations.TryBegin(out var first));
        Assert.True(operations.TryBegin(out var second));

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, operations.RunningCount);
        second.Dispose();
        Assert.Equal(0, operations.RunningCount);
    }

    private static async Task<PlateLibraryService> LoadAsync(LibraryFixture fixture, OwnedOperations operations)
    {
        var library = new PlateLibraryService(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now, operations: operations);
        await library.InitializeAsync();
        return library;
    }

    /// <summary>Stands in for the framework thread: dispatched work waits until <see cref="RunQueued"/>.</summary>
    private sealed class ManualFramework
    {
        private readonly List<Func<Task>> queued = new();

        internal Task Dispatch(Func<Task> work)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (queued)
            {
                queued.Add(async () =>
                {
                    try
                    {
                        await work();
                        completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                });
            }

            return completion.Task;
        }

        internal void RunQueued()
        {
            List<Func<Task>> batch;
            lock (queued)
            {
                batch = new List<Func<Task>>(queued);
                queued.Clear();
            }

            foreach (var work in batch)
            {
                work().GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>Plain files whose next write can be held mid-flight (after it has begun, before
    /// anything reaches disk) and then released — or made to fail.</summary>
    private sealed class GatedStore : IPlateFileStore
    {
        private readonly SystemFileStore files = new();
        private TaskCompletionSource? gate;
        private TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool failOnRelease;

        internal Task WriteStarted => writeStarted.Task;

        internal void Hold(bool failOnRelease = false)
        {
            this.failOnRelease = failOnRelease;
            writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Release() => gate?.TrySetResult();

        public bool FileExists(string path) => files.FileExists(path);

        public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

        public Task ReadTextAsync(string path, Action<string> reader) => files.ReadTextAsync(path, reader);

        public async Task WriteTextAsync(string path, string contents)
        {
            if (gate is { } held)
            {
                writeStarted.TrySetResult();
                await held.Task.ConfigureAwait(false);
                gate = null;
                if (failOnRelease)
                {
                    throw new IOException("Injected failure of a write in flight.");
                }
            }

            await files.WriteTextAsync(path, contents).ConfigureAwait(false);
        }

        public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

        public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

        public void DeleteFile(string path) => files.DeleteFile(path);
    }
}
