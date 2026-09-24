using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Thumbnails;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates: Export and Import of .aetherframe files. Both start only from the player's own
/// choice of file; exporting never replaces an existing file without asking, and importing hands
/// the chosen file to the Import Preview, which validates it before showing anything.
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    private const string OverwritePopupId = "Replace File?##AetherFrameExportOverwrite";
    private const string PackageFilter = "AetherFrame Plate{" + PackagePolicy.FileExtension + "}";

    private bool pendingOverwritePopup;
    private (Guid PlateId, string Name, string Path)? pendingOverwrite;

    /// <summary>Called by the Import Preview after a successful import.</summary>
    internal void OnPlateImported(Guid plateId, string name)
    {
        selectedPlateId = plateId;
        searchText = string.Empty;
        errorMessage = null;
        statusMessage = $"Imported \"{name}\" as a new Plate.";
        IsOpen = true;
    }

    private void OpenImportDialog() =>
        fileDialogManager.OpenFileDialog("Import Plate", PackageFilter, (chosen, path) =>
        {
            if (chosen && !string.IsNullOrWhiteSpace(path))
            {
                beginImport(path);
            }
        });

    private void OpenExportDialog(Guid plateId, string name) =>
        fileDialogManager.SaveFileDialog("Export Plate", PackageFilter, PackagePaths.SuggestFileName(name), PackagePolicy.FileExtension, (chosen, path) =>
        {
            if (!chosen || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!path.EndsWith(PackagePolicy.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                path += PackagePolicy.FileExtension;
            }

            if (File.Exists(path))
            {
                pendingOverwrite = (plateId, name, path);
                pendingOverwritePopup = true;
                return;
            }

            StartExport(plateId, name, path, overwrite: false);
        });

    private void StartExport(Guid plateId, string name, string path, bool overwrite)
    {
        // Read on the draw thread; the export itself (file IO, hashing) runs off it.
        var previewPath = ReadyThumbnailPath(plateId);
        RunOperation<PackageExportResult>("export the Plate", () => Task.Run(() => packages.Export(plateId, path, overwrite, previewPath)), result =>
        {
            if (result.Succeeded)
            {
                statusMessage = $"Exported \"{name}\" to {Path.GetFileName(result.FilePath)}.";
            }
            else
            {
                errorMessage = result.FailureMessage;
            }
        });
    }

    /// <summary>A ready thumbnail of exactly the saved Plate, if one exists (none are generated yet).</summary>
    private string? ReadyThumbnailPath(Guid plateId)
    {
        if (library.FindPlate(plateId) is not { IsReady: true } plate)
        {
            return null;
        }

        var thumbnail = thumbnails.Get(plateId, PlateThumbnailService.VersionKeyFor(plate.Revision, plate.ModifiedUtc), () => library.GetSavedDocument(plateId));
        return thumbnail.State == PlateThumbnailState.Ready ? thumbnail.ImagePath : null;
    }

    private void DrawOverwritePopup()
    {
        if (pendingOverwritePopup)
        {
            ImGui.OpenPopup(OverwritePopupId);
            pendingOverwritePopup = false;
        }

        if (!ImGui.BeginPopupModal(OverwritePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (pendingOverwrite is not { } pending)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"\"{Path.GetFileName(pending.Path)}\" already exists.");
        ImGui.TextUnformatted("Replace it with this Plate?");
        ImGui.Spacing();

        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Replace", new Vector2(110f, 0f)))
            {
                pendingOverwrite = null;
                StartExport(pending.PlateId, pending.Name, pending.Path, overwrite: true);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            pendingOverwrite = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
