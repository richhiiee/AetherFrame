using System;

namespace AetherFrame.Services.Diagnostics;

/// <summary>Logging for the Dalamud-independent core (Dalamud's IPluginLog in game).</summary>
internal interface IAetherFrameLog
{
    void Information(string message);

    void Warning(string message);

    void Error(Exception? exception, string message);
}

/// <summary>Discards everything; the default when no log is supplied.</summary>
internal sealed class NullAetherFrameLog : IAetherFrameLog
{
    internal static readonly NullAetherFrameLog Instance = new();

    public void Information(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void Error(Exception? exception, string message)
    {
    }
}
