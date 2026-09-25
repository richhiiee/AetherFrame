using System.Threading.Tasks;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The document actions of the shared editor action bar — Undo, Redo, Save, Revert — and exactly
/// when each is available. The Basic and Advanced editors both act through this one definition
/// over the one shared <see cref="EditorSession"/>, so they can never disagree about whether the
/// open Plate can be saved, reverted, undone, or redone (see <c>EditorActionBar</c>).
///
/// <para>Save and Revert only apply to unsaved changes: both are unavailable for a clean Plate, and
/// while a save is in flight. Every action re-checks its own availability, so a shortcut (Ctrl+S)
/// or a stale click can never save a clean Plate or revert one that has nothing to revert.</para>
/// </summary>
internal sealed class EditorDocumentCommands
{
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;

    internal EditorDocumentCommands(ProfileService profileService, EditorSession editorSession)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
    }

    internal bool HasPlate => profileService.CurrentProfile is not null;

    /// <summary>A save is being written.</summary>
    internal bool IsSaving => profileService.IsBusy;

    /// <summary>The open Plate has unsaved changes (including an edit still in progress).</summary>
    internal bool IsDirty => HasPlate && editorSession.IsDirty;

    internal bool CanUndo => HasPlate && editorSession.CanUndo;

    internal bool CanRedo => HasPlate && editorSession.CanRedo;

    internal bool CanSave => HasPlate && !profileService.IsBusy && editorSession.IsDirty;

    internal bool CanRevert => HasPlate && !profileService.IsBusy && editorSession.IsDirty && editorSession.CanRevert;

    internal void Undo()
    {
        if (CanUndo)
        {
            editorSession.Undo();
        }
    }

    internal void Redo()
    {
        if (CanRedo)
        {
            editorSession.Redo();
        }
    }

    /// <summary>Saves the open Plate when it has unsaved changes (the button and Ctrl+S alike). Returns whether a save started.</summary>
    internal bool Save()
    {
        if (!CanSave)
        {
            return false;
        }

        editorSession.SaveProfile();
        return true;
    }

    /// <summary>
    /// <see cref="Save"/>, for a caller that waits on the outcome (the unsaved-changes prompt's
    /// Save). False without saving when there's nothing to save; otherwise the save's own result.
    /// </summary>
    internal Task<bool> SaveAsync() => CanSave ? editorSession.SaveProfileAsync() : Task.FromResult(false);

    /// <summary>
    /// Restores the last saved version (after the editor has confirmed it). The revert is itself
    /// one undo step, so it can be taken back. Returns whether anything was reverted.
    /// </summary>
    internal bool Revert()
    {
        if (!CanRevert)
        {
            return false;
        }

        editorSession.RevertToSaved(undoable: true);
        return true;
    }
}
