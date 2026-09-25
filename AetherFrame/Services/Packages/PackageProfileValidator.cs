using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;

namespace AetherFrame.Services.Packages;

/// <summary>A profile.json that passed validation: its migrated JSON and the document read from it.</summary>
/// <param name="Raw">The JSON after schema migration — what gets imported (with identity reset), so
/// every field this build doesn't know is carried over exactly.</param>
/// <param name="AssetReferences">Every managed asset the document references where this build
/// knows assets appear (image elements, the background). All must be in the package.</param>
internal sealed record ValidatedProfile(JsonObject Raw, ProfileDocument Document, IReadOnlySet<Guid> AssetReferences);

/// <summary>
/// Validates an imported profile.json before anything is written. Reads it only through the
/// existing machinery — the Plate document schema chain for migration, and
/// <see cref="PlateDocuments"/> for typed reading (whose element types are a fixed, compile-time
/// list; an unknown element is preserved as inert JSON, never instantiated) — then checks the
/// result for values no editor could have produced.
///
/// <para><b>Errors vs warnings.</b> Anything unsafe or impossible is an error: non-finite or
/// absurd numbers, sizes beyond <see cref="PackagePolicy"/>, over-long text, duplicate element
/// ids, a Plate name that disagrees with the manifest. A value this build merely doesn't recognize
/// (a newer Theme id, a Pattern or setting added later) is a warning and is kept exactly as it is,
/// the same way a saved Plate from a newer compatible build is treated. Nothing is silently
/// clamped or "fixed": a Plate imports as it was made or not at all. The only changes are the
/// existing, documented legacy repairs every loaded Plate gets.</para>
/// </summary>
internal static class PackageProfileValidator
{
    /// <summary>
    /// Ceiling on any non-integer JSON number anywhere in the document, known field or not.
    /// Guarantees every value survives conversion to float without becoming infinite.
    /// </summary>
    internal const double MaxJsonNumberMagnitude = 1e9;

    private const string Damaged = "The Plate in this file is damaged.";

    internal static ValidatedProfile? Validate(JsonObject raw, PackageManifest manifest, PackageDiagnostics diagnostics)
    {
        void Invalid(string detail) => diagnostics.Error(PackageErrorCode.ProfileInvalid, Damaged, detail);

        // ---- structure, before any typed reading.
        if (raw[nameof(ProfileDocument.Elements)] is { } elementsNode)
        {
            if (elementsNode is not JsonArray elements)
            {
                Invalid("Elements is not an array");
                return null;
            }

            if (elements.Count > PackagePolicy.MaxElementCount)
            {
                diagnostics.Error(PackageErrorCode.PackageTooLarge, $"The Plate has too many elements (the limit is {PackagePolicy.MaxElementCount}).",
                    $"{elements.Count} elements");
                return null;
            }

            foreach (var element in elements)
            {
                if (element is not JsonObject)
                {
                    Invalid("an element is not an object");
                    return null;
                }
            }
        }

        if (raw[nameof(ProfileDocument.Components)] is { } componentsNode)
        {
            if (componentsNode is not JsonArray components)
            {
                Invalid("Components is not an array");
                return null;
            }

            if (components.Count > PlateComponentLimits.MaxComponentCount)
            {
                diagnostics.Error(PackageErrorCode.PackageTooLarge, $"The Plate has too many Components (the limit is {PlateComponentLimits.MaxComponentCount}).",
                    $"{components.Count} components");
                return null;
            }
        }

        if (FindBadNumber(raw) is { } badNumber)
        {
            Invalid($"number out of range: {badNumber}");
            return null;
        }

        // The legacy repairs below turn a missing (zero) canvas or element size into a default.
        // A NEGATIVE size was never written by any build: it's corruption, and must not be
        // "repaired" into something that looks fine.
        if (FindNegativeSize(raw) is { } negative)
        {
            Invalid(negative);
            return null;
        }

        // ---- schema: the Plate's own version, separate from the package's format version.
        var schema = PersistenceSchemas.ProfileDocument;
        if (!schema.TryReadVersion(raw, out var declaredVersion, out _))
        {
            Invalid("unreadable Plate schema version");
            return null;
        }

        if (declaredVersion != manifest.PlateSchemaVersion)
        {
            Invalid($"Plate schema version {declaredVersion} does not match the manifest's {manifest.PlateSchemaVersion}");
            return null;
        }

        var migration = schema.Migrate(raw);
        if (migration.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            diagnostics.Error(PackageErrorCode.UnsupportedVersion, "This Plate was made with a newer version of AetherFrame. Update AetherFrame to import it.",
                $"Plate schema {declaredVersion}, supported {schema.CurrentVersion}");
            return null;
        }

        if (!migration.IsUsable)
        {
            Invalid(migration.Error ?? "unusable Plate schema version");
            return null;
        }

        // ---- typed reading (fixed element types only) plus the standard legacy repairs.
        ProfileDocument document;
        try
        {
            document = PlateDocuments.Deserialize((JsonObject)raw.DeepClone()) ?? throw new JsonException("empty document");
            PlateDocuments.ApplyLegacyRepairs(document);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            Invalid("unreadable content: " + ex.GetType().Name);
            return null;
        }

        var checker = new Checker(diagnostics);
        checker.CheckDocument(document, manifest);
        if (checker.Failed)
        {
            return null;
        }

        if (document.UnrecognizedComponents is { Count: > 0 })
        {
            diagnostics.Warning(PackageWarningCode.UnsupportedElements,
                "Some of this Plate's Components couldn't be read. They won't be shown, but they're kept.");
        }

        if (document.HasUnsupportedElements)
        {
            diagnostics.Warning(PackageWarningCode.UnsupportedElements,
                "This Plate has parts made with a newer version of AetherFrame. They won't be shown, but they're kept.");
        }

        return new ValidatedProfile(raw, document, checker.AssetReferences);
    }

    /// <summary>
    /// The first non-integer number anywhere in the JSON whose magnitude exceeds
    /// <see cref="MaxJsonNumberMagnitude"/>, as a short description; null if none. Integers are
    /// exact and bounded by their typed fields, so they're left to typed reading.
    /// </summary>
    internal static string? FindBadNumber(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    if (FindBadNumber(child) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (FindBadNumber(child) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                if (value.TryGetValue<long>(out _) || value.TryGetValue<ulong>(out _))
                {
                    return null;
                }

                return value.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Abs(number) <= MaxJsonNumberMagnitude
                    ? null
                    : "non-integer beyond the allowed magnitude";

            default:
                return null;
        }
    }

    private static string? FindNegativeSize(JsonObject raw)
    {
        static bool IsNegative(JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) && number < 0;

        if (IsNegative(raw[nameof(ProfileDocument.CanvasWidth)]) || IsNegative(raw[nameof(ProfileDocument.CanvasHeight)]))
        {
            return "negative canvas size";
        }

        if (raw[nameof(ProfileDocument.Elements)] is JsonArray elements)
        {
            foreach (var element in elements)
            {
                if (element is JsonObject obj
                    && obj[ProfileElement.TypeDiscriminatorPropertyName] is JsonValue type && type.TryGetValue<string>(out var discriminator)
                    && ProfileElement.IsKnownTypeDiscriminator(discriminator)
                    && obj[nameof(ProfileElement.Size)] is JsonObject size && (IsNegative(size["X"]) || IsNegative(size["Y"])))
                {
                    return "negative element size";
                }
            }
        }

        return null;
    }

    /// <summary>The field-by-field checks. Stops recording after the first error kind per field is enough to refuse.</summary>
    private sealed class Checker
    {
        private readonly PackageDiagnostics diagnostics;

        internal Checker(PackageDiagnostics diagnostics) => this.diagnostics = diagnostics;

        internal bool Failed { get; private set; }

        internal HashSet<Guid> AssetReferences { get; } = new();

        internal void CheckDocument(ProfileDocument document, PackageManifest manifest)
        {
            if (document.Name != manifest.PlateName)
            {
                Fail("Plate name does not match the manifest");
            }

            if (!PlateNaming.TryNormalizeName(document.Name, out var normalized, out _) || normalized != document.Name)
            {
                Fail("Plate name is blank, too long, or untrimmed");
            }

            if (!InRange(document.CanvasWidth, PackagePolicy.MinCanvasDimension, PackagePolicy.MaxCanvasDimension)
                || !InRange(document.CanvasHeight, PackagePolicy.MinCanvasDimension, PackagePolicy.MaxCanvasDimension))
            {
                Fail("canvas size out of range");
            }

            if (document.Elements.Count + document.UnsupportedElementCount > PackagePolicy.MaxElementCount)
            {
                Fail("too many elements");
            }

            CheckBackground(document.Background);

            var ids = new HashSet<Guid>();
            foreach (var element in document.Elements)
            {
                if (element.Id == Guid.Empty || !ids.Add(element.Id))
                {
                    Fail("missing or duplicate element id");
                }

                CheckElement(element);
            }

            if (document.BasicIdentity is { } identity)
            {
                CheckIdentity(identity);
            }

            if (document.BasicPlate is { } plate)
            {
                CheckBasicPlate(plate);
            }

            if (document.Components is { } components)
            {
                CheckComponents(components);
            }
        }

        private void CheckComponents(List<PlateComponent> components)
        {
            foreach (var component in components)
            {
                Text(component.DefinitionId, PlateComponentLimits.MaxDefinitionIdLength, "Component id");
                if (component.Color is { } color)
                {
                    Color(color, "Component color");
                }

                Style(component.Opacity, "Component opacity");
                Style(component.Scale, "Component scale");
                Style(component.RotationDegrees, "Component rotation");
                Coordinate(component.Offset, "Component offset");

                if (component.AssetId is { } assetId && assetId != Guid.Empty)
                {
                    AssetReferences.Add(assetId);
                }

                // A newer build's kind or definition: kept exactly as it is, just not drawn here.
                if (ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out _) is ComponentStatus.UnknownKind or ComponentStatus.MissingDefinition or ComponentStatus.KindMismatch)
                {
                    Unrecognized();
                }
            }
        }

        private void CheckBackground(ProfileBackground? background)
        {
            if (background is null)
            {
                Fail("no background");
                return;
            }

            Recognized(background.Mode, "background mode");
            Recognized(background.Texture, "Pattern");
            Recognized(background.ImageFit, "background image fit");
            Color(background.PrimaryColor, "background color");
            Color(background.SecondaryColor, "background color");
            Style(background.GradientAngle, "gradient angle");
            Style(background.Opacity, "background opacity");
            Style(background.TextureIntensity, "Pattern intensity");
            Style(background.TextureRotation, "Pattern rotation");
            if (!InRange(background.TextureScale, 0f, PackagePolicy.MaxStyleMagnitude))
            {
                Fail("Pattern scale out of range");
            }

            if (background.ImageAssetId is { } assetId && assetId != Guid.Empty)
            {
                AssetReferences.Add(assetId);
            }
        }

        private void CheckElement(ProfileElement element)
        {
            Text(element.Name, ProfileElement.MaxNameLength, "element name");
            Coordinate(element.Position, "element position");
            if (!InRange(element.Size.X, 0f, PackagePolicy.MaxCoordinateMagnitude) || !InRange(element.Size.Y, 0f, PackagePolicy.MaxCoordinateMagnitude)
                || element.Size.X <= 0f || element.Size.Y <= 0f)
            {
                Fail("element size out of range");
            }

            Recognized(element.Role, "element role");

            switch (element)
            {
                case TextProfileElement text:
                    CheckText(text);
                    break;

                case ImageProfileElement image:
                    Style(image.Opacity, "image opacity");
                    Style(image.RotationDegrees, "image rotation");
                    Recognized(image.DisplayMode, "image fit");
                    if (image.AssetId != Guid.Empty)
                    {
                        AssetReferences.Add(image.AssetId);
                    }

                    break;
            }
        }

        private void CheckText(TextProfileElement text)
        {
            Text(text.Text, TextProfileElement.MaxTextLength, "text");
            Text(text.Prefix, TextProfileElement.MaxAffixLength, "text prefix");
            Text(text.Suffix, TextProfileElement.MaxAffixLength, "text suffix");
            Text(text.FontFamily, PackagePolicy.MaxIdentifierLength, "font");

            if (!InRange(text.FontSize, 0f, PackagePolicy.MaxFontSize) || text.FontSize <= 0f
                || !InRange(text.AutoFitMinimumSize, 0f, PackagePolicy.MaxFontSize))
            {
                Fail("font size out of range");
            }

            Color(text.Color, "text color");
            Color(text.OutlineColor, "outline color");
            Color(text.ShadowColor, "shadow color");
            Style(text.LetterSpacing, "letter spacing");
            Style(text.LineSpacing, "line spacing");
            Style(text.OutlineThickness, "outline thickness");
            Style(text.OutlineOpacity, "outline opacity");
            Style(text.ShadowOpacity, "shadow opacity");
            Style(text.ShadowOffsetX, "shadow offset");
            Style(text.ShadowOffsetY, "shadow offset");
            Recognized(text.Alignment, "text alignment");
            Recognized(text.VerticalAlignment, "text alignment");

            if (text.LayoutVersion < TextProfileElement.LegacyLayoutVersion)
            {
                Fail("negative text layout version");
            }
            else if (text.LayoutVersion > TextProfileElement.CurrentLayoutVersion)
            {
                Unrecognized();
            }
        }

        private void CheckIdentity(BasicIdentityHeader identity)
        {
            Recognized(identity.TitleSource, "title source");
            Recognized(identity.Layout, "title layout");
            Text(identity.CustomTitle, BasicIdentityHeader.MaxCustomTitleLength, "custom title");
            Coordinate(identity.RegionPosition, "header position");
            if (!InRange(identity.RegionWidth, 0f, PackagePolicy.MaxCoordinateMagnitude))
            {
                Fail("header width out of range");
            }

            if (identity.AppliedLayout is { } applied)
            {
                Rect(applied.Name);
                Rect(applied.Title);
                Rect(applied.Tagline);
            }

            if (identity.LayoutStyle is { } style)
            {
                Recognized(style.Layout, "title layout");
                StyleValues(style.Applied);
                StyleValues(style.Previous);
            }
        }

        private void StyleValues(TitleStyleValues? values)
        {
            if (values is null)
            {
                return;
            }

            if (values.FontSize is { } size && (!InRange(size, 0f, PackagePolicy.MaxFontSize)))
            {
                Fail("title font size out of range");
            }

            if (values.LetterSpacing is { } spacing)
            {
                Style(spacing, "title letter spacing");
            }
        }

        private void CheckBasicPlate(BasicPlateSettings plate)
        {
            Recognized(plate.Orientation, "layout orientation");
            Recognized(plate.PortraitSource, "portrait source");

            if (plate.Playstyles is { } playstyles)
            {
                if (playstyles.Count > BasicPlateSettings.MaxPlaystyles)
                {
                    Fail("too many playstyles");
                }

                foreach (var playstyle in playstyles)
                {
                    Text(playstyle, BasicPlateSettings.MaxPlaystyleLength, "playstyle");
                }
            }

            if (plate.Placements is { } placements)
            {
                if (placements.Count > PackagePolicy.MaxElementCount)
                {
                    Fail("too many section placements");
                }

                foreach (var placement in placements)
                {
                    if (placement is null)
                    {
                        Fail("empty section placement");
                        continue;
                    }

                    Recognized(placement.Role, "section");
                    Rect(placement.Rect);
                }
            }

            if (plate.ActiveHours is { } hours)
            {
                if (hours.StartMinutes is < 0 or > BasicActiveHours.MinutesPerDay || hours.EndMinutes is < 0 or > BasicActiveHours.MinutesPerDay)
                {
                    Fail("active hours out of range");
                }

                Text(hours.TimeZone, BasicActiveHours.MaxTimeZoneLength, "time zone");
            }

            if (plate.Level is < 0 or > BasicPlateText.MaxLevel)
            {
                Fail("level out of range");
            }

            if (plate.FavoriteJobIds.Count > BasicFavoriteJobs.MaxJobs)
            {
                Fail("too many favorite jobs");
            }

            Text(plate.ThemeId, PackagePolicy.MaxIdentifierLength, "Theme");
            if (!string.IsNullOrEmpty(plate.ThemeId) && ProfileThemePresets.Find(plate.ThemeId) is null)
            {
                Unrecognized();
            }
        }

        // ---------------------------------------------------------------- primitives

        private void Rect(ElementRect? rect)
        {
            if (rect is { } r)
            {
                Coordinate(r.Position, "layout position");
                Coordinate(r.Size, "layout size");
            }
        }

        private void Coordinate(Vector2 value, string what)
        {
            if (!InRange(value.X, -PackagePolicy.MaxCoordinateMagnitude, PackagePolicy.MaxCoordinateMagnitude)
                || !InRange(value.Y, -PackagePolicy.MaxCoordinateMagnitude, PackagePolicy.MaxCoordinateMagnitude))
            {
                Fail(what + " out of range");
            }
        }

        private void Color(Vector4 value, string what)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W)
                || value.Length() > PackagePolicy.MaxStyleMagnitude)
            {
                Fail(what + " out of range");
            }
        }

        private void Style(float value, string what)
        {
            if (!InRange(value, -PackagePolicy.MaxStyleMagnitude, PackagePolicy.MaxStyleMagnitude))
            {
                Fail(what + " out of range");
            }
        }

        private void Text(string? value, int maxLength, string what)
        {
            if (value is not null && value.Length > maxLength)
            {
                Fail($"{what} longer than {maxLength} characters");
            }
        }

        private void Recognized<TEnum>(TEnum value, string what)
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                Unrecognized();
            }
        }

        private void Unrecognized() =>
            diagnostics.Warning(PackageWarningCode.UnrecognizedSetting,
                "This Plate uses settings from a newer version of AetherFrame. They're kept, but may look different here.");

        private void Fail(string detail)
        {
            Failed = true;
            diagnostics.Error(PackageErrorCode.ProfileInvalid, Damaged, detail);
        }

        private static bool InRange(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
    }
}
