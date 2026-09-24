namespace AetherFrame.UI.Editor;

/// <summary>
/// When the Advanced editor must refuse a close and ask about unsaved work first: the window was
/// open and has just been closed (by its title bar Close, Escape, or a toggle from elsewhere), the
/// close wasn't already confirmed (Save/Discard answered, or a handoff to the Basic editor), a Plate
/// is open, and it has unsaved changes. Evaluated before Dalamud acts on the close, so a refused
/// close never starts closing at all.
/// </summary>
internal static class CloseGuard
{
    internal static bool ShouldVeto(bool wasOpen, bool isOpen, bool closeConfirmed, bool hasPlate, bool isDirty) =>
        wasOpen && !isOpen && !closeConfirmed && hasPlate && isDirty;
}
