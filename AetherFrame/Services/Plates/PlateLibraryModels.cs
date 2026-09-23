using System;
using System.Collections.Generic;

namespace AetherFrame.Services.Plates;

internal enum PlateStatus
{
    /// <summary>Loaded and fully usable.</summary>
    Ready,

    /// <summary>Saved by a newer AetherFrame. Listed, but never opened, edited, or rewritten.</summary>
    NewerVersion,

    /// <summary>The file (and any backup of it) couldn't be read. Listed so it isn't silently lost.</summary>
    Unreadable,
}

/// <summary>An immutable snapshot of one Plate for the Library UI.</summary>
internal sealed record PlateSummary(
    Guid PlateId,
    PlateStatus Status,
    string DisplayName,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    int Revision,
    string? Problem,
    IReadOnlyList<string> CharacterNames,
    IReadOnlyList<ulong> ActiveForContentIds)
{
    internal bool IsReady => Status == PlateStatus.Ready;
}

/// <summary>The logged-in character, as far as the Library needs to know. ContentId is only the
/// local binding key; name and World are stored as descriptive metadata only.</summary>
internal readonly record struct CharacterContext(ulong ContentId, string? Name, string? HomeWorld);

internal sealed record PlateCreationResult(Guid PlateId, bool BecameActive);

internal sealed record PlateDeletionResult(Guid PlateId, IReadOnlyList<ulong> ClearedActiveForContentIds);

/// <summary>Every asset referenced by any Plate, including trashed ones. Only a complete scan
/// (every document readable) may ever be used to decide an asset is unreferenced.</summary>
internal sealed record AssetReferenceScan(bool IsComplete, IReadOnlySet<Guid> ReferencedAssetIds, IReadOnlyList<string> Problems);

/// <summary>A Library operation refused for a reason the player should see (message is player-facing).</summary>
internal sealed class PlateLibraryException : Exception
{
    internal PlateLibraryException(string message)
        : base(message)
    {
    }
}
