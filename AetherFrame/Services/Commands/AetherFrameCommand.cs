using System;

namespace AetherFrame.Services.Commands;

internal enum AetherFrameCommandAction
{
    /// <summary>Plain <c>/aetherframe</c> (and any argument it doesn't recognize): toggles My Plates, as always.</summary>
    ToggleMyPlates,

    /// <summary><c>/aetherframe view</c>: opens the Plate Viewer on the character's Active Plate.</summary>
    ViewActivePlate,
}

/// <summary>The <c>/aetherframe</c> chat command: what each argument does. Deliberately small.</summary>
internal static class AetherFrameCommand
{
    internal const string Name = "/aetherframe";

    internal const string ViewArgument = "view";

    internal const string HelpMessage = "Opens My Plates, your AetherFrame Plate collection. /aetherframe view shows your Active Plate.";

    internal static AetherFrameCommandAction Parse(string? arguments) =>
        string.Equals(arguments?.Trim(), ViewArgument, StringComparison.OrdinalIgnoreCase)
            ? AetherFrameCommandAction.ViewActivePlate
            : AetherFrameCommandAction.ToggleMyPlates;
}
