using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace AetherFrame.Persistence;

/// <summary>
/// Lenient single-field reads from raw saved JSON, for showing what a file is (its name and dates)
/// even when the whole document can't be deserialized. A missing or unexpected value reads as null;
/// nothing here throws or writes.
/// </summary>
internal static class JsonFields
{
    /// <summary>The property as a string, or null when it is missing or not a string.</summary>
    internal static string? ReadString(JsonObject raw, string property) =>
        raw[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The property as a date (a JSON date value or a round-trip date string), or null.</summary>
    internal static DateTime? ReadDate(JsonObject raw, string property)
    {
        if (raw[property] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<DateTime>(out var date))
        {
            return date;
        }

        return value.TryGetValue<string>(out var text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
