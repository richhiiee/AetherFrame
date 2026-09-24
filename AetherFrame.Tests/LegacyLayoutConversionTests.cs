using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The explicit "Convert to Classic" action for a Plate saved with the retired Badge or Accent
/// layout (see <c>BasicProfileEditorWindow.Identity.DrawLegacyLayoutConversion</c>): opening such a
/// Plate must never mutate or dirty it, and the conversion itself — calling
/// <see cref="AetherFrame.UI.Editor.BasicIdentitySession.SetLayout"/> with
/// <see cref="IdentityTitleLayout.Classic"/>, exactly what the button does — must be one undo step
/// that redoes exactly. The UI button itself lives in Windows/ (Dalamud/ImGui) and can't be linked
/// into this Dalamud-free test project; these tests cover the domain action it calls.
/// </summary>
public class LegacyLayoutConversionTests
{
    private static ProfileDocument LegacyBadgePlate()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        var nameSize = BasicSections.FindText(document, ProfileElementRole.BasicName)!.FontSize;
        var badge = IdentityHeaderRules.LayoutLook(IdentityTitleLayout.Badge, nameSize)!;
        badge.ApplyTo(title);
        title.Text = "the Brave";
        title.ZIndex = 50;
        document.Elements.Add(title);
        document.BasicIdentity!.Layout = IdentityTitleLayout.Badge;
        document.BasicIdentity.TitleSource = IdentityTitleSource.Custom;
        document.BasicIdentity.CustomTitle = "the Brave";
        document.BasicIdentity.LayoutStyle = null;
        IdentityHeaderRules.Place(document, _ => 100f);
        return document;
    }

    private static ProfileDocument LegacyAccentPlate()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        title.Text = "the Wanderer";
        title.Italic = true;
        title.Prefix = "✦";
        title.Suffix = "✦";
        title.ZIndex = 50;
        document.Elements.Add(title);
        document.BasicIdentity!.Layout = IdentityTitleLayout.Accent;
        document.BasicIdentity.TitleSource = IdentityTitleSource.Custom;
        document.BasicIdentity.CustomTitle = "the Wanderer";
        document.BasicIdentity.LayoutStyle = null;
        IdentityHeaderRules.Place(document, _ => 100f);
        return document;
    }

    // ---------------------------------------------------------------- does not happen automatically

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningALegacyPlate_NeverConvertsOrDirtiesIt(bool badge)
    {
        var document = badge ? LegacyBadgePlate() : LegacyAccentPlate();
        var expectedJson = JsonSerializer.Serialize(document, JsonOptions.Default);
        var expectedLayout = badge ? IdentityTitleLayout.Badge : IdentityTitleLayout.Accent;

        using var harness = await BasicHarness.OpenDocumentAsync(document);
        harness.SimulateBasicFrame();

        Assert.Equal(expectedJson, harness.Json());
        Assert.False(harness.Session.IsDirty);
        Assert.Equal(expectedLayout, harness.Document.BasicIdentity!.Layout);
    }

    // ---------------------------------------------------------------- the conversion itself

    [Fact]
    public async Task LegacyBadge_ConvertToClassic_RemovesTheBadgeLook_ButKeepsText()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(LegacyBadgePlate());
        harness.SimulateBasicFrame();

        harness.Identity.SetLayout(IdentityTitleLayout.Classic);

        var title = BasicSections.FindText(harness.Document, ProfileElementRole.BasicTitle)!;
        Assert.Equal(IdentityTitleLayout.Classic, harness.Document.BasicIdentity!.Layout);
        Assert.False(title.Bold);
        Assert.Equal(0f, title.LetterSpacing);
        Assert.Equal("the Brave", title.Text);
    }

    [Fact]
    public async Task LegacyAccent_ConvertToClassic_RemovesTheItalicLook_ButKeepsTextAndSymbols()
    {
        using var harness = await BasicHarness.OpenDocumentAsync(LegacyAccentPlate());
        harness.SimulateBasicFrame();

        harness.Identity.SetLayout(IdentityTitleLayout.Classic);

        var title = BasicSections.FindText(harness.Document, ProfileElementRole.BasicTitle)!;
        Assert.Equal(IdentityTitleLayout.Classic, harness.Document.BasicIdentity!.Layout);
        Assert.False(title.Italic);
        Assert.Equal("the Wanderer", title.Text);

        // The undrawable "✦" can't be told apart from a symbol the user picked, so a layout change
        // never removes it on its own — only the separate, explicit "Remove Decoration" action does.
        Assert.Equal("✦", title.Prefix);
        Assert.Equal("✦", title.Suffix);
    }

    // ---------------------------------------------------------------- undo / redo

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConvertToClassic_IsOneUndoStep_AndRedoesExactly(bool badge)
    {
        var document = badge ? LegacyBadgePlate() : LegacyAccentPlate();
        using var harness = await BasicHarness.OpenDocumentAsync(document);
        harness.SimulateBasicFrame();
        var beforeConversion = harness.Json();

        harness.Identity.SetLayout(IdentityTitleLayout.Classic);
        var afterConversion = harness.Json();
        Assert.NotEqual(beforeConversion, afterConversion);

        harness.Session.Undo();
        Assert.Equal(beforeConversion, harness.Json());
        Assert.Equal(badge ? IdentityTitleLayout.Badge : IdentityTitleLayout.Accent, harness.Document.BasicIdentity!.Layout);

        harness.Session.Redo();
        Assert.Equal(afterConversion, harness.Json());
        Assert.Equal(IdentityTitleLayout.Classic, harness.Document.BasicIdentity!.Layout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConvertToClassic_AfterASave_LeavesNoPhantomDirtyState(bool badge)
    {
        using var harness = await BasicHarness.OpenDocumentAsync(badge ? LegacyBadgePlate() : LegacyAccentPlate());
        harness.SimulateBasicFrame();

        harness.Identity.SetLayout(IdentityTitleLayout.Classic);
        Assert.True(harness.Session.IsDirty);

        await harness.Session.SaveProfileAsync();
        harness.Session.SyncWithCurrentProfile();
        Assert.False(harness.Session.IsDirty);
    }
}
