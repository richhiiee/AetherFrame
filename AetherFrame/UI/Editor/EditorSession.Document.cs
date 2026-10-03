using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Document-level half of <see cref="EditorSession"/>: the shared <see cref="ProfileBackground"/>
/// and the canvas size. Same undo/redo and dirty-state rules as element edits.
/// </summary>
internal sealed partial class EditorSession
{
    // A background slider/color edit in progress; same "commit on deactivate" pattern as
    // pendingEditBefore, just for the profile-level background.
    private ProfileBackground? pendingBackgroundBefore;

    // A multi-part document edit in progress (Basic mode: several elements plus the Identity
    // settings at once), coalesced into one history entry like the element/background pending
    // edits; see BeginOrContinueDocumentEdit.
    private ProfileService.DocumentState? pendingDocumentBefore;

    // The most recent document edit's history record, while it's still the top of the undo stack,
    // so a follow-up refinement can be folded into it (AmendLastDocumentEdit).
    private DocumentEditRecord? lastDocumentEdit;

    /// <summary>
    /// Imports an image and makes it the background: sets the image and switches to Image mode.
    /// Every other background setting (colors, gradient, texture) is kept for switching back.
    /// </summary>
    internal void SetBackground(string sourceFilePath)
    {
        ErrorMessage = null;

        if (ImportImage(sourceFilePath) is not { } assetId)
        {
            return;
        }

        ApplyBackgroundEdit(style =>
        {
            style.ImageAssetId = assetId;
            style.Mode = ProfileBackgroundMode.Image;
        });
    }

    /// <summary>
    /// Clears the background image (and leaves Image mode, which would otherwise show nothing).
    /// The asset itself is left on disk, so undo can restore it.
    /// </summary>
    internal void RemoveBackground() => ApplyBackgroundEdit(style =>
    {
        style.ImageAssetId = null;
        if (style.Mode == ProfileBackgroundMode.Image)
        {
            style.Mode = ProfileBackgroundMode.None;
        }
    });

    /// <summary>
    /// Applies a discrete background edit (combo, button, preset, swatch) and immediately records
    /// one history entry, mirroring <see cref="ApplyImmediateEdit"/> for elements.
    /// </summary>
    internal void ApplyBackgroundEdit(Action<ProfileBackground> update)
    {
        ErrorMessage = null;
        CommitPendingEdits();

        ProfileBackground before;
        try
        {
            before = profileService.CaptureBackgroundState();
            profileService.UpdateBackground(update);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        var after = profileService.CaptureBackgroundState();
        if (after.ContentEquals(before))
        {
            return;
        }

        RecordHistory(
            undo: () => profileService.RestoreBackgroundState(before),
            redo: () => profileService.RestoreBackgroundState(after));
    }

    /// <summary>
    /// Applies a live, in-progress background edit (e.g. an opacity slider or color picker being
    /// dragged) without recording history yet. Mirrors <see cref="BeginOrContinueEdit"/> for
    /// elements; call <see cref="CommitPendingBackgroundEdit"/> once the edit completes.
    /// </summary>
    internal void BeginOrContinueBackgroundEdit(Action<ProfileBackground> apply)
    {
        ErrorMessage = null;

        if (pendingBackgroundBefore is null)
        {
            CommitPendingEdit();

            try
            {
                pendingBackgroundBefore = profileService.CaptureBackgroundState();
            }
            catch (Exception ex)
            {
                ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
                return;
            }
        }

        try
        {
            profileService.UpdateBackground(apply);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
        }
    }

    /// <summary>
    /// One frame of a background angle slider (the gradient's, the Pattern's) as a continuous
    /// background edit, the value going through <see cref="TypedSliderValues.Angle"/>. ImGui applies
    /// a value typed with Ctrl+Click on every keystroke, so by the last one the background already
    /// holds what the earlier ones made of it (typing 1e39 passes 1e3, which wraps to 280). An
    /// angle that changes nothing therefore leaves the one the background held before this edit
    /// began, not the previous keystroke's.
    /// </summary>
    internal void ContinueBackgroundAngleEdit(float sliderValue, Func<ProfileBackground, float> read, Action<ProfileBackground, float> write) =>
        BeginOrContinueBackgroundEdit(style => write(style, TypedSliderValues.Angle(sliderValue, read(pendingBackgroundBefore ?? style))));

    /// <summary>Finalizes a pending background edit started by <see cref="BeginOrContinueBackgroundEdit"/>.</summary>
    internal void CommitPendingBackgroundEdit()
    {
        if (pendingBackgroundBefore is not { } before)
        {
            return;
        }

        pendingBackgroundBefore = null;

        ProfileBackground after;
        try
        {
            after = profileService.CaptureBackgroundState();
        }
        catch
        {
            // Profile no longer editable (e.g. character switch mid-edit); nothing to record.
            return;
        }

        if (after.ContentEquals(before))
        {
            return;
        }

        RecordHistory(
            undo: () => profileService.RestoreBackgroundState(before),
            redo: () => profileService.RestoreBackgroundState(after));
    }

    /// <summary>
    /// Applies an edit spanning any number of elements and document settings (performed by
    /// <paramref name="edit"/> directly through <see cref="ProfileService"/>, which records no
    /// history itself) as ONE undoable history entry, via whole-document before/after snapshots.
    /// Nothing is recorded if the edit changed nothing. Used by the Basic editor, whose single
    /// actions (e.g. choosing a title layout) routinely touch several elements at once.
    /// All or nothing: an edit that throws partway (a section's heading created, then its value
    /// refused at the element limit) is rolled back, so nothing of it stays on the Plate outside
    /// the history.
    /// </summary>
    internal bool ApplyDocumentEdit(Action edit)
    {
        ErrorMessage = null;
        CommitPendingEdits();

        ProfileService.DocumentState before;
        try
        {
            before = profileService.CaptureDocumentState();
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return false;
        }

        try
        {
            edit();
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            RollBackFailedEdit(before);
            return false;
        }

        RecordDocumentEdit(before, profileService.CaptureDocumentState());

        // An edit can take away what is selected (removing a Component, choosing None for its slot).
        DropSelectionIfMissing();
        return true;
    }

    /// <summary>
    /// Continuous counterpart of <see cref="ApplyDocumentEdit"/> (slider drags, typing): applies
    /// live, and records a single entry for the whole run once <see cref="CommitPendingDocumentEdit"/>
    /// is called (e.g. when the widget is released). A step that throws ends the run and rolls the
    /// whole run back to where it began (nothing is recorded), like <see cref="ApplyDocumentEdit"/>.
    /// </summary>
    internal void BeginOrContinueDocumentEdit(Action edit)
    {
        ErrorMessage = null;

        if (pendingDocumentBefore is null)
        {
            CommitPendingEdit();
            CommitPendingBackgroundEdit();

            try
            {
                pendingDocumentBefore = profileService.CaptureDocumentState();
            }
            catch (Exception ex)
            {
                ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
                return;
            }
        }

        try
        {
            edit();
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            if (pendingDocumentBefore is { } before)
            {
                pendingDocumentBefore = null;
                RollBackFailedEdit(before);
            }
        }
    }

    /// <summary>
    /// Best-effort return to <paramref name="before"/> after a document edit threw partway. Restoring
    /// replaces the element instances, so a selection of an element that is gone is dropped, as
    /// after an undo. A restore that fails itself is logged: the Plate then holds part of the edit.
    /// </summary>
    private void RollBackFailedEdit(ProfileService.DocumentState before)
    {
        try
        {
            profileService.RestoreDocumentState(before);
            DropSelectionIfMissing();
        }
        catch (Exception restoreEx)
        {
            log.Error(restoreEx, "AetherFrame couldn't roll back a document edit that failed partway; the open Plate may hold part of it.");
        }

        InvalidateDirtyMemo();
    }

    /// <summary>Finalizes a pending edit started by <see cref="BeginOrContinueDocumentEdit"/>.</summary>
    internal void CommitPendingDocumentEdit()
    {
        if (pendingDocumentBefore is not { } before)
        {
            return;
        }

        pendingDocumentBefore = null;

        ProfileService.DocumentState after;
        try
        {
            after = profileService.CaptureDocumentState();
        }
        catch
        {
            // Profile no longer editable (e.g. character switch mid-edit); nothing to record.
            return;
        }

        RecordDocumentEdit(before, after);
    }

    /// <summary>True while a continuous document edit (see <see cref="BeginOrContinueDocumentEdit"/>) is open.</summary>
    internal bool HasPendingDocumentEdit => pendingDocumentBefore is not null;

    /// <summary>
    /// Folds a follow-up change into the most recent document edit instead of recording a new
    /// history entry — for refinements that complete that edit (e.g. re-measuring an inline title
    /// layout once its font finishes loading). Returns false, changing nothing, if that edit is no
    /// longer the latest history entry (something else happened since, or it was undone) — or,
    /// without an error message, if the Plate can't be changed right now (a save in flight, whose
    /// file must match the Plate it was captured from); the caller may try again later.
    /// </summary>
    internal bool AmendLastDocumentEdit(Action edit)
    {
        if (lastDocumentEdit is not { } record || undoStack.Count == 0 || !ReferenceEquals(undoStack[^1], record.Entry)
            || redoStack.Count > 0 || pendingEditBefore is not null || pendingBackgroundBefore is not null || pendingDocumentBefore is not null)
        {
            return false;
        }

        try
        {
            // The same check every other edit path makes before touching the document: this one
            // runs unprompted (each frame, until the font is ready), so being refused is no error.
            profileService.RequireEditableProfile();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        try
        {
            edit();
            record.After = profileService.CaptureDocumentState();
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return false;
        }

        InvalidateDirtyMemo();
        return true;
    }

    private void RecordDocumentEdit(ProfileService.DocumentState before, ProfileService.DocumentState after)
    {
        if (StatesEqual(before, after))
        {
            return;
        }

        var record = new DocumentEditRecord(before, after);
        record.Entry = RecordHistory(
            undo: () =>
            {
                profileService.RestoreDocumentState(record.Before);
                DropSelectionIfMissing();
            },
            redo: () =>
            {
                profileService.RestoreDocumentState(record.After);
                DropSelectionIfMissing();
            });
        lastDocumentEdit = record;
    }

    /// <summary>
    /// Resizes the current profile's canvas, optionally scaling every element's Position/Size
    /// proportionally to the new dimensions (rotation values are never touched either way — see
    /// <c>ProfileService.ResizeCanvas</c>), and records one undoable history entry. Surfaces
    /// failures (no profile loaded, etc.) via <see cref="ErrorMessage"/>.
    /// </summary>
    internal void ApplyCanvasResize(float newWidth, float newHeight, bool scaleContentsProportionally)
    {
        ErrorMessage = null;
        CommitPendingEdits();

        ProfileService.CanvasLayoutState before;
        try
        {
            before = profileService.CaptureCanvasLayoutState();
            profileService.ResizeCanvas(newWidth, newHeight, scaleContentsProportionally);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        var after = profileService.CaptureCanvasLayoutState();
        RecordHistory(
            undo: () => profileService.RestoreCanvasLayoutState(before),
            redo: () => profileService.RestoreCanvasLayoutState(after));
    }

    /// <summary>Before/after snapshots of one document edit; After is replaced by an amendment.</summary>
    private sealed class DocumentEditRecord
    {
        internal DocumentEditRecord(ProfileService.DocumentState before, ProfileService.DocumentState after)
        {
            Before = before;
            After = after;
        }

        internal ProfileService.DocumentState Before { get; }

        internal ProfileService.DocumentState After { get; set; }

        internal HistoryEntry? Entry { get; set; }
    }
}
