using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AetherFrame.Persistence;

/// <summary>
/// Plain file-system <see cref="IPlateFileStore"/> with no backup database: writes go to a
/// temporary sibling file that then atomically replaces the target, so a crash mid-write leaves
/// either the old or the new content, never a torn file.
/// </summary>
internal sealed class SystemFileStore : IPlateFileStore
{
    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() : [];

    public async Task ReadTextAsync(string path, Action<string> reader)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
        reader(text);
    }

    public Task WriteTextAsync(string path, string contents)
    {
        WriteAtomically(path, Encoding.UTF8.GetBytes(contents));
        return Task.CompletedTask;
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(sourcePath, destinationPath, overwrite: false);
    }

    public void CopyFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Write-then-rename, shared with the other plain-IO writers (asset metadata, thumbnails).</summary>
    internal static void WriteAtomically(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
