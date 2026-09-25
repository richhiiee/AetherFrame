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
    [InlineData("viewer")]
    [InlineData("view extra")]
    [InlineData("edit")]
    public void UnrecognizedArguments_BehaveLikeThePlainCommand(string arguments) =>
        Assert.Equal(AetherFrameCommandAction.ToggleMyPlates, AetherFrameCommand.Parse(arguments));
}
