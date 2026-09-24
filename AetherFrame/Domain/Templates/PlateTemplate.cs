using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Templates;

/// <summary>
/// A reusable starting design, saved locally. Using a Template creates a brand-new, independent
/// Plate (see <c>TemplateLibraryService.InstantiateAsync</c>); editing that Plate never changes
/// the Template, and editing the Template's <see cref="Document"/> is never possible from an
/// instantiated Plate. Never carries character identity or Library state: those live entirely
/// outside the document (see <see cref="TemplateOrigin"/> for what IS kept).
/// </summary>
public sealed class PlateTemplate
{
    /// <summary>This envelope's own schema version — independent of <see cref="Document"/>'s.</summary>
    public const int CurrentSchemaVersion = 1;

    public int Version { get; set; } = CurrentSchemaVersion;

    /// <summary>The Template's identity. Stable forever; never reused as a Plate's identity.</summary>
    public Guid TemplateId { get; set; }

    /// <summary>The Template's display name (see <c>TemplateNaming</c>).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public TemplateOrigin Origin { get; set; } = new();

    /// <summary>
    /// The Template's saved creative content. Its OWN schema/migration (<c>ProfileDocument</c>'s)
    /// applies here, exactly as it does for a Plate — this envelope only versions itself.
    /// <see cref="ProfileDocument.OwnerContentId"/> is always 0: a Template never carries a
    /// character identity.
    /// </summary>
    public ProfileDocument Document { get; set; } = null!;

    /// <summary>Top-level properties this build doesn't know, carried through load and save so
    /// saving never silently drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>How a user Template came to exist. Never stores a local Plate or Template id: there is
/// no current product need for provenance lookups, and an id would be installation-specific
/// metadata a future portable Template export would have to scrub. Only the classification is
/// kept; a built-in Template never has an <see cref="TemplateOrigin"/> at all (it is never
/// persisted — see <c>BuiltInTemplateCatalog</c>).</summary>
public sealed class TemplateOrigin
{
    public TemplateOriginKind Kind { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public enum TemplateOriginKind
{
    /// <summary>No origin was recorded, or the recorded value isn't one this build knows (e.g. a
    /// newer build's origin kind). Deliberately the default (0): a missing or unrecognized "Kind"
    /// in JSON must never be silently read as <see cref="SavedFromPlate"/> — it stays Unknown and
    /// is treated as purely informational everywhere Origin is read.</summary>
    Unknown = 0,

    /// <summary>Created via Save as Template, from a Plate's last saved state.</summary>
    SavedFromPlate = 1,

    /// <summary>Created via Duplicate Template, from another user Template.</summary>
    Duplicated = 2,
}
