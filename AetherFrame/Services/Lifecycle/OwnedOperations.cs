using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// The plugin's record of the work it owns that writes files — Plate Library and Template Library
/// operations, package import and export — so unloading can let that work finish before the
/// services it depends on (Dalamud's reliable file storage and framework thread, the thumbnail
/// cache, …) are disposed.
///
/// <para>An operation registers with <see cref="TryBegin"/> at the moment it actually starts and
/// disposes its <see cref="Lease"/> when it ends, however it ends. <see cref="ShutdownAsync"/> then:
/// refuses every new operation, signals <see cref="Stopping"/> (so work that hasn't started yet —
/// such as an operation still queued behind another — gives up instead of starting), and waits for
/// the operations already running, but never longer than its timeout: the file writes themselves
/// are atomic replacements, so the worst an abandoned write can do is not happen.</para>
///
/// <para>Thread-safe; operations may begin and end on any thread.</para>
/// </summary>
internal sealed class OwnedOperations
{
    /// <summary>How long unloading waits for running work before giving up on it.</summary>
    internal static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int running;
    private bool shuttingDown;

    /// <summary>Signaled when shutdown begins: work that hasn't started must not start.</summary>
    internal CancellationToken Stopping => stopping.Token;

    /// <summary>True once <see cref="ShutdownAsync"/> has been called; no operation can begin after that.</summary>
    internal bool IsShuttingDown
    {
        get { lock (gate) return shuttingDown; }
    }

    /// <summary>Operations currently running.</summary>
    internal int RunningCount
    {
        get { lock (gate) return running; }
    }

    /// <summary>
    /// Registers an operation that is starting now. False (and no lease) once shutdown has begun:
    /// the caller must then not start at all.
    /// </summary>
    internal bool TryBegin([NotNullWhen(true)] out Lease? lease)
    {
        lock (gate)
        {
            if (shuttingDown)
            {
                lease = null;
                return false;
            }

            running++;
        }

        lease = new Lease(this);
        return true;
    }

    /// <summary>
    /// Begins shutdown and waits for the operations already running to end (successfully or not).
    /// True when they all did; false when <paramref name="timeout"/> ran out first. Safe to call
    /// more than once; never throws.
    /// </summary>
    internal async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        lock (gate)
        {
            shuttingDown = true;
            if (running == 0)
            {
                drained.TrySetResult();
            }
        }

        // Outside the lock: cancellation callbacks (e.g. an operation waiting its turn) run inline.
        stopping.Cancel();

        if (drained.Task.IsCompleted)
        {
            return true;
        }

        using var timer = new CancellationTokenSource();
        var finished = await Task.WhenAny(drained.Task, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);
        timer.Cancel();
        return finished == drained.Task;
    }

    private void End()
    {
        lock (gate)
        {
            running--;
            if (running == 0 && shuttingDown)
            {
                drained.TrySetResult();
            }
        }
    }

    /// <summary>One running operation; disposing it (once or more) ends it.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly OwnedOperations owner;
        private int ended;

        internal Lease(OwnedOperations owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                owner.End();
            }
        }
    }
}
