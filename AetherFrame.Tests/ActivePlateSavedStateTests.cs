using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The default Active Plate view presents the Active Plate as last SAVED — what the character
/// presents — while editor Preview (which draws <see cref="ProfileService.CurrentProfile"/>) and an
/// explicit viewer request for the open Plate keep showing the live, possibly unsaved, document.
/// </summary>
public class ActivePlateSavedStateTests
{
    private sealed class Scenario
    {
        internal required PlateLibraryService Library { get; init; }
        internal required ProfileService Editor { get; init; }
        internal required CurrentCharacterStub Character { get; init; }
        internal required PlateCreationResult Active { get; init; }

        /// <summary>The Active Plate, open in the editors with one unsaved text element.</summary>
        internal static async Task<Scenario> DirtyActivePlateAsync(LibraryFixture fixture)
        {
            var library = await fixture.LoadAsync();
            var active = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
            var editor = new ProfileService(library);
            editor.OpenPlate(active.PlateId);
            editor.AddTextElement("Unsaved change");
            return new Scenario { Library = library, Editor = editor, Character = new CurrentCharacterStub(), Active = active };
        }

        internal PlateViewerContent View(PlateViewerTarget target) =>
            target.Resolve(Character.ResolverFor(Library), Library.GetSavedDocument, Editor.CurrentProfile);
    }

    [Fact]
    public async Task DirtyOpenActivePlate_DoesNotOverrideItsSavedDocument_ForDefaultViewing()
    {
        using var fixture = new LibraryFixture();
        var scenario = await Scenario.DirtyActivePlateAsync(fixture);
        var saved = scenario.Library.GetSavedDocument(scenario.Active.PlateId);

        var content = scenario.View(new PlateViewerTarget());

        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(saved, content.Document);
        Assert.NotSame(scenario.Editor.CurrentProfile, content.Document);
        Assert.Empty(content.Document!.Elements);
        Assert.Single(scenario.Editor.CurrentProfile!.Elements);

        // The resolver itself hands out the saved document too.
        Assert.Same(saved, scenario.Character.ResolverFor(scenario.Library).ResolveForCurrentCharacter().Document);
    }

    [Fact]
    public async Task SavingTheActivePlate_UpdatesWhatDefaultViewingResolves()
    {
        using var fixture = new LibraryFixture();
        var scenario = await Scenario.DirtyActivePlateAsync(fixture);
        var target = new PlateViewerTarget();
        Assert.Empty(scenario.View(target).Document!.Elements);

        await scenario.Editor.SaveCurrentProfileAsync();

        var content = scenario.View(target);
        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(scenario.Library.GetSavedDocument(scenario.Active.PlateId), content.Document);
        Assert.NotSame(scenario.Editor.CurrentProfile, content.Document);
        var element = Assert.Single(content.Document!.Elements);
        Assert.Equal("Unsaved change", Assert.IsType<TextProfileElement>(element).Text);

        // Later unsaved edits again stay out of the default view.
        scenario.Editor.AddTextElement("Another unsaved change");
        Assert.Single(scenario.View(target).Document!.Elements);
    }

    [Fact]
    public async Task EditorPreview_StillUsesTheLiveDocument()
    {
        using var fixture = new LibraryFixture();
        var scenario = await Scenario.DirtyActivePlateAsync(fixture);
        var live = scenario.Editor.CurrentProfile;

        // Opening the default viewer changes nothing about what the editors (and their Clean
        // Preview, which draws ProfileService.CurrentProfile) show.
        scenario.View(new PlateViewerTarget());

        Assert.Same(live, scenario.Editor.CurrentProfile);
        Assert.Single(scenario.Editor.CurrentProfile!.Elements);
        Assert.Empty(scenario.Library.GetSavedDocument(scenario.Active.PlateId)!.Elements);
    }

    [Fact]
    public async Task ExplicitViewerRequest_ForTheOpenPlate_StillShowsTheLiveDocument()
    {
        using var fixture = new LibraryFixture();
        var scenario = await Scenario.DirtyActivePlateAsync(fixture);
        var target = new PlateViewerTarget();

        target.RequestPlate(scenario.Active.PlateId);
        var content = scenario.View(target);

        Assert.Equal(PlateViewerState.Showing, content.State);
        Assert.Same(scenario.Editor.CurrentProfile, content.Document);
        Assert.Single(content.Document!.Elements);
    }

    [Fact]
    public async Task ExplicitAndDefaultRequests_StaySeparate_ForTheSameDirtyPlate()
    {
        using var fixture = new LibraryFixture();
        var scenario = await Scenario.DirtyActivePlateAsync(fixture);
        var target = new PlateViewerTarget();

        target.RequestPlate(scenario.Active.PlateId);
        Assert.Same(scenario.Editor.CurrentProfile, scenario.View(target).Document);

        target.RequestActivePlate();
        Assert.Same(scenario.Library.GetSavedDocument(scenario.Active.PlateId), scenario.View(target).Document);

        target.RequestPlate(scenario.Active.PlateId);
        Assert.Same(scenario.Editor.CurrentProfile, scenario.View(target).Document);
    }

    [Fact]
    public async Task ExplicitViewerRequest_ForAPlateNotOpen_StillShowsItsSavedDocument()
    {
        using var fixture = new LibraryFixture();
        var scenario = await Scenario.DirtyActivePlateAsync(fixture);
        var other = await scenario.Library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var target = new PlateViewerTarget();

        target.RequestPlate(other.PlateId);

        Assert.Same(scenario.Library.GetSavedDocument(other.PlateId), scenario.View(target).Document);
    }
}
