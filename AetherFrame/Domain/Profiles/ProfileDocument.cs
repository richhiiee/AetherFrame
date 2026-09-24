using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Profiles;

public sealed class ProfileDocument
{
    public const int MaxElementCount = 256;

    // The canvas size every profile saved before per-profile canvas dimensions existed was
    // implicitly fixed at. A profile whose JSON predates CanvasWidth/CanvasHeight deserializes
    // those fields to 0 (the unset float default), which NormalizeLegacyCanvasSize below treats
    // as "repair to this size" — the same zero-sentinel convention ProfileElement.Size already
    // uses for legacy layout repair.
    public const float LegacyCanvasWidth = 1920f;
    public const float LegacyCanvasHeight = 1080f;

    // Default for newly created profiles: the AetherFrame Adventure Plate canvas, matching the
    // real FFXIV Adventurer Plate's proportions (16:9, not the old fixed 1920x1080 canvas' scale).
    public const float DefaultCanvasWidth = 1280f;
    public const float DefaultCanvasHeight = 720f;

    /// <summary>
    /// The document schema version this build writes. 1: the original single-profile documents
    /// (implicit 1920x1080 canvas, legacy background fields); 2: explicit canvas size and the
    /// <see cref="ProfileBackground"/> model — both of which older files still get through the
    /// field-driven, in-memory legacy repairs below rather than by rewriting files. A file with a
    /// HIGHER version was written by a newer AetherFrame and is never loaded for editing or
    /// rewritten (see <c>ProfileDocumentSchema</c>).
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int Version { get; set; } = 1;

    /// <summary>The Plate's identity. Stable forever, including across rename and every save.</summary>
    public Guid ProfileId { get; set; }

    /// <summary>
    /// Legacy: the character a single-profile-era document was created for. Kept only so
    /// migration can associate such a document with that character; never an identity or access
    /// check, and never set for new Plates (0) — character associations live in
    /// <c>CharacterBinding</c>, outside the document.
    /// </summary>
    public ulong OwnerContentId { get; set; }

    /// <summary>The Plate's display name (see <c>PlateNaming</c>).</summary>
    public string Name { get; set; } = string.Empty;

    public int Revision { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Logical canvas size; element Position/Size are expressed in this space, independent of
    /// the edit window's actual pixel size or the editor's current zoom level. 0 (the unset
    /// float default) means "not yet resolved" — see <see cref="NormalizeLegacyCanvasSize"/>.
    /// Never mutate directly to resize an existing profile; that must also decide whether
    /// element layouts scale along with it, which is an editor (EditorSession) concern.
    /// </summary>
    public float CanvasWidth { get; set; }

    public float CanvasHeight { get; set; }

    public List<ProfileElement> Elements { get; set; } = new();

    /// <summary>
    /// The background style (see <see cref="ProfileBackground"/>). Null only transiently: for a
    /// profile saved before this model existed, until <see cref="NormalizeLegacyBackground"/>
    /// derives it from the legacy fields below. Every load and every newly created profile
    /// resolves it before anything renders or edits the profile.
    /// </summary>
    public ProfileBackground? Background { get; set; }

    /// <summary>
    /// Basic mode's Identity Header settings (title source, layout, header region), or null when
    /// they've never been configured — every older profile. Null is a valid, permanent state: it is
    /// only created by an explicit Identity edit in the Basic editor, never by loading or opening.
    /// </summary>
    public BasicIdentityHeader? BasicIdentity { get; set; }

    /// <summary>
    /// Basic mode's Adventure Plate settings (orientation, where Basic last placed each section,
    /// playstyle and active hours data...), or null when never configured — every older profile.
    /// Like <see cref="BasicIdentity"/>, only ever created by an explicit Basic edit (or by
    /// creating a new Adventure Plate Classic), never by loading or opening.
    /// </summary>
    public BasicPlateSettings? BasicPlate { get; set; }

    /// <summary>
    /// The Plate's reusable Components (frames, backings, decorations — see <see cref="PlateComponent"/>),
    /// or null when it has none (every Plate before Components existed, and every new one until a
    /// Component is chosen). Part of the Plate's creative state like everything else here, so
    /// Templates, duplication and packages carry it without any storage of their own. Never written
    /// when null, so a Plate without Components serializes exactly as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PlateComponent>? Components { get; set; }

    /// <summary>
    /// Components this build couldn't read (malformed, e.g. a wrong-typed value), kept verbatim as
    /// raw JSON and written back after <see cref="Components"/> on every save — one bad Component
    /// never costs the Plate, or its other Components. Filled and written by <c>PlateDocuments</c>.
    /// </summary>
    [JsonIgnore]
    public List<JsonElement>? UnrecognizedComponents { get; set; }

    /// <summary>
    /// A "Components" value that isn't an array at all (so no individual Component can be read from
    /// it). Kept verbatim and written back as long as the Plate has no Components of its own; the
    /// first Component the user adds replaces it. Filled and written by <c>PlateDocuments</c>.
    /// </summary>
    [JsonIgnore]
    public JsonElement? MalformedComponentsValue { get; set; }

    // Legacy background fields (image + fit + opacity), from before ProfileBackground existed.
    // Read only so NormalizeLegacyBackground can migrate an old profile in memory; nulled once
    // migrated, so they are never written back (WhenWritingNull). JSON names are unchanged.

    [JsonPropertyName("BackgroundAssetId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LegacyBackgroundAssetId { get; set; }

    [JsonPropertyName("BackgroundFitMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BackgroundFitMode? LegacyBackgroundFitMode { get; set; }

    [JsonPropertyName("BackgroundOpacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyBackgroundOpacity { get; set; }

    /// <summary>Top-level properties this build doesn't know (e.g. written by a newer compatible
    /// build), carried through load and save so saving never silently drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Elements of a type this build doesn't know (see <see cref="ProfileElement.IsKnownTypeDiscriminator"/>),
    /// kept verbatim as raw JSON: never rendered, edited, or deserialized into a guessed type, but
    /// written back unchanged whenever the document is saved, so a newer build's content survives
    /// an older build's edits. Filled and written by <c>PlateDocuments</c>, not by the serializer.
    /// </summary>
    [JsonIgnore]
    public List<JsonElement>? UnrecognizedElements { get; set; }

    /// <summary>How many elements this build can't display (see <see cref="UnrecognizedElements"/>).</summary>
    [JsonIgnore]
    public int UnsupportedElementCount => UnrecognizedElements?.Count ?? 0;

    /// <summary>
    /// True when the Plate holds elements this build can't display. Informational only: the Plate
    /// stays fully readable and editable, and those elements are preserved on every save.
    /// </summary>
    [JsonIgnore]
    public bool HasUnsupportedElements => UnsupportedElementCount > 0;

    /// <summary>
    /// Repairs a profile loaded with an invalid (non-positive) canvas size: a legacy profile
    /// saved before CanvasWidth/CanvasHeight existed, which deserializes both to 0. Must run
    /// before <see cref="ProfileElement.NormalizeLegacyLayout"/> on this profile's elements, so
    /// their own repair clamps against the correct (now-resolved) canvas bounds. A profile that
    /// already has a valid size — including one explicitly saved at 1920x1080 — is left
    /// untouched, so opening a profile never changes its dimensions on its own.
    /// </summary>
    /// <returns>True if a repair was applied.</returns>
    internal bool NormalizeLegacyCanvasSize()
    {
        if (CanvasWidth > 0f && CanvasHeight > 0f)
        {
            return false;
        }

        CanvasWidth = LegacyCanvasWidth;
        CanvasHeight = LegacyCanvasHeight;
        return true;
    }

    /// <summary>
    /// Gives every Component a usable, unique instance id: a missing (empty) or repeated id — only
    /// possible in a hand-edited file — gets a fresh one, in memory, like the other load repairs.
    /// Only ids change; every other value, and the order, is left exactly as loaded.
    /// </summary>
    /// <returns>True if a repair was applied.</returns>
    internal bool NormalizeComponentIds()
    {
        if (Components is not { Count: > 0 } components)
        {
            return false;
        }

        var seen = new HashSet<Guid>();
        var repaired = false;
        foreach (var component in components)
        {
            if (component.Id == Guid.Empty || !seen.Add(component.Id))
            {
                component.Id = Guid.NewGuid();
                seen.Add(component.Id);
                repaired = true;
            }
        }

        return repaired;
    }

    /// <summary>
    /// Resolves <see cref="Background"/> for a profile saved before <see cref="ProfileBackground"/>
    /// existed, from its legacy image/fit/opacity fields — in memory only, like the other legacy
    /// repairs: the file on disk is untouched until the user explicitly saves, and the result
    /// renders identically to the old background. A profile that already has a Background is left
    /// untouched.
    /// </summary>
    /// <returns>True if a migration was applied.</returns>
    internal bool NormalizeLegacyBackground()
    {
        if (Background is not null)
        {
            return false;
        }

        Background = ProfileBackground.FromLegacy(LegacyBackgroundAssetId, LegacyBackgroundFitMode, LegacyBackgroundOpacity);
        LegacyBackgroundAssetId = null;
        LegacyBackgroundFitMode = null;
        LegacyBackgroundOpacity = null;
        return true;
    }
}

/// <summary>Legacy background fit modes, kept only to deserialize old profiles (see
/// <see cref="ProfileDocument.LegacyBackgroundFitMode"/>). New code uses <see cref="ProfileImageFit"/>.</summary>
public enum BackgroundFitMode
{
    Cover,
    Contain,
    Stretch,
}
