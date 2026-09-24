using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.Services.Packages;

/// <summary>Why a package (or one part of it) was refused. Stable categories for logs and tests.</summary>
internal enum PackageErrorCode
{
    InvalidArchive,
    PackageTooLarge,
    UnsafePath,
    DuplicateEntry,
    UnexpectedEntry,
    UnsupportedVersion,
    UnsupportedCapability,
    ManifestInvalid,
    ProfileInvalid,
    AssetMissing,
    AssetUndeclared,
    HashMismatch,
    ImageInvalid,
    CommitFailed,
    ExportFailed,
}

/// <summary>Things a player should know that don't stop an import.</summary>
internal enum PackageWarningCode
{
    /// <summary>The Plate holds elements this build can't show; they're kept, not lost.</summary>
    UnsupportedElements,

    /// <summary>A setting (Theme, Pattern, alignment...) has a value this build doesn't know; it's kept as-is.</summary>
    UnrecognizedSetting,

    /// <summary>preview.png was missing or unusable; only the picture is left out.</summary>
    PreviewOmitted,
}

/// <summary>
/// The overall verdict shown before importing. Supported and SupportedWithWarnings can be
/// imported; Unsupported is a well-formed package this build can't read (made by a newer
/// AetherFrame); Invalid is anything damaged, inconsistent, or unsafe.
/// </summary>
internal enum PackageCompatibility
{
    Supported,
    SupportedWithWarnings,
    Unsupported,
    Invalid,
}

/// <param name="Message">Player-facing: short, no file system paths or parser internals.</param>
/// <param name="Detail">Optional diagnostic detail for the log (safe entry names, numbers — never content).</param>
internal sealed record PackageError(PackageErrorCode Code, string Message, string? Detail = null)
{
    public override string ToString() => Detail is null ? $"{Code}: {Message}" : $"{Code}: {Message} ({Detail})";
}

internal sealed record PackageWarning(PackageWarningCode Code, string Message);

/// <summary>What a package says about itself, for the Import Preview. Only ever filled from a validated manifest.</summary>
internal sealed record PackageSummary(
    int FormatVersion,
    string PlateName,
    int PlateSchemaVersion,
    float CanvasWidth,
    float CanvasHeight,
    int ElementCount,
    int UnsupportedElementCount,
    int AssetCount,
    long TotalAssetBytes,
    bool HasPreview);

/// <summary>Collects problems while validating: validation reports failures as data, never by throwing.</summary>
internal sealed class PackageDiagnostics
{
    private readonly List<PackageError> errors = new();
    private readonly List<PackageWarning> warnings = new();

    internal IReadOnlyList<PackageError> Errors => errors;

    internal IReadOnlyList<PackageWarning> Warnings => warnings;

    internal bool HasErrors => errors.Count > 0;

    internal bool IsUnsupported => errors.Count > 0 && errors.All(e => e.Code is PackageErrorCode.UnsupportedVersion or PackageErrorCode.UnsupportedCapability);

    internal void Error(PackageErrorCode code, string message, string? detail = null) => errors.Add(new PackageError(code, message, detail));

    internal void Warning(PackageWarningCode code, string message)
    {
        // One line per kind of issue is plenty for a player.
        if (warnings.All(w => w.Code != code || w.Message != message))
        {
            warnings.Add(new PackageWarning(code, message));
        }
    }

    internal PackageCompatibility Compatibility =>
        IsUnsupported ? PackageCompatibility.Unsupported
        : HasErrors ? PackageCompatibility.Invalid
        : warnings.Count > 0 ? PackageCompatibility.SupportedWithWarnings
        : PackageCompatibility.Supported;
}

/// <summary>The outcome of exporting one Plate.</summary>
internal sealed record PackageExportResult(bool Succeeded, string? FilePath, int AssetCount, bool IncludedPreview, IReadOnlyList<PackageError> Errors)
{
    internal static PackageExportResult Failed(IReadOnlyList<PackageError> errors) => new(false, null, 0, false, errors);

    internal static PackageExportResult Failed(PackageErrorCode code, string message, string? detail = null) =>
        Failed([new PackageError(code, message, detail)]);

    /// <summary>A player-facing summary of why the export failed.</summary>
    internal string FailureMessage => Errors.Count > 0 ? Errors[0].Message : "The Plate couldn't be exported.";
}

/// <summary>The outcome of committing a validated package as a new Plate.</summary>
internal sealed record PackageImportResult(bool Succeeded, Guid PlateId, string PlateName, PackageError? Error)
{
    internal static PackageImportResult Failed(PackageErrorCode code, string message, string? detail = null) =>
        new(false, Guid.Empty, string.Empty, new PackageError(code, message, detail));
}

/// <summary>Player-facing text for each compatibility state.</summary>
internal static class PackageText
{
    internal static string Describe(PackageCompatibility compatibility) => compatibility switch
    {
        PackageCompatibility.Supported => "Ready to import",
        PackageCompatibility.SupportedWithWarnings => "Ready to import, with notes",
        PackageCompatibility.Unsupported => "Made with a newer version of AetherFrame",
        _ => "Can't be imported",
    };

    internal static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes} bytes";
}
