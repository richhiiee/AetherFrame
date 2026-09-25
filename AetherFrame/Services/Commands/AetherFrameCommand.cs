using System;
using System.Collections.Generic;

namespace AetherFrame.Services.Commands;

internal enum AetherFrameCommandAction
{
    /// <summary>Plain <c>/aetherframe</c> (and any argument it doesn't recognize): toggles My Plates, as always.</summary>
    ToggleMyPlates,

    /// <summary><c>/aetherframe view</c>: opens the Plate Viewer on the character's Active Plate.</summary>
    ViewActivePlate,

    /// <summary><c>/aetherframe version</c>: reports which AetherFrame build is running.</summary>
    ShowVersion,
}

/// <summary>
/// The AetherFrame chat command: its names and what each argument does. Deliberately small.
/// <c>/aetherframe</c> is canonical and <c>/af</c> is its short alias; both are registered with
/// the same handler, so they share one argument parser and every subcommand works through either.
/// </summary>
internal static class AetherFrameCommand
{
    internal const string Name = "/aetherframe";

    internal const string Alias = "/af";

    internal const string ViewArgument = "view";

    internal const string VersionArgument = "version";

    /// <summary>Every name the command is registered under, canonical first.</summary>
    internal static readonly IReadOnlyList<string> Names = [Name, Alias];

    internal const string HelpMessage = "Opens My Plates (short form: /af). /aetherframe view or /af view shows your Active Plate.";

    internal const string AliasHelpMessage = "Short for /aetherframe: opens My Plates. /af view shows your Active Plate.";

    /// <summary>The help text shown for one registered name.</summary>
    internal static string HelpFor(string commandName) => commandName == Alias ? AliasHelpMessage : HelpMessage;

    internal static AetherFrameCommandAction Parse(string? arguments)
    {
        var argument = arguments?.Trim();
        if (string.Equals(argument, ViewArgument, StringComparison.OrdinalIgnoreCase))
        {
            return AetherFrameCommandAction.ViewActivePlate;
        }

        return string.Equals(argument, VersionArgument, StringComparison.OrdinalIgnoreCase)
            ? AetherFrameCommandAction.ShowVersion
            : AetherFrameCommandAction.ToggleMyPlates;
    }
}
