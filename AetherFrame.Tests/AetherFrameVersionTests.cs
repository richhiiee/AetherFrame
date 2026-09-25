using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using AetherFrame.Services.Diagnostics;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The product version is set once, in Version.props, and everything that reports a version is
/// derived from it: assembly metadata by the SDK, the Dalamud manifest by DalamudPackager, and
/// what the running plugin shows by <see cref="AetherFrameBuildInfo"/>.
/// </summary>
public class AetherFrameVersionTests
{
    /// <summary>This milestone's version. Changing Version.props means changing this, deliberately.</summary>
    private const string ExpectedProductVersion = "0.1.1";

    private static readonly string[] VersionProperties =
        ["Version", "VersionPrefix", "VersionSuffix", "AssemblyVersion", "FileVersion", "InformationalVersion", "PackageVersion"];

    [Fact]
    public void AuthoritativeProductVersion_IsThisMilestones()
    {
        Assert.Equal(ExpectedProductVersion, ProductVersion());
        Assert.Equal(3, System.Version.Parse(ProductVersion()).ToString().Split('.').Length);
    }

    [Fact]
    public void BothProjects_ImportTheSharedVersion_AndSetNoVersionOfTheirOwn()
    {
        foreach (var project in new[] { "AetherFrame/AetherFrame.csproj", "AetherFrame.Tests/AetherFrame.Tests.csproj" })
        {
            var document = XDocument.Load(Path.Combine(RepositoryPaths.Root().FullName, project));
            Assert.Contains(document.Descendants("Import"), i => (string?)i.Attribute("Project") == @"..\Version.props");
        }

        // Version.props is the only MSBuild file anywhere in the repository that sets a version.
        foreach (var file in MsBuildFiles())
        {
            var setters = XDocument.Load(file).Descendants().Where(e => VersionProperties.Contains(e.Name.LocalName) && e.Parent?.Name.LocalName == "PropertyGroup");
            if (Path.GetFileName(file) == "Version.props")
            {
                Assert.Equal(["Version"], setters.Select(e => e.Name.LocalName));
            }
            else
            {
                Assert.True(!setters.Any(), $"{file} sets its own version; the product version belongs in Version.props only.");
            }
        }
    }

    [Fact]
    public void NoActiveConfiguration_StillCarriesThePreVersioningPlaceholder()
    {
        var active = MsBuildFiles()
            .Concat(Directory.GetFiles(Path.Combine(RepositoryPaths.Root().FullName, ".github"), "*.yml", SearchOption.AllDirectories));

        Assert.All(active, file => Assert.DoesNotContain("0.0.0.1", File.ReadAllText(file)));
    }

    [Fact]
    public void Readme_StatesTheCurrentProductVersion() =>
        Assert.Contains($"AetherFrame {ProductVersion()}", File.ReadAllText(Path.Combine(RepositoryPaths.Root().FullName, "README.md")));

    [Fact]
    public void AssemblyMetadata_BuiltFromVersionProps_CarriesTheProductVersion()
    {
        // This test assembly imports Version.props exactly as the plugin does, so its metadata is
        // what the SDK derives from it.
        var assembly = typeof(AetherFrameVersionTests).Assembly;
        var expected = System.Version.Parse(ProductVersion());

        Assert.Equal(FourPart(expected), assembly.GetName().Version);
        Assert.Equal(FourPart(expected).ToString(), assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
        Assert.Equal(ProductVersion(), AetherFrameBuildInfo.FromAssembly(assembly).Version);
        Assert.Equal(ProductVersion(), AetherFrameBuildInfo.Current.Version);
        Assert.Equal($"AetherFrame {ProductVersion()}", AetherFrameBuildInfo.Current.DisplayName);
    }

    [Fact]
    public void PluginAssembly_AndItsDalamudManifest_ReportTheProductVersion()
    {
        // The plugin isn't referenced (it would pull in Dalamud); its metadata is read from the
        // file. CI names the build in AETHERFRAME_PLUGIN_ASSEMBLY; locally the plugin's own build
        // output is checked when there is one.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        var expected = System.Version.Parse(ProductVersion());
        Assert.Equal(FourPart(expected), AssemblyName.GetAssemblyName(path).Version);

        var file = FileVersionInfo.GetVersionInfo(path);
        Assert.Equal(FourPart(expected).ToString(), file.FileVersion);
        Assert.Equal(ProductVersion(), AetherFrameBuildInfo.Parse(file.ProductVersion).Version);

        // DalamudPackager writes the manifest beside the DLL from the assembly's own version.
        var manifestPath = Path.Combine(Path.GetDirectoryName(path)!, "AetherFrame.json");
        Assert.True(File.Exists(manifestPath), $"No Dalamud manifest beside {path}.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(FourPart(expected).ToString(), manifest.RootElement.GetProperty("AssemblyVersion").GetString());
    }

    [Theory]
    [InlineData("0.1.0+1bf26e1b01a3d792254f645001dbfc3dbd0ea6fc", "0.1.0", "1bf26e1", "AetherFrame 0.1.0 (build 1bf26e1)")]
    [InlineData("0.1.0+1BF26E1", "0.1.0", "1BF26E1", "AetherFrame 0.1.0 (build 1BF26E1)")]
    [InlineData("0.1.0", "0.1.0", null, "AetherFrame 0.1.0")]
    [InlineData("0.1.0+", "0.1.0", null, "AetherFrame 0.1.0")]
    [InlineData("  0.2.1  ", "0.2.1", null, "AetherFrame 0.2.1")]
    [InlineData("0.1.0+local", "0.1.0", "local", "AetherFrame 0.1.0 (build local)")]
    [InlineData("0.1.0+abc", "0.1.0", "abc", "AetherFrame 0.1.0 (build abc)")]
    [InlineData(null, "unknown", null, "AetherFrame unknown")]
    [InlineData("", "unknown", null, "AetherFrame unknown")]
    public void BuildInfo_ReadsTheVersionAndShortRevision(string? informational, string version, string? revision, string described)
    {
        var info = AetherFrameBuildInfo.Parse(informational);

        Assert.Equal(version, info.Version);
        Assert.Equal(revision, info.Revision);
        Assert.Equal(described, info.Describe());
    }

    [Fact]
    public void CurrentBuild_Revision_IsAShortCommitIdWhenTheBuildHadOne()
    {
        // Builds without Git source information (e.g. from a source archive) have no revision.
        var revision = AetherFrameBuildInfo.Current.Revision;
        if (revision is null)
        {
            return;
        }

        Assert.Equal(AetherFrameBuildInfo.ShortRevisionLength, revision.Length);
        Assert.All(revision, c => Assert.True(Uri.IsHexDigit(c)));
    }

    private static Version FourPart(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static string ProductVersion()
    {
        var props = XDocument.Load(Path.Combine(RepositoryPaths.Root().FullName, "Version.props"));
        var version = props.Descendants("Version").Single().Value.Trim();
        Assert.False(string.IsNullOrEmpty(version));
        return version;
    }

    /// <summary>Every project, props and targets file in the repository outside build output.</summary>
    private static IEnumerable<string> MsBuildFiles()
    {
        var root = RepositoryPaths.Root().FullName;
        return new[] { "*.csproj", "*.props", "*.targets" }
            .SelectMany(pattern => Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".claude" or ".git" or ".vs"));
    }
}
