using System;
using System.Reflection;

namespace AetherFrame.Services.Diagnostics;

/// <summary>
/// Which AetherFrame build is running, read from the assembly's own metadata: nothing here is
/// maintained by hand. The product version is set once in Version.props; the SDK writes it into
/// <see cref="AssemblyInformationalVersionAttribute"/>, appending "+&lt;commit&gt;" when the build
/// had Git source information. That commit is the checked-out one, so a build with uncommitted
/// changes reports the commit it started from.
/// </summary>
internal sealed record AetherFrameBuildInfo(string Version, string? Revision)
{
    /// <summary>Length a full commit id is shortened to, as Git abbreviates it.</summary>
    internal const int ShortRevisionLength = 7;

    /// <summary>The build this code was compiled into.</summary>
    internal static AetherFrameBuildInfo Current { get; } = FromAssembly(typeof(AetherFrameBuildInfo).Assembly);

    internal static AetherFrameBuildInfo FromAssembly(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3));

    /// <summary>
    /// "0.1.0+1bf26e1b01a3…" is version 0.1.0, build 1bf26e1. Build metadata that isn't a commit
    /// id is kept as written; without any, there is no revision.
    /// </summary>
    internal static AetherFrameBuildInfo Parse(string? informationalVersion)
    {
        var value = informationalVersion?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return new AetherFrameBuildInfo("unknown", null);
        }

        var plus = value.IndexOf('+');
        if (plus < 0)
        {
            return new AetherFrameBuildInfo(value, null);
        }

        var version = value[..plus];
        var metadata = value[(plus + 1)..];
        if (metadata.Length == 0)
        {
            return new AetherFrameBuildInfo(version, null);
        }

        return new AetherFrameBuildInfo(version, IsCommitId(metadata) ? metadata[..ShortRevisionLength] : metadata);
    }

    /// <summary>"AetherFrame 0.1.0".</summary>
    internal string DisplayName => $"AetherFrame {Version}";

    /// <summary>"AetherFrame 0.1.0 (build 1bf26e1)", or just the display name without a revision.</summary>
    internal string Describe() => Revision is null ? DisplayName : $"{DisplayName} (build {Revision})";

    private static bool IsCommitId(string text)
    {
        if (text.Length < ShortRevisionLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
