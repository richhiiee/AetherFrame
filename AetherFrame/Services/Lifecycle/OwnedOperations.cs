using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// The plugin's record of the work it owns that writes files — Plate Library and Template Library
/// operations, package import and export — so unloading can let that work finish before the
/// services it depends on (Dalamud's reliable file storage, the thumbnail cache, …) go away.
///
/// <para>An operation registers with <see cref="TryBegin"/> once it has taken its turn and disposes
/// its <see cref="Lease"/> when it ends, however it ends. <see cref="ShutdownAsync"/> then refuses
/// every new operation, signals <see cref="Stopping"/> (so work still waiting its turn gives up
/// instead of starting), and waits for the registered operations to end.</para>
///
/// <para><b>When the wait times out</b> the operations still registered are <i>abandoned</i>
/// (<see cref="IsAbandoned"/>), because unloading must not hang and, once it returns, Dalamud
/// disposes the plugin's services. An abandoned operation never starts if it hadn't yet (see
/// <see cref="ThrowIfAbandoned"/>), and one that is running stops before its next file step (see
/// <see cref="ShutdownGuardedFileStore"/>): every file step is an atomic replacement or move, so
/// the operation is left exactly as if the game had closed between two of its files — the case
/// the Libraries' write ordering is built to recover from on the next load. The step already under
/// way completes. What an abandoned operation still uses of AetherFrame's own is only disposed
/// once it ends (see <see cref="Drained"/>).</para>
///
/// <para>Thread-safe; operations may begin and end on any thread.</para>
/// </summary>
internal sealed class OwnedOperations
{
    /// <summary>How long unloading waits for running work before abandoning it.</summary>
    internal static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int running;
    private bool shuttingDown;
    private volatile bool abandoned;

    /// <summary>Signaled when shutdown begins: work that hasn't taken its turn must not start.</summary>
    internal CancellationToken Stopping => stopping.Token;

    /// <summary>True once <see cref="ShutdownAsync"/> has been called; no operation can begin after that.</summary>
    internal bool IsShuttingDown
    {
        get { lock (gate) return shuttingDown; }
    }

    /// <summary>True once shutdown stopped waiting for the operations still running (see class remarks).</summary>
    internal bool IsAbandoned => abandoned;

    /// <summary>Operations currently registered.</summary>
    internal int RunningCount
    {
        get { lock (gate) return running; }
    }

    /// <summary>Completes once shutdown has begun and no operation is registered any more — even
    /// when that happens only after <see cref="ShutdownAsync"/> timed out.</summary>
    internal Task Drained => drained.Task;

    /// <summary>
    /// Registers an operation that has taken its turn. False (and no lease) once shutdown has
    /// begun: the caller must then not start at all.
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
    /// Called by a registered operation before each step that touches files or the plugin's
    /// services (including its very first): throws <see cref="OperationAbandonedException"/> once
    /// the operation has been abandoned, so it never takes another step.
    /// </summary>
    internal void ThrowIfAbandoned()
    {
        if (abandoned)
        {
            throw new OperationAbandonedException();
        }
    }

    /// <summary>
    /// Begins shutdown and waits for the registered operations to end (successfully or not).
    /// True when they all did; false when <paramref name="timeout"/> ran out first, in which case
    /// they are abandoned before this returns. Safe to call more than once; never throws.
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
        if (finished == drained.Task)
        {
            return true;
        }

        abandoned = true;
        return drained.Task.IsCompleted;
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

    /// <summary>One registered operation; disposing it (once or more) ends it.</summary>
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

/// <summary>An operation stopped, before its next file step, because unloading stopped waiting for it.</summary>
internal sealed class OperationAbandonedException : Exception
{
    internal OperationAbandonedException()
        : base("AetherFrame was unloading, so this was stopped before its next step.")
    {
    }
}
