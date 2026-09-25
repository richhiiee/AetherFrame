using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// My Plates never shows the logged-in character's name or World (or any other automatic
/// identifier) in its own chrome, while the character still decides, internally, which Plate is
/// Active and which bindings change.
/// </summary>
public class MyPlatesPrivacyTests
{
    private static readonly CharacterContext Named = new(4242, "Rich Example", "Gilgamesh");

    [Fact]
    public void Header_WithACharacterLoggedIn_ShowsNoCharacterLine()
    {
        Assert.Null(MyPlatesCharacterText.HeaderStatus(Named));
        Assert.Null(MyPlatesCharacterText.HeaderStatus(new CharacterContext(1, null, null)));
    }

    [Fact]
    public void Header_WithNoCharacter_StillSaysSo() =>
        Assert.Equal("No character logged in", MyPlatesCharacterText.HeaderStatus(null));

    [Fact]
    public void EveryCharacterMessage_IsNeutral_NeverTheNameWorldOrContentId()
    {
        var texts = new[]
            {
                MyPlatesCharacterText.CurrentCharacter,
                MyPlatesCharacterText.NowActive("Evening Look"),
                MyPlatesCharacterText.FirstPlateCreated,
                MyPlatesCharacterText.NewPlateBelongsToCurrent,
            }
            .Concat(MyPlatesCharacterText.DeletingCurrentActive)
            .ToList();

        Assert.All(texts, text =>
        {
            Assert.DoesNotContain(Named.Name!, text);
            Assert.DoesNotContain(Named.HomeWorld!, text);
            Assert.DoesNotContain(Named.ContentId.ToString(), text);
            Assert.DoesNotContain("Playing as", text);
            Assert.DoesNotContain("@", text);
        });
        Assert.Contains("Evening Look", MyPlatesCharacterText.NowActive("Evening Look"));
    }

    [Fact]
    public void MyPlatesWindowSources_NeverFormatTheCharactersNameOrWorld()
    {
        // The window is ImGui code (not built here), so this reads its source: no line of My Plates
        // may read a CharacterContext's Name or HomeWorld, or bring back the "Playing as" line.
        var windows = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame", "Windows");
        var files = Directory.GetFiles(windows, "PlateLibraryWindow*.cs");
        Assert.NotEmpty(files);

        var identity = new Regex(@"\b(who|character|owner|CurrentCharacter)(\.Value)?\??\.(Name|HomeWorld)\b");
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Playing as", source);
            Assert.False(identity.IsMatch(source), $"{Path.GetFileName(file)} reads the character's name or World: {identity.Match(source).Value}");
        }
    }

    [Fact]
    public async Task CharacterBindings_StayIndependentPerCharacter_AndStillRecordWhoTheyAreFor()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alicePlate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bobPlate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        var shared = await library.CreatePlateAsync(PlateStartingLayout.Blank, character: null);

        await library.SetActivePlateAsync(Characters.Alice, shared.PlateId);

        Assert.Equal(shared.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(bobPlate.PlateId, library.GetActivePlateId(Characters.Bob.ContentId));
        Assert.NotEqual(alicePlate.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));

        // The binding itself is unchanged by the presentation change: still keyed by the character,
        // still remembering its last-known name and World for local use.
        var alice = library.GetBinding(Characters.Alice.ContentId)!;
        Assert.Equal(Characters.Alice.Name, alice.LastKnownCharacterName);
        Assert.Equal(Characters.Alice.HomeWorld, alice.LastKnownHomeWorld);
        Assert.Equal(Characters.Bob.Name, library.GetBinding(Characters.Bob.ContentId)!.LastKnownCharacterName);

        // And it survives a reload, per character.
        var reloaded = await fixture.LoadAsync();
        Assert.Equal(shared.PlateId, reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal(bobPlate.PlateId, reloaded.GetActivePlateId(Characters.Bob.ContentId));
    }

    [Fact]
    public async Task ActivePlateResolution_StillFollowsTheLoggedInCharacter()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var alicePlate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var bobPlate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Bob);
        var current = new CurrentCharacterStub();
        var resolver = current.ResolverFor(library);

        current.Current = Characters.Alice;
        Assert.Equal(alicePlate.PlateId, resolver.ResolveForCurrentCharacter().PlateId);

        current.Current = Characters.Bob;
        Assert.Equal(bobPlate.PlateId, resolver.ResolveForCurrentCharacter().PlateId);

        current.Current = null;
        Assert.False(resolver.ResolveForCurrentCharacter().IsResolved);
    }
}
