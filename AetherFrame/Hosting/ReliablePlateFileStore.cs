using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using Dalamud.Plugin.Services;

namespace AetherFrame.Hosting;

/// <summary>
/// <see cref="IPlateFileStore"/> over Dalamud's <see cref="IReliableFileStorage"/>: atomic,
/// journaled writes, and reads that fall back to the backup copy when a file is damaged.
/// Existence and enumeration deliberately use the real file system (see
/// <see cref="IPlateFileStore"/>): the service's own Exists also reports backups of files that
/// were intentionally moved away, such as a deleted Plate.
/// </summary>
internal sealed class ReliablePlateFileStore : IPlateFileStore
{
    private readonly IReliableFileStorage storage;
    private readonly SystemFileStore files = new();

    internal ReliablePlateFileStore(IReliableFileStorage storage)
    {
        this.storage = storage;
    }

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

    public Task ReadTextAsync(string path, Action<string> reader) => storage.ReadAllTextAsync(path, reader);

    public Task WriteTextAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return storage.WriteAllTextAsync(path, contents);
    }

    public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path) => files.DeleteFile(path);
}
