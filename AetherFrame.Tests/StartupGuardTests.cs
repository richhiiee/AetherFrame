using System;
using System.Collections.Generic;
using AetherFrame.Services.Lifecycle;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A constructor that fails part-way leaves nothing hooked up (Dalamud never disposes such a
/// plugin), and a damaged configuration never stops AetherFrame from starting.
/// </summary>
public class StartupGuardTests
{
    [Fact]
    public void RollBack_UndoesEveryStep_NewestFirst_EvenPastOneThatFails()
    {
        var log = new TestLog();
        var guard = new StartupGuard(log);
        var undone = new List<string>();
        guard.OnFailure("fonts", () => undone.Add("fonts"));
        guard.OnFailure("shortcuts", () => throw new InvalidOperationException("already gone"));
        guard.OnFailure("events", () => undone.Add("events"));

        guard.RollBack();
        guard.RollBack(); // nothing is undone twice

        Assert.Equal(["events", "fonts"], undone);
        Assert.Single(log.Messages, m => m.StartsWith("E AetherFrame could not undo shortcuts", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterComplete_NothingIsRolledBack()
    {
        var guard = new StartupGuard(new TestLog());
        var undone = false;
        guard.OnFailure("events", () => undone = true);

        guard.Complete();
        guard.RollBack();

        Assert.False(undone);
    }

    [Fact]
    public void TryLoad_AFailingLoad_IsLoggedAndReportedInsteadOfThrown()
    {
        var log = new TestLog();

        var loaded = StartupGuard.TryLoad<object>(() => throw new FormatException("damaged"), "its configuration", log, out var failed);

        Assert.Null(loaded);
        Assert.True(failed);
        Assert.Single(log.Messages, m => m.StartsWith("E AetherFrame could not read its configuration", StringComparison.Ordinal));

        Assert.NotNull(StartupGuard.TryLoad(() => new object(), "its configuration", log, out var failedAgain));
        Assert.False(failedAgain);
    }
}
