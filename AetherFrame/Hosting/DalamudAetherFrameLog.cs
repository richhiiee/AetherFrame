using System;
using AetherFrame.Services.Diagnostics;
using Dalamud.Plugin.Services;

namespace AetherFrame.Hosting;

/// <summary><see cref="IAetherFrameLog"/> over Dalamud's plugin log.</summary>
internal sealed class DalamudAetherFrameLog : IAetherFrameLog
{
    private readonly IPluginLog log;

    internal DalamudAetherFrameLog(IPluginLog log)
    {
        this.log = log;
    }

    public void Information(string message) => log.Information(message);

    public void Warning(string message) => log.Warning(message);

    public void Error(Exception? exception, string message)
    {
        if (exception is null)
        {
            log.Error(message);
        }
        else
        {
            log.Error(exception, message);
        }
    }
}
