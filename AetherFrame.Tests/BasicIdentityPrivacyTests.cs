using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Basic Editor never draws the logged-in character's name as a convenience (no "Use &lt;name&gt;"
/// button, no name as a placeholder or hint). The Character Name is whatever the Plate stores, typed
/// by hand, and the character still decides, internally, which Plate is Active.
/// </summary>
public class BasicIdentityPrivacyTests
{
    [Fact]
    public void BasicEditorWindowSources_NeverDrawTheLoggedInCharactersName()
    {
        // The window is ImGui code (not built here), so this reads its source.
        var windows = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows");
        var files = Directory.GetFiles(windows, "BasicProfileEditorWindow*.cs");
        Assert.NotEmpty(files);

        var liveName = new Regex(@"\.CharacterName\b|\binfo\??\.Name\b|\bcurrent\??\.Name\b|UseCharacterName");
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Logged in as", source);
            Assert.DoesNotContain("Playing as", source);
            Assert.DoesNotContain("Use Current Character", source);
            Assert.False(liveName.IsMatch(source), $"{Path.GetFileName(file)} reads the logged-in character's name: {liveName.Match(source).Value}");
        }
    }

    [Fact]
    public async Task CharacterName_TypedByHand_UndoesRedoesSavesAndReopens()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        Assert.Equal("Hero Example", NameText(harness.Document));

        harness.Identity.SetNameText("Lyra Moonfall");
        harness.Identity.Commit();
        Assert.Equal("Lyra Moonfall", NameText(harness.Document));

        harness.Session.Undo();
        Assert.Equal("Hero Example", NameText(harness.Document));
        harness.Session.Redo();
        Assert.Equal("Lyra Moonfall", NameText(harness.Document));

        Assert.True(await harness.Session.SaveProfileAsync());
        Assert.Equal("Lyra Moonfall", NameText(harness.Library.OpenDocumentForEditing(harness.PlateId)));
    }

    [Fact]
    public async Task StoredCharacterName_IsKept_WhoeverIsLoggedIn()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        harness.Identity.SetNameText("Lyra Moonfall");
        harness.Identity.Commit();
        Assert.True(await harness.Session.SaveProfileAsync());

        harness.Character.CurrentInfo = FakeCharacter.Hero with { Name = "Someone Else" };
        harness.SimulateBasicFrame();
        Assert.Equal("Lyra Moonfall", NameText(harness.Document));

        harness.Character.CurrentInfo = null;
        harness.SimulateBasicFrame();
        Assert.Equal("Lyra Moonfall", NameText(harness.Document));
        Assert.Equal("Lyra Moonfall", NameText(harness.Library.OpenDocumentForEditing(harness.PlateId)));
    }

    [Fact]
    public async Task EditingTheName_LeavesTheActivePlateBindingAlone()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero), Characters.Alice);
        Assert.Equal(harness.PlateId, harness.Library.GetActivePlateId(Characters.Alice.ContentId));

        harness.Identity.SetNameText("Lyra Moonfall");
        harness.Identity.Commit();
        Assert.True(await harness.Session.SaveProfileAsync());

        Assert.Equal(harness.PlateId, harness.Library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Contains(harness.PlateId, harness.Library.GetBinding(Characters.Alice.ContentId)!.PlateIds);
    }

    private static string? NameText(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicName)?.Text;
}
