using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AetherFrame.Persistence.Schema;

internal enum SchemaMigrationOutcome
{
    /// <summary>Already at the current version; nothing changed.</summary>
    Current,

    /// <summary>Upgraded in memory to the current version.</summary>
    Migrated,

    /// <summary>Written by a newer AetherFrame. The JSON is left untouched and must not be
    /// rewritten or treated as this build's data.</summary>
    NewerVersion,

    /// <summary>Not a usable object of this kind (bad version field, no migration path).</summary>
    Invalid,
}

internal readonly record struct SchemaMigrationResult(SchemaMigrationOutcome Outcome, int OriginalVersion, int Version, string? Error)
{
    internal bool IsUsable => Outcome is SchemaMigrationOutcome.Current or SchemaMigrationOutcome.Migrated;
}

/// <summary>One upgrade from <see cref="FromVersion"/> to FromVersion + 1, applied to raw JSON.</summary>
internal sealed record SchemaMigrationStep(int FromVersion, string Description, Action<JsonObject> Apply);

/// <summary>
/// Sequential, JSON-level schema migration for one persisted object kind. Operating on
/// <see cref="JsonObject"/> (before typed deserialization) keeps each step independent of the
/// current C# model, preserves properties the step doesn't touch, and needs no game state.
///
/// Rules: a missing "Version" means <see cref="MissingVersionMeans"/>; a version above
/// <see cref="CurrentVersion"/> is reported as <see cref="SchemaMigrationOutcome.NewerVersion"/>
/// with the object left exactly as read; every step runs in order, each bumping "Version" by one.
/// Migration only ever happens in memory — callers decide if and when a result is written.
/// </summary>
internal sealed class SchemaDefinition
{
    internal const string VersionPropertyName = "Version";

    private readonly Dictionary<int, SchemaMigrationStep> steps;

    internal SchemaDefinition(string name, int currentVersion, int missingVersionMeans, int minimumVersion, IEnumerable<SchemaMigrationStep> migrationSteps)
    {
        Name = name;
        CurrentVersion = currentVersion;
        MissingVersionMeans = missingVersionMeans;
        MinimumVersion = minimumVersion;
        steps = migrationSteps.ToDictionary(s => s.FromVersion);

        // A contiguous chain from the minimum to the current version, checked once up front so a
        // gap is a programming error found at startup, not a user's file failing later.
        for (var v = minimumVersion; v < currentVersion; v++)
        {
            if (!steps.ContainsKey(v))
            {
                throw new InvalidOperationException($"{name} schema has no migration from version {v}.");
            }
        }
    }

    internal string Name { get; }

    internal int CurrentVersion { get; }

    internal int MissingVersionMeans { get; }

    internal int MinimumVersion { get; }

    /// <summary>Reads the declared version without changing anything.</summary>
    internal bool TryReadVersion(JsonObject json, out int version, out string? error)
    {
        error = null;
        version = MissingVersionMeans;

        if (!json.TryGetPropertyValue(VersionPropertyName, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var parsed))
        {
            version = parsed;
            return true;
        }

        if (node is JsonValue other && other.TryGetValue<double>(out var asDouble) && asDouble == Math.Floor(asDouble) && asDouble is >= int.MinValue and <= int.MaxValue)
        {
            version = (int)asDouble;
            return true;
        }

        error = $"{Name} has an unreadable version field.";
        return false;
    }

    /// <summary>Upgrades <paramref name="json"/> in place to <see cref="CurrentVersion"/>.</summary>
    internal SchemaMigrationResult Migrate(JsonObject json)
    {
        if (!TryReadVersion(json, out var original, out var error))
        {
            return new SchemaMigrationResult(SchemaMigrationOutcome.Invalid, 0, 0, error);
        }

        if (original > CurrentVersion)
        {
            return new SchemaMigrationResult(
                SchemaMigrationOutcome.NewerVersion, original, original,
                $"{Name} version {original} is newer than this AetherFrame supports ({CurrentVersion}).");
        }

        if (original < MinimumVersion)
        {
            return new SchemaMigrationResult(
                SchemaMigrationOutcome.Invalid, original, original, $"{Name} version {original} is not a known version.");
        }

        var version = original;
        while (version < CurrentVersion)
        {
            steps[version].Apply(json);
            version++;
            json[VersionPropertyName] = version;
        }

        return new SchemaMigrationResult(
            original == CurrentVersion ? SchemaMigrationOutcome.Current : SchemaMigrationOutcome.Migrated, original, version, null);
    }
}
