using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>The Adventure Plate section rules, directly on documents.</summary>
public class BasicPlateEditorTests
{
    private static readonly Vector2 Moved = new(13, 17);

    private static ElementRect LayoutRect(ProfileDocument document, ProfileElementRole role) =>
        AdventurePlateClassicLayout.GetRect(role, BasicPlateEditor.GetOrientation(document), document)!.Value;

    private static void MoveElsewhere(ProfileDocument document, ProfileElementRole role) =>
        BasicSections.Find(document, role)!.Position += Moved;

    // ---------------------------------------------------------------- creation and reuse

    [Fact]
    public void SettingAValue_CreatesItsSectionOnce_ThenReusesIt()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.SetText(ProfileElementRole.BasicWorld, "Phoenix");
        var world = BasicSections.Find(document, ProfileElementRole.BasicWorld)!;
        editor.SetText(ProfileElementRole.BasicWorld, "Odin");
        editor.EnsureSection(BasicSection.World);
        editor.SetSectionVisible(BasicSection.World, true);
        editor.ResetSection(BasicSection.World);
        editor.ApplyLayout(BasicSection.World);

        Assert.Single(document.Elements, e => e.Role == ProfileElementRole.BasicWorld);
        Assert.Single(document.Elements, e => e.Role == ProfileElementRole.BasicWorldHeading);
        Assert.Equal(2, document.Elements.Count);
        Assert.Same(world, BasicSections.Find(document, ProfileElementRole.BasicWorld));
        Assert.Equal("Odin", ((TextProfileElement)world).Text);
    }

    [Fact]
    public void ExistingElements_AreBoundWhereTheyAre_AndNothingElseIsCreated()
    {
        var document = BasicDocuments.Blank();
        var message = new TextProfileElement { Role = ProfileElementRole.BasicMessage, Text = "Old", Position = new Vector2(5, 6), Size = new Vector2(300, 90) };
        document.Elements.Add(message);

        BasicDocuments.Editor(document).SetText(ProfileElementRole.BasicMessage, "New");

        Assert.Same(message, Assert.Single(document.Elements));
        Assert.Equal("New", message.Text);
        Assert.Equal(new Vector2(5, 6), message.Position);
        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicMessageHeading));
    }

    [Fact]
    public void NewElements_AreRecordedAsFollowingTheLayout()
    {
        var document = BasicDocuments.Blank();
        BasicDocuments.Editor(document).EnsureSection(BasicSection.Job);

        var job = BasicSections.Find(document, ProfileElementRole.BasicJob)!;
        Assert.Equal(LayoutRect(document, ProfileElementRole.BasicJob), BasicDocuments.RectOf(job));
        Assert.True(BasicPlateEditor.IsManaged(document, job));
        Assert.False(BasicPlateEditor.IsSectionCustomized(document, BasicSection.Job));
    }

    [Fact]
    public void ThePortrait_IsNeverCreatedWithoutAnImage()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.EnsureSection(BasicSection.Portrait);
        editor.SetSectionVisible(BasicSection.Portrait, true);
        editor.ApplyLayoutToAll();

        Assert.Empty(document.Elements);
    }

    // ---------------------------------------------------------------- visibility

    [Fact]
    public void Hiding_KeepsContent_AndShowingRestoresIt()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);

        editor.SetSectionVisible(BasicSection.FreeCompany, false);

        Assert.False(BasicSections.IsVisible(document, BasicSection.FreeCompany));
        Assert.All(BasicPlateEditor.RolesOf(BasicSection.FreeCompany), role => Assert.False(BasicSections.Find(document, role)!.Visible));
        Assert.Equal("«ABC»", BasicSections.FindText(document, ProfileElementRole.BasicFreeCompany)!.Text);
        Assert.True(BasicSections.IsVisible(document, BasicSection.World));

        editor.SetSectionVisible(BasicSection.FreeCompany, true);
        Assert.True(BasicSections.IsVisible(document, BasicSection.FreeCompany));
    }

    // ---------------------------------------------------------------- customization

    [Fact]
    public void MovingAnElement_MakesOnlyItsSectionCustomized()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        Assert.Empty(BasicPlateEditor.CustomizedSections(document));

        MoveElsewhere(document, ProfileElementRole.BasicWorldHeading);

        Assert.Equal([BasicSection.World], BasicPlateEditor.CustomizedSections(document));
    }

    [Fact]
    public void ContentAndStyleEdits_NeverMoveACustomizedSection()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        MoveElsewhere(document, ProfileElementRole.BasicWorld);
        var before = BasicDocuments.Placements(document);

        editor.SetText(ProfileElementRole.BasicWorld, "Odin [Light]");
        editor.SetSectionVisible(BasicSection.World, false);
        editor.SetSectionVisible(BasicSection.World, true);
        editor.ApplyTheme(ProfileThemePresets.All[2]);
        editor.SetFavoriteJobs([FakeJobs.WhiteMage, FakeJobs.RedMage]);
        editor.SetPlaystyles(["Casual"]);

        // Nothing moves.
        foreach (var (id, rect) in before)
        {
            Assert.Equal(rect, BasicDocuments.RectOf(document.Elements.Single(e => e.Id == id)));
        }

        Assert.True(BasicPlateEditor.IsSectionCustomized(document, BasicSection.World));
    }

    [Fact]
    public void ApplyLayout_BringsACustomizedSectionBack_AndTouchesNothingElse()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var freeform = new TextProfileElement { Text = "Mine", Position = new Vector2(500, 500), Size = new Vector2(100, 40) };
        document.Elements.Add(freeform);
        MoveElsewhere(document, ProfileElementRole.BasicWorld);
        MoveElsewhere(document, ProfileElementRole.BasicJob);

        BasicDocuments.Editor(document).ApplyLayout(BasicSection.World);

        Assert.Equal(LayoutRect(document, ProfileElementRole.BasicWorld), BasicDocuments.RectOf(BasicSections.Find(document, ProfileElementRole.BasicWorld)!));
        Assert.Equal([BasicSection.Job], BasicPlateEditor.CustomizedSections(document));
        Assert.Equal(new Vector2(500, 500), freeform.Position);
    }

    // ---------------------------------------------------------------- orientation

    [Fact]
    public void Mirroring_MovesOnlySectionsStillFollowingTheLayout()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        MoveElsewhere(document, ProfileElementRole.BasicFreeCompany);
        var customizedAt = BasicSections.Find(document, ProfileElementRole.BasicFreeCompany)!.Position;

        var leftInPlace = editor.SetOrientation(AdventurePlateOrientation.Mirrored);

        Assert.Equal(1, leftInPlace);
        Assert.Equal(AdventurePlateOrientation.Mirrored, document.BasicPlate!.Orientation);
        Assert.Equal(40f, BasicSections.Find(document, ProfileElementRole.BasicWorld)!.Position.X);
        Assert.Equal(customizedAt, BasicSections.Find(document, ProfileElementRole.BasicFreeCompany)!.Position);
        Assert.Equal([BasicSection.FreeCompany], BasicPlateEditor.CustomizedSections(document));

        editor.SetOrientation(AdventurePlateOrientation.Normal);
        Assert.Equal(480f, BasicSections.Find(document, ProfileElementRole.BasicWorld)!.Position.X);
        Assert.Equal(customizedAt, BasicSections.Find(document, ProfileElementRole.BasicFreeCompany)!.Position);
    }

    // ---------------------------------------------------------------- reset

    [Fact]
    public void ResetSection_RestoresPlacementAndStyle_KeepsContent_AndIsolatesOtherSections()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorld)!;
        var heading = BasicSections.FindText(document, ProfileElementRole.BasicWorldHeading)!;
        var job = BasicSections.FindText(document, ProfileElementRole.BasicJob)!;
        MoveElsewhere(document, ProfileElementRole.BasicWorld);
        MoveElsewhere(document, ProfileElementRole.BasicJob);
        world.FontSize = 60;
        world.Visible = false;
        heading.Text = "Renamed";
        job.FontSize = 44;
        var jobRect = BasicDocuments.RectOf(job);

        editor.ResetSection(BasicSection.World);

        Assert.Equal(LayoutRect(document, ProfileElementRole.BasicWorld), BasicDocuments.RectOf(world));
        Assert.Equal(20f, world.FontSize);
        Assert.Equal("Phoenix [Light]", world.Text);
        Assert.False(world.Visible);
        Assert.Equal("HOME WORLD", heading.Text);
        Assert.Equal(44f, job.FontSize);
        Assert.Equal(jobRect, BasicDocuments.RectOf(job));
    }

    [Fact]
    public void ResetSection_RestoresAMissingHeading()
    {
        var document = BasicDocuments.Classic();
        document.Elements.Remove(BasicSections.Find(document, ProfileElementRole.BasicMessageHeading)!);

        BasicDocuments.Editor(document).ResetSection(BasicSection.Message);

        Assert.Single(document.Elements, e => e.Role == ProfileElementRole.BasicMessageHeading);
    }

    [Fact]
    public void ResetLayout_RestoresNormalPlacement_ForBasicSectionsOnly()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        var freeform = new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(1, 2), Size = new Vector2(30, 40), RotationDegrees = 15 };
        document.Elements.Add(freeform);
        var freeformBefore = freeform.Clone();
        editor.CreatePortrait(Guid.NewGuid());
        editor.SetOrientation(AdventurePlateOrientation.Mirrored);
        MoveElsewhere(document, ProfileElementRole.BasicWorld);
        MoveElsewhere(document, ProfileElementRole.BasicPortrait);
        var count = document.Elements.Count;

        editor.ResetLayout();

        Assert.Equal(AdventurePlateOrientation.Normal, document.BasicPlate!.Orientation);
        Assert.Empty(BasicPlateEditor.CustomizedSections(document));
        Assert.Equal(40f, BasicSections.Find(document, ProfileElementRole.BasicPortrait)!.Position.X);
        Assert.Equal(count, document.Elements.Count);
        Assert.True(freeform.ContentEquals(freeformBefore));
    }

    // ---------------------------------------------------------------- structured content

    [Fact]
    public void Playstyles_KeepAtMostSix_NoDuplicates_InOrder()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.SetPlaystyles(["Casual", "casual", " ", "Raiding", "PvP", "Glamour", "Housing", "Hunts", "Crafting"]);

        Assert.Equal(["Casual", "Raiding", "PvP", "Glamour", "Housing", "Hunts"], document.BasicPlate!.Playstyles);
        Assert.False(editor.AddPlaystyle("Gathering"));
        Assert.Equal(BasicPlateSettings.MaxPlaystyles, document.BasicPlate.Playstyles.Count);

        editor.RemovePlaystyleAt(0);
        editor.MovePlaystyle(0, 1);
        Assert.True(editor.AddPlaystyle("Gathering"));
        Assert.False(editor.AddPlaystyle("GATHERING"));

        Assert.Equal(["PvP", "Raiding", "Glamour", "Housing", "Hunts", "Gathering"], document.BasicPlate.Playstyles);
        Assert.Equal(BasicPlateText.Playstyles(document.BasicPlate.Playstyles), BasicSections.FindText(document, ProfileElementRole.BasicPlaystyle)!.Text);
    }

    [Fact]
    public void ActiveHours_AreNormalized_AndClearedToAnEmptyText()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.SetActiveHours(new BasicActiveHours { Days = (BasicWeekdays)0xFFF, StartMinutes = -60, EndMinutes = 1500, TimeZone = "  A very long time zone name  " });

        var stored = document.BasicPlate!.ActiveHours!;
        Assert.Equal(BasicWeekdays.Everyday, stored.Days);
        Assert.Equal(23 * 60, stored.StartMinutes);
        Assert.Equal(60, stored.EndMinutes);
        Assert.Equal(BasicActiveHours.MaxTimeZoneLength, stored.TimeZone.Length);
        Assert.StartsWith("Every day", BasicSections.FindText(document, ProfileElementRole.BasicActiveHours)!.Text);

        editor.SetActiveHours(null);
        Assert.Null(document.BasicPlate.ActiveHours);
        Assert.Equal(string.Empty, BasicSections.FindText(document, ProfileElementRole.BasicActiveHours)!.Text);
        Assert.False(BasicSections.HasVisibleContent(document, BasicSection.ActiveHours));
    }

    [Fact]
    public void FavoriteJobs_StoreStructuredValues_AndTheirText_AndNeverALevel()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);

        editor.SetFavoriteJobs([FakeJobs.WhiteMage]);

        Assert.Equal(24u, document.BasicPlate!.FavoriteJobId);
        Assert.Equal([24u], document.BasicPlate.FavoriteJobIds);
        Assert.Equal("White Mage", BasicSections.FindText(document, ProfileElementRole.BasicJob)!.Text);
        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel));
        Assert.Equal(0, document.BasicPlate.Level);
    }

    // ---------------------------------------------------------------- portrait

    [Fact]
    public void Portrait_IsAnImportedImage_PlacedByTheLayout_AndRemovable()
    {
        var document = BasicDocuments.Blank();
        var editor = BasicDocuments.Editor(document);
        var asset = Guid.NewGuid();

        var portrait = editor.CreatePortrait(asset);

        Assert.Equal(asset, portrait.AssetId);
        Assert.Equal(ProfileImageFit.Fill, portrait.DisplayMode);
        Assert.Equal(BasicPortraitSource.ImportedImage, document.BasicPlate!.PortraitSource);
        Assert.True(BasicPlateEditor.IsManaged(document, portrait));
        Assert.Throws<InvalidOperationException>(() => editor.CreatePortrait(Guid.NewGuid()));

        editor.RemovePortrait();
        Assert.Empty(document.Elements);
        Assert.Null(document.BasicPlate.GetPlacement(ProfileElementRole.BasicPortrait));
    }

    // ---------------------------------------------------------------- theme

    [Fact]
    public void Theme_FillsEditableValues_KeepsOpacity_AndAnImageBackground()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        var pastel = ProfileThemePresets.All[1];
        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorld)!;
        world.Color = world.Color with { W = 0.5f };
        var freeform = new TextProfileElement { Text = "Mine", Color = new Vector4(1, 0, 0, 1) };
        document.Elements.Add(freeform);

        editor.ApplyTheme(pastel);

        Assert.Equal(pastel.PrimaryColor, document.Background!.PrimaryColor);
        Assert.Equal(pastel.TextColor with { W = 0.5f }, world.Color);
        Assert.Equal(pastel.AccentTextColor, BasicSections.FindText(document, ProfileElementRole.BasicWorldHeading)!.Color);
        Assert.Equal(pastel.PreferredNameColor, BasicSections.FindText(document, ProfileElementRole.BasicName)!.Color); // the name's own treatment
        Assert.Equal(new Vector4(1, 0, 0, 1), freeform.Color);
        Assert.Equal(pastel.Id, document.BasicPlate!.ThemeId);

        // Still editable afterwards: nothing is locked to the preset.
        world.Color = new Vector4(0, 1, 0, 1);
        Assert.Equal(new Vector4(0, 1, 0, 1), world.Color);

        document.Background.Mode = ProfileBackgroundMode.Image;
        document.Background.ImageAssetId = Guid.NewGuid();
        editor.ApplyTheme(ProfileThemePresets.All[3]);
        Assert.Equal(ProfileBackgroundMode.Image, document.Background.Mode);
        Assert.Equal(ProfileThemePresets.All[3].PrimaryColor, document.Background.PrimaryColor);
    }

    [Fact]
    public void ResetSection_UsesTheLastAppliedThemesColors()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(ProfileThemePresets.All[1]);
        var world = BasicSections.FindText(document, ProfileElementRole.BasicWorld)!;
        world.Color = new Vector4(0, 1, 0, 1);

        editor.ResetSection(BasicSection.World);

        Assert.Equal(ProfileThemePresets.All[1].TextColor, world.Color);
    }
}

/// <summary>What a brand-new Adventure Plate Classic starts with.</summary>
public class AdventurePlateStarterTests
{
    [Fact]
    public void WithACharacter_EverySectionIsInPlace_AndFilledWhereKnown()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);

        Assert.Equal("Hero Example", BasicSections.FindText(document, ProfileElementRole.BasicName)!.Text);
        Assert.Equal("Phoenix [Light]", BasicSections.FindText(document, ProfileElementRole.BasicWorld)!.Text);
        Assert.Equal("Paladin", BasicSections.FindText(document, ProfileElementRole.BasicJob)!.Text);
        Assert.Equal(BasicFavoriteJobs.SingularHeading, BasicSections.FindText(document, ProfileElementRole.BasicJobHeading)!.Text);
        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicLevel)); // no level on a new Plate
        Assert.Equal("«ABC»", BasicSections.FindText(document, ProfileElementRole.BasicFreeCompany)!.Text);
        Assert.Equal(19u, document.BasicPlate!.FavoriteJobId);
        Assert.Equal([19u], document.BasicPlate.FavoriteJobIds);
        Assert.Equal(0, document.BasicPlate.Level);
        Assert.Equal(ProfileThemePresets.All[0].Id, document.BasicPlate.ThemeId);

        foreach (var section in new[] { BasicSection.Playstyle, BasicSection.ActiveHours, BasicSection.Message })
        {
            Assert.True(BasicSections.Exists(document, section));
            Assert.False(BasicSections.HasVisibleContent(document, section));
        }

        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicPortrait));
        Assert.Null(BasicSections.Find(document, ProfileElementRole.BasicTitle));
        Assert.Empty(BasicPlateEditor.CustomizedSections(document));
        Assert.NotNull(document.BasicIdentity!.AppliedLayout);
        Assert.Equal(IdentityTitleSource.None, document.BasicIdentity.TitleSource);
        Assert.Equal(document.Elements.Count, document.Elements.Select(e => e.ZIndex).Distinct().Count());
        Assert.All(document.Elements, e => Assert.Single(document.Elements, o => o.Role == e.Role));
    }

    [Fact]
    public void WithoutACharacter_SectionsExistButAreEmpty_AndNothingIsGuessed()
    {
        var document = BasicDocuments.Classic(null);

        Assert.Equal(string.Empty, BasicSections.FindText(document, ProfileElementRole.BasicName)!.Text);
        Assert.Equal(string.Empty, BasicSections.FindText(document, ProfileElementRole.BasicWorld)!.Text);
        Assert.Equal(0, document.BasicPlate!.Level);
        Assert.All(document.Elements.Where(e => BasicSections.IsHeading(e.Role)), heading =>
            Assert.False(BasicSections.IsDrawnInFinishedRendering(document, heading)));
    }

    [Fact]
    public void NotInAFreeCompany_LeavesItEmpty()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero with { FreeCompanyTag = string.Empty });

        Assert.Equal(string.Empty, BasicSections.FindText(document, ProfileElementRole.BasicFreeCompany)!.Text);
        Assert.False(BasicSections.HasVisibleContent(document, BasicSection.FreeCompany));
    }

    [Fact]
    public void WithoutStarterContent_TheBareDocumentIsUnchanged()
    {
        var bare = Domain.Plates.PlateFactory.Create(Domain.Plates.PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "x", DateTime.UtcNow);
        var blank = Domain.Plates.PlateFactory.Create(Domain.Plates.PlateStartingLayout.Blank, Guid.NewGuid(), "x", DateTime.UtcNow, new Domain.Plates.PlateStarterContent(FakeCharacter.Hero));

        Assert.Empty(bare.Elements);
        Assert.Null(bare.BasicPlate);
        Assert.Empty(blank.Elements);
        Assert.Null(blank.BasicPlate);
    }
}
