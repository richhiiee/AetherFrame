using System.Threading.Tasks;

namespace AetherFrame.UI.Editor;

/// <summary>
/// When an editor window must refuse a close and ask about unsaved work first: the window was
/// open and has just been closed (by its title bar Close, Escape, or a toggle from elsewhere), the
/// close wasn't already confirmed (Save/Discard answered, or a handoff to the other editor), a Plate
/// is open, and it has unsaved changes. Evaluated before Dalamud acts on the close, so a refused
/// close never starts closing at all.
/// </summary>
internal static class CloseGuard
{
    internal static bool ShouldVeto(bool wasOpen, bool isOpen, bool closeConfirmed, bool hasPlate, bool isDirty) =>
        wasOpen && !isOpen && !closeConfirmed && hasPlate && isDirty;
}

/// <summary>
/// One editor window's unsaved-changes protection — the same for the Basic and Advanced editors,
/// which each own one. A close that would lose unsaved work is refused (<see cref="PreOpenCheck"/>,
/// with <see cref="ShouldReopenOnClose"/> as the fallback for a close that got through) and the
/// question asked instead: <b>Save</b> (close once the save succeeds; on failure stay open with the
/// edits and the error), <b>Discard</b> (restore the last saved version, then close), or
/// <b>Cancel</b> (stay open, every edit intact). Handing editing to the other editor is never
/// guarded (<see cref="ConfirmClose"/>): the same document, dirty state, and history continue there.
///
/// <para>The window drives it: <c>IsOpen = guard.PreOpenCheck(IsOpen)</c> every frame, closes when
/// <see cref="Advance"/> or <see cref="Discard"/> says so, and draws the question while
/// <see cref="IsAsking"/> (opening it when <see cref="ConsumePromptRequest"/> says to).</para>
/// </summary>
internal sealed class EditorCloseGuard
{
    private readonly EditorSession editorSession;
    private readonly EditorDocumentCommands commands;

    // Whether the window was open at the previous PreOpenCheck (so a close is noticed exactly once).
    private bool openLastFrame;
    private bool closeConfirmed;
    private bool promptRequested;
    private Task<bool>? saveTask;

    internal EditorCloseGuard(EditorSession editorSession, EditorDocumentCommands commands)
    {
        this.editorSession = editorSession;
        this.commands = commands;
    }

    /// <summary>A close is waiting on the Save / Discard / Cancel answer (or on the save it chose).</summary>
    internal bool IsAsking { get; private set; }

    /// <summary>The answer was Save and that save is still being written.</summary>
    internal bool IsSaving => saveTask is not null;

    /// <summary>Whether the question's Save can be chosen now.</summary>
    internal bool CanSave => IsAsking && saveTask is null && commands.CanSave;

    /// <summary>
    /// Every frame, before Dalamud checks whether the window is open: returns what the window's
    /// open state must be. A close that would lose unsaved work is turned back into "still open"
    /// and the question asked.
    /// </summary>
    internal bool PreOpenCheck(bool isOpen)
    {
        if (!isOpen && openLastFrame && !closeConfirmed && commands.HasPlate)
        {
            editorSession.CommitPendingEdits();
            if (CloseGuard.ShouldVeto(wasOpen: true, isOpen: false, closeConfirmed, hasPlate: true, editorSession.IsDirty))
            {
                isOpen = true;
                Ask();
            }
        }

        openLastFrame = isOpen;
        return isOpen;
    }

    /// <summary>
    /// The window's OnClose: true when the close must be undone (the window reopened) because it
    /// would lose unsaved work — the question is asked instead. Otherwise the close stands and the
    /// guard is ready for the next time the window opens.
    /// </summary>
    internal bool ShouldReopenOnClose()
    {
        editorSession.CommitPendingEdits();
        if (!closeConfirmed && commands.HasPlate && editorSession.IsDirty)
        {
            Ask();
            return true;
        }

        closeConfirmed = false;
        return false;
    }

    /// <summary>The next close is already settled (a handoff to the other editor): let it through.</summary>
    internal void ConfirmClose() => closeConfirmed = true;

    /// <summary>True once per question: the window opens its popup.</summary>
    internal bool ConsumePromptRequest()
    {
        var requested = promptRequested;
        promptRequested = false;
        return requested;
    }

    /// <summary>Save: starts the save; the window closes once <see cref="Advance"/> reports it succeeded.</summary>
    internal void Save()
    {
        if (CanSave)
        {
            saveTask = commands.SaveAsync();
        }
    }

    /// <summary>Discard: restores the last saved version. Returns true: the window closes now.</summary>
    internal bool Discard()
    {
        if (!IsAsking || saveTask is not null)
        {
            return false;
        }

        editorSession.DiscardChanges();
        IsAsking = false;
        closeConfirmed = true;
        return true;
    }

    /// <summary>Cancel: the window stays open with every edit intact.</summary>
    internal void Cancel()
    {
        if (saveTask is null)
        {
            IsAsking = false;
        }
    }

    /// <summary>
    /// Every frame: true once a Save chosen in the question has succeeded (the window closes now).
    /// A failed save ends the question and keeps the window open, edits and error intact.
    /// </summary>
    internal bool Advance()
    {
        if (saveTask is not { IsCompleted: true } task)
        {
            return false;
        }

        saveTask = null;
        IsAsking = false;
        editorSession.SyncWithCurrentProfile();

        if (task.IsCompletedSuccessfully && task.Result)
        {
            closeConfirmed = true;
            return true;
        }

        return false;
    }

    private void Ask()
    {
        IsAsking = true;

        // Closing again while the chosen save is still being written just keeps the window open:
        // it closes once that save succeeds.
        if (saveTask is null)
        {
            promptRequested = true;
        }
    }
}
