using System;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// The order unloading follows once no window, command or menu can start anything new: first the
/// owned file operations already running are allowed to finish (bounded by a timeout), and only
/// then are the services they use disposed.
/// </summary>
internal static class PluginShutdown
{
    internal static async Task RunAsync(OwnedOperations operations, TimeSpan timeout, Action disposeServices, IAetherFrameLog log)
    {
        if (!await operations.ShutdownAsync(timeout).ConfigureAwait(false))
        {
            // Every Plate, binding, index and Template write is an atomic replacement: one that is
            // still running either lands whole or not at all.
            log.Warning($"AetherFrame stopped waiting after {timeout.TotalSeconds:0.#}s for {operations.RunningCount} file operation(s) to finish while unloading.");
        }

        disposeServices();
    }
}
