using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    public int Version { get; set; } = 1;

    public Guid ProfileId { get; set; }

    public ulong OwnerContentId { get; set; }

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
