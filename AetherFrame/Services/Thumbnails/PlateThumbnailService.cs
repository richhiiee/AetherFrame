using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Thumbnails;

internal enum PlateThumbnailState
{
    /// <summary>No thumbnail for this version of the Plate (and none being made): show the fallback card.</summary>
    Missing,

    /// <summary>Being generated in the background: show the fallback card meanwhile.</summary>
    Generating,

    /// <summary>A thumbnail image for exactly this version of the Plate exists at <see cref="PlateThumbnail.ImagePath"/>.</summary>
    Ready,

    /// <summary>Generating or displaying it failed; not retried until the Plate changes. Show the fallback card.</summary>
    Failed,
}

internal readonly record struct PlateThumbnail(PlateThumbnailState State, string? ImagePath);

/// <summary>
/// Renders a Plate document to a PNG thumbnail file. The extension point for a future CPU
/// compositor: implementations run off the render thread, must treat the document as read-only,
/// and may throw — a failure only ever marks that thumbnail <see cref="PlateThumbnailState.Failed"/>.
/// </summary>
internal interface IPlateThumbnailGenerator
{
    Task GenerateAsync(ProfileDocument document, string outputPngPath, CancellationToken cancellationToken);
}

/// <summary>
/// Thumbnails are derived data: cached PNGs ("thumbnails/{guid}.png") tagged with the Plate
/// version they were made from ("{guid}.key"), regenerated on demand, safe to delete at any time.
/// The Plate document is always the source of truth, and nothing here can block saving, opening,
/// editing, previewing, activating, duplicating, or deleting a Plate: every call is non-blocking
/// and every failure degrades to the fallback card.
///
/// With no <see cref="IPlateThumbnailGenerator"/> (the current state: AetherFrame has no offscreen
/// renderer, and renders nothing with native game or GPU render targets), thumbnails are simply
/// <see cref="PlateThumbnailState.Missing"/> unless a matching file already exists.
/// </summary>
internal sealed class PlateThumbnailService : IDisposable
{
    private readonly string directory;
    private readonly IPlateThumbnailGenerator? generator;
    private readonly IAetherFrameLog log;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();
    private readonly SemaphoreSlim generationSlot = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();

    internal PlateThumbnailService(string directory, IPlateThumbnailGenerator? generator = null, IAetherFrameLog? log = null)
    {
        this.directory = directory;
        this.generator = generator;
        this.log = log ?? NullAetherFrameLog.Instance;
    }

    /// <summary>A version key that changes whenever the Plate's saved state does.</summary>
    internal static string VersionKeyFor(int revision, DateTime modifiedUtc) => $"r{revision}-{modifiedUtc.Ticks}";

    internal string GetImagePath(Guid plateId) => Path.Combine(directory, $"{plateId}.png");

    private string GetKeyPath(Guid plateId) => Path.Combine(directory, $"{plateId}.key");

    /// <summary>
    /// The thumbnail state for this version of the Plate. Cheap to call every frame: the disk is
    /// only consulted the first time a version is seen, and generation (when available) runs in
    /// the background. <paramref name="documentProvider"/> is only called off the calling thread,
    /// during generation.
    /// </summary>
    internal PlateThumbnail Get(Guid plateId, string versionKey, Func<ProfileDocument?> documentProvider)
    {
        lock (gate)
        {
            if (entries.TryGetValue(plateId, out var cached) && cached.Key == versionKey)
            {
                return cached.Thumbnail;
            }

            var thumbnail = ResolveFromDisk(plateId, versionKey);
            if (thumbnail.State == PlateThumbnailState.Missing && generator is not null)
            {
                thumbnail = new PlateThumbnail(PlateThumbnailState.Generating, null);
                entries[plateId] = new Entry(versionKey, thumbnail);
                // Off the caller's thread entirely: the caller (ImGui Draw) never waits on it.
                _ = Task.Run(() => GenerateAsync(plateId, versionKey, documentProvider));
                return thumbnail;
            }

            entries[plateId] = new Entry(versionKey, thumbnail);
            return thumbnail;
        }
    }

    /// <summary>A Ready thumbnail's image couldn't be displayed (e.g. a corrupt file): fall back
    /// for this version rather than retrying every frame.</summary>
    internal void ReportDisplayFailure(Guid plateId, string reason)
    {
        lock (gate)
        {
            if (entries.TryGetValue(plateId, out var entry) && entry.Thumbnail.State == PlateThumbnailState.Ready)
            {
                entries[plateId] = entry with { Thumbnail = new PlateThumbnail(PlateThumbnailState.Failed, null) };
                log.Warning($"AetherFrame could not display the thumbnail for Plate {plateId}: {reason}");
            }
        }
    }

    /// <summary>Forgets what's known about a Plate's thumbnail (e.g. after it's saved).</summary>
    internal void Invalidate(Guid plateId)
    {
        lock (gate)
        {
            entries.Remove(plateId);
        }
    }

    /// <summary>Forgets and deletes a Plate's derived thumbnail files (e.g. after it's deleted).</summary>
    internal void Remove(Guid plateId)
    {
        Invalidate(plateId);
        TryDelete(GetImagePath(plateId));
        TryDelete(GetKeyPath(plateId));
    }

    public void Dispose()
    {
        shutdown.Cancel();
        shutdown.Dispose();
    }

    private PlateThumbnail ResolveFromDisk(Guid plateId, string versionKey)
    {
        try
        {
            var imagePath = GetImagePath(plateId);
            var keyPath = GetKeyPath(plateId);
            if (File.Exists(imagePath) && File.Exists(keyPath) && File.ReadAllText(keyPath, Encoding.UTF8).Trim() == versionKey)
            {
                return new PlateThumbnail(PlateThumbnailState.Ready, imagePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not check the thumbnail for Plate {plateId}: {ex.Message}");
        }

        return new PlateThumbnail(PlateThumbnailState.Missing, null);
    }

    private async Task GenerateAsync(Guid plateId, string versionKey, Func<ProfileDocument?> documentProvider)
    {
        CancellationToken token;
        try
        {
            token = shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var result = new PlateThumbnail(PlateThumbnailState.Failed, null);

        try
        {
            await generationSlot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var document = documentProvider() ?? throw new InvalidOperationException("The Plate is not available.");
                Directory.CreateDirectory(directory);

                var imagePath = GetImagePath(plateId);
                var temporaryPath = imagePath + ".tmp";
                await generator!.GenerateAsync(document, temporaryPath, token).ConfigureAwait(false);

                File.Move(temporaryPath, imagePath, overwrite: true);
                SystemFileStore.WriteAtomically(GetKeyPath(plateId), Encoding.UTF8.GetBytes(versionKey));
                result = new PlateThumbnail(PlateThumbnailState.Ready, imagePath);
            }
            finally
            {
                generationSlot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            log.Warning($"AetherFrame could not generate a thumbnail for Plate {plateId}: {ex.Message}");
        }

        lock (gate)
        {
            // Only if nothing newer was asked for in the meantime.
            if (entries.TryGetValue(plateId, out var entry) && entry.Key == versionKey)
            {
                entries[plateId] = entry with { Thumbnail = result };
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not remove a thumbnail file: {ex.Message}");
        }
    }

    private sealed record Entry(string Key, PlateThumbnail Thumbnail);
}
