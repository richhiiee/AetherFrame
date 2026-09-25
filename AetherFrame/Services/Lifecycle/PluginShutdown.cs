using System;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// The order unloading follows once no window, command or menu can start anything new:
/// <list type="number">
/// <item>the owned file operations already running are allowed to finish, for at most the timeout
/// (after which they are abandoned — see <see cref="OwnedOperations"/>);</item>
/// <item>what no owned operation uses (windows, textures, fonts) is disposed;</item>
/// <item>what owned operations do use (the Plate thumbnail service their save and delete events
/// call into) is disposed only once every operation has ended — immediately in the normal case,
/// or later, when an abandoned operation finally ends.</item>
/// </list>
/// </summary>
internal static class PluginShutdown
{
    internal static async Task RunAsync(
        OwnedOperations operations, TimeSpan timeout, Func<Task> disposeUnusedByOperations, Action disposeUsedByOperations, IAetherFrameLog log)
    {
        var drained = await operations.ShutdownAsync(timeout).ConfigureAwait(false);
        if (!drained)
        {
            log.Warning(
                $"AetherFrame stopped waiting after {timeout.TotalSeconds:0.#}s for {operations.RunningCount} file operation(s) while unloading; "
                + "each stops before its next file step, and what it uses is kept until it ends.");
        }

        await disposeUnusedByOperations().ConfigureAwait(false);

        if (drained)
        {
            DisposeUsed(disposeUsedByOperations, log);
            return;
        }

        _ = operations.Drained.ContinueWith(_ => DisposeUsed(disposeUsedByOperations, log), TaskScheduler.Default);
    }

    private static void DisposeUsed(Action disposeUsedByOperations, IAetherFrameLog log)
    {
        try
        {
            disposeUsedByOperations();
        }
        catch (Exception ex)
        {
            log.Error(ex, "AetherFrame could not dispose what its file operations used.");
        }
    }
}
