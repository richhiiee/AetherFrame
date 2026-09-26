using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// What a release and a Dalamud submission rely on outside the code: the CHANGELOG section the
/// release workflow publishes, the AI-generated asset disclosure the Dalamud AI Usage Policy requires
/// in the plugin description, and the draft D17 manifest pointing at the plugin project.
/// </summary>
public class ReleaseMetadataTests
{
    [Fact]
    public void Changelog_HasASectionForTheCurrentVersion()
    {
        var version = XDocument.Load(RepositoryFile("Version.props")).Descendants("Version").Single().Value.Trim();
        var changelog = File.ReadAllText(RepositoryFile("CHANGELOG.md"));

        Assert.Matches(new Regex($@"^## \[{Regex.Escape(version)}\] - \d{{4}}-\d{{2}}-\d{{2}}\s*$", RegexOptions.Multiline), changelog);
        Assert.Contains($"[{version}]: https://github.com/richhiiee/AetherFrame/", changelog);
    }

    [Fact]
    public void PluginDescription_DisclosesTheAiAssistedArtwork()
    {
        var description = XDocument.Load(RepositoryFile("AetherFrame/AetherFrame.csproj")).Descendants("Description").Single().Value;

        Assert.Contains("created with AI assistance", description);
        Assert.Contains("Celestial Dream", description);
        Assert.Contains("Celestial Sakura", description);
    }

    [Fact]
    public void DraftSubmissionManifest_PointsAtThePluginProject()
    {
        var manifest = File.ReadAllText(RepositoryFile("docs/dalamud-submission/manifest.toml"));

        Assert.Contains("repository = \"https://github.com/richhiiee/AetherFrame.git\"", manifest);
        var projectPath = Regex.Match(manifest, "^project_path = \"([^\"]+)\"", RegexOptions.Multiline).Groups[1].Value;
        Assert.True(File.Exists(RepositoryFile(Path.Combine(projectPath, "AetherFrame.csproj"))), $"project_path '{projectPath}' has no AetherFrame.csproj.");
        Assert.True(File.Exists(RepositoryFile(Path.Combine(projectPath, "packages.lock.json"))), $"project_path '{projectPath}' has no packages.lock.json.");
    }

    private static string RepositoryFile(string relativePath) => Path.Combine(RepositoryPaths.Root().FullName, relativePath);
}
