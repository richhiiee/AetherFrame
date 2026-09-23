using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Persistence.Schema;

/// <summary>The schema chain for every persisted AetherFrame JSON object kind.</summary>
internal static class PersistenceSchemas
{
    /// <summary>
    /// Plate documents. Version 0 never shipped but is tolerated as "before versioning"; the
    /// missing-version default is 1 because the original model's property default was 1.
    /// Step 1 → 2 is structurally a no-op on purpose: what version 2 added (explicit canvas size,
    /// the background model) is repaired field-by-field, in memory, by the document's own legacy
    /// normalizers at load — so migrating never repositions, re-sizes, or rewrites anything.
    /// </summary>
    internal static readonly SchemaDefinition ProfileDocument = new(
        "Plate document",
        currentVersion: Domain.Profiles.ProfileDocument.CurrentSchemaVersion,
        missingVersionMeans: 1,
        minimumVersion: 0,
        [
            new SchemaMigrationStep(0, "Pre-versioning document: identical to version 1.", static _ => { }),
            new SchemaMigrationStep(1, "Canvas size and background model are resolved by in-memory legacy repair at load.", static _ => { }),
        ]);

    /// <summary>
    /// Character bindings. 1 → 2 (Plate Library): the old binding's single Active profile stays
    /// that character's Active Plate — it was never ambiguous — and is guaranteed to be among the
    /// character's associated Plates. A binding with no Active profile stays without one (never
    /// guessed). Nothing is removed, and JSON names stay the same for older builds.
    /// </summary>
    internal static readonly SchemaDefinition CharacterBinding = new(
        "Character binding",
        currentVersion: Domain.Characters.CharacterBinding.CurrentVersion,
        missingVersionMeans: 1,
        minimumVersion: 1,
        [
            new SchemaMigrationStep(1, "Associated Plates list includes the Active Plate; descriptive character metadata added.", MigrateBindingV1ToV2),
        ]);

    internal static readonly SchemaDefinition PlateLibrary = new(
        "Plate library",
        currentVersion: PlateLibraryState.CurrentVersion,
        missingVersionMeans: 1,
        minimumVersion: 1,
        []);

    internal static readonly SchemaDefinition AssetMetadata = new(
        "Asset metadata",
        currentVersion: Domain.Assets.AssetMetadata.CurrentVersion,
        missingVersionMeans: 1,
        minimumVersion: 1,
        []);

    private static void MigrateBindingV1ToV2(JsonObject json)
    {
        var ids = json["ProfileIds"] as JsonArray;
        if (ids is null)
        {
            ids = new JsonArray();
            json["ProfileIds"] = ids;
        }

        var active = json["ActiveProfileId"] is JsonValue activeValue && activeValue.TryGetValue<string>(out var activeText) ? activeText : null;
        if (active is null || !Guid.TryParse(active, out var activeId) || activeId == Guid.Empty)
        {
            return;
        }

        var present = new HashSet<Guid>();
        foreach (var node in ids)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id))
            {
                present.Add(id);
            }
        }

        if (!present.Contains(activeId))
        {
            ids.Add(activeId.ToString());
        }
    }
}
