using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Persistence.Schema;

namespace AetherFrame.Persistence;

/// <summary>The result of reading one versioned JSON object.</summary>
internal sealed class VersionedReadResult<T>
    where T : class
{
    internal VersionedReadResult(SchemaMigrationResult migration, JsonObject? raw, T? value)
    {
        Migration = migration;
        Raw = raw;
        Value = value;
    }

    internal SchemaMigrationResult Migration { get; }

    /// <summary>Usable: the migrated JSON. NewerVersion: the JSON exactly as read. Invalid: null.</summary>
    internal JsonObject? Raw { get; }

    /// <summary>The typed object; only for usable (current or migrated) results.</summary>
    internal T? Value { get; }

    internal bool IsUsable => Migration.IsUsable && Value is not null;

    internal bool IsNewerVersion => Migration.Outcome == SchemaMigrationOutcome.NewerVersion;

    internal bool WasMigrated => Migration.Outcome == SchemaMigrationOutcome.Migrated;
}

/// <summary>Parsing, schema migration, and typed reading of versioned JSON files.</summary>
internal static class VersionedJson
{
    /// <summary>
    /// Parses <paramref name="json"/>, migrates it in memory, and deserializes it. Never throws for
    /// bad content — an unusable object comes back as <see cref="SchemaMigrationOutcome.Invalid"/>.
    /// </summary>
    internal static VersionedReadResult<T> Parse<T>(string json, SchemaDefinition schema)
        where T : class
    {
        JsonObject raw;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                return Invalid<T>($"{schema.Name} is not a JSON object.");
            }

            raw = parsed;
        }
        catch (JsonException ex)
        {
            return Invalid<T>($"{schema.Name} is not valid JSON: {ex.Message}");
        }

        var migration = schema.Migrate(raw);
        if (migration.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            return new VersionedReadResult<T>(migration, raw, null);
        }

        if (!migration.IsUsable)
        {
            return new VersionedReadResult<T>(migration, null, null);
        }

        try
        {
            var value = raw.Deserialize<T>(JsonOptions.Default);
            return value is null
                ? Invalid<T>($"{schema.Name} is empty.")
                : new VersionedReadResult<T>(migration, raw, value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            return Invalid<T>($"{schema.Name} has unreadable content: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads and parses a file through <paramref name="store"/>. Invalid content throws inside the
    /// store's reader so a store with backups retries from its backup copy; a NEWER-version file
    /// deliberately does not, because falling back to an older backup of it would silently hand
    /// back stale data that a later save could then write over the newer file. Throws
    /// <see cref="InvalidDataException"/> (or the IO error) when no usable copy exists.
    /// </summary>
    internal static async Task<VersionedReadResult<T>> ReadAsync<T>(IPlateFileStore store, string path, SchemaDefinition schema)
        where T : class
    {
        VersionedReadResult<T>? result = null;

        await store.ReadTextAsync(path, text =>
        {
            var parsed = Parse<T>(text, schema);
            if (!parsed.IsUsable && !parsed.IsNewerVersion)
            {
                throw new InvalidDataException(parsed.Migration.Error ?? $"{schema.Name} is unreadable.");
            }

            result = parsed;
        }).ConfigureAwait(false);

        return result ?? throw new InvalidDataException($"{schema.Name} could not be read.");
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions.Default);

    internal static string Serialize(JsonObject raw) => raw.ToJsonString(JsonOptions.Default);

    private static VersionedReadResult<T> Invalid<T>(string error)
        where T : class =>
        new(new SchemaMigrationResult(SchemaMigrationOutcome.Invalid, 0, 0, error), null, null);
}
