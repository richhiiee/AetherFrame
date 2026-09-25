using System;

namespace AetherFrame.Services.Commands;

/// <summary>
/// The callback registered with Dalamud for <c>/aetherframe</c>. <see cref="Handle"/> has the
/// shape of Dalamud's <c>CommandInfo.HandlerDelegate</c>: Dalamud splits the typed line at the
/// first space and passes the command name (<c>"/aetherframe"</c>) and everything after that space
/// (<c>"view"</c>, <c>"   view"</c>, or <c>""</c>) separately. Routing is decided by the arguments
/// alone; the command name is never parsed.
/// </summary>
internal sealed class AetherFrameCommandHandler
{
    private readonly Action toggleMyPlates;
    private readonly Action openActivePlateViewer;
    private readonly Action showVersion;

    internal AetherFrameCommandHandler(Action toggleMyPlates, Action openActivePlateViewer, Action showVersion)
    {
        this.toggleMyPlates = toggleMyPlates;
        this.openActivePlateViewer = openActivePlateViewer;
        this.showVersion = showVersion;
    }

    internal void Handle(string command, string arguments)
    {
        switch (AetherFrameCommand.Parse(arguments))
        {
            case AetherFrameCommandAction.ViewActivePlate:
                openActivePlateViewer();
                break;
            case AetherFrameCommandAction.ShowVersion:
                showVersion();
                break;
            default:
                toggleMyPlates();
                break;
        }
    }
}
