using System;

namespace AetherFrame.UI.Editor;

internal enum EditorSurfaceKind
{
    Basic,
    Advanced,
}

/// <summary>An editor window that edits the open Plate (the Basic or Advanced editor).</summary>
internal interface IEditorSurface
{
    bool IsOpen { get; }

    /// <summary>Opens the window (and brings it forward).</summary>
    void Show();

    /// <summary>
    /// Closes the window because the other surface is taking over editing. Unlike a user close,
    /// this never asks about unsaved changes: nothing is lost — the same live document, dirty
    /// state, and undo history simply continue in the other surface.
    /// </summary>
    void CloseForHandoff();
}

/// <summary>
/// Enforces one active editing surface over the single editing session. The Basic and Advanced
/// editors share one <see cref="EditorSession"/> (one live document, one dirty-state baseline,
/// one undo history), so switching between them never copies or reloads anything; this class
/// only makes sure at most one of the two is open at a time, and that ownership moves cleanly:
/// before the outgoing surface closes, any edit still in progress in it (a slider drag, a typing
/// burst, a canvas drag) is committed to the shared history, so it's neither lost nor split.
/// </summary>
internal sealed class EditorSurfaceCoordinator
{
    private readonly Action prepareHandoff;
    private IEditorSurface? basic;
    private IEditorSurface? advanced;

    /// <param name="prepareHandoff">Commits in-progress edits and ends canvas interactions.</param>
    internal EditorSurfaceCoordinator(Action prepareHandoff)
    {
        this.prepareHandoff = prepareHandoff;
    }

    /// <summary>Which surface currently owns editing, if any.</summary>
    internal EditorSurfaceKind? ActiveSurface =>
        basic?.IsOpen == true ? EditorSurfaceKind.Basic
        : advanced?.IsOpen == true ? EditorSurfaceKind.Advanced
        : null;

    /// <summary>Registers the two windows (they're created after the coordinator they call back into).</summary>
    internal void Attach(IEditorSurface basicSurface, IEditorSurface advancedSurface)
    {
        basic = basicSurface;
        advanced = advancedSurface;
    }

    /// <summary>Shows a surface, handing editing over from the other one if it's open.</summary>
    internal void Show(EditorSurfaceKind kind)
    {
        HandOffFrom(Other(kind));
        Get(kind).Show();
    }

    /// <summary>
    /// Called by a surface whenever it opens, by any path: if the other surface is still open, it
    /// hands over. Keeps the one-surface rule true even for opens that don't go through <see cref="Show"/>.
    /// </summary>
    internal void NotifyOpened(EditorSurfaceKind kind) => HandOffFrom(Other(kind));

    private void HandOffFrom(EditorSurfaceKind outgoing)
    {
        var surface = Get(outgoing);
        if (!surface.IsOpen)
        {
            return;
        }

        prepareHandoff();
        surface.CloseForHandoff();
    }

    private IEditorSurface Get(EditorSurfaceKind kind) =>
        (kind == EditorSurfaceKind.Basic ? basic : advanced) ?? throw new InvalidOperationException("Editor surfaces are not attached.");

    private static EditorSurfaceKind Other(EditorSurfaceKind kind) =>
        kind == EditorSurfaceKind.Basic ? EditorSurfaceKind.Advanced : EditorSurfaceKind.Basic;
}
