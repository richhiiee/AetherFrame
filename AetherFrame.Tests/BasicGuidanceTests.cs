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
        guidance.Resolve(GuidanceConfigOrigin.Missing, libraryHasPlates: false);
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
        nextSession.Resolve(GuidanceConfigOrigin.Current, libraryHasPlates: true);
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
        nextSession.Resolve(GuidanceConfigOrigin.Current, libraryHasPlates: true);

        Assert.True(nextSession.ShouldSuggestBasic);
        Assert.Equal(1, store.Saves); // nothing new to record
    }

    [Fact]
    public void AnExistingPlayerWithPlates_IsNeverNagged_AfterTheUpdate()
    {
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);

        guidance.Resolve(GuidanceConfigOrigin.Missing, libraryHasPlates: true);

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.True(store.BasicGuidanceHandled);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void AConfigurationFromBeforeTheFlag_CountsAsHandled()
    {
        var store = new FakeStore();
        var guidance = new BasicGuidance(store);

        guidance.Resolve(GuidanceConfigOrigin.Legacy, libraryHasPlates: false);

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.True(store.BasicGuidanceHandled);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void NothingIsSuggested_UntilTheLibraryHasLoaded()
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

        guidance.Resolve(GuidanceConfigOrigin.Missing, libraryHasPlates: false);

        Assert.False(guidance.ShouldSuggestBasic);
        Assert.True(store.BasicGuidanceHandled);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void IsHandled_ForAMissingConfiguration_FollowsTheLibrary(bool stored, bool libraryHasPlates, bool expected) =>
        Assert.Equal(expected, BasicGuidance.IsHandled(GuidanceConfigOrigin.Missing, stored, libraryHasPlates));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void IsHandled_ForACurrentConfiguration_IsWhatItStored(bool stored, bool expected)
    {
        Assert.Equal(expected, BasicGuidance.IsHandled(GuidanceConfigOrigin.Current, stored, libraryHasPlates: true));
        Assert.Equal(expected, BasicGuidance.IsHandled(GuidanceConfigOrigin.Current, stored, libraryHasPlates: false));
    }
}
