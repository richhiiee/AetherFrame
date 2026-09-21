using System;
using Dalamud.Configuration;

namespace AetherFrame;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
}
