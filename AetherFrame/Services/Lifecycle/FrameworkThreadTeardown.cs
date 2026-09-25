using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// Runs a piece of unload teardown on the framework thread, exactly once.
///
/// <para>Dalamud calls an async plugin's <c>DisposeAsync</c> directly, on whatever thread is
/// unloading it (sync plugins' <c>Dispose</c> is the one it moves to the framework thread), yet
/// <c>WindowSystem</c> and the plugin's windows and caches are only ever used by <c>Draw</c>, on
/// the game's main thread — which is also the framework thread, and runs a tick and a Draw never at
/// the same time. So teardown touching them is handed to the framework thread
/// (<paramref name="runOnFrameworkThread"/>, i.e. <c>IFramework.RunOnFrameworkThread</c>, which runs
/// it at once when already there).</para>
///
/// <para>It never waits on a framework thread that can't come: while the game is closing
/// (<paramref name="frameworkUnloading"/>) ticks may no longer run, and a main thread that hasn't
/// picked the work up within <paramref name="timeout"/> is stalled — neither is drawing — so the
/// teardown then runs where it is. If the framework thread started it meanwhile, that run is
/// waited for instead; it is never run twice.</para>
/// </summary>
internal static class FrameworkThreadTeardown
{
    /// <summary>How long a tick may take to pick up teardown before it runs where it is.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(
        string name, Action teardown, Func<Action, Task> runOnFrameworkThread, Func<bool> frameworkUnloading, TimeSpan timeout, IAetherFrameLog log)
    {
        var once = new Once(teardown);

        if (frameworkUnloading())
        {
            await RunHereAsync(name, once, log).ConfigureAwait(false);
            return;
        }

        Task scheduled;
        try
        {
            scheduled = runOnFrameworkThread(once.Run);
        }
        catch (Exception ex)
        {
            log.Warning($"AetherFrame could not schedule {name} on the framework thread ({ex.GetType().Name}); running it now.");
            await RunHereAsync(name, once, log).ConfigureAwait(false);
            return;
        }

        using var timer = new CancellationTokenSource();
        var finished = await Task.WhenAny(scheduled, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);
        timer.Cancel();

        if (finished == scheduled && once.Completion.IsCompleted)
        {
            Observe(name, once.Completion, log);
            return;
        }

        // Not run by a tick in time (or canceled because the framework is going away).
        if (finished != scheduled)
        {
            log.Warning($"AetherFrame's {name} wasn't picked up by the framework thread within {timeout.TotalSeconds:0.#}s; running it now.");
        }

        await RunHereAsync(name, once, log).ConfigureAwait(false);
    }

    private static async Task RunHereAsync(string name, Once once, IAetherFrameLog log)
    {
        // Already started on the framework thread: wait for that run rather than racing it.
        once.TryRun();
        try
        {
            await once.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observed below.
        }

        Observe(name, once.Completion, log);
    }

    private static void Observe(string name, Task completion, IAetherFrameLog log)
    {
        if (completion.IsFaulted)
        {
            log.Error(completion.Exception?.GetBaseException(), $"AetherFrame's {name} failed.");
        }
    }

    /// <summary>The teardown, run by whichever caller gets there first.</summary>
    private sealed class Once(Action action)
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;

        internal Task Completion => done.Task;

        /// <summary>For the framework thread: failures are reported through <see cref="Completion"/>.</summary>
        internal void Run() => TryRun();

        internal bool TryRun()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                return false;
            }

            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }

            return true;
        }
    }
}
