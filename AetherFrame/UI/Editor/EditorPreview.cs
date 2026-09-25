namespace AetherFrame.UI.Editor;

/// <summary>
/// Preview, as both editors' action bars offer it: one meaning everywhere — the finished Plate
/// exactly as a viewer sees it, shown by the one Clean Preview (<c>CleanPreviewPresenter</c>), never
/// by the editor's own content. The Basic and Advanced editors enter and leave it through here, over
/// the one shared <see cref="EditorSession.PreviewActive"/>; only one editor is ever open, so it is
/// always that editor's window that becomes the preview.
/// </summary>
internal static class EditorPreview
{
    /// <summary>The action bar's Preview tooltip, the same in both editors.</summary>
    internal const string Tooltip = "Preview: the finished Plate only, over the game (Esc to exit)";

    /// <summary>
    /// Shows Preview: any edit still in progress (typing, a slider or canvas drag) is committed
    /// first, so the preview shows exactly the Plate as it now is. Changes nothing on the Plate.
    /// </summary>
    internal static void Enter(EditorSession editorSession)
    {
        editorSession.CommitPendingEdits();
        editorSession.EndInteraction();
        editorSession.PreviewActive = true;
    }

    /// <summary>Leaves Preview: the editor comes back as it was.</summary>
    internal static void Exit(EditorSession editorSession) => editorSession.PreviewActive = false;
}
