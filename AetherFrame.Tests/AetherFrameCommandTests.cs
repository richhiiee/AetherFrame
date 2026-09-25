using AetherFrame.Services.Commands;
using Xunit;

namespace AetherFrame.Tests;

public class AetherFrameCommandTests
{
    [Fact]
    public void CommandName_IsUnchanged() => Assert.Equal("/aetherframe", AetherFrameCommand.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PlainCommand_StillTogglesMyPlates(string? arguments) =>
        Assert.Equal(AetherFrameCommandAction.ToggleMyPlates, AetherFrameCommand.Parse(arguments));

    [Theory]
    [InlineData("view")]
    [InlineData("VIEW")]
    [InlineData("  View  ")]
    public void View_RoutesToTheActivePlateViewer(string arguments) =>
        Assert.Equal(AetherFrameCommandAction.ViewActivePlate, AetherFrameCommand.Parse(arguments));

    [Theory]
    [InlineData("version")]
    [InlineData("VERSION")]
    [InlineData("  Version  ")]
    public void Version_RoutesToTheVersionReport(string arguments) =>
        Assert.Equal(AetherFrameCommandAction.ShowVersion, AetherFrameCommand.Parse(arguments));

    [Theory]
    [InlineData("viewer")]
    [InlineData("view extra")]
    [InlineData("edit")]
    [InlineData("versions")]
    [InlineData("version extra")]
    public void UnrecognizedArguments_BehaveLikeThePlainCommand(string arguments) =>
        Assert.Equal(AetherFrameCommandAction.ToggleMyPlates, AetherFrameCommand.Parse(arguments));
}

/// <summary>
/// The callback Dalamud actually invokes, driven the way Dalamud drives it: the command name and
/// the text after the first space arrive as separate arguments.
/// </summary>
public class AetherFrameCommandHandlerTests
{
    private sealed class Calls
    {
        internal int ToggleMyPlates { get; private set; }

        internal int OpenActivePlateViewer { get; private set; }

        internal int ShowVersion { get; private set; }

        internal AetherFrameCommandHandler Handler() => new(() => ToggleMyPlates++, () => OpenActivePlateViewer++, () => ShowVersion++);
    }

    [Fact]
    public void Version_ReportsTheBuild_AndOpensNothing()
    {
        var calls = new Calls();

        calls.Handler().Handle("/aetherframe", " version ");

        Assert.Equal((0, 0, 1), (calls.ToggleMyPlates, calls.OpenActivePlateViewer, calls.ShowVersion));
    }

    [Fact]
    public void View_OpensTheActivePlateViewer_AndNeverTogglesMyPlates()
    {
        var calls = new Calls();

        calls.Handler().Handle("/aetherframe", "view");

        Assert.Equal(1, calls.OpenActivePlateViewer);
        Assert.Equal(0, calls.ToggleMyPlates);
    }

    [Fact]
    public void NoArguments_TogglesMyPlates_AndNeverOpensTheViewer()
    {
        var calls = new Calls();

        calls.Handler().Handle("/aetherframe", "");

        Assert.Equal(1, calls.ToggleMyPlates);
        Assert.Equal(0, calls.OpenActivePlateViewer);
    }

    // "/aetherframe VIEW", "/aetherframe    view" (Dalamud keeps the extra spaces after the first
    // one), a trailing space, and a tab.
    [Theory]
    [InlineData("VIEW")]
    [InlineData("   view")]
    [InlineData("view ")]
    [InlineData("\tview")]
    public void ViewVariants_OpenTheActivePlateViewer(string arguments)
    {
        var calls = new Calls();

        calls.Handler().Handle("/aetherframe", arguments);

        Assert.Equal(1, calls.OpenActivePlateViewer);
        Assert.Equal(0, calls.ToggleMyPlates);
    }

    [Fact]
    public void UnknownArguments_KeepThePlainCommandsFallback()
    {
        var calls = new Calls();

        calls.Handler().Handle("/aetherframe", "settings");

        Assert.Equal(1, calls.ToggleMyPlates);
        Assert.Equal(0, calls.OpenActivePlateViewer);
    }

    [Fact]
    public void Routing_ComesFromTheArguments_NotTheCommandName()
    {
        var calls = new Calls();
        var handler = calls.Handler();

        handler.Handle("view", "");
        Assert.Equal((1, 0), (calls.ToggleMyPlates, calls.OpenActivePlateViewer));

        handler.Handle("/aetherframe view", "view");
        Assert.Equal((1, 1), (calls.ToggleMyPlates, calls.OpenActivePlateViewer));
    }

    [Fact]
    public void RepeatedView_OpensTheViewerEachTime_WithoutEverTogglingMyPlates()
    {
        var calls = new Calls();
        var handler = calls.Handler();

        handler.Handle("/aetherframe", "view");
        handler.Handle("/aetherframe", "view");

        Assert.Equal(2, calls.OpenActivePlateViewer);
        Assert.Equal(0, calls.ToggleMyPlates);
    }
}
