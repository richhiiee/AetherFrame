using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Packages;

/// <summary>
/// One validated image waiting in the staging folder. <see cref="LocalAssetId"/> is a brand-new
/// id minted for this import: the package's own id is never used as a local id, so a package can
/// never collide with, alias, or overwrite an image already in the installation.
/// </summary>
internal sealed record StagedAsset(PackageAssetDeclaration Declaration, Guid LocalAssetId, string StagedPath, ImageInspection Inspection);

/// <summary>
/// The result of opening and validating one .aetherframe file: every verdict, and — only when it
/// can be imported — the validated content, staged in a private folder of its own. Nothing here
/// is in the Plate Library or managed asset storage; <see cref="PackageImporter"/> is the only way
/// in. Disposing deletes the staging folder (and only it).
/// </summary>
internal sealed class StagedPackage : IDisposable
{
    private readonly IAetherFrameLog log;
    private bool disposed;

    internal StagedPackage(string sourceFileName, string stagingDirectory, IAetherFrameLog log)
    {
        SourceFileName = sourceFileName;
        StagingDirectory = stagingDirectory;
        this.log = log;
    }

    /// <summary>The package's file name (no directory), for display.</summary>
    internal string SourceFileName { get; }

    internal string StagingDirectory { get; }

    internal PackageDiagnostics Diagnostics { get; } = new();

    internal PackageCompatibility Compatibility => Diagnostics.Compatibility;

    internal bool CanImport => !disposed && Compatibility is PackageCompatibility.Supported or PackageCompatibility.SupportedWithWarnings && PreparedProfile is not null;

    internal PackageManifest? Manifest { get; set; }

    internal PackageSummary? Summary { get; set; }

    /// <summary>
    /// The profile JSON, validated and migrated, with every package asset id already replaced by
    /// its <see cref="StagedAsset.LocalAssetId"/>. Identity (Plate id, timestamps) is assigned at
    /// import. Null unless the package can be imported.
    /// </summary>
    internal JsonObject? PreparedProfile { get; set; }

    /// <summary>A read-only document of exactly what would be imported, for the Import Preview.</summary>
    internal ProfileDocument? PreviewDocument { get; set; }

    internal IReadOnlyList<StagedAsset> Assets { get; set; } = [];

    /// <summary>The validated preview.png in staging, or null.</summary>
    internal string? PreviewImagePath { get; set; }

    /// <summary>Every problem, for the log (entry names sanitized; no content, no local paths).</summary>
    internal string DescribeForLog() =>
        $"package \"{PackageManifest.SafeForLog(SourceFileName)}\": {Compatibility}"
        + (Manifest is { } m ? $", format {m.FormatVersion}, Plate schema {m.PlateSchemaVersion}, {m.Assets.Count} assets" : string.Empty)
        + (Diagnostics.Errors.Count > 0 ? "; " + string.Join("; ", Diagnostics.Errors.Take(8)) : string.Empty)
        + (Diagnostics.Warnings.Count > 0 ? "; warnings: " + string.Join(", ", Diagnostics.Warnings.Select(w => w.Code)) : string.Empty);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PreparedProfile = null;
        PreviewDocument = null;
        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless: only this import's own copies are in there, and leftovers are swept at startup.
            log.Warning($"AetherFrame could not remove a temporary import folder yet ({ex.GetType().Name}).");
        }
    }
}
