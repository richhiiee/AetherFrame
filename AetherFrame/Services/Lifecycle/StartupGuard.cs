using System;
using System.Collections.Generic;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// Undoes a constructor that fails part-way. Dalamud never disposes a plugin whose constructor
/// threw, so anything it already hooked up — a framework or login event, a font, a texture cache —
/// would otherwise outlive it, still firing into a half-built plugin (and, for commands, blocking
/// the next load). Each step is registered as soon as it takes effect; <see cref="RollBack"/> undoes
/// them newest first, every one attempted even when an earlier one fails. A constructor that
/// finishes calls <see cref="Complete"/>, after which normal disposal owns everything.
/// </summary>
internal sealed class StartupGuard
{
    private readonly Stack<(string Name, Action Undo)> steps = new();
    private readonly IAetherFrameLog log;

    internal StartupGuard(IAetherFrameLog log)
    {
        this.log = log;
    }

    /// <summary>Registers how to undo a step that has just taken effect.</summary>
    internal void OnFailure(string name, Action undo) => steps.Push((name, undo));

    /// <summary>The constructor finished: nothing will be rolled back.</summary>
    internal void Complete() => steps.Clear();

    /// <summary>Undoes every registered step, newest first. Never throws.</summary>
    internal void RollBack()
    {
        while (steps.TryPop(out var step))
        {
            try
            {
                step.Undo();
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AetherFrame could not undo {step.Name} after it failed to start.");
            }
        }
    }

    /// <summary>
    /// Runs a load that must never stop AetherFrame from starting (the plugin configuration, say):
    /// a failure is logged and reported through <paramref name="failed"/>, and the result is null.
    /// </summary>
    internal static T? TryLoad<T>(Func<T?> load, string what, IAetherFrameLog log, out bool failed)
        where T : class
    {
        try
        {
            failed = false;
            return load();
        }
        catch (Exception ex)
        {
            failed = true;
            log.Error(ex, $"AetherFrame could not read {what}; it starts with the defaults instead.");
            return null;
        }
    }
}
