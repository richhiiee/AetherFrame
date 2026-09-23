using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates actions: the selected-Plate action bar, the Create / Rename / Delete prompts, the
/// unsaved-changes prompt guarding a switch to another Plate, and running Library operations
/// without blocking Draw (each runs as a task; its outcome is applied on the render thread).
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    private const string CreatePopupId = "Create Plate##AetherFrameCreatePlate";
    private const string RenamePopupId = "Rename Plate##AetherFrameRenamePlate";
    private const string DeletePopupId = "Delete Plate##AetherFrameDeletePlate";
    private const string UnsavedPopupId = "Unsaved Changes##AetherFramePlateSwitch";

    // Deferred popup opens: requests can come from inside the card grid's child window, but
    // OpenPopup/BeginPopup must share one id-stack scope (see ProfileEditorWindow).
    private bool pendingCreatePopup;
    private bool pendingRenamePopup;
    private bool pendingDeletePopup;
    private bool pendingGuardPrompt;

    private PlateStartingLayout createLayout = PlateStartingLayout.AdventurePlateClassic;

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

    // ---------------------------------------------------------------- action bar

    private void DrawActionBar(CharacterContext? character, Guid? activePlateId)
    {
        var selected = selectedPlateId is { } id ? library.FindPlate(id) : null;
        var ready = selected is { IsReady: true };

        using (ImRaii.Disabled(!ready))
        {
            if (ImGui.Button("Preview"))
            {
                showInViewer(selected!.PlateId);
            }

            EditorWidgets.Tooltip("Show this Plate in the Plate Viewer. Nothing about it changes.");

            ImGui.SameLine();
            if (ImGui.Button("Edit"))
            {
                RequestOpen(selected!.PlateId, basic: true);
            }

            EditorWidgets.Tooltip("Open in the Basic Editor. Editing never changes which Plate is Active.");

            ImGui.SameLine();
            if (ImGui.Button("Advanced"))
            {
                RequestOpen(selected!.PlateId, basic: false);
            }

            EditorWidgets.Tooltip("Open in the Advanced Editor.");
        }

        ImGui.SameLine();
        var isActive = selected is not null && selected.PlateId == activePlateId;
        using (ImRaii.Disabled(!ready || character is null || isActive || IsBusy))
        {
            if (ImGui.Button("Set Active") && character is { } who)
            {
                var plateId = selected!.PlateId;
                var name = selected.DisplayName;
                RunOperation("set the Active Plate", () => library.SetActivePlateAsync(who, plateId),
                    () => statusMessage = $"\"{name}\" is now {DescribeCharacter(who)}'s Active Plate.");
            }
        }

        if (character is null)
        {
            EditorWidgets.Tooltip("Log in to a character to choose its Active Plate.");
        }
        else if (isActive)
        {
            EditorWidgets.Tooltip($"Already {DescribeCharacter(character.Value)}'s Active Plate.");
        }
        else
        {
            EditorWidgets.Tooltip($"Make this {DescribeCharacter(character.Value)}'s Active Plate.");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!ready || IsBusy))
        {
            if (ImGui.Button("Duplicate"))
            {
                var sourceId = selected!.PlateId;
                RunOperation<Guid>("duplicate the Plate", () => library.DuplicatePlateAsync(sourceId, characterIdentity.CurrentCharacter),
                    newId => selectedPlateId = newId);
            }

            EditorWidgets.Tooltip("Make an independent copy of this Plate as last saved. Images are shared, not copied.");

            ImGui.SameLine();
            if (ImGui.Button("Rename"))
            {
                renameTargetId = selected!.PlateId;
                renameBuffer = selected.DisplayName;
                renameError = null;
                pendingRenamePopup = true;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(selected is null || IsBusy))
        {
            if (ImGui.Button("Delete"))
            {
                deleteTargetId = selected!.PlateId;
                pendingDeletePopup = true;
            }
        }

        // Second row: what's happening, or what went wrong.
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
    }

    private static string DescribeCharacter(CharacterContext character) =>
        string.IsNullOrWhiteSpace(character.Name) ? "this character" : character.Name;

    // ---------------------------------------------------------------- opening Plates

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

    private void ShowEditor(bool basic)
    {
        if (basic)
        {
            openBasicEditor();
        }
        else
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

        if (!ImGui.BeginPopupModal(UnsavedPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (guardedOpen is not { } open)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
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

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- create

    private void DrawCreatePopup()
    {
        if (pendingCreatePopup)
        {
            ImGui.OpenPopup(CreatePopupId);
            pendingCreatePopup = false;
        }

        if (!ImGui.BeginPopupModal(CreatePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TextUnformatted("Start from:");
        ImGui.Spacing();

        DrawLayoutChoice(PlateStartingLayout.AdventurePlateClassic, "Adventure Plate Classic",
            "The Adventure Plate canvas with a theme background.\nOpens in the Basic Editor.");
        DrawLayoutChoice(PlateStartingLayout.Blank, "Blank Plate",
            "An empty Adventure Plate canvas.\nOpens in the Advanced Editor.");

        ImGui.Spacing();
        var character = characterIdentity.CurrentCharacter;
        EditorWidgets.Hint(character is { } who
            ? $"The new Plate will belong to {DescribeCharacter(who)}. It becomes the Active Plate only if it's the character's first."
            : "No character is logged in, so the new Plate won't belong to a character yet.");
        ImGui.Spacing();

        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Create", new Vector2(110f, 0f)))
            {
                var layout = createLayout;
                RunOperation<PlateCreationResult>("create the Plate", () => library.CreatePlateAsync(layout, character), result =>
                {
                    selectedPlateId = result.PlateId;
                    searchText = string.Empty;
                    if (result.BecameActive && character is { } owner)
                    {
                        statusMessage = $"Created your first Plate. It's now {DescribeCharacter(owner)}'s Active Plate.";
                    }

                    RequestOpen(result.PlateId, PlateFactory.OpensInBasicEditor(layout));
                });
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawLayoutChoice(PlateStartingLayout layout, string label, string description)
    {
        if (ImGui.RadioButton(label, createLayout == layout))
        {
            createLayout = layout;
        }

        using (ImRaii.PushIndent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X))
        {
            EditorWidgets.Hint(description);
        }

        ImGui.Spacing();
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
        catch (Exception ex) when (ex is not PlateLibraryException)
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

            default:
                errorMessage = $"Couldn't {operationName}. See the Dalamud log for details.";
                break;
        }
    }
}
