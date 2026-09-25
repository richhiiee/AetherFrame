using System;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Advanced Editor held back for the one-time Basic suggestion, through the same objects the
/// plugin wires at load (configuration origin, guidance, gate) and a real Plate Library: when the
/// prompt is asked, what it offers, which editor each answer opens, and that the answer persists.
/// </summary>
public class AdvancedEntryGateTests
{
    /// <summary>What Plugin does at load: classify the configuration, then resolve the guidance.</summary>
    private static (AdvancedEntryGate Gate, BasicGuidance Guidance, FakeGuidanceStore Store) Load(bool configurationFound, int version, bool handled)
    {
        var store = new FakeGuidanceStore { Version = version, BasicGuidanceHandled = handled };
        var guidance = new BasicGuidance(store);
        guidance.Resolve(BasicGuidance.OriginOf(configurationFound, store.Version, currentVersion: 2));
        return (new AdvancedEntryGate(guidance), guidance, store);
    }

    private static ProfileDocument Freeform()
    {
        var document = BasicDocuments.Blank();
        document.Elements.Add(new TextProfileElement { Text = "Freeform", ZIndex = 0 });
        document.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), ZIndex = 1 });
        return document;
    }

    // ---------------------------------------------------------------- the failed in-game case

    [Fact]
    public async Task Version2False_WithExistingPlates_AClassicPlateRequestingAdvanced_IsAskedBeforeAdvancedOpens()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        await harness.Library.CreatePlateAsync(PlateStartingLayout.Blank, null); // more existing Plates
        Assert.True(harness.Library.GetOrderedPlates().Count >= 2);
        var (gate, _, store) = Load(configurationFound: true, version: 2, handled: false);

        var openNow = gate.TryEnterAdvanced(advancedAlreadyOpen: false, harness.Document);

        Assert.False(openNow); // Advanced is held back
        Assert.True(gate.IsWaiting);
        Assert.True(gate.ConsumePromptRequest()); // the prompt is opened
        Assert.True(gate.OffersTryBasic);
        Assert.False(store.BasicGuidanceHandled);
        Assert.Equal(0, store.Saves); // a Version 2 configuration isn't rewritten at load
    }

    [Fact]
    public async Task Version2True_NeverAsks()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, _) = Load(configurationFound: true, version: 2, handled: true);

        Assert.True(gate.TryEnterAdvanced(false, harness.Document));
        Assert.False(gate.IsWaiting);
        Assert.False(gate.ConsumePromptRequest());
    }

    [Fact]
    public async Task AFreshInstall_Asks_EvenWithPlatesAlreadySaved()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(configurationFound: false, version: 2, handled: false);

        Assert.False(gate.TryEnterAdvanced(false, harness.Document));
        Assert.True(gate.IsWaiting);
        Assert.Equal(1, store.Saves); // saved at load as a Version 2 "not handled"
    }

    [Fact]
    public async Task ALegacyConfiguration_IsHandledByMigration_AndNeverAsks()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(configurationFound: true, version: 1, handled: false);

        Assert.True(gate.TryEnterAdvanced(false, harness.Document));
        Assert.True(store.BasicGuidanceHandled);
        Assert.Equal(2, store.Version);
    }

    [Fact]
    public async Task OpeningBasicFirst_MeansAdvancedIsNeverHeld()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, guidance, store) = Load(true, 2, false);

        guidance.MarkHandled(); // Plugin.OpenBasicEditor

        Assert.True(gate.TryEnterAdvanced(false, harness.Document));
        Assert.True(store.BasicGuidanceHandled);
    }

    [Fact]
    public async Task WithAdvancedAlreadyShowing_NothingIsHeld()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, _) = Load(true, 2, false);

        Assert.True(gate.TryEnterAdvanced(advancedAlreadyOpen: true, harness.Document));
    }

    // ---------------------------------------------------------------- what the prompt offers

    [Fact]
    public async Task AClassicPlate_IsOfferedTryBasic()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, _) = Load(true, 2, false);

        gate.TryEnterAdvanced(false, harness.Document);

        Assert.Equal(BasicGuidancePrompt.OfferBasic, gate.Pending);
        Assert.True(gate.OffersTryBasic);
    }

    [Fact]
    public async Task ABlankCanvasPlate_IsNotOfferedTryBasic()
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.Blank, null);
        var (gate, _, _) = Load(true, 2, false);

        gate.TryEnterAdvanced(false, harness.Document);

        Assert.Equal(BasicGuidancePrompt.AdvancedOnly, gate.Pending);
        Assert.False(gate.OffersTryBasic);
    }

    [Fact]
    public void AFreeformPlate_IsNotOfferedTryBasic()
    {
        var (gate, _, _) = Load(true, 2, false);

        gate.TryEnterAdvanced(false, Freeform());

        Assert.Equal(BasicGuidancePrompt.AdvancedOnly, gate.Pending);
        Assert.False(gate.OffersTryBasic);
    }

    // ---------------------------------------------------------------- answers

    [Fact]
    public async Task ContinueToAdvanced_OpensAdvanced_AndPersists()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);
        gate.MarkShown();

        Assert.Equal(EditorSurfaceKind.Advanced, gate.Answer(BasicGuidanceAnswer.ContinueToAdvanced));
        Assert.True(store.BasicGuidanceHandled);
        Assert.Equal(1, store.Saves);
        Assert.False(gate.IsWaiting);
    }

    [Fact]
    public async Task ClosingThePrompt_OpensAdvanced_AndPersists()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);
        gate.MarkShown();

        Assert.True(gate.WasShown);
        Assert.Equal(EditorSurfaceKind.Advanced, gate.Answer(BasicGuidanceAnswer.Closed));
        Assert.True(store.BasicGuidanceHandled);
    }

    [Fact]
    public async Task TryBasicEditor_OpensBasic_AndPersists()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);
        gate.MarkShown();

        Assert.Equal(EditorSurfaceKind.Basic, gate.Answer(BasicGuidanceAnswer.TryBasicEditor));
        Assert.True(store.BasicGuidanceHandled);
    }

    [Fact]
    public void TryBasicEditor_OnAFreeformPlate_NeverOpensBasic()
    {
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, Freeform());

        Assert.Equal(EditorSurfaceKind.Advanced, gate.Answer(BasicGuidanceAnswer.TryBasicEditor));
        Assert.True(store.BasicGuidanceHandled);
    }

    [Theory]
    [InlineData((int)BasicGuidanceAnswer.TryBasicEditor)]
    [InlineData((int)BasicGuidanceAnswer.ContinueToAdvanced)]
    [InlineData((int)BasicGuidanceAnswer.Closed)]
    public async Task AfterAnyAnswer_ThePromptNeverRepeats_EvenAfterReloading(int answerCode)
    {
        var answer = (BasicGuidanceAnswer)answerCode;
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);
        gate.MarkShown();
        gate.Answer(answer);

        Assert.True(gate.TryEnterAdvanced(false, harness.Document));
        Assert.True(gate.TryEnterAdvanced(false, BasicDocuments.Blank()));

        // Next session: the saved Version 2 "handled" is authoritative.
        var (reloaded, _, _) = Load(true, store.Version, store.BasicGuidanceHandled);
        Assert.True(reloaded.TryEnterAdvanced(false, harness.Document));
    }

    // ---------------------------------------------------------------- lifetime

    [Fact]
    public async Task AFrameBeforeThePromptIsShown_CanNeverCountAsClosed()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);

        // The window only treats a closed prompt as an answer once it has been on screen.
        Assert.False(gate.WasShown);
        Assert.True(gate.IsWaiting);
        Assert.False(store.BasicGuidanceHandled);
    }

    [Fact]
    public async Task ARequestWhileAlreadyAsking_IsStillHeld()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, _) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);

        Assert.False(gate.TryEnterAdvanced(false, harness.Document));
        Assert.True(gate.IsWaiting);
    }

    [Fact]
    public async Task AbandoningTheRequest_OpensNothing_AndAsksAgainNextTime()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var (gate, _, store) = Load(true, 2, false);
        gate.TryEnterAdvanced(false, harness.Document);
        gate.ConsumePromptRequest();

        gate.Abandon(); // My Plates closed with the prompt up

        Assert.False(gate.IsWaiting);
        Assert.Null(gate.Answer(BasicGuidanceAnswer.Closed));
        Assert.False(store.BasicGuidanceHandled);
        Assert.False(gate.TryEnterAdvanced(false, harness.Document));
        Assert.True(gate.ConsumePromptRequest());
    }

    [Fact]
    public void AnAnswerWithNothingWaiting_ChangesNothing()
    {
        var (gate, _, store) = Load(true, 2, false);

        Assert.Null(gate.Answer(BasicGuidanceAnswer.ContinueToAdvanced));
        Assert.False(store.BasicGuidanceHandled);
    }
}
