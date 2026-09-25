using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Services;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Editor keyboard shortcuts: recognized normally while an editor has focus, and never while
/// Dalamud has hidden plugin UI — nothing is queued or taken from the game then, and nothing held
/// or queued from before is replayed when the UI comes back.
/// </summary>
public class EditorShortcutTests
{
    private const float Tick = 1f / 60f;

    [Fact]
    public void VisibleAdvancedEditor_RecognizesEveryShortcut()
    {
        var (shortcuts, keys) = FocusedAdvanced();

        Assert.Equal(EditorShortcutActionKind.Save, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Undo, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Z).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Redo, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Y).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Redo, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Shift, ShortcutKey.Z).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Delete, Press(shortcuts, keys, ShortcutKey.Delete).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Duplicate, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.D).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.FitCanvas, Press(shortcuts, keys, ShortcutKey.F).Single().Kind);

        var nudge = Press(shortcuts, keys, ShortcutKey.Left).Single();
        Assert.Equal(EditorShortcutActionKind.Nudge, nudge.Kind);
        Assert.Equal(new Vector2(-1f, 0f), nudge.NudgeDelta);
        Assert.Equal(new Vector2(0f, 10f), Press(shortcuts, keys, ShortcutKey.Shift, ShortcutKey.Down).Single().NudgeDelta);

        // Consumed keys are kept from the game.
        Assert.Contains(ShortcutKey.Delete, keys.Suppressed);
        Assert.Contains(ShortcutKey.S, keys.Suppressed);
    }

    [Fact]
    public void VisibleBasicEditor_RecognizesItsDocumentShortcutsOnly()
    {
        var shortcuts = new EditorShortcutInterpreter();
        var keys = new FakeKeyboard();
        shortcuts.SetDocumentShortcutFocusState(editorFocused: true, textInputActive: false);

        Assert.Equal(EditorShortcutActionKind.Save, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Undo, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Z).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Redo, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Y).Single().Kind);
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Delete));
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Left));
    }

    [Fact]
    public void HideUi_SuspendsShortcuts()
    {
        var (shortcuts, keys) = FocusedAdvanced();

        shortcuts.OnUiHidden();

        Assert.True(shortcuts.IsUiHidden);
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Z));
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Y));
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.D));
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.F));
        Assert.Empty(keys.Suppressed);
    }

    [Fact]
    public void Hidden_Delete_DoesNotEdit_OrReachPastTheGame()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();

        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Delete));
        Assert.DoesNotContain(ShortcutKey.Delete, keys.Suppressed);
    }

    [Fact]
    public void Hidden_Arrows_DoNotEdit_EvenHeldPastTheRepeatDelay()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();

        keys.Down(ShortcutKey.Right, ShortcutKey.Up);
        for (var i = 0; i < 120; i++)
        {
            shortcuts.Update(keys, Tick);
        }

        keys.UpAll();
        shortcuts.Update(keys, Tick);

        Assert.Empty(shortcuts.DequeuePendingActions());
        Assert.Empty(keys.Suppressed);
    }

    [Fact]
    public void Hidden_CtrlS_DoesNotSave()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();

        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S));
        Assert.DoesNotContain(ShortcutKey.S, keys.Suppressed);
    }

    [Fact]
    public void Hidden_EvenIfSomethingStillPublishesFocus_NothingIsRecognized()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();

        // A stale focus report (e.g. a draw racing with the hide) can't re-enable shortcuts.
        shortcuts.SetEditorFocusState(editorFocused: true, textInputActive: false, previewActive: false, canvasInteractionActive: false);

        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Delete));
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S));
    }

    [Fact]
    public void HideUi_DropsActionsQueuedButNotYetApplied()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        keys.Down(ShortcutKey.Delete);
        shortcuts.Update(keys, Tick); // queued, but the editor hasn't drawn to apply it yet

        shortcuts.OnUiHidden();
        shortcuts.OnUiShown();

        Assert.Empty(shortcuts.DequeuePendingActions());
    }

    [Fact]
    public void ShowUi_WithoutEditorFocusAgain_StillRecognizesNothing()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();
        shortcuts.OnUiShown();

        Assert.False(shortcuts.IsUiHidden);
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Delete));
        Assert.Empty(Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S));
    }

    [Fact]
    public void ShowUi_DoesNotReplayKeysHeldAcrossTheHide()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();

        // Held down while hidden, and still held when the editor has focus again.
        keys.Down(ShortcutKey.Control, ShortcutKey.S, ShortcutKey.Delete, ShortcutKey.Left, ShortcutKey.Z);
        for (var i = 0; i < 30; i++)
        {
            shortcuts.Update(keys, Tick);
        }

        shortcuts.OnUiShown();
        FocusAdvanced(shortcuts);
        for (var i = 0; i < 60; i++)
        {
            shortcuts.Update(keys, Tick);
        }

        Assert.Empty(shortcuts.DequeuePendingActions());
        Assert.Empty(keys.Suppressed);

        // Released and pressed again, each key works as normal.
        keys.UpAll();
        shortcuts.Update(keys, Tick);
        Assert.Equal(EditorShortcutActionKind.Delete, Press(shortcuts, keys, ShortcutKey.Delete).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Save, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Nudge, Press(shortcuts, keys, ShortcutKey.Left).Single().Kind);
    }

    [Fact]
    public void AfterShowUi_WithFocus_ShortcutsResumeNormally()
    {
        var (shortcuts, keys) = FocusedAdvanced();
        shortcuts.OnUiHidden();
        Press(shortcuts, keys, ShortcutKey.Delete);

        shortcuts.OnUiShown();
        FocusAdvanced(shortcuts);
        shortcuts.Update(keys, Tick); // the first focused tick only notes what's held

        Assert.Equal(EditorShortcutActionKind.Delete, Press(shortcuts, keys, ShortcutKey.Delete).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Undo, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.Z).Single().Kind);
        Assert.Equal(EditorShortcutActionKind.Nudge, Press(shortcuts, keys, ShortcutKey.Up).Single().Kind);
    }

    [Fact]
    public void RepeatedHideAndShow_AreHarmless()
    {
        var (shortcuts, keys) = FocusedAdvanced();

        // Dalamud may fire either consecutively.
        shortcuts.OnUiHidden();
        shortcuts.OnUiHidden();
        shortcuts.OnUiShown();
        shortcuts.OnUiShown();
        FocusAdvanced(shortcuts);
        shortcuts.Update(keys, Tick);

        Assert.Equal(EditorShortcutActionKind.Save, Press(shortcuts, keys, ShortcutKey.Control, ShortcutKey.S).Single().Kind);
    }

    [Fact]
    public void HeldArrow_StillRepeatsNormally_WhileVisible()
    {
        var (shortcuts, keys) = FocusedAdvanced();

        keys.Down(ShortcutKey.Right);
        for (var i = 0; i < 60; i++)
        {
            shortcuts.Update(keys, Tick);
        }

        // One press, then repeats after the initial delay.
        Assert.True(shortcuts.DequeuePendingActions().Count(a => a.Kind == EditorShortcutActionKind.Nudge) > 1);
    }

    private static (EditorShortcutInterpreter Shortcuts, FakeKeyboard Keys) FocusedAdvanced()
    {
        var shortcuts = new EditorShortcutInterpreter();
        FocusAdvanced(shortcuts);
        return (shortcuts, new FakeKeyboard());
    }

    private static void FocusAdvanced(EditorShortcutInterpreter shortcuts) =>
        shortcuts.SetEditorFocusState(editorFocused: true, textInputActive: false, previewActive: false, canvasInteractionActive: false);

    /// <summary>Presses the keys together for one tick, releases them for another, and returns what was queued.</summary>
    private static List<EditorShortcutAction> Press(EditorShortcutInterpreter shortcuts, FakeKeyboard keys, params ShortcutKey[] chord)
    {
        keys.Down(chord);
        shortcuts.Update(keys, Tick);
        keys.UpAll();
        shortcuts.Update(keys, Tick);
        return shortcuts.DequeuePendingActions();
    }

    private sealed class FakeKeyboard : IShortcutKeyboard
    {
        private readonly HashSet<ShortcutKey> down = new();

        internal List<ShortcutKey> Suppressed { get; } = new();

        internal void Down(params ShortcutKey[] keys) => down.UnionWith(keys);

        internal void UpAll() => down.Clear();

        public bool IsDown(ShortcutKey key) => down.Contains(key);

        public void Suppress(ShortcutKey key) => Suppressed.Add(key);
    }
}
