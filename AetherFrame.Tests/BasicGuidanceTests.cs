using System;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The one-time "New to AetherFrame?" suggestion of the Basic Editor: when it's shown, that it's
/// handled for good by either answer or by simply opening Basic first, that the flag persists, and
/// that an update never nags players who already had AetherFrame.
/// </summary>
public class BasicGuidanceTests
{
    private sealed class FakeStore : IBasicGuidanceStore
    {
        public bool BasicGuidanceHandled { get; set; }

        public int Saves { get; private set; }

        public void Save() => Saves++;
    }

    private static (BasicGuidance Guidance, FakeStore Store) NewInstall()
    {
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);
        guidance.Resolve(GuidanceConfigOrigin.Missing);
        return (guidance, store);
    }

    [Fact]
    public void ANewPlayer_IsSuggestedBasic()
    {
        var (guidance, store) = NewInstall();

        Assert.True(guidance.ShouldSuggestBasic);
        Assert.False(store.BasicGuidanceHandled);
        Assert.Equal(1, store.Saves); // the new install's decision is recorded once
    }

    [Fact]
    public void OpeningBasicFirst_MarksTheGuidanceHandled_AndPersistsIt()
    {
        var (guidance, store) = NewInstall();

        guidance.MarkHandled();

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.True(store.BasicGuidanceHandled);
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public void AnsweringThePrompt_PreventsItFromEverReturning()
    {
        var (guidance, store) = NewInstall();
        guidance.MarkHandled(); // Continue to Advanced (or Try Basic Editor, or closing it)

        Assert.False(guidance.ShouldSuggestBasic);

        // Next session: the saved flag is read back.
        var nextSession = new BasicGuidance(store);
        nextSession.Resolve(GuidanceConfigOrigin.Current);
        Assert.False(nextSession.ShouldSuggestBasic);
    }

    [Fact]
    public void MarkingHandledTwice_SavesOnlyOnce()
    {
        var (guidance, store) = NewInstall();

        guidance.MarkHandled();
        guidance.MarkHandled();

        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public void ANewPlayerWhoNeverAnswered_IsStillSuggestedNextSession()
    {
        var (_, store) = NewInstall();

        var nextSession = new BasicGuidance(store);
        nextSession.Resolve(GuidanceConfigOrigin.Current);

        Assert.True(nextSession.ShouldSuggestBasic);
        Assert.Equal(1, store.Saves); // nothing new to record
    }

    [Fact]
    public void AMissingConfiguration_IsAskedOnce_AndSavedAsAVersion2FalseAtOnce()
    {
        // The real in-game failure (2026-09-25 06:30:46): the first load of this version found no
        // configuration (no earlier build saved one) and two saved Plates, and the old rule wrote
        // "handled" before the player did anything. Saved Plates no longer decide it.
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);

        guidance.Resolve(GuidanceConfigOrigin.Missing);

        Assert.True(guidance.ShouldSuggestBasic);
        Assert.False(store.BasicGuidanceHandled);
        Assert.Equal(1, store.Saves); // recorded, so from now on the stored flag decides
    }

    [Fact]
    public void AConfigurationFromBeforeTheFlag_CountsAsHandled()
    {
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);

        guidance.Resolve(GuidanceConfigOrigin.Legacy);

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.True(store.BasicGuidanceHandled);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void NothingIsSuggested_UntilTheConfigurationIsResolved()
    {
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void HandlingBeforeTheDecision_IsNeverUndoneByIt()
    {
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);
        guidance.MarkHandled(); // Basic opened before the Library finished loading

        guidance.Resolve(GuidanceConfigOrigin.Missing);

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.True(store.BasicGuidanceHandled);
    }

    // ---------------------------------------------------------------- before the first Advanced Editor

    [Fact]
    public void BeforeTheFirstAdvancedEditor_APlateThatSuitsBasic_IsOfferedBasic()
    {
        var (guidance, _) = NewInstall();

        Assert.Equal(BasicGuidancePrompt.OfferBasic, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, BasicDocuments.Classic(FakeCharacter.Hero)));
    }

    [Fact]
    public void BeforeTheFirstAdvancedEditor_ABlankCanvas_IsNeverOfferedBasic()
    {
        var (guidance, _) = NewInstall();

        Assert.Equal(BasicGuidancePrompt.AdvancedOnly, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, BasicDocuments.Blank()));
    }

    [Fact]
    public void BeforeTheFirstAdvancedEditor_AFreeformPlate_IsNeverOfferedBasic()
    {
        var (guidance, _) = NewInstall();
        var freeform = BasicDocuments.Blank();
        freeform.Elements.Add(new TextProfileElement { Text = "Freeform", ZIndex = 0 });
        freeform.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 1 });

        Assert.Equal(BasicGuidancePrompt.AdvancedOnly, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, freeform));
        Assert.Equal(BasicGuidancePrompt.AdvancedOnly, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, plate: null));
    }

    [Theory]
    [InlineData(PlateStartingLayout.AdventurePlateClassic, true)]
    [InlineData(PlateStartingLayout.Blank, false)]
    public async Task ANewlyCreatedPlate_IsAskedAboutByItsContent(PlateStartingLayout layout, bool offerBasic)
    {
        // Use Template / Create Plate: the prompt judges the Plate just created and opened.
        using var harness = await BasicHarness.CreatePlateAsync(layout, new PlateStarterContent(FakeCharacter.Hero));
        var (guidance, _) = NewInstall();

        var expected = offerBasic ? BasicGuidancePrompt.OfferBasic : BasicGuidancePrompt.AdvancedOnly;
        Assert.Equal(expected, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, harness.Profiles.CurrentProfile));
    }

    [Fact]
    public void WithTheAdvancedEditorAlreadyShowing_NothingIsAsked()
    {
        var (guidance, _) = NewInstall();

        Assert.Equal(BasicGuidancePrompt.None, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: true, BasicDocuments.Classic(FakeCharacter.Hero)));
    }

    [Fact]
    public void SwitchingToAdvancedFromBasic_IsNeverAsked()
    {
        var (guidance, _) = NewInstall();
        guidance.MarkHandled(); // the Basic Editor opened

        Assert.Equal(BasicGuidancePrompt.None, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, BasicDocuments.Classic(FakeCharacter.Hero)));
        Assert.Equal(BasicGuidancePrompt.None, guidance.PromptBeforeAdvanced(advancedAlreadyOpen: false, BasicDocuments.Blank()));
    }

    [Fact]
    public void OnceAnswered_NoAdvancedRouteIsAskedAgain()
    {
        var (guidance, _) = NewInstall();
        Assert.Equal(BasicGuidancePrompt.AdvancedOnly, guidance.PromptBeforeAdvanced(false, BasicDocuments.Blank()));

        guidance.MarkHandled(); // Continue to Advanced, or closed

        Assert.Equal(BasicGuidancePrompt.None, guidance.PromptBeforeAdvanced(false, BasicDocuments.Blank()));
        Assert.Equal(BasicGuidancePrompt.None, guidance.PromptBeforeAdvanced(false, BasicDocuments.Classic(FakeCharacter.Hero)));
    }

    [Fact]
    public void AnEstablishedPlayer_OrAnUndecidedInstall_IsNeverAsked()
    {
        var undecided = new BasicGuidance(new FakeStore());
        Assert.Equal(BasicGuidancePrompt.None, undecided.PromptBeforeAdvanced(false, BasicDocuments.Blank()));

        var established = new BasicGuidance(new FakeStore());
        established.Resolve(GuidanceConfigOrigin.Legacy);
        Assert.Equal(BasicGuidancePrompt.None, established.PromptBeforeAdvanced(false, BasicDocuments.Classic(FakeCharacter.Hero)));
    }

    [Fact]
    public void IsHandled_ForAMissingConfiguration_IsFalse() =>
        Assert.False(BasicGuidance.IsHandled(GuidanceConfigOrigin.Missing, storedHandled: false));

    [Fact]
    public void IsHandled_ForALegacyConfiguration_IsTrue_DuringMigrationOnly() =>
        Assert.True(BasicGuidance.IsHandled(GuidanceConfigOrigin.Legacy, storedHandled: false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsHandled_ForAVersion2Configuration_IsExactlyWhatItStored(bool stored) =>
        Assert.Equal(stored, BasicGuidance.IsHandled(GuidanceConfigOrigin.Current, stored));

    [Theory]
    [InlineData(false, 0, (int)GuidanceConfigOrigin.Missing)]
    [InlineData(true, 1, (int)GuidanceConfigOrigin.Legacy)]
    [InlineData(true, 2, (int)GuidanceConfigOrigin.Current)]
    [InlineData(true, 3, (int)GuidanceConfigOrigin.Current)]
    public void OriginOf_ClassifiesTheLoadedConfiguration(bool found, int version, int expected) =>
        Assert.Equal((GuidanceConfigOrigin)expected, BasicGuidance.OriginOf(found, version, currentVersion: 2));
}
