using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Components;
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
    /// the whole document (or being forced into a guessed type); Components are read one at a time,
    /// so a malformed one is set aside verbatim in <see cref="ProfileDocument.UnrecognizedComponents"/>
    /// without affecting the rest. <see cref="ToJson"/> writes both back. <paramref name="raw"/>
    /// itself is never modified.
    /// </summary>
    internal static ProfileDocument? Deserialize(JsonObject raw)
    {
        var elements = raw[nameof(ProfileDocument.Elements)] as JsonArray;
        var hasUnknownElements = elements is not null && !elements.All(IsKnownElement);
        var componentsNode = raw[nameof(ProfileDocument.Components)];

        if (!hasUnknownElements && componentsNode is null)
        {
            return raw.Deserialize<ProfileDocument>(JsonOptions.Default);
        }

        var filtered = (JsonObject)raw.DeepClone();
        filtered.Remove(nameof(ProfileDocument.Components));

        var unrecognized = new List<JsonElement>();
        if (hasUnknownElements)
        {
            var known = new JsonArray();
            foreach (var element in elements!)
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

            filtered[nameof(ProfileDocument.Elements)] = known;
        }

        var document = filtered.Deserialize<ProfileDocument>(JsonOptions.Default);
        if (document is not null)
        {
            if (hasUnknownElements)
            {
                document.UnrecognizedElements = unrecognized;
            }

            ReadComponents(componentsNode, document);
        }

        return document;
    }

    /// <summary>
    /// Reads each Component on its own: a readable one becomes a <see cref="PlateComponent"/> (its
    /// unknown fields kept in its extension data, an unknown kind or definition kept as-is); anything
    /// else — not an object, a wrong-typed value, no definition id — is kept verbatim as raw JSON.
    /// A "Components" value that isn't an array is kept verbatim as a whole.
    /// </summary>
    private static void ReadComponents(JsonNode? node, ProfileDocument document)
    {
        if (node is null)
        {
            return;
        }

        if (node is not JsonArray array)
        {
            document.MalformedComponentsValue = JsonSerializer.SerializeToElement(node, JsonOptions.Default);
            return;
        }

        var components = new List<PlateComponent>(array.Count);
        List<JsonElement>? malformed = null;
        foreach (var item in array)
        {
            var component = TryReadComponent(item);
            if (component is not null)
            {
                components.Add(component);
            }
            else
            {
                (malformed ??= new List<JsonElement>()).Add(item is null
                    ? JsonSerializer.SerializeToElement<object?>(null, JsonOptions.Default)
                    : JsonSerializer.SerializeToElement(item, JsonOptions.Default));
            }
        }

        document.Components = components;
        document.UnrecognizedComponents = malformed;
    }

    private static PlateComponent? TryReadComponent(JsonNode? item)
    {
        if (item is not JsonObject)
        {
            return null;
        }

        try
        {
            var component = item.Deserialize<PlateComponent>(JsonOptions.Default);
            return component is { DefinitionId: not null } ? component : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
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

        document.NormalizeComponentIds();

        // A Basic name still using its theme's older automatic color gets the current one, and a
        // Basic-managed Favorite Job row placed by the older fixed-width level column is made compact.
        Domain.Basic.BasicNameColor.UpgradeLegacy(document);
        Domain.Basic.BasicPlateEditor.UpgradeFavoriteJobRow(document);
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

        if (document.UnrecognizedComponents is { Count: > 0 } unreadable)
        {
            if (json[nameof(ProfileDocument.Components)] is not JsonArray components)
            {
                components = new JsonArray();
                json[nameof(ProfileDocument.Components)] = components;
            }

            foreach (var component in unreadable)
            {
                components.Add(JsonNode.Parse(component.GetRawText()));
            }
        }
        else if (document.MalformedComponentsValue is { } whole && (document.Components is null || document.Components.Count == 0))
        {
            json[nameof(ProfileDocument.Components)] = JsonNode.Parse(whole.GetRawText());
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
        (JsonFields.ReadString(raw, nameof(ProfileDocument.Name)),
         JsonFields.ReadDate(raw, nameof(ProfileDocument.CreatedAtUtc)),
         JsonFields.ReadDate(raw, nameof(ProfileDocument.UpdatedAtUtc)));
}
