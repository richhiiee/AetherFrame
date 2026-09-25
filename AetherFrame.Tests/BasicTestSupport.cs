using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;

namespace AetherFrame.Tests;

internal sealed class FakeCharacter : ICharacterInfoSource
{
    internal static readonly BasicCharacterInfo Hero = new("Hero Example", "Phoenix", "Light", 19, "Paladin", 100, "ABC");

    public BasicCharacterInfo? CurrentInfo { get; set; }
}

internal sealed class FakeImages : IEditorImageInfo
{
    public (int Width, int Height)? GetNativeSize(Guid assetId) => null;

    public void Clear()
    {
    }
}

/// <summary>Deterministic "font": half the font size per character, always ready.</summary>
internal sealed class FakeMeasurer : IIdentityTextMeasurer
{
    public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
    {
        width = element.GetDisplayText().Length * element.FontSize * 0.5f;
        return true;
    }

    public bool TryCountLines(TextProfileElement element, float fontSize, float maxWidth, out int lines)
    {
        lines = FakeTextWrap.CountLines(element.GetDisplayText(), fontSize * 0.5f, maxWidth);
        return true;
    }
}

/// <summary>The fake font, or — to model fonts that aren't built yet — no font at all.</summary>
internal sealed class SwitchableMeasurer : IIdentityTextMeasurer
{
    private readonly FakeMeasurer font = new();

    internal bool FontReady { get; set; } = true;

    public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
    {
        width = 0f;
        return FontReady && font.TryMeasureNaturalWidth(element, out width);
    }

    public bool TryCountLines(TextProfileElement element, float fontSize, float maxWidth, out int lines)
    {
        lines = 0;
        return FontReady && font.TryCountLines(element, fontSize, maxWidth, out lines);
    }
}

/// <summary>Greedy word wrap for a fixed-advance "font" (every character <c>advance</c> wide), like the renderer's.</summary>
internal static class FakeTextWrap
{
    internal static int CountLines(string text, float advance, float maxWidth)
    {
        var perLine = Math.Max(1, (int)MathF.Floor(maxWidth / advance));
        var lines = 1;
        var used = 0; // characters on the current line
        foreach (var word in text.Split(' '))
        {
            var length = word.Length;
            if (used > 0 && used + 1 + length <= perLine)
            {
                used += 1 + length;
                continue;
            }

            if (used > 0)
            {
                lines++;
            }

            // A word longer than a line breaks inside itself.
            while (length > perLine)
            {
                length -= perLine;
                lines++;
            }

            used = length;
        }

        return lines;
    }
}

/// <summary>A few real FFXIV jobs with their game data row ids and abbreviations.</summary>
internal sealed class FakeJobs : IFavoriteJobSource
{
    internal static readonly FavoriteJob Paladin = new(19, "Paladin", "PLD");
    internal static readonly FavoriteJob WhiteMage = new(24, "White Mage", "WHM");
    internal static readonly FavoriteJob Astrologian = new(33, "Astrologian", "AST");
    internal static readonly FavoriteJob RedMage = new(35, "Red Mage", "RDM");
    internal static readonly FavoriteJob Dancer = new(38, "Dancer", "DNC");
    internal static readonly FavoriteJob Gunbreaker = new(37, "Gunbreaker", "GNB");
    internal static readonly FavoriteJob Gladiator = new(1, "Gladiator", "GLA");

    internal static readonly FavoriteJob[] All = [Paladin, WhiteMage, Astrologian, RedMage, Dancer, Gunbreaker, Gladiator];

    public FavoriteJob? Find(uint jobId) => System.Array.Find(All, job => job.Id == jobId);
}

internal sealed class FakeTitles : IGameTitleSource
{
    internal static readonly GameTitle Prefix = new(7, "the Brave", "the Brave", true, 1);

    public GameTitle? Find(uint titleId) => titleId == Prefix.Id ? Prefix : null;

    public bool FeminineForms => false;
}

internal sealed class FakeSurface : IEditorSurface
{
    public bool IsOpen { get; set; }

    public void Show() => IsOpen = true;

    public void CloseForHandoff() => IsOpen = false;
}

/// <summary>
/// The Basic and Advanced editors' real session stack — PlateLibraryService, ProfileService,
/// EditorSession, BasicIdentitySession, BasicEditorSession, EditorSurfaceCoordinator — over a
/// temporary Plate Library, with fakes only for the game (character, titles) and the renderer.
/// </summary>
internal sealed class BasicHarness : IDisposable
{
    private int frame;

    private BasicHarness(LibraryFixture fixture, PlateLibraryService library)
    {
        Fixture = fixture;
        Library = library;
        Profiles = new ProfileService(library);
        Assets = new AssetStorageService(Fixture.Paths.AssetsDirectory, Fixture.Paths.AssetStagingDirectory, new AssetMetadataStore(Fixture.Paths.AssetMetadataDirectory));
        Session = new EditorSession(Profiles, Assets, new FakeImages(), Fixture.Log, () => ++frame);
        Identity = new BasicIdentitySession(Profiles, Session, Character, Measurer, new FakeTitles());
        Basic = new BasicEditorSession(Profiles, Session, Assets, Identity, Character, new FakeJobs());
        Surfaces = new EditorSurfaceCoordinator(() =>
        {
            Session.CommitPendingEdits();
            Session.EndInteraction();
        });
        Surfaces.Attach(BasicSurface, AdvancedSurface);
    }

    internal LibraryFixture Fixture { get; }

    internal PlateLibraryService Library { get; }

    internal ProfileService Profiles { get; }

    internal AssetStorageService Assets { get; }

    internal EditorSession Session { get; }

    internal BasicIdentitySession Identity { get; }

    internal BasicEditorSession Basic { get; }

    internal FakeCharacter Character { get; } = new();

    /// <summary>The text measurer both Basic sessions use (a deterministic fake font by default).</summary>
    internal SwitchableMeasurer Measurer { get; } = new();

    internal EditorSurfaceCoordinator Surfaces { get; }

    internal FakeSurface BasicSurface { get; } = new();

    internal FakeSurface AdvancedSurface { get; } = new();

    internal Guid PlateId { get; private set; }

    internal ProfileDocument Document => Profiles.CurrentProfile!;

    /// <summary>A new Plate created through My Plates (optionally with starter content), opened.</summary>
    internal static async Task<BasicHarness> CreatePlateAsync(PlateStartingLayout layout, PlateStarterContent? starter, CharacterContext? character = null)
    {
        var harness = await LoadAsync(null);
        var result = await harness.Library.CreatePlateAsync(layout, character, starter: starter);
        harness.Open(result.PlateId);
        return harness;
    }

    /// <summary>An existing Plate file (as some earlier or newer build saved it), opened.</summary>
    internal static async Task<BasicHarness> OpenJsonAsync(string json, Guid plateId)
    {
        var harness = await LoadAsync(fixture => fixture.WritePlateJson(plateId, json));
        harness.Open(plateId);
        return harness;
    }

    internal static Task<BasicHarness> OpenDocumentAsync(ProfileDocument document) =>
        OpenJsonAsync(JsonSerializer.Serialize(document, JsonOptions.Default), document.ProfileId);

    private static async Task<BasicHarness> LoadAsync(Action<LibraryFixture>? seed)
    {
        var fixture = new LibraryFixture();
        seed?.Invoke(fixture);
        return new BasicHarness(fixture, await fixture.LoadAsync());
    }

    private void Open(Guid plateId)
    {
        PlateId = plateId;
        Profiles.OpenPlate(plateId);
        Session.SyncWithCurrentProfile();
    }

    /// <summary>Everything the Basic window does on open and on every frame, minus drawing: it must change nothing.</summary>
    internal void SimulateBasicFrame()
    {
        BasicSurface.IsOpen = true;
        Surfaces.NotifyOpened(EditorSurfaceKind.Basic);
        Session.SyncWithCurrentProfile();
        Basic.Identity.RefineLayout();

        var profile = Document;
        _ = BasicEditorSession.CustomizedSections(profile);
        _ = BasicEditorSession.CanResetLayout(profile);
        _ = BasicEditorSession.GetOrientation(profile);
        _ = Basic.Portrait;
        _ = Basic.CharacterInfo.CurrentInfo;
        foreach (var definition in BasicSections.All)
        {
            _ = BasicSections.IsVisible(profile, definition.Section);
            _ = BasicSections.Exists(profile, definition.Section);
        }

        _ = BasicIdentitySession.GetTitleSource(profile);
        _ = BasicIdentitySession.GetCustomTitle(profile);
        _ = Session.IsDirty;
        Session.CommitPendingEditsIfIdle(anyWidgetActive: false);
    }

    /// <summary>Moves an element the way the Advanced canvas does (drag, then release).</summary>
    internal void DragInAdvanced(ProfileElementRole role, Vector2 by)
    {
        var element = BasicSections.Find(Document, role)!;
        var start = element.Position + (element.Size / 2f);
        Session.BeginDrag(element, start);
        Session.UpdateInteraction(start + by, snap: false, snapThreshold: 0f);
        Session.EndInteraction();
    }

    internal string Json() => JsonSerializer.Serialize(Document, JsonOptions.Default);

    internal int Count(ProfileElementRole role) => Document.Elements.Count(e => e.Role == role);

    internal string ImportablePng(string name = "portrait.png", int width = 400, int height = 640) =>
        TestImages.Write(Path.Combine(Fixture.Root, "user-files"), name, TestImages.Png(width, height));

    public void Dispose() => Fixture.Dispose();
}

/// <summary>Plain documents for exercising the pure Basic rules without a Library.</summary>
internal static class BasicDocuments
{
    internal static ProfileDocument Blank(float width = 1280f, float height = 720f)
    {
        var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "Test", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        document.CanvasWidth = width;
        document.CanvasHeight = height;
        return document;
    }

    internal static ProfileDocument Classic(BasicCharacterInfo? character = null) =>
        PlateFactory.Create(PlateStartingLayout.AdventurePlateClassic, Guid.NewGuid(), "Test", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), new PlateStarterContent(character));

    /// <summary>
    /// A Classic Plate as an earlier version saved it: with the character's level ("Lv. 90") shown
    /// before the Favorite Job, both Basic-managed. Not yet loaded — opening it (e.g.
    /// <see cref="BasicHarness.OpenDocumentAsync"/>) applies the load-time upgrade.
    /// </summary>
    internal static ProfileDocument LegacyClassic(BasicCharacterInfo character)
    {
        var document = Classic(character);
        if (character.Level > 0)
        {
            AddLegacyLevel(document, character.Level);
        }

        return document;
    }

    /// <summary>
    /// Adds a level exactly where earlier versions put one: a "Lv. N" box at the start of the Favorite
    /// Job cell, the job after it, both at their recorded (Basic-managed) placements, and the level
    /// stored in the settings.
    /// </summary>
    internal static void AddLegacyLevel(ProfileDocument document, int level)
    {
        var settings = document.BasicPlate ??= new BasicPlateSettings();
        var job = Find(document, ProfileElementRole.BasicJob);
        var scaleX = AdventurePlateClassicLayout.CanvasScale(document).X;
        var levelWidth = 60f * scaleX;
        var gap = 11f * scaleX;

        var element = new TextProfileElement
        {
            Role = ProfileElementRole.BasicLevel,
            Text = BasicPlateText.Level(level),
            FontFamily = job.FontFamily,
            FontSize = job.FontSize,
            Color = job.Color,
            Position = job.Position,
            Size = new Vector2(levelWidth, job.Size.Y),
            ZIndex = document.Elements.Max(e => e.ZIndex) + 1,
        };
        document.Elements.Add(element);
        settings.Level = level;
        settings.SetPlacement(ProfileElementRole.BasicLevel, new ElementRect(element.Position, element.Size));

        job.Position += new Vector2(levelWidth + gap, 0f);
        job.Size -= new Vector2(levelWidth + gap, 0f);
        settings.SetPlacement(ProfileElementRole.BasicJob, new ElementRect(job.Position, job.Size));

        static TextProfileElement Find(ProfileDocument d, ProfileElementRole role) => BasicSections.FindText(d, role)!;
    }

    internal static BasicPlateEditor Editor(ProfileDocument document) =>
        new(document,
            element =>
            {
                element.ZIndex = document.Elements.Count == 0 ? 0 : document.Elements.Max(e => e.ZIndex) + 1;
                document.Elements.Add(element);
            },
            element => document.Elements.Remove(element));

    internal static ElementRect RectOf(ProfileElement element) => new(element.Position, element.Size);

    /// <summary>Every role-tagged element's placement, for "nothing moved" checks.</summary>
    internal static Dictionary<Guid, ElementRect> Placements(ProfileDocument document) =>
        document.Elements.ToDictionary(e => e.Id, RectOf);
}
