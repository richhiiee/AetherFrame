using System;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Packages;

/// <summary>
/// Export and import of .aetherframe files for the UI: wires the pure package code to the Plate
/// Library, managed asset storage, and the private staging folder. Every member does file IO and
/// hashing, so the UI calls them off the draw thread; none of them touch ImGui.
///
/// <para>Export and Import write files, so each is an <see cref="OwnedOperations"/> operation:
/// unloading waits for one already running, and neither starts once unloading has begun.</para>
/// </summary>
internal sealed class PlatePackageService
{
    private readonly PlateLibraryService library;
    private readonly AssetStorageService assets;
    private readonly string stagingRoot;
    private readonly string generator;
    private readonly Func<string, bool>? isDecoderSupported;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly OwnedOperations operations;

    internal PlatePackageService(
        PlateLibraryService library,
        AssetStorageService assets,
        PlateStoragePaths paths,
        string generator,
        Func<string, bool>? isDecoderSupported = null,
        IAetherFrameLog? log = null,
        Func<DateTime>? utcNow = null,
        OwnedOperations? operations = null)
    {
        this.library = library;
        this.assets = assets;
        stagingRoot = paths.PackageStagingDirectory;
        this.generator = generator;
        this.isDecoderSupported = isDecoderSupported;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.operations = operations ?? new OwnedOperations();
    }

    /// <summary>
    /// Exports a Plate's SAVED state. <paramref name="previewPngPath"/> is an optional picture of
    /// it; there's no offscreen renderer yet, so this is only ever a ready thumbnail if one exists.
    /// </summary>
    internal PackageExportResult Export(Guid plateId, string destinationPath, bool overwrite, string? previewPngPath = null)
    {
        if (!operations.TryBegin(out var operation))
        {
            return PackageExportResult.Failed(PackageErrorCode.ExportFailed, "AetherFrame is closing, so nothing was exported.");
        }

        using (operation)
        {
            return ExportSaved(plateId, destinationPath, overwrite, previewPngPath);
        }
    }

    private PackageExportResult ExportSaved(Guid plateId, string destinationPath, bool overwrite, string? previewPngPath)
    {
        (string Json, string Name) saved;
        try
        {
            saved = library.GetSavedJsonForExport(plateId);
        }
        catch (PlateLibraryException ex)
        {
            return PackageExportResult.Failed(PackageErrorCode.ExportFailed, ex.Message);
        }

        return PackageExporter.Export(
            new PackageExportRequest(saved.Json, saved.Name, destinationPath, previewPngPath, overwrite), assets, stagingRoot, generator, isDecoderSupported, log);
    }

    /// <summary>Opens and fully validates a package. The caller disposes the result (deleting its staging folder).</summary>
    internal StagedPackage Inspect(string packagePath) => PackageReader.Open(packagePath, stagingRoot, isDecoderSupported, log);

    /// <summary>Imports a validated package as a new Plate. Never activates, opens, or binds it.</summary>
    internal async Task<PackageImportResult> ImportAsync(StagedPackage package)
    {
        if (!operations.TryBegin(out var operation))
        {
            return PackageImportResult.Failed(PackageErrorCode.CommitFailed, "AetherFrame is closing, so nothing was imported.");
        }

        using (operation)
        {
            return await PackageImporter.ImportAsync(package, library, assets, log, utcNow()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes staging folders left behind by an interrupted import (e.g. the game closing
    /// mid-preview). Only folders this service creates — named by a GUID — are ever removed.
    /// </summary>
    internal void SweepStaging()
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }

            foreach (var folder in Directory.GetDirectories(stagingRoot))
            {
                if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not clear old temporary import files ({ex.GetType().Name}).");
        }
    }
}
