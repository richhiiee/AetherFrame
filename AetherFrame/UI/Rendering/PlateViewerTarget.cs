using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;

namespace AetherFrame.UI.Rendering;

/// <summary>What the Plate Viewer was last asked to show.</summary>
internal enum PlateViewerRequestKind
{
    /// <summary>The default request — nothing specific was asked for: the logged-in character's Active Plate.</summary>
    ActivePlate,

    /// <summary>One specific saved Plate, chosen explicitly (e.g. View in My Plates).</summary>
    Plate,

    /// <summary>A document that isn't a saved Plate (a Template preview).</summary>
    Document,
}

/// <summary>What the Plate Viewer has to present this frame.</summary>
internal enum PlateViewerState
{
    /// <summary><see cref="PlateViewerContent.Document"/> is the Plate to present.</summary>
    Showing,

    /// <summary>A default request, and the character has no Active Plate: the intentional empty state.</summary>
    NoActivePlate,

    /// <summary>A default request with no character logged in.</summary>
    NoCharacter,

    /// <summary>A default request while the Plate Library isn't loaded.</summary>
    LibraryUnavailable,

    /// <summary>The requested Plate (explicit, or the Active one) doesn't exist or can't be read.</summary>
    PlateUnavailable,
}

internal readonly record struct PlateViewerContent(PlateViewerState State, ProfileDocument? Document = null);

/// <summary>
/// The Plate Viewer's request and how it resolves to something to present. An explicit request
/// (a specific Plate, or a Template's document) always wins; with none, the viewer presents the
/// logged-in character's Active Plate via <see cref="ActivePlateResolver"/> — or, when there's no
/// Active Plate, says so. Never picks some other Plate in its place.
///
/// <para>Resolved every frame, so a default request follows Set Active, a save of the Active
/// Plate, a delete of the Active Plate, or a character change while the viewer is open.</para>
///
/// <para><b>Saved vs live.</b> The default request always presents the Active Plate's last
/// <i>saved</i> document — the Plate as the character presents it (and, later, as published) —
/// even while it's open with unsaved edits in Basic or Advanced; saving is what changes it. An
/// explicit Plate request instead shows the editors' live document when that Plate is the one open
/// (so unsaved edits show without a reload). Editor Preview is separate and always live.</para>
/// </summary>
internal sealed class PlateViewerTarget
{
    internal PlateViewerRequestKind Kind { get; private set; } = PlateViewerRequestKind.ActivePlate;

    /// <summary>The explicitly requested Plate (only for <see cref="PlateViewerRequestKind.Plate"/>).</summary>
    internal Guid? PlateId { get; private set; }

    /// <summary>The previewed document (only for <see cref="PlateViewerRequestKind.Document"/>).</summary>
    internal ProfileDocument? Document { get; private set; }

    /// <summary>The default request: whatever the logged-in character's Active Plate is.</summary>
    internal void RequestActivePlate() => Set(PlateViewerRequestKind.ActivePlate, null, null);

    internal void RequestPlate(Guid plateId) => Set(PlateViewerRequestKind.Plate, plateId, null);

    internal void RequestDocument(ProfileDocument document) => Set(PlateViewerRequestKind.Document, null, document);

    /// <param name="activePlates">Resolves the default request.</param>
    /// <param name="savedDocument">A saved Plate's read-only document, or null when it can't be shown.</param>
    /// <param name="liveDocument">The document open in the editors, or null. Used only for an
    /// explicit Plate request, never for the default Active Plate one.</param>
    internal PlateViewerContent Resolve(ActivePlateResolver activePlates, Func<Guid, ProfileDocument?> savedDocument, ProfileDocument? liveDocument)
    {
        switch (Kind)
        {
            case PlateViewerRequestKind.Document:
                return new PlateViewerContent(PlateViewerState.Showing, Document);

            case PlateViewerRequestKind.Plate:
                var plateId = PlateId!.Value;
                var document = liveDocument?.ProfileId == plateId ? liveDocument : savedDocument(plateId);
                return document is null
                    ? new PlateViewerContent(PlateViewerState.PlateUnavailable)
                    : new PlateViewerContent(PlateViewerState.Showing, document);

            default:
                // The saved Active Plate only: unsaved editor changes never show here.
                var active = activePlates.ResolveForCurrentCharacter();
                return active.Status switch
                {
                    ActivePlateStatus.Resolved => new PlateViewerContent(PlateViewerState.Showing, active.Document),
                    ActivePlateStatus.NoActivePlate => new PlateViewerContent(PlateViewerState.NoActivePlate),
                    ActivePlateStatus.NoCharacter => new PlateViewerContent(PlateViewerState.NoCharacter),
                    ActivePlateStatus.LibraryUnavailable => new PlateViewerContent(PlateViewerState.LibraryUnavailable),
                    _ => new PlateViewerContent(PlateViewerState.PlateUnavailable),
                };
        }
    }

    private void Set(PlateViewerRequestKind kind, Guid? plateId, ProfileDocument? document)
    {
        Kind = kind;
        PlateId = plateId;
        Document = document;
    }
}
