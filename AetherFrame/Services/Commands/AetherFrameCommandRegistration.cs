using System;
using System.Collections.Generic;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Commands;

/// <summary>Registers chat commands with the host (Dalamud's ICommandManager in game).</summary>
internal interface ICommandRegistrar
{
    /// <summary>Registers <paramref name="command"/>; false when the host refuses it (e.g. another
    /// plugin already owns that name).</summary>
    bool Add(string command, string helpMessage, Action<string, string> handler);

    bool Remove(string command);
}

/// <summary>
/// Registers every <see cref="AetherFrameCommand.Names"/> entry with one shared
/// <see cref="AetherFrameCommandHandler"/>. A name the host refuses (another plugin already owns
/// <c>/af</c>, say) is logged and skipped: registration never throws, never substitutes a different
/// name, and never affects the other names — <c>/aetherframe</c> stays available regardless.
/// Unregistering removes only the names this registration actually added.
/// </summary>
internal sealed class AetherFrameCommandRegistration
{
    private readonly ICommandRegistrar registrar;
    private readonly IAetherFrameLog log;
    private readonly List<string> registered = new();
    private readonly List<string> unavailable = new();

    internal AetherFrameCommandRegistration(ICommandRegistrar registrar, IAetherFrameLog log)
    {
        this.registrar = registrar;
        this.log = log;
    }

    /// <summary>Names registered to AetherFrame.</summary>
    internal IReadOnlyList<string> Registered => registered;

    /// <summary>Names the host refused; AetherFrame doesn't answer to these.</summary>
    internal IReadOnlyList<string> Unavailable => unavailable;

    internal void Register(AetherFrameCommandHandler handler)
    {
        foreach (var name in AetherFrameCommand.Names)
        {
            bool added;
            Exception? failure = null;
            try
            {
                added = registrar.Add(name, AetherFrameCommand.HelpFor(name), handler.Handle);
            }
            catch (Exception ex)
            {
                added = false;
                failure = ex;
            }

            if (added)
            {
                registered.Add(name);
                continue;
            }

            unavailable.Add(name);
            var message = name == AetherFrameCommand.Name
                ? $"AetherFrame could not register {name}; it may already be registered by another plugin."
                : $"AetherFrame could not register the {name} shortcut; it may already be registered by another plugin. Use {AetherFrameCommand.Name} instead.";
            if (failure is null)
            {
                log.Warning(message);
            }
            else
            {
                log.Error(failure, message);
            }
        }
    }

    internal void Unregister()
    {
        foreach (var name in registered)
        {
            try
            {
                registrar.Remove(name);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AetherFrame could not unregister {name}.");
            }
        }

        registered.Clear();
    }
}
