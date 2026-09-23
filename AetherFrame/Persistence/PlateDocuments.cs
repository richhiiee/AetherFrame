using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Persistence;

/// <summary>
/// Turning a saved Plate's JSON into documents to render or edit, and the few JSON-level edits the
/// Library makes to saved Plates (rename, duplicate). Those edits deliberately work on the raw
/// JSON rather than a re-serialized model: every other byte of creative content — including
/// fields this build doesn't know — is carried over exactly, and nothing is normalized just
/// because the Library touched the file.
/// </summary>
internal static class PlateDocuments
{
    /// <summary>
    /// A fresh, independent document from saved JSON, with the in-memory legacy repairs applied
    /// (never persisted by this; only an explicit save writes them). Every call returns a new
    /// instance sharing nothing with the JSON or any other document.
    /// </summary>
    internal static ProfileDocument Materialize(JsonObject raw)
    {
        var document = Deserialize(raw) ?? throw new JsonException("Plate document deserialized to null.");

        ApplyLegacyRepairs(document);
        return document;
    }

    /// <summary>
    /// The one way a document is read from JSON. Elements whose type this build doesn't know are
    /// set aside verbatim in <see cref="ProfileDocument.UnrecognizedElements"/> instead of failing
    /// the whole document (or being forced into a guessed type); <see cref="ToJson"/> writes them
    /// back. <paramref name="raw"/> itself is never modified.
    /// </summary>
    internal static ProfileDocument? Deserialize(JsonObject raw)
    {
        if (raw[nameof(ProfileDocument.Elements)] is not JsonArray elements || elements.All(IsKnownElement))
        {
            return raw.Deserialize<ProfileDocument>(JsonOptions.Default);
        }

        var known = new JsonArray();
        var unrecognized = new List<JsonElement>();
        foreach (var element in elements)
        {
            if (IsKnownElement(element))
            {
                known.Add(element!.DeepClone());
            }
            else if (element is not null)
            {
                unrecognized.Add(JsonSerializer.SerializeToElement(element, JsonOptions.Default));
            }
        }

        var filtered = (JsonObject)raw.DeepClone();
        filtered[nameof(ProfileDocument.Elements)] = known;

        var document = filtered.Deserialize<ProfileDocument>(JsonOptions.Default);
        if (document is not null)
        {
            document.UnrecognizedElements = unrecognized;
        }

        return document;
    }

    private static bool IsKnownElement(JsonNode? element) =>
        element is JsonObject obj
        && obj[ProfileElement.TypeDiscriminatorPropertyName] is JsonValue value
        && value.TryGetValue<string>(out var discriminator)
        && ProfileElement.IsKnownTypeDiscriminator(discriminator);

    /// <summary>
    /// The legacy repairs every loaded document gets, in this order: an invalid (legacy) canvas
    /// size first, so element repair clamps against the resolved bounds; then the pre-model
    /// background fields; then each element's legacy layout. A current document is untouched.
    /// </summary>
    internal static void ApplyLegacyRepairs(ProfileDocument document)
    {
        document.NormalizeLegacyCanvasSize();
        document.NormalizeLegacyBackground();

        foreach (var element in document.Elements)
        {
            element.NormalizeLegacyLayout(document.CanvasWidth, document.CanvasHeight);
        }
    }

    internal static JsonObject ToJson(ProfileDocument document)
    {
        var json = JsonSerializer.SerializeToNode(document, JsonOptions.Default) as JsonObject
            ?? throw new JsonException("Plate document serialized to something other than an object.");

        if (document.UnrecognizedElements is { Count: > 0 } unrecognized && json[nameof(ProfileDocument.Elements)] is JsonArray elements)
        {
            foreach (var element in unrecognized)
            {
                elements.Add(JsonNode.Parse(element.GetRawText()));
            }
        }

        return json;
    }

    /// <summary>Renames a saved Plate in its JSON: only the name and modified time change.</summary>
    internal static void SetName(JsonObject raw, string name, DateTime modifiedUtc)
    {
        raw[nameof(ProfileDocument.Name)] = name;
        raw[nameof(ProfileDocument.UpdatedAtUtc)] = modifiedUtc;
    }

    /// <summary>
    /// An independent copy of a saved Plate's JSON under a new identity: all creative content
    /// (canvas, background, elements, Basic metadata, asset references) copied as-is; identity,
    /// name, timestamps (created and modified: now), and revision reset; no legacy character owner
    /// (character associations live in bindings, which the Library copies from the source).
    /// Image bytes are never copied — the copy references the same managed assets.
    /// </summary>
    internal static JsonObject CreateDuplicate(JsonObject source, Guid newPlateId, string name, DateTime nowUtc)
    {
        var copy = (JsonObject)source.DeepClone();
        copy[nameof(ProfileDocument.ProfileId)] = newPlateId;
        copy[nameof(ProfileDocument.Name)] = name;
        copy[nameof(ProfileDocument.Revision)] = 0;
        copy[nameof(ProfileDocument.CreatedAtUtc)] = nowUtc;
        copy[nameof(ProfileDocument.UpdatedAtUtc)] = nowUtc;
        copy[nameof(ProfileDocument.OwnerContentId)] = 0;
        return copy;
    }

    /// <summary>Best-effort display fields straight from JSON (e.g. a newer-version Plate that
    /// can't be deserialized by this build).</summary>
    internal static (string? Name, DateTime? CreatedUtc, DateTime? ModifiedUtc) ReadDisplayFields(JsonObject raw) =>
        (ReadString(raw, nameof(ProfileDocument.Name)),
         ReadDate(raw, nameof(ProfileDocument.CreatedAtUtc)),
         ReadDate(raw, nameof(ProfileDocument.UpdatedAtUtc)));

    private static string? ReadString(JsonObject raw, string property) =>
        raw[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTime? ReadDate(JsonObject raw, string property)
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
