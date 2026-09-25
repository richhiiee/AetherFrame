using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Characters;

/// <summary>
/// Local association between one character and the Plates it uses. <see cref="ContentId"/> is only
/// a local binding key (the file name, too) — never shared, never a Plate's identity; a Plate stays
/// independently identified by its own Guid and may exist with no character at all. Name and home
/// World are descriptive only (shown in the Library, matched by search), refreshed whenever the
/// binding is written for the logged-in character, and never used to identify anyone.
///
/// JSON property names are the pre-Plate-Library ones ("ProfileIds", "ActiveProfileId"), so an
/// older AetherFrame build still reads a migrated binding.
/// </summary>
public sealed class CharacterBinding
{
    /// <summary>
    /// 1: the single-profile era (one Active profile, auto-created at login).
    /// 2: Plate Library — any number of associated Plates, explicit Active Plate, descriptive
    /// character metadata. See <c>PersistenceSchemas.CharacterBinding</c> for the migration.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public ulong ContentId { get; set; }

    /// <summary>This character's Active Plate, or null when none is Active. Only ever changed by an
    /// explicit Set Active, the first-Plate rule, or deleting that Plate — never guessed.</summary>
    [JsonPropertyName("ActiveProfileId")]
    public Guid? ActivePlateId { get; set; }

    /// <summary>Plates associated with this character (including the Active one).</summary>
    [JsonPropertyName("ProfileIds")]
    public List<Guid> PlateIds { get; set; } = new();

    public string? LastKnownCharacterName { get; set; }

    public string? LastKnownHomeWorld { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Properties this build doesn't know (e.g. written by a newer compatible build),
    /// kept so rewriting the binding never drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    internal CharacterBinding Clone() => new()
    {
        Version = Version,
        ContentId = ContentId,
        ActivePlateId = ActivePlateId,
        PlateIds = PlateIds.ToList(),
        LastKnownCharacterName = LastKnownCharacterName,
        LastKnownHomeWorld = LastKnownHomeWorld,
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc,
        ExtensionData = ExtensionData is null ? null : new Dictionary<string, JsonElement>(ExtensionData),
    };
}
