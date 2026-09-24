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
        Identity = new BasicIdentitySession(Profiles, Session, Character, new FakeMeasurer(), new FakeTitles());
        Basic = new BasicEditorSession(Profiles, Session, Assets, Identity, Character);
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
