using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Services.Commands;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// <c>/aetherframe</c> and its <c>/af</c> alias: one handler registered under both names, so both
/// share one parser and one routing path. Commands are invoked through the exact delegates handed to
/// the (fake) command service, the way Dalamud invokes them.
/// </summary>
public class AetherFrameCommandAliasTests
{
    /// <summary>Records what was registered; can refuse or throw for chosen names, like a name
    /// another plugin already owns.</summary>
    private sealed class FakeRegistrar : ICommandRegistrar
    {
        internal HashSet<string> Refuse { get; } = new();

        internal HashSet<string> Throw { get; } = new();

        internal List<string> Attempted { get; } = new();

        internal List<string> Removed { get; } = new();

        internal Dictionary<string, (string Help, Action<string, string> Handler)> Commands { get; } = new();

        public bool Add(string command, string helpMessage, Action<string, string> handler)
        {
            Attempted.Add(command);
            if (Throw.Contains(command))
            {
                throw new InvalidOperationException("Command service rejected the command.");
            }

            if (Refuse.Contains(command) || Commands.ContainsKey(command))
            {
                return false;
            }

            Commands[command] = (helpMessage, handler);
            return true;
        }

        public bool Remove(string command)
        {
            Removed.Add(command);
            return Commands.Remove(command);
        }

        /// <summary>What Dalamud does for a typed line: split at the first space, call the handler.</summary>
        internal void Type(string line)
        {
            var space = line.IndexOf(' ');
            var command = space < 0 ? line : line[..space];
            var arguments = space < 0 ? string.Empty : line[(space + 1)..];
            Commands[command].Handler(command, arguments);
        }
    }

    private sealed class Harness
    {
        internal FakeRegistrar Registrar { get; } = new();

        internal TestLog Log { get; } = new();

        internal int Toggles { get; private set; }

        internal int Views { get; private set; }

        internal int Versions { get; private set; }

        internal AetherFrameCommandHandler Handler { get; }

        internal AetherFrameCommandRegistration Registration { get; }

        internal Harness(Action<FakeRegistrar>? configure = null)
        {
            configure?.Invoke(Registrar);
            Handler = new AetherFrameCommandHandler(() => Toggles++, () => Views++, () => Versions++);
            Registration = new AetherFrameCommandRegistration(Registrar, Log);
            Registration.Register(Handler);
        }

        internal (int Toggles, int Views) Type(string line)
        {
            var before = (Toggles, Views);
            Registrar.Type(line);
            return (Toggles - before.Toggles, Views - before.Views);
        }
    }

    private static readonly (int, int) TogglesMyPlates = (1, 0);
    private static readonly (int, int) OpensActiveViewer = (0, 1);

    [Fact]
    public void Names_AreTheCanonicalCommandThenTheAlias()
    {
        Assert.Equal(["/aetherframe", "/af"], AetherFrameCommand.Names);
        Assert.Equal("/af", AetherFrameCommand.Alias);
    }

    [Fact]
    public void BothNames_AreRegistered_OnTheSameHandlerInstance()
    {
        var harness = new Harness();

        Assert.Equal(["/aetherframe", "/af"], harness.Registration.Registered);
        Assert.Empty(harness.Registration.Unavailable);
        Assert.Same(harness.Handler, harness.Registrar.Commands["/aetherframe"].Handler.Target);
        Assert.Same(harness.Handler, harness.Registrar.Commands["/af"].Handler.Target);
        Assert.Equal(harness.Registrar.Commands["/aetherframe"].Handler.Method, harness.Registrar.Commands["/af"].Handler.Method);
        Assert.Empty(harness.Log.Messages);
    }

    [Theory]
    [InlineData("/aetherframe")]
    [InlineData("/af")]
    public void EmptyArguments_ToggleMyPlates(string command) =>
        Assert.Equal(TogglesMyPlates, new Harness().Type(command));

    [Theory]
    [InlineData("/aetherframe view")]
    [InlineData("/af view")]
    public void View_OpensTheActivePlateViewer(string line) =>
        Assert.Equal(OpensActiveViewer, new Harness().Type(line));

    // Capitalisation, extra spaces (Dalamud keeps everything after the first space), trailing
    // whitespace, unknown arguments, and a trailing bare space.
    [Theory]
    [InlineData("", 1, 0)]
    [InlineData(" ", 1, 0)]
    [InlineData(" view", 0, 1)]
    [InlineData(" VIEW", 0, 1)]
    [InlineData(" View", 0, 1)]
    [InlineData("    view", 0, 1)]
    [InlineData(" view ", 0, 1)]
    [InlineData(" \tview", 0, 1)]
    [InlineData(" viewer", 1, 0)]
    [InlineData(" view extra", 1, 0)]
    [InlineData(" settings", 1, 0)]
    public void BothNames_RouteEveryArgumentIdentically(string suffix, int toggles, int views)
    {
        var harness = new Harness();

        Assert.Equal((toggles, views), harness.Type("/aetherframe" + suffix));
        Assert.Equal((toggles, views), harness.Type("/af" + suffix));
    }

    [Theory]
    [InlineData(" version")]
    [InlineData(" VERSION")]
    [InlineData("   version ")]
    public void BothNames_ReportTheVersion_ThroughTheSameRoute(string suffix)
    {
        var harness = new Harness();

        Assert.Equal((0, 0), harness.Type("/aetherframe" + suffix));
        Assert.Equal(1, harness.Versions);
        Assert.Equal((0, 0), harness.Type("/af" + suffix));
        Assert.Equal(2, harness.Versions);
    }

    [Fact]
    public void HelpText_MakesTheAliasAndViewDiscoverable()
    {
        var harness = new Harness();
        var canonical = harness.Registrar.Commands["/aetherframe"].Help;
        var alias = harness.Registrar.Commands["/af"].Help;

        Assert.Equal(AetherFrameCommand.HelpMessage, canonical);
        Assert.Equal(AetherFrameCommand.AliasHelpMessage, alias);
        Assert.Contains("/af", canonical);
        Assert.Contains("/aetherframe view", canonical);
        Assert.Contains("/af view", canonical);
        Assert.Contains("/aetherframe", alias);
        Assert.Contains("/af view", alias);
    }

    [Fact]
    public void AliasAlreadyTaken_KeepsAetherFrameWorking_AndReportsTheConflict()
    {
        var harness = new Harness(r => r.Refuse.Add("/af"));

        Assert.Equal(["/aetherframe"], harness.Registration.Registered);
        Assert.Equal(["/af"], harness.Registration.Unavailable);
        var warning = Assert.Single(harness.Log.Messages);
        Assert.StartsWith("W ", warning);
        Assert.Contains("/af", warning);
        Assert.Contains("/aetherframe", warning);

        // No other alias is tried in its place, and /aetherframe still does everything.
        Assert.Equal(["/aetherframe", "/af"], harness.Registrar.Attempted);
        Assert.False(harness.Registrar.Commands.ContainsKey("/af"));
        Assert.Equal(TogglesMyPlates, harness.Type("/aetherframe"));
        Assert.Equal(OpensActiveViewer, harness.Type("/aetherframe view"));
    }

    [Fact]
    public void AliasRegistrationThrowing_NeverEscapes_AndIsReported()
    {
        var harness = new Harness(r => r.Throw.Add("/af"));

        Assert.Equal(["/aetherframe"], harness.Registration.Registered);
        Assert.Equal(["/af"], harness.Registration.Unavailable);
        Assert.StartsWith("E ", Assert.Single(harness.Log.Messages));
        Assert.Equal(OpensActiveViewer, harness.Type("/aetherframe view"));
    }

    [Fact]
    public void CanonicalNameTaken_IsReported_AndTheAliasStillRegisters()
    {
        var harness = new Harness(r => r.Refuse.Add("/aetherframe"));

        Assert.Equal(["/af"], harness.Registration.Registered);
        Assert.Equal(["/aetherframe"], harness.Registration.Unavailable);
        Assert.Contains("/aetherframe", Assert.Single(harness.Log.Messages));
        Assert.Equal(OpensActiveViewer, harness.Type("/af view"));
    }

    [Fact]
    public void Unregister_RemovesOnlyTheNamesAetherFrameRegistered()
    {
        var harness = new Harness(r => r.Refuse.Add("/af"));

        harness.Registration.Unregister();

        // Never removes /af: it belongs to whoever registered it.
        Assert.Equal(["/aetherframe"], harness.Registrar.Removed);
        Assert.Empty(harness.Registration.Registered);
    }

    [Fact]
    public void Unregister_RemovesBothNames_WhenBothWereRegistered()
    {
        var harness = new Harness();

        harness.Registration.Unregister();

        Assert.Equal(["/aetherframe", "/af"], harness.Registrar.Removed.ToList());
        Assert.Empty(harness.Registrar.Commands);
    }
}
