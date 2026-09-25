using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AetherFrame.Persistence;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// The Plate and Template Libraries' file store as the plugin gives it to them: every read, write,
/// move, copy and delete first checks that its operation hasn't been abandoned by unloading (see
/// <see cref="OwnedOperations"/>). An abandoned operation therefore stops between two of its
/// atomic file steps — never inside one — instead of calling into Dalamud's reliable file storage
/// after the plugin's scope has disposed it.
/// </summary>
internal sealed class ShutdownGuardedFileStore : IPlateFileStore
{
    private readonly IPlateFileStore inner;
    private readonly OwnedOperations operations;

    internal ShutdownGuardedFileStore(IPlateFileStore inner, OwnedOperations operations)
    {
        this.inner = inner;
        this.operations = operations;
    }

    public bool FileExists(string path)
    {
        operations.ThrowIfAbandoned();
        return inner.FileExists(path);
    }

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern)
    {
        operations.ThrowIfAbandoned();
        return inner.ListFiles(directory, searchPattern);
    }

    public Task ReadTextAsync(string path, Action<string> reader)
    {
        operations.ThrowIfAbandoned();
        return inner.ReadTextAsync(path, reader);
    }

    public Task WriteTextAsync(string path, string contents)
    {
        operations.ThrowIfAbandoned();
        return inner.WriteTextAsync(path, contents);
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        operations.ThrowIfAbandoned();
        inner.MoveFile(sourcePath, destinationPath);
    }

    public void CopyFile(string sourcePath, string destinationPath)
    {
        operations.ThrowIfAbandoned();
        inner.CopyFile(sourcePath, destinationPath);
    }

    public void DeleteFile(string path)
    {
        operations.ThrowIfAbandoned();
        inner.DeleteFile(path);
    }
}
