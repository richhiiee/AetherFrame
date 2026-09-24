using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Tagline is no longer part of Basic mode: new Plates never get one, the Identity Header is
/// Character Name and Title, and an older Plate's tagline is preserved as ordinary Advanced content
/// that no Basic action reads, places, restyles, or deletes.
/// </summary>
public class TaglineRetirementTests
{
    private static readonly ElementRect LegacyTaglineRect = new(new Vector2(480, 150), new Vector2(760, 26));

    /// <summary>A Classic Plate as an earlier build saved it: with a tagline under the header, recorded in its applied layout.</summary>
    private static ProfileDocument LegacyPlateWithTagline()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.Elements.Add(new TextProfileElement
        {
            Role = ProfileElementRole.BasicTagline,
            Text = "Always exploring",
            Italic = true,
            FontSize = 15,
            Position = LegacyTaglineRect.Position,
            Size = LegacyTaglineRect.Size,
            ZIndex = document.Elements.Max(e => e.ZIndex) + 1,
        });
        document.BasicIdentity!.AppliedLayout!.Tagline = LegacyTaglineRect;
        return document;
    }

    private static Task<BasicHarness> OpenLegacyAsync() => BasicHarness.OpenDocumentAsync(LegacyPlateWithTagline());

    private static TextProfileElement Tagline(ProfileDocument document) => BasicSections.FindText(document, ProfileElementRole.BasicTagline)!;

    // ---------------------------------------------------------------- new Plates

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewClassicPlate_HasNoTagline_ThroughEveryIdentityAction(bool withCharacter)
    {
        using var harness = await BasicHarness.CreatePlateAsync(
            PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(withCharacter ? FakeCharacter.Hero : null));
        Assert.Equal(0, harness.Count(ProfileElementRole.BasicTagline));

        harness.Identity.SetCustomTitle("the Wanderer");
        harness.Identity.Commit();
        foreach (var layout in Enum.GetValues<IdentityTitleLayout>())
        {
            harness.Identity.SetLayout(layout);
        }

        harness.Basic.ResetSection(BasicSection.Identity);
        harness.Basic.ApplyLayout();
        harness.Basic.ResetBasicLayout();

        Assert.Equal(0, harness.Count(ProfileElementRole.BasicTagline));
        Assert.Null(harness.Document.BasicIdentity!.AppliedLayout!.Tagline);
        Assert.All(harness.Document.Elements, e => Assert.NotEqual("Add a tagline", EditorPlaceholders.GetPlaceholder(e)));
    }

    [Fact]
    public void BasicMode_ExposesNoTaglineControlModel()
    {
        Assert.Equal([ProfileElementRole.BasicName, ProfileElementRole.BasicTitle], BasicSections.Get(BasicSection.Identity).Values);
        Assert.Null(BasicSections.SectionOf(ProfileElementRole.BasicTagline));

        var members = typeof(BasicIdentitySession).GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(members, m => m.Name.Contains("Tagline", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IdentityHeaderLayout.Result).GetProperties(), p => p.Name.Contains("Tagline", StringComparison.OrdinalIgnoreCase));

        var legacy = LegacyPlateWithTagline();
        Assert.Equal(["Hero Example"], BasicEditorView.SummaryOf(legacy, BasicEditorCategory.Identity));
        Tagline(legacy).Visible = false;
        Assert.False(BasicEditorView.StatusOf(legacy, BasicEditorCategory.Identity).Hidden);
    }

    // ---------------------------------------------------------------- the reworked header

    [Theory]
    [InlineData((int)AdventurePlateOrientation.Normal)]
    [InlineData((int)AdventurePlateOrientation.Mirrored)]
    public async Task EveryIdentityLayout_KeepsNameAndTitleApart_AndClearOfHomeWorld(int orientationValue)
    {
        using var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Basic.SetOrientation((AdventurePlateOrientation)orientationValue);
        harness.Identity.SetCustomTitle("the Warrior of Light");
        harness.Identity.Commit();

        foreach (var layout in Enum.GetValues<IdentityTitleLayout>())
        {
            harness.Identity.SetLayout(layout);
            var document = harness.Document;
            var name = BasicSections.Find(document, ProfileElementRole.BasicName)!;
            var title = BasicSections.Find(document, ProfileElementRole.BasicTitle)!;
            var worldHeading = BasicSections.Find(document, ProfileElementRole.BasicWorldHeading)!;
            var headerBottom = Math.Max(name.Position.Y + name.Size.Y, title.Position.Y + title.Size.Y);

            // Generous separation from the first details row (the overlap seen in game).
            Assert.True(worldHeading.Position.Y - headerBottom >= 40f, $"{layout}: only {worldHeading.Position.Y - headerBottom} above Home World");

            // The header stays inside its reserved area, clear of the details panel's first row.
            var reserved = AdventurePlateClassicLayout.GetGroupBounds(BasicSection.Identity, (AdventurePlateOrientation)orientationValue, document);
            Assert.Equal(reserved, reserved.Union(BasicDocuments.RectOf(name)).Union(BasicDocuments.RectOf(title)));

            // Stacked layouts: a visible gap between the name and the title.
            if (layout is not (IdentityTitleLayout.InlineBefore or IdentityTitleLayout.InlineAfter))
            {
                var (upper, lower) = name.Position.Y < title.Position.Y ? (name, title) : (title, name);
                Assert.True(lower.Position.Y - (upper.Position.Y + upper.Size.Y) >= 6f, $"{layout}: name and title touch");
            }

            // The name stays the anchor: larger than the title in every layout.
            Assert.True(((TextProfileElement)name).FontSize > ((TextProfileElement)title).FontSize * 1.5f);
            Assert.True(((TextProfileElement)name).Bold);
            Assert.False(BasicEditorSession.IsSectionCustomized(document, BasicSection.Identity));
        }
    }

    // ---------------------------------------------------------------- existing Taglines

    [Fact]
    public async Task OpeningALegacyPlateWithATagline_ChangesNothing()
    {
        var document = LegacyPlateWithTagline();
        var json = JsonSerializer.Serialize(document, JsonOptions.Default);
        using var harness = await BasicHarness.OpenJsonAsync(json, document.ProfileId);
        var before = harness.Json();

        harness.SimulateBasicFrame();
        harness.SimulateBasicFrame();

        Assert.Equal(before, harness.Json());
        Assert.Equal(json, harness.Fixture.ReadPlateJson(document.ProfileId));
        Assert.False(harness.Session.IsDirty);
        Assert.Equal("Always exploring", Tagline(harness.Document).Text);
    }

    [Fact]
    public async Task ALegacyTagline_SurvivesBasicEditsSaveAndReload()
    {
        using var harness = await OpenLegacyAsync();
        var original = Tagline(harness.Document).Clone();

        harness.Basic.SetText(ProfileElementRole.BasicWorld, "Odin [Light]");
        harness.Basic.CommitTextEdit();
        harness.Identity.SetCustomTitle("the Wanderer");
        harness.Identity.Commit();
        Assert.True(await harness.Session.SaveProfileAsync());

        var reloaded = harness.Library.OpenDocumentForEditing(harness.PlateId);
        Assert.True(Tagline(reloaded).ContentEquals(original));
        Assert.Single(reloaded.Elements, e => e.Role == ProfileElementRole.BasicTagline);
    }

    [Fact]
    public async Task ALegacyTagline_StaysEditableInAdvanced()
    {
        using var harness = await OpenLegacyAsync();
        var id = Tagline(harness.Document).Id;

        harness.Session.ApplyImmediateEdit(id, e => ((TextProfileElement)e).Text = "Edited in Advanced");
        harness.DragInAdvanced(ProfileElementRole.BasicTagline, new Vector2(0, 300));
        harness.Session.SetElementVisible(id, false);

        var tagline = Tagline(harness.Document);
        Assert.Equal("Edited in Advanced", tagline.Text);
        Assert.Equal(LegacyTaglineRect.Position + new Vector2(0, 300), tagline.Position);
        Assert.False(tagline.Visible);

        harness.Session.RemoveElement(id);
        Assert.Equal(0, harness.Count(ProfileElementRole.BasicTagline));
        harness.Session.Undo();
        Assert.Equal(1, harness.Count(ProfileElementRole.BasicTagline));
    }

    [Fact]
    public async Task EveryBasicLayoutAction_LeavesALegacyTaglineExactlyAsItWas()
    {
        using var harness = await OpenLegacyAsync();
        var original = Tagline(harness.Document).Clone();

        foreach (var action in new Action[]
        {
            () => harness.Basic.ApplyLayout(),
            () => harness.Basic.ApplySectionLayout(BasicSection.Identity),
            () => harness.Basic.ResetSection(BasicSection.Identity),
            () => harness.Basic.ResetBasicLayout(),
            () => harness.Basic.SetOrientation(AdventurePlateOrientation.Mirrored),
            () => harness.Basic.SetOrientation(AdventurePlateOrientation.Normal),
            () => harness.Identity.SetLayout(IdentityTitleLayout.Classic),
            () => harness.Identity.SetLayout(IdentityTitleLayout.InlineAfter),
            () => harness.Basic.ApplyTheme(ProfileThemePresets.All[2]),
        })
        {
            action();
            Assert.True(Tagline(harness.Document).ContentEquals(original));
        }

        Assert.Null(harness.Document.BasicIdentity!.AppliedLayout!.Tagline);
    }

    // ---------------------------------------------------------------- ownership

    [Fact]
    public async Task MovingALegacyTagline_DoesntCustomizeIdentity_ButMovingNameOrTitleDoes()
    {
        using var harness = await OpenLegacyAsync();
        harness.Identity.SetCustomTitle("the Wanderer");
        harness.Identity.Commit();
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));

        harness.DragInAdvanced(ProfileElementRole.BasicTagline, new Vector2(20, 400));
        Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
        Assert.Empty(BasicEditorSession.CustomizedSections(harness.Document));

        foreach (var role in new[] { ProfileElementRole.BasicName, ProfileElementRole.BasicTitle })
        {
            harness.DragInAdvanced(role, new Vector2(0, 5));
            Assert.True(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
            harness.Session.Undo();
            Assert.False(BasicEditorSession.IsSectionCustomized(harness.Document, BasicSection.Identity));
        }
    }

    [Fact]
    public void AnOldSnapshotEntry_ForAMovedTagline_DoesntCustomizeIdentity()
    {
        var document = LegacyPlateWithTagline();
        Tagline(document).Position += new Vector2(0, 90);

        Assert.False(BasicPlateEditor.IsSectionCustomized(document, BasicSection.Identity));
    }

    [Fact]
    public void ALegacyTagline_IsntPartOfIdentitysBounds_OrItsCollisions()
    {
        var document = LegacyPlateWithTagline();
        var world = BasicSections.Find(document, ProfileElementRole.BasicWorldHeading)!;

        // Even sitting right on the Home World row, it's Advanced content: not a Basic collision.
        Tagline(document).Position = world.Position;

        var identity = BasicPlateEditor.CurrentGroupBounds(document, BasicSection.Identity)!.Value;
        Assert.False(identity.Intersects(BasicDocuments.RectOf(world)));
        Assert.Empty(BasicPlateEditor.FindOverlaps(document));
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public void PreviewClicks_OnNameAndTitle_OpenIdentity_ButALegacyTaglineIsLookedThrough()
    {
        var document = LegacyPlateWithTagline();
        document.BasicIdentity!.TitleSource = IdentityTitleSource.Custom;
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        title.Text = "the Wanderer";
        title.Position = new Vector2(480, 110);
        title.Size = new Vector2(760, 35);
        document.Elements.Add(title);
        var buffer = new List<ProfileElement>();

        Vector2 Center(ProfileElement e) => e.Position + (e.Size / 2f);

        Assert.Equal(BasicEditorCategory.Identity, BasicEditorView.CategoryAt(document, Center(BasicSections.Find(document, ProfileElementRole.BasicName)!), buffer));
        Assert.Equal(BasicEditorCategory.Identity, BasicEditorView.CategoryAt(document, Center(title), buffer));
        Assert.Null(BasicEditorView.CategoryAt(document, Center(Tagline(document)), buffer));
    }
}
