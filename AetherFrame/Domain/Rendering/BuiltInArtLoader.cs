using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Rendering;

/// <summary>One artwork's loaded levels (largest first, as <see cref="BundledArtImage.BuildLevels"/>
/// makes them) and each level's longer side, for <see cref="BundledArtImage.SelectLevel"/>.</summary>
public sealed record LoadedArt<TTexture>(IReadOnlyList<TTexture> Levels, int[] LongSides);

/// <summary>
/// Loads each bundled artwork at most once per lifetime, lazily and off the draw thread; the draw
/// thread only ever looks up what is ready. Pure scheduling (no Dalamud: the texture type and the
/// load are supplied), so the rules are tested.
///
/// <para><b>Why.</b> Decoding a full-resolution Celestial Sakura piece and preparing its levels
/// takes ~45–55 ms of CPU, plus an 8 MB upload; done inside Draw on first use, one Plate showing
/// the whole family stalled the UI for hundreds of milliseconds. Now the first request starts the
/// load on the thread pool and returns null (the artwork isn't drawn yet, like a user image still
/// loading), and a later frame draws it.</para>
///
/// <para><b>Lifetime.</b> Every texture a load produces is disposed exactly once: by
/// <see cref="Dispose"/> when the load has finished, or as soon as it finishes when it is still
/// running then (it is cancelled too, so it usually stops early). A failed load is not retried —
/// the load reports it — and the artwork is simply not drawn.</para>
/// </summary>
public sealed class BuiltInArtLoader<TTexture> : IDisposable
    where TTexture : class, IDisposable
{
    private readonly Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load;
    private readonly Dictionary<string, Task<LoadedArt<TTexture>>> loads = new(StringComparer.Ordinal);

    // Never disposed: a load still running when this is disposed keeps reading its token.
    private readonly CancellationTokenSource disposing = new();
    private bool disposed;

    /// <param name="load">Decodes and uploads one artwork. Always started on the thread pool, never on
    /// the caller's thread; may throw (the artwork is then never drawn).</param>
    public BuiltInArtLoader(Func<BuiltInArtAsset, CancellationToken, Task<LoadedArt<TTexture>>> load)
    {
        this.load = load;
    }

    /// <summary>How many loads have been started (at most one per artwork).</summary>
    public int LoadsStarted { get; private set; }

    /// <summary>
    /// Draw thread: the level of <paramref name="art"/> to draw <paramref name="screenPixels"/> across,
    /// or null while it is loading, after it failed, or once disposed. Never blocks and never touches
    /// pixels: the first call starts the one load, every later call is a dictionary lookup.
    /// </summary>
    public TTexture? GetLevelOrNull(BuiltInArtAsset art, float screenPixels)
    {
        if (disposed)
        {
            return null;
        }

        if (!loads.TryGetValue(art.Id, out var task))
        {
            var token = disposing.Token;
            task = Task.Run(() => load(art, token), token);
            task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // reported by the load; never retried
            loads.Add(art.Id, task);
            LoadsStarted++;
        }

        if (!task.IsCompletedSuccessfully)
        {
            return null;
        }

        var loaded = task.Result;
        return loaded.Levels.Count == 0 ? null : loaded.Levels[BundledArtImage.SelectLevel(loaded.LongSides, screenPixels)];
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposing.Cancel();
        foreach (var task in loads.Values)
        {
            // Exactly one of the two runs per load, so every texture is released exactly once.
            if (task.IsCompleted)
            {
                Release(task);
            }
            else
            {
                task.ContinueWith(Release, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        loads.Clear();
    }

    private static void Release(Task<LoadedArt<TTexture>> task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            return;
        }

        foreach (var level in task.Result.Levels)
        {
            level.Dispose();
        }
    }
}
