using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Templates;

namespace AetherFrame.Persistence;

/// <summary>
/// Turning a saved Template's JSON into a <see cref="PlateTemplate"/> envelope and back. Mirrors
/// <see cref="PlateDocuments"/>'s role for Plates, but a Template has two independently versioned
/// layers: this envelope (see <c>PersistenceSchemas.Template</c>) and its embedded
/// <c>ProfileDocument</c> (see <c>PersistenceSchemas.ProfileDocument</c>). Migrating each layer's
/// own "Version" is the caller's job (<c>TemplateLibraryService</c>); this class only ever turns
/// already-migrated JSON into objects and back, never decides versioning.
/// </summary>
internal static class TemplateDocuments
{
    /// <summary>
    /// The one way a Template envelope is read from already-migrated JSON. <paramref name="raw"/>'s
    /// "Document" property must already be migrated to the current ProfileDocument schema version
    /// (see <see cref="AetherFrame.Persistence.Schema.PersistenceSchemas.ProfileDocument"/>). The
    /// embedded document goes through the exact same unknown-element-preserving path a Plate uses
    /// (<see cref="PlateDocuments.Deserialize"/>) — never a hand-rolled second copy of it. Returns
    /// null for a malformed envelope (no "Document" object, or the document itself doesn't
    /// deserialize) so the caller can isolate the failure to this one file. <paramref name="raw"/>
    /// itself is never modified.
    /// </summary>
    internal static PlateTemplate? Deserialize(JsonObject raw)
    {
        if (raw[nameof(PlateTemplate.Document)] is not JsonObject documentRaw)
        {
            return null;
        }

        var document = PlateDocuments.Deserialize(documentRaw);
        if (document is null)
        {
            return null;
        }

        // The embedded document is deserialized separately above (so unknown element types never
        // reach the plain polymorphic deserializer below); it's removed here first so nothing
        // about it — known or not — is touched a second time.
        var withoutDocument = (JsonObject)raw.DeepClone();
        withoutDocument.Remove(nameof(PlateTemplate.Document));

        var template = withoutDocument.Deserialize<PlateTemplate>(JsonOptions.Default);
        if (template is null)
        {
            return null;
        }

        template.Document = document;
        template.Origin = NormalizeOrigin(template.Origin);
        return template;
    }

    /// <summary>
    /// Origin is informational only, so a missing "Origin" (explicit JSON null bypasses the field
    /// initializer) or an unrecognized "Kind" (e.g. written by a newer build) is never allowed to
    /// read as a real classification — both are normalized to <see cref="TemplateOriginKind.Unknown"/>
    /// deliberately here, rather than left to whatever a missing/unknown value happens to decode to.
    /// </summary>
    private static TemplateOrigin NormalizeOrigin(TemplateOrigin? origin)
    {
        if (origin is null)
        {
            return new TemplateOrigin();
        }

        if (!Enum.IsDefined(origin.Kind))
        {
            origin.Kind = TemplateOriginKind.Unknown;
        }

        return origin;
    }

    /// <summary>
    /// A fresh, independent envelope from saved JSON, with the embedded document's in-memory
    /// legacy repairs applied (never persisted by this; only an explicit save writes them).
    /// </summary>
    internal static PlateTemplate Materialize(JsonObject raw)
    {
        var template = Deserialize(raw) ?? throw new JsonException("Template deserialized to null.");
        PlateDocuments.ApplyLegacyRepairs(template.Document);
        return template;
    }

    /// <summary>Serializes the envelope, reattaching the embedded document's unrecognized elements
    /// exactly as <see cref="PlateDocuments.ToJson"/> does for a Plate — never a plain serialize of
    /// <see cref="PlateTemplate.Document"/>, which would silently drop them.</summary>
    internal static JsonObject ToJson(PlateTemplate template)
    {
        var json = JsonSerializer.SerializeToNode(template, JsonOptions.Default) as JsonObject
            ?? throw new JsonException("Template serialized to something other than an object.");

        json[nameof(PlateTemplate.Document)] = PlateDocuments.ToJson(template.Document);
        return json;
    }

    /// <summary>Renames a saved Template in its JSON: only the name and modified time change.</summary>
    internal static void SetName(JsonObject raw, string name, DateTime modifiedUtc)
    {
        raw[nameof(PlateTemplate.Name)] = name;
        raw[nameof(PlateTemplate.UpdatedAtUtc)] = modifiedUtc;
    }

    /// <summary>
    /// An independent copy of a saved Template's JSON under a new identity: the embedded document
    /// (and everything else) copied as-is — image bytes are never copied, the copy references the
    /// same managed assets; identity, name, timestamps, and origin reset. Mirrors
    /// <see cref="PlateDocuments.CreateDuplicate"/> for the envelope layer.
    /// </summary>
    internal static JsonObject CreateDuplicate(JsonObject source, Guid newTemplateId, string name, DateTime nowUtc)
    {
        var copy = (JsonObject)source.DeepClone();
        copy[nameof(PlateTemplate.TemplateId)] = newTemplateId;
        copy[nameof(PlateTemplate.Name)] = name;
        copy[nameof(PlateTemplate.CreatedAtUtc)] = nowUtc;
        copy[nameof(PlateTemplate.UpdatedAtUtc)] = nowUtc;
        copy[nameof(PlateTemplate.Origin)] = JsonSerializer.SerializeToNode(new TemplateOrigin { Kind = TemplateOriginKind.Duplicated }, JsonOptions.Default);
        return copy;
    }

    /// <summary>Best-effort display fields straight from JSON (e.g. a newer-version Template that
    /// can't be deserialized by this build).</summary>
    internal static (string? Name, DateTime? CreatedUtc, DateTime? ModifiedUtc) ReadDisplayFields(JsonObject raw) =>
        (JsonFields.ReadString(raw, nameof(PlateTemplate.Name)),
         JsonFields.ReadDate(raw, nameof(PlateTemplate.CreatedAtUtc)),
         JsonFields.ReadDate(raw, nameof(PlateTemplate.UpdatedAtUtc)));
}
