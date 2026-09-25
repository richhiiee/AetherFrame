using System;
using Dalamud.Configuration;

namespace AetherFrame;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    /// <summary>The version that added <see cref="BasicGuidanceHandled"/>; an older saved configuration predates it.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Whether the one-time "New to AetherFrame?" suggestion of the Basic Editor has been handled
    /// (see <c>BasicGuidance</c>). Once true it is never shown again.
    /// </summary>
    public bool BasicGuidanceHandled { get; set; }
}
