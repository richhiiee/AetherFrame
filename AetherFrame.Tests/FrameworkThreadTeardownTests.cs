using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Services.Lifecycle;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Unload teardown that touches windows and textures runs on the framework thread (where Draw runs)
/// whenever that thread can take it, exactly once, and never waits on a framework thread that
/// isn't coming.
/// </summary>
public class FrameworkThreadTeardownTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunsOnTheFrameworkThread_WhenItPicksTheWorkUp()
    {
        var framework = new FakeFrameworkThread();
        var ranOnFramework = false;

        var teardown = FrameworkThreadTeardown.RunAsync(
            "teardown", () => ranOnFramework = framework.IsCurrent, framework.RunOnFrameworkThread, () => false, Generous, new TestLog());
        Assert.False(teardown.IsCompleted);

        framework.Tick();
        await teardown.WaitAsync(Generous);

        Assert.True(ranOnFramework);
    }

    [Fact]
    public async Task AlreadyOnTheFrameworkThread_RunsAtOnce()
    {
        var runs = 0;

        // IFramework.RunOnFrameworkThread runs inline when called on that thread.
        var teardown = FrameworkThreadTeardown.RunAsync(
            "teardown", () => runs++, action => { action(); return Task.CompletedTask; }, () => false, Generous, new TestLog());

        Assert.True(teardown.IsCompleted);
        await teardown;
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task WhileTheGameIsClosing_RunsWhereItIs_WithoutWaitingForATick()
    {
        var scheduled = false;
        var runs = 0;

        await FrameworkThreadTeardown.RunAsync(
            "teardown", () => runs++, _ => { scheduled = true; return new TaskCompletionSource().Task; }, () => true, Generous, new TestLog())
            .WaitAsync(Generous);

        Assert.False(scheduled);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task NoTickInTime_RunsWhereItIs_Once_EvenIfATickComesLater()
    {
        var framework = new FakeFrameworkThread();
        var runs = 0;
        var log = new TestLog();

        await FrameworkThreadTeardown.RunAsync(
            "teardown", () => Interlocked.Increment(ref runs), framework.RunOnFrameworkThread, () => false, TimeSpan.FromMilliseconds(50), log)
            .WaitAsync(Generous);

        Assert.Equal(1, runs);
        Assert.Contains(log.Messages, m => m.Contains("wasn't picked up"));

        framework.Tick();
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task StartedByTheFrameworkThread_ButSlow_IsWaitedFor_NotRunTwice()
    {
        var release = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var runs = 0;
        Action? onFramework = null;

        var teardown = FrameworkThreadTeardown.RunAsync(
            "teardown",
            () =>
            {
                Interlocked.Increment(ref runs);
                started.Set();
                release.Wait(Generous);
            },
            action =>
            {
                onFramework = action;
                return Task.Run(action);
            },
            () => false,
            TimeSpan.FromMilliseconds(50),
            new TestLog());

        Assert.True(started.Wait(Generous));
        await Task.Delay(150);
        Assert.False(teardown.IsCompleted);

        release.Set();
        await teardown.WaitAsync(Generous);
        Assert.Equal(1, runs);
        Assert.NotNull(onFramework);
    }

    [Fact]
    public async Task CanceledBeforeItRan_RunsWhereItIs()
    {
        var runs = 0;

        // The framework going away cancels queued work without running it.
        await FrameworkThreadTeardown.RunAsync(
            "teardown", () => runs++, _ => Task.FromCanceled(new CancellationToken(true)), () => false, Generous, new TestLog())
            .WaitAsync(Generous);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task SchedulingThatThrows_RunsWhereItIs()
    {
        var runs = 0;

        await FrameworkThreadTeardown.RunAsync(
            "teardown", () => runs++, _ => throw new ObjectDisposedException("framework"), () => false, Generous, new TestLog())
            .WaitAsync(Generous);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task FailingTeardown_IsLogged_NotThrown()
    {
        var log = new TestLog();

        await FrameworkThreadTeardown.RunAsync(
            "teardown", () => throw new InvalidOperationException("boom"), action => { action(); return Task.CompletedTask; }, () => false, Generous, log)
            .WaitAsync(Generous);

        Assert.Contains(log.Messages, m => m.Contains("failed"));
    }

    /// <summary>A framework thread that runs queued work only on <see cref="Tick"/>.</summary>
    private sealed class FakeFrameworkThread
    {
        private readonly object gate = new();
        private Action? queued;
        private TaskCompletionSource? completion;

        [ThreadStatic]
        private static bool onFrameworkThread;

        internal bool IsCurrent => onFrameworkThread;

        internal Task RunOnFrameworkThread(Action action)
        {
            lock (gate)
            {
                queued = action;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return completion.Task;
            }
        }

        internal void Tick()
        {
            Action? action;
            TaskCompletionSource? done;
            lock (gate)
            {
                (action, done, queued, completion) = (queued, completion, null, null);
            }

            onFrameworkThread = true;
            try
            {
                action?.Invoke();
                done?.TrySetResult();
            }
            finally
            {
                onFrameworkThread = false;
            }
        }
    }
}
