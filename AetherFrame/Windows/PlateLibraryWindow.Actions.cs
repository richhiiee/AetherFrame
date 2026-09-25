using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates actions: the selected-Plate action bar, the Create / Rename / Delete prompts, the
/// unsaved-changes prompt guarding a switch to another Plate, and running Library operations
/// without blocking Draw (each runs as a task; its outcome is applied on the render thread).
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    private const string RenamePopupId = "Rename Plate##AetherFrameRenamePlate";
    private const string DeletePopupId = "Delete Plate##AetherFrameDeletePlate";
    private const string UnsavedPopupId = "Unsaved Changes##AetherFramePlateSwitch";
    private const string BasicGuidancePopupId = "New to AetherFrame?##AetherFrameBasicGuidance";

    // Deferred popup opens: requests can come from inside the card grid's child window, but
    // OpenPopup/BeginPopup must share one id-stack scope (see ProfileEditorWindow).
    private bool pendingRenamePopup;
    private bool pendingDeletePopup;
    private bool pendingGuardPrompt;

    private Guid renameTargetId;
    private string renameBuffer = string.Empty;
    private string? renameError;

    private Guid deleteTargetId;

    // Opening another Plate while the open one has unsaved changes waits on this answer.
    private (Guid PlateId, bool Basic)? guardedOpen;
    private Task<bool>? guardSaveTask;

    private Task<Action?>? operationTask;
    private string operationName = string.Empty;
    private string? errorMessage;
    private string? statusMessage;
    private bool libraryLoadFailed;

    private bool IsBusy => operationTask is not null;

    /// <summary>Shown instead of the grid when the Library failed to load at startup.</summary>
    internal void MarkLoadFailed() => libraryLoadFailed = true;

    // ---------------------------------------------------------------- status footer

    /// <summary>What's happening (busy/error/status), or the selected Plate's own info, plus a
    /// quiet reminder that actions now live on each card's right-click menu (see
    /// <see cref="DrawPlateContextMenuItems"/>) — the persistent action button row is gone.</summary>
    private void DrawStatusFooter()
    {
        var selected = selectedPlateId is { } id ? library.FindPlate(id) : null;

        if (IsBusy)
        {
            ImGui.TextDisabled("Working...");
        }
        else if (errorMessage is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
        else if (statusMessage is { } status)
        {
            ImGui.TextColored(EditorWidgets.SuccessColor with { W = 0.85f }, status);
        }
        else if (selected is { } plate)
        {
            var openHere = profileService.OpenPlateId == plate.PlateId;
            ImGui.TextDisabled(plate.IsReady
                ? $"Last saved {plate.ModifiedUtc.ToLocalTime():g}{(openHere ? " · Open in the editor" : string.Empty)}"
                : plate.Problem ?? "This Plate can't be opened.");
        }
        else
        {
            ImGui.TextDisabled("Select a Plate. Double-click to edit; drag to reorder.");
        }

        ImGui.TextDisabled("Right click a Plate for actions");
    }

    /// <summary>
    /// The selected-Plate action menu: Preview, Open in Basic / Advanced Editor (always the player's
    /// explicit choice; a double-click picks automatically), Set Active, Duplicate,
    /// Save as Template, Export, Rename, Delete — one reusable menu shown on a card's right-click,
    /// replacing the old persistent action bar. Every item reuses the exact same service calls,
    /// <see cref="RunOperation{T}"/> plumbing, and existing Rename/Delete/Save-as-Template popups
    /// the old buttons already used (matching their exact enabled/disabled rules) — nothing here
    /// is a second copy of that logic, and nothing is keyed off <see cref="PlateSummary.DisplayName"/>
    /// beyond pre-filling a text field. Must be called between a matching BeginPopup/EndPopup.
    /// </summary>
    private void DrawPlateContextMenuItems(PlateSummary plate, CharacterContext? character, Guid? activePlateId)
    {
        var ready = plate.IsReady;
        var isActive = plate.PlateId == activePlateId;

        using (ImRaii.Disabled(!ready))
        {
            if (ImGui.MenuItem("Preview"))
            {
                showInViewer(plate.PlateId);
            }

            if (ImGui.MenuItem("Open in Basic Editor"))
            {
                RequestOpen(plate.PlateId, basic: true);
            }

            if (ImGui.MenuItem("Open in Advanced Editor"))
            {
                RequestOpen(plate.PlateId, basic: false);
            }
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!ready || character is null || isActive || IsBusy))
        {
            if (ImGui.MenuItem("Set Active") && character is { } who)
            {
                var plateId = plate.PlateId;
                var name = plate.DisplayName;
                RunOperation("set the Active Plate", () => library.SetActivePlateAsync(who, plateId),
                    () => statusMessage = $"\"{name}\" is now {DescribeCharacter(who)}'s Active Plate.");
            }
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!ready || IsBusy))
        {
            if (ImGui.MenuItem("Duplicate"))
            {
                var sourceId = plate.PlateId;
                RunOperation<Guid>("duplicate the Plate", () => library.DuplicatePlateAsync(sourceId),
                    newId => selectedPlateId = newId);
            }

            if (ImGui.MenuItem("Save as Template"))
            {
                saveAsTemplateSourcePlateId = plate.PlateId;
                saveAsTemplateBuffer = plate.DisplayName;
                saveAsTemplateError = null;
                pendingSaveAsTemplatePopup = true;
            }

            if (ImGui.MenuItem("Export"))
            {
                OpenExportDialog(plate.PlateId, plate.DisplayName);
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Rename"))
            {
                renameTargetId = plate.PlateId;
                renameBuffer = plate.DisplayName;
                renameError = null;
                pendingRenamePopup = true;
            }
        }

        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.MenuItem("Delete"))
            {
                deleteTargetId = plate.PlateId;
                pendingDeletePopup = true;
            }
        }
    }

    private static string DescribeCharacter(CharacterContext character) =>
        string.IsNullOrWhiteSpace(character.Name) ? "this character" : character.Name;

    // ---------------------------------------------------------------- opening Plates

    /// <summary>
    /// A double-click on a Plate: the editor already showing this Plate, if one is; otherwise the one
    /// its content suits (<see cref="EditorSurfaceChooser"/>) — Basic for an Adventure Plate layout,
    /// Advanced for Blank Canvas and freeform Plates. Judged from the open document when it's the
    /// open Plate (it may have unsaved changes), else from its saved version.
    /// </summary>
    private void RequestEdit(Guid plateId)
    {
        var isOpen = profileService.OpenPlateId == plateId;
        var kind = isOpen && activeEditor() is { } showing
            ? showing
            : EditorSurfaceChooser.ForDocument(isOpen ? profileService.CurrentProfile : library.GetSavedDocument(plateId));
        RequestOpen(plateId, kind == EditorSurfaceKind.Basic);
    }

    /// <summary>
    /// Opens a Plate in an editor. Switching away from a Plate with unsaved changes asks first;
    /// reopening the Plate that's already open just shows the editor.
    /// </summary>
    private void RequestOpen(Guid plateId, bool basic)
    {
        errorMessage = null;

        if (profileService.OpenPlateId == plateId)
        {
            ShowEditor(basic);
            return;
        }

        editorSession.CommitPendingEdits();
        if (profileService.CurrentProfile is not null && editorSession.IsDirty)
        {
            guardedOpen = (plateId, basic);
            guardSaveTask = null;
            pendingGuardPrompt = true;
            return;
        }

        OpenNow(plateId, basic);
    }

    /// <summary>
    /// The one-time suggestion before the first Advanced Editor, while <see cref="advancedEntry"/>
    /// holds an Advanced open back (see <see cref="ShowEditor"/>). For a Plate that suits Basic: Try
    /// Basic Editor (the suggested choice, opening this Plate in Basic) or Continue to Advanced. For
    /// a freeform Plate, which can't sensibly open in Basic: what Basic is and how to start with it,
    /// and Continue to Advanced only. Closing it always continues to Advanced, and any answer
    /// handles the guidance for good.
    /// </summary>
    private void DrawBasicGuidancePopup()
    {
        if (advancedEntry.ConsumePromptRequest())
        {
            ImGui.OpenPopup(BasicGuidancePopupId);
        }

        if (!advancedEntry.IsWaiting)
        {
            return;
        }

        var open = true;
        BasicGuidanceAnswer? answer = null;
        using (var popup = ImRaii.PopupModal(BasicGuidancePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            if (popup.Success)
            {
                advancedEntry.MarkShown();
                var offerBasic = advancedEntry.OffersTryBasic;
                using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (380f * ImGuiHelpers.GlobalScale)))
                {
                    ImGui.TextUnformatted("Basic Editor is the easiest place to start and uses familiar FFXIV style controls.");
                    ImGui.Spacing();
                    ImGui.TextDisabled("Advanced Editor gives you freeform positioning, layering and additional controls.");
                    if (!offerBasic)
                    {
                        ImGui.Spacing();
                        ImGui.TextUnformatted("This Plate is a freeform design, so it opens in the Advanced Editor.");
                        ImGui.TextDisabled("Basic is the recommended start for Adventure Plate layouts: choose Create Plate and pick Adventure Plate Classic.");
                    }
                }

                ImGui.Spacing();
                var buttonSize = new Vector2(170f * ImGuiHelpers.GlobalScale, 0f);
                if (offerBasic)
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.ActiveToggleColor))
                    {
                        if (ImGui.Button("Try Basic Editor", buttonSize))
                        {
                            answer = BasicGuidanceAnswer.TryBasicEditor;
                            ImGui.CloseCurrentPopup();
                        }
                    }

                    ImGui.SameLine();
                }

                if (ImGui.Button("Continue to Advanced", buttonSize))
                {
                    answer = BasicGuidanceAnswer.ContinueToAdvanced;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        // Its close button: continue to Advanced. Only once the prompt has really been on screen,
        // so a frame where it isn't open yet can never count as an answer.
        if (answer is null && !open && advancedEntry.WasShown)
        {
            answer = BasicGuidanceAnswer.Closed;
        }

        if (answer is { } given && advancedEntry.Answer(given) is { } editor)
        {
            if (editor == EditorSurfaceKind.Basic)
            {
                openBasicEditor();
            }
            else
            {
                openAdvancedEditor();
            }
        }
    }

    private void OpenNow(Guid plateId, bool basic)
    {
        try
        {
            profileService.OpenPlate(plateId);
            ShowEditor(basic);
        }
        catch (Exception ex) when (ex is PlateLibraryException or InvalidOperationException)
        {
            errorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            errorMessage = "That Plate couldn't be opened. See the Dalamud log for details.";
            DalamudServices.Log.Error(ex, "AetherFrame failed to open a Plate.");
        }
    }

    /// <summary>
    /// Shows the open Plate in an editor. Every way My Plates opens the Advanced Editor comes
    /// through here — Open in Advanced Editor, Edit and double-click on a Plate that opens in
    /// Advanced, Use Template (Blank Canvas and other freeform Templates), and opening after the
    /// unsaved-changes prompt — so this is where a player who has never used the Basic Editor is
    /// asked once, before their first Advanced Editor (see <see cref="BasicGuidance"/>). The only
    /// other way into Advanced, switching from the Basic Editor, needs no question: opening Basic
    /// already handled it.
    /// </summary>
    private void ShowEditor(bool basic)
    {
        if (basic)
        {
            openBasicEditor();
            return;
        }

        if (advancedEntry.TryEnterAdvanced(activeEditor() == EditorSurfaceKind.Advanced, profileService.CurrentProfile))
        {
            openAdvancedEditor();
        }
    }

    /// <summary>After "Save" in the unsaved-changes prompt: open the next Plate once the save
    /// succeeds; if it fails, stay on the current Plate with the error showing.</summary>
    private void AdvanceGuardedOpen()
    {
        if (guardSaveTask is not { IsCompleted: true } task || guardedOpen is not { } open)
        {
            return;
        }

        guardSaveTask = null;
        guardedOpen = null;

        if (task.IsCompletedSuccessfully && task.Result)
        {
            OpenNow(open.PlateId, open.Basic);
        }
        else
        {
            errorMessage = editorSession.ErrorMessage ?? "The open Plate couldn't be saved, so it's still open.";
        }
    }

    private void DrawUnsavedChangesPopup()
    {
        if (pendingGuardPrompt)
        {
            ImGui.OpenPopup(UnsavedPopupId);
            pendingGuardPrompt = false;
        }

        using var popup = ImRaii.PopupModal(UnsavedPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
        {
            return;
        }

        if (guardedOpen is not { } open)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        var openName = profileService.CurrentProfile?.Name ?? "The open Plate";
        var targetName = library.FindPlate(open.PlateId)?.DisplayName ?? "the other Plate";
        ImGui.TextUnformatted($"\"{openName}\" has unsaved changes.");
        ImGui.TextUnformatted($"Save them before opening \"{targetName}\"?");
        ImGui.Spacing();

        var buttonSize = new Vector2(110f, 0f);
        using (ImRaii.Disabled(profileService.IsBusy || guardSaveTask is not null))
        {
            if (ImGui.Button(guardSaveTask is null ? "Save" : "Saving...", buttonSize))
            {
                guardSaveTask = editorSession.SaveProfileAsync();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Discard", buttonSize))
        {
            editorSession.DiscardChanges();
            guardedOpen = null;
            ImGui.CloseCurrentPopup();
            OpenNow(open.PlateId, open.Basic);
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
        {
            guardedOpen = null;
            ImGui.CloseCurrentPopup();
        }
    }

    // ---------------------------------------------------------------- rename

    private void DrawRenamePopup()
    {
        if (pendingRenamePopup)
        {
            ImGui.OpenPopup(RenamePopupId);
            pendingRenamePopup = false;
        }

        if (!ImGui.BeginPopupModal(RenamePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(300f);
        var submitted = ImGui.InputText("##PlateName", ref renameBuffer, PlateNaming.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);

        if (renameError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Rename", new Vector2(110f, 0f)) || submitted)
            {
                if (!PlateNaming.TryNormalizeName(renameBuffer, out var name, out var validationError))
                {
                    renameError = validationError;
                }
                else
                {
                    var plateId = renameTargetId;
                    RunOperation("rename the Plate", () => library.RenamePlateAsync(plateId, name));
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- delete

    private void DrawDeletePopup(CharacterContext? character)
    {
        if (pendingDeletePopup)
        {
            ImGui.OpenPopup(DeletePopupId);
            pendingDeletePopup = false;
        }

        if (!ImGui.BeginPopupModal(DeletePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var plate = library.FindPlate(deleteTargetId);
        if (plate is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Delete \"{plate.DisplayName}\"?");
        EditorWidgets.Hint("It's moved to AetherFrame's Plate trash folder, not destroyed. Its images are kept.");

        var activeForCurrent = character is { } who && plate.ActiveForContentIds.Contains(who.ContentId);
        var activeForOthers = plate.ActiveForContentIds.Count - (activeForCurrent ? 1 : 0);
        if (activeForCurrent)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, $"This is {DescribeCharacter(character!.Value)}'s Active Plate.");
            ImGui.TextColored(EditorWidgets.WarningColor, $"{DescribeCharacter(character.Value)} will be left without an Active Plate.");
        }

        if (activeForOthers > 0)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, activeForOthers == 1
                ? "It's also another character's Active Plate; that character will be left without one."
                : $"It's also the Active Plate of {activeForOthers} other characters; they will be left without one.");
        }

        if (profileService.OpenPlateId == plate.PlateId)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, editorSession.IsDirty
                ? "It's open in the editor with unsaved changes, which will be lost."
                : "It's open in the editor, which will close it.");
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.18f, 0.18f, 1f)))
            {
                if (ImGui.Button("Delete", new Vector2(110f, 0f)))
                {
                    var plateId = plate.PlateId;
                    var name = plate.DisplayName;
                    RunOperation<PlateDeletionResult>("delete the Plate", () => library.DeletePlateAsync(plateId), result =>
                    {
                        if (selectedPlateId == plateId)
                        {
                            selectedPlateId = null;
                        }

                        statusMessage = result.ClearedActiveForContentIds.Count > 0
                            ? $"Deleted \"{name}\". No Plate is Active for {(result.ClearedActiveForContentIds.Count == 1 ? "that character" : "those characters")} now."
                            : $"Deleted \"{name}\".";
                    });
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- running operations

    private void RunOperation(string name, Func<Task> operation, Action? onSuccess = null) =>
        StartOperation(name, async () =>
        {
            await operation().ConfigureAwait(false);
            return onSuccess;
        });

    private void RunOperation<T>(string name, Func<Task<T>> operation, Action<T> onSuccess) =>
        StartOperation(name, async () =>
        {
            var result = await operation().ConfigureAwait(false);
            return () => onSuccess(result);
        });

    private void StartOperation(string name, Func<Task<Action?>> operation)
    {
        if (IsBusy)
        {
            errorMessage = "Please wait for the current action to finish.";
            return;
        }

        errorMessage = null;
        statusMessage = null;
        operationName = name;

        operationTask = RunLoggedAsync(name, operation);
    }

    /// <summary>
    /// Logs an unexpected failure the moment it happens — not when the window next draws, which
    /// may be never if it was closed meanwhile. Refusals meant for the player aren't logged.
    /// </summary>
    private static async Task<Action?> RunLoggedAsync(string name, Func<Task<Action?>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PlateLibraryException and not TemplateLibraryException)
        {
            DalamudServices.Log.Error(ex, $"AetherFrame failed to {name}.");
            throw;
        }
    }

    /// <summary>Applies a finished operation's result or error on the render thread.</summary>
    private void AdvanceOperation()
    {
        if (operationTask is not { IsCompleted: true } task)
        {
            return;
        }

        operationTask = null;

        if (task.IsCompletedSuccessfully)
        {
            task.Result?.Invoke();
            return;
        }

        var exception = task.Exception?.GetBaseException();
        switch (exception)
        {
            case PlateLibraryException refused:
                errorMessage = refused.Message;
                break;

            case TemplateLibraryException refused:
                errorMessage = refused.Message;
                break;

            default:
                errorMessage = $"Couldn't {operationName}. See the Dalamud log for details.";
                break;
        }
    }
}
