using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Services;
using AetherFrame.Services.Packages;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// Import Preview: shows a validated .aetherframe file — what it is, whether this version can use
/// it, and exactly what would be imported, rendered by the shared Plate renderer — and imports it
/// as a new Plate only when the player says so.
///
/// Validation and import run off the draw thread (file IO and hashing); their results are only
/// ever applied here, in Draw. Nothing from the file is shown before it passed validation: an
/// Invalid or Unsupported package shows only its verdict and reasons, never its content.
/// </summary>
internal sealed class PackageImportWindow : Window, IDisposable
{
    private static readonly Vector2 PreviewSize = new(480f, 270f);

    private readonly PlatePackageService packages;
    private readonly ProfileRenderResources renderResources;
    private readonly Action<Guid, string> onImported;

    private Task<StagedPackage>? inspectTask;
    private StagedPackage? staged;
    private Task<PackageImportResult>? importTask;
    private string? fileName;
    private string? importError;

    /// <param name="onImported">Called (on the draw thread) with the new Plate's id and name.</param>
    internal PackageImportWindow(PlatePackageService packages, ProfileRenderResources renderResources, Action<Guid, string> onImported)
        : base("Import Plate##AetherFramePackageImport", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.packages = packages;
        this.renderResources = renderResources;
        this.onImported = onImported;
        RespectCloseHotkey = true;
    }

    private bool IsBusy => inspectTask is not null || importTask is not null;

    /// <summary>Starts checking a file the player chose. Replaces whatever was being previewed.</summary>
    internal void Begin(string path)
    {
        if (IsBusy)
        {
            return;
        }

        Release();
        fileName = Path.GetFileName(path);
        importError = null;
        inspectTask = Task.Run(() => packages.Inspect(path));
        IsOpen = true;
    }

    public override void OnClose()
    {
        // Anything still running cleans up after itself when it finishes.
        if (inspectTask is { } inspecting)
        {
            _ = inspecting.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Dispose();
                }
            }, TaskScheduler.Default);
            inspectTask = null;
        }

        if (importTask is { } importing && staged is { } pending)
        {
            _ = importing.ContinueWith(_ => pending.Dispose(), TaskScheduler.Default);
            importTask = null;
            staged = null;
        }

        Release();
    }

    public void Dispose() => OnClose();

    public override void Draw()
    {
        AdvanceTasks();

        if (inspectTask is not null)
        {
            ImGui.TextUnformatted($"Checking \"{fileName}\"...");
            ImGui.TextDisabled("Every part of the file is checked before anything is shown.");
            return;
        }

        if (staged is not { } package)
        {
            ImGui.TextDisabled("Choose Import in My Plates to pick an .aetherframe file.");
            return;
        }

        DrawVerdict(package);
        ImGui.Spacing();

        if (package.CanImport && package.Summary is { } summary && package.PreviewDocument is { } document)
        {
            DrawPreview(document);
            ImGui.Spacing();
            DrawDetails(summary);
        }

        DrawProblems(package);
        ImGui.Separator();
        DrawButtons(package);
    }

    private void DrawVerdict(StagedPackage package)
    {
        var title = package.Summary?.PlateName ?? fileName ?? "Plate file";
        ImGui.TextUnformatted(title);

        var color = package.Compatibility switch
        {
            PackageCompatibility.Supported => EditorWidgets.SuccessColor,
            PackageCompatibility.SupportedWithWarnings => EditorWidgets.WarningColor,
            _ => EditorWidgets.ErrorColor,
        };
        ImGui.TextColored(color, PackageText.Describe(package.Compatibility));
    }

    private void DrawPreview(Domain.Profiles.ProfileDocument document)
    {
        // The Plate's visual bounds (canvas plus Component overflow) fitted into the preview box. The
        // clip is the box, not the canvas, so intentional overflow shows but never spills onto the dialog.
        var previewMin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(PreviewSize);
        var fit = PlateViewFit.Fit(PreviewSize, ProfileVisualBounds.Compute(document));
        if (fit.Scale <= 0f)
        {
            return;
        }

        var origin = previewMin + fit.CanvasOffset;
        var canvasSize = new Vector2(document.CanvasWidth, document.CanvasHeight) * fit.Scale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + canvasSize, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1f)));
        drawList.PushClipRect(previewMin, previewMin + PreviewSize, true);
        ProfileRenderer.Draw(drawList, document, origin, fit.Scale, renderResources, ProfileRenderOptions.Finished);
        drawList.PopClipRect();
    }

    private static void DrawDetails(PackageSummary summary)
    {
        using var table = ImRaii.Table("##PackageDetails", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
        {
            return;
        }

        Row("Canvas", $"{summary.CanvasWidth:0} x {summary.CanvasHeight:0}");
        Row("Elements", summary.UnsupportedElementCount > 0
            ? $"{summary.ElementCount} ({summary.UnsupportedElementCount} from a newer version)"
            : summary.ElementCount.ToString());
        Row("Images", summary.AssetCount == 0 ? "None" : $"{summary.AssetCount} ({PackageText.FormatBytes(summary.TotalAssetBytes)})");
        Row("File format", $"Version {summary.FormatVersion}");

        static void Row(string label, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(label);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(value);
        }
    }

    private void DrawProblems(StagedPackage package)
    {
        foreach (var message in package.Diagnostics.Errors.Select(e => e.Message).Distinct().Take(4))
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, message);
        }

        foreach (var warning in package.Diagnostics.Warnings)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, warning.Message);
        }

        if (importError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
    }

    private void DrawButtons(StagedPackage package)
    {
        if (package.CanImport)
        {
            EditorWidgets.Hint("It's added to My Plates as a new Plate. Nothing you have is replaced,\nand it won't become any character's Active Plate.");
            ImGui.Spacing();
        }

        using (ImRaii.Disabled(!package.CanImport || importTask is not null))
        {
            if (ImGui.Button(importTask is null ? "Import as New Plate" : "Importing...", new Vector2(160f, 0f)))
            {
                importError = null;
                importTask = packages.ImportAsync(package);
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(importTask is not null))
        {
            if (ImGui.Button(package.CanImport ? "Cancel" : "Close", new Vector2(110f, 0f)))
            {
                IsOpen = false;
            }
        }
    }

    /// <summary>Applies finished background work, on the draw thread.</summary>
    private void AdvanceTasks()
    {
        if (inspectTask is { IsCompleted: true } inspecting)
        {
            inspectTask = null;
            if (inspecting.IsCompletedSuccessfully)
            {
                staged = inspecting.Result;
                renderResources.Images.SetPreviewImages(staged.Assets.Select(a => (a.LocalAssetId, a.StagedPath)));
            }
            else
            {
                importError = "The file couldn't be checked. See the Dalamud log for details.";
                DalamudServices.Log.Error(inspecting.Exception?.GetBaseException(), "AetherFrame failed to check a Plate file.");
            }
        }

        if (importTask is { IsCompleted: true } importing)
        {
            importTask = null;
            var result = importing.IsCompletedSuccessfully
                ? importing.Result
                : PackageImportResult.Failed(PackageErrorCode.CommitFailed, "The Plate couldn't be imported. Nothing was changed.");

            if (!importing.IsCompletedSuccessfully)
            {
                DalamudServices.Log.Error(importing.Exception?.GetBaseException(), "AetherFrame failed to import a Plate file.");
            }

            if (result.Succeeded)
            {
                onImported(result.PlateId, result.PlateName);
                IsOpen = false;
                Release();
            }
            else
            {
                importError = result.Error?.Message ?? "The Plate couldn't be imported.";
            }
        }
    }

    /// <summary>Drops the previewed package: its textures, then its staging folder.</summary>
    private void Release()
    {
        renderResources.Images.ClearPreviewImages();
        staged?.Dispose();
        staged = null;
    }
}
