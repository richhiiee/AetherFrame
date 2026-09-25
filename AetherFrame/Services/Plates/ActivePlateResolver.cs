using System;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Services.Plates;

/// <summary>Why an Active Plate resolution did or didn't produce a Plate to present.</summary>
internal enum ActivePlateStatus
{
    /// <summary>The character's Active Plate exists and its saved document is ready.</summary>
    Resolved,

    /// <summary>The Plate Library isn't loaded (still loading, or its load failed).</summary>
    LibraryUnavailable,

    /// <summary>No character is logged in, so there's no one to have an Active Plate.</summary>
    NoCharacter,

    /// <summary>The character has no Active Plate (never chosen, deleted, or pointing at a Plate
    /// that no longer exists).</summary>
    NoActivePlate,

    /// <summary>The Active Plate exists but can't be shown (damaged, or saved by a newer build).</summary>
    PlateUnavailable,
}

/// <summary>The outcome of resolving a character's Active Plate. <see cref="PlateId"/> is set for
/// <see cref="ActivePlateStatus.Resolved"/> and <see cref="ActivePlateStatus.PlateUnavailable"/>;
/// <see cref="Document"/> (the Library's read-only saved copy — never mutate it) only for Resolved.</summary>
internal readonly record struct ActivePlateResolution(ActivePlateStatus Status, Guid? PlateId = null, ProfileDocument? Document = null)
{
    internal bool IsResolved => Status == ActivePlateStatus.Resolved;
}

/// <summary>
/// The one read path for "the Plate AetherFrame presents for this character when nothing more
/// specific was asked for": character → the binding's stored ActivePlateId → that Plate's saved
/// document. Read-only: it never writes a binding, never replaces or repairs a stale
/// ActivePlateId, and never falls back to some other Plate — no Active Plate resolves to none.
///
/// <para>Everything that needs a default Plate (the Plate Viewer's default request, and whatever
/// later presents a character's published Active Plate) resolves through here rather than
/// re-deriving it from bindings.</para>
/// </summary>
internal sealed class ActivePlateResolver
{
    private readonly PlateLibraryService library;
    private readonly Func<CharacterContext?> currentCharacter;

    /// <param name="currentCharacter">The logged-in character, or null when none is.</param>
    internal ActivePlateResolver(PlateLibraryService library, Func<CharacterContext?> currentCharacter)
    {
        this.library = library;
        this.currentCharacter = currentCharacter;
    }

    /// <summary>The logged-in character's Active Plate. Safe from any thread, including ImGui Draw.</summary>
    internal ActivePlateResolution ResolveForCurrentCharacter() => Resolve(currentCharacter());

    /// <summary>A given character's Active Plate; null <paramref name="character"/> means nobody is logged in.</summary>
    internal ActivePlateResolution Resolve(CharacterContext? character)
    {
        if (!library.IsLoaded)
        {
            return new ActivePlateResolution(ActivePlateStatus.LibraryUnavailable);
        }

        if (character is not { } who)
        {
            return new ActivePlateResolution(ActivePlateStatus.NoCharacter);
        }

        // Already ignores an ActivePlateId whose Plate no longer exists.
        if (library.GetActivePlateId(who.ContentId) is not { } plateId)
        {
            return new ActivePlateResolution(ActivePlateStatus.NoActivePlate);
        }

        return library.GetSavedDocument(plateId) is { } document
            ? new ActivePlateResolution(ActivePlateStatus.Resolved, plateId, document)
            : new ActivePlateResolution(ActivePlateStatus.PlateUnavailable, plateId);
    }
}
