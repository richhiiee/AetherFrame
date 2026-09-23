using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

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

    /// <summary>
    /// Imports an image and makes it the background: sets the image and switches to Image mode.
    /// Every other background setting (colors, gradient, texture) is kept for switching back.
    /// </summary>
    internal void SetBackground(string sourceFilePath)
    {
        ErrorMessage = null;

        Guid assetId;
        try
        {
            assetId = assetStorage.ImportImage(sourceFilePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
            ErrorMessage = ex.Message;
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
                ErrorMessage = ex.Message;
                return;
            }
        }

        try
        {
            profileService.UpdateBackground(apply);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

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
            ErrorMessage = ex.Message;
            return;
        }

        var after = profileService.CaptureCanvasLayoutState();
        RecordHistory(
            undo: () => profileService.RestoreCanvasLayoutState(before),
            redo: () => profileService.RestoreCanvasLayoutState(after));
    }
}
