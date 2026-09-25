using System;
using AetherFrame.Services.Commands;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace AetherFrame.Hosting;

/// <summary><see cref="ICommandRegistrar"/> over Dalamud's command service. Dalamud's AddHandler
/// returns false (rather than throwing) when the name is already registered.</summary>
internal sealed class DalamudCommandRegistrar : ICommandRegistrar
{
    private readonly ICommandManager commandManager;

    internal DalamudCommandRegistrar(ICommandManager commandManager)
    {
        this.commandManager = commandManager;
    }

    public bool Add(string command, string helpMessage, Action<string, string> handler) =>
        commandManager.AddHandler(command, new CommandInfo((name, arguments) => handler(name, arguments))
        {
            HelpMessage = helpMessage,
        });

    public bool Remove(string command) => commandManager.RemoveHandler(command);
}
