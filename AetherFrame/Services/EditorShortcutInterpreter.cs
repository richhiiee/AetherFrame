using System.Collections.Generic;
using System.Numerics;

namespace AetherFrame.Services;

internal enum EditorShortcutActionKind
{
    Nudge,
    Delete,
    Undo,
    Redo,
    Save,
    Duplicate,
    FitCanvas,
    ExitPreview,
}

internal readonly record struct EditorShortcutAction(EditorShortcutActionKind Kind, Vector2 NudgeDelta = default);

/// <summary>The keys AetherFrame's editor shortcuts read.</summary>
internal enum ShortcutKey
{
    Control,
    Shift,
    Alt,
    Escape,
    Delete,
    Left,
    Right,
    Up,
    Down,
    S,
    Z,
    Y,
    D,
    F,
}

/// <summary>The game's keyboard state as the shortcut interpreter uses it: read a key, or keep a
/// key it consumed from also reaching the game.</summary>
internal interface IShortcutKeyboard
{
    bool IsDown(ShortcutKey key);

    void Suppress(ShortcutKey key);
}

/// <summary>
/// Turns key state into AetherFrame's editor shortcut actions, one framework tick at a time (see
/// <see cref="KeyboardShortcutService"/>, which feeds it the game's keyboard). Independent of
/// Dalamud so its rules are testable.
///
/// <para>Shortcuts are only ever recognized while an editor has published keyboard focus, and
/// never while Dalamud has hidden plugin UI (cutscenes, gpose, the player hiding the UI…): hiding
/// drops focus and anything queued and not yet applied, and nothing is recognized or taken from the
/// game until the UI is shown again AND an editor draws with focus again. The first such tick only
/// takes note of the keys already held, so a key held across the hide never counts as a fresh
/// press — pressing it again does.</para>
///
/// <para>Focus is published from the render thread and actions are applied there; <see cref="Update"/>
/// runs on the framework thread. The action queue (guarded by <see cref="gate"/>) is the thread-safe
/// handoff between them.</para>
/// </summary>
internal sealed class EditorShortcutInterpreter
{
    private const float InitialRepeatDelaySeconds = 0.35f;
    private const float RepeatIntervalSeconds = 0.05f;

    private readonly object gate = new();
    private readonly List<EditorShortcutAction> pendingActions = new();

    private readonly KeyRepeatState leftRepeat = new();
    private readonly KeyRepeatState rightRepeat = new();
    private readonly KeyRepeatState upRepeat = new();
    private readonly KeyRepeatState downRepeat = new();
    private bool previousZDown;
    private bool previousYDown;
    private bool previousDeleteDown;
    private bool previousSDown;
    private bool previousDDown;
    private bool previousFDown;
    private bool previousEscapeDown;

    // Published by the open editor's previous Draw (or OnClose); read on the next Update. Volatile:
    // written on the render thread, read on the framework thread.
    private volatile bool editorFocused;
    private volatile bool textInputActive;
    private volatile bool previewActive;
    private volatile bool canvasInteractionActive;
    private volatile bool documentShortcutsOnly;

    // Dalamud has hidden plugin UI (UiBuilder.HideUi until ShowUi).
    private volatile bool uiHidden;

    // Set on hide: the first focused tick afterwards only records which keys are already held.
    private volatile bool resyncHeldKeys;

    /// <summary>True while Dalamud has plugin UI hidden: no shortcut is recognized or queued.</summary>
    internal bool IsUiHidden => uiHidden;

    /// <inheritdoc cref="KeyboardShortcutService.SetEditorFocusState(bool, bool, bool, bool)"/>
    internal void SetEditorFocusState(bool editorFocused, bool textInputActive, bool previewActive, bool canvasInteractionActive)
    {
        this.editorFocused = editorFocused;
        this.textInputActive = textInputActive;
        this.previewActive = previewActive;
        this.canvasInteractionActive = canvasInteractionActive;
        documentShortcutsOnly = false;
    }

    /// <inheritdoc cref="KeyboardShortcutService.SetDocumentShortcutFocusState"/>
    internal void SetDocumentShortcutFocusState(bool editorFocused, bool textInputActive)
    {
        SetEditorFocusState(editorFocused, textInputActive, previewActive: false, canvasInteractionActive: false);
        documentShortcutsOnly = true;
    }

    /// <summary>Drains and returns any shortcut actions queued since the last call.</summary>
    internal List<EditorShortcutAction> DequeuePendingActions()
    {
        lock (gate)
        {
            if (pendingActions.Count == 0)
            {
                return [];
            }

            var actions = new List<EditorShortcutAction>(pendingActions);
            pendingActions.Clear();
            return actions;
        }
    }

    /// <summary>
    /// Dalamud hid plugin UI: stop recognizing shortcuts, forget the editor's focus (it's published
    /// again by the next Draw, which only happens once the UI is shown), and drop anything queued
    /// that hasn't been applied, so it can't be applied later.
    /// </summary>
    internal void OnUiHidden()
    {
        lock (gate)
        {
            uiHidden = true;
            pendingActions.Clear();
        }

        resyncHeldKeys = true;
        SetEditorFocusState(editorFocused: false, textInputActive: false, previewActive: false, canvasInteractionActive: false);
    }

    /// <summary>Dalamud showed plugin UI again. Shortcuts resume once an editor draws with focus.</summary>
    internal void OnUiShown()
    {
        lock (gate)
        {
            uiHidden = false;
        }
    }

    /// <summary>One framework tick: recognizes shortcuts from <paramref name="keyboard"/> and queues them.</summary>
    internal void Update(IShortcutKeyboard keyboard, float deltaSeconds)
    {
        if (uiHidden || !editorFocused)
        {
            ResetHeldKeys();
            return;
        }

        if (resyncHeldKeys)
        {
            resyncHeldKeys = false;
            RecordHeldKeys(keyboard);
            return;
        }

        var ctrlDown = keyboard.IsDown(ShortcutKey.Control);
        var shiftDown = keyboard.IsDown(ShortcutKey.Shift);
        var altDown = keyboard.IsDown(ShortcutKey.Alt);

        var sDown = keyboard.IsDown(ShortcutKey.S);
        var sPressed = sDown && !previousSDown;
        previousSDown = sDown;

        // Ctrl+S saves even mid-typing: it has no meaning inside a text field, and saving is
        // exactly what someone pressing it while editing text expects.
        if (ctrlDown && sPressed)
        {
            Enqueue(EditorShortcutActionKind.Save);
            keyboard.Suppress(ShortcutKey.S);
        }

        if (textInputActive)
        {
            // A text field owns every other key: release held-key state so a later return to the
            // canvas starts clean rather than treating a held key as a fresh press.
            ResetHeldKeys(keepSaveKey: true);
            return;
        }

        if (documentShortcutsOnly)
        {
            DetectHistoryShortcuts(keyboard, ctrlDown, shiftDown);
            ResetHeldKeys(keepSaveKey: true, keepHistoryKeys: true);
            return;
        }

        var escapeDown = keyboard.IsDown(ShortcutKey.Escape);
        var escapePressed = escapeDown && !previousEscapeDown;
        previousEscapeDown = escapeDown;

        if (previewActive)
        {
            // Clean Preview is view-only: Escape leaves it (and must not also reach the game,
            // where it would open the system menu or clear the target); nothing else is claimed.
            if (escapePressed)
            {
                Enqueue(EditorShortcutActionKind.ExitPreview);
                keyboard.Suppress(ShortcutKey.Escape);
            }

            ResetHeldKeys(keepSaveKey: true, keepEscapeKey: true);
            return;
        }

        if (canvasInteractionActive && altDown)
        {
            // The editor reads Alt (via ImGui) to bypass snapping during this drag; keep the game
            // from also treating it as a held modifier. Never claimed outside such a drag.
            keyboard.Suppress(ShortcutKey.Alt);
        }

        var dDown = keyboard.IsDown(ShortcutKey.D);
        var dPressed = dDown && !previousDDown;
        previousDDown = dDown;

        var fDown = keyboard.IsDown(ShortcutKey.F);
        var fPressed = fDown && !previousFDown;
        previousFDown = fDown;

        if (ctrlDown && dPressed)
        {
            Enqueue(EditorShortcutActionKind.Duplicate);
            keyboard.Suppress(ShortcutKey.D);
        }

        if (fPressed && !ctrlDown && !altDown && !shiftDown)
        {
            Enqueue(EditorShortcutActionKind.FitCanvas);
            keyboard.Suppress(ShortcutKey.F);
        }

        DetectHistoryShortcuts(keyboard, ctrlDown, shiftDown);

        var deleteDown = keyboard.IsDown(ShortcutKey.Delete);
        var deletePressed = deleteDown && !previousDeleteDown;
        previousDeleteDown = deleteDown;

        if (deletePressed)
        {
            Enqueue(EditorShortcutActionKind.Delete);
            keyboard.Suppress(ShortcutKey.Delete);
        }

        var step = shiftDown ? 10f : 1f;

        if (leftRepeat.ConsumeTrigger(keyboard.IsDown(ShortcutKey.Left), deltaSeconds))
        {
            Enqueue(new Vector2(-step, 0f));
            keyboard.Suppress(ShortcutKey.Left);
        }

        if (rightRepeat.ConsumeTrigger(keyboard.IsDown(ShortcutKey.Right), deltaSeconds))
        {
            Enqueue(new Vector2(step, 0f));
            keyboard.Suppress(ShortcutKey.Right);
        }

        if (upRepeat.ConsumeTrigger(keyboard.IsDown(ShortcutKey.Up), deltaSeconds))
        {
            Enqueue(new Vector2(0f, -step));
            keyboard.Suppress(ShortcutKey.Up);
        }

        if (downRepeat.ConsumeTrigger(keyboard.IsDown(ShortcutKey.Down), deltaSeconds))
        {
            Enqueue(new Vector2(0f, step));
            keyboard.Suppress(ShortcutKey.Down);
        }
    }

    /// <summary>
    /// Ctrl+Z (Undo), Ctrl+Shift+Z and Ctrl+Y (Redo): recognizing one of these consumes Z or Y
    /// here and nothing else interprets that same press.
    /// </summary>
    private void DetectHistoryShortcuts(IShortcutKeyboard keyboard, bool ctrlDown, bool shiftDown)
    {
        var zDown = keyboard.IsDown(ShortcutKey.Z);
        var zPressed = zDown && !previousZDown;
        previousZDown = zDown;

        var yDown = keyboard.IsDown(ShortcutKey.Y);
        var yPressed = yDown && !previousYDown;
        previousYDown = yDown;

        if (ctrlDown && zPressed)
        {
            Enqueue(shiftDown ? EditorShortcutActionKind.Redo : EditorShortcutActionKind.Undo);
            keyboard.Suppress(ShortcutKey.Z);
        }
        else if (ctrlDown && yPressed)
        {
            Enqueue(EditorShortcutActionKind.Redo);
            keyboard.Suppress(ShortcutKey.Y);
        }
    }

    /// <summary>
    /// After the UI was hidden: every key already down is treated as held from before, so only
    /// pressing it again acts (and a held arrow doesn't start repeating).
    /// </summary>
    private void RecordHeldKeys(IShortcutKeyboard keyboard)
    {
        previousSDown = keyboard.IsDown(ShortcutKey.S);
        previousZDown = keyboard.IsDown(ShortcutKey.Z);
        previousYDown = keyboard.IsDown(ShortcutKey.Y);
        previousDeleteDown = keyboard.IsDown(ShortcutKey.Delete);
        previousDDown = keyboard.IsDown(ShortcutKey.D);
        previousFDown = keyboard.IsDown(ShortcutKey.F);
        previousEscapeDown = keyboard.IsDown(ShortcutKey.Escape);
        leftRepeat.HoldUntilReleased(keyboard.IsDown(ShortcutKey.Left));
        rightRepeat.HoldUntilReleased(keyboard.IsDown(ShortcutKey.Right));
        upRepeat.HoldUntilReleased(keyboard.IsDown(ShortcutKey.Up));
        downRepeat.HoldUntilReleased(keyboard.IsDown(ShortcutKey.Down));
    }

    /// <summary>
    /// Releases held-key state so a later focus regain starts clean rather than treating an
    /// already-held key as a fresh press or resuming mid-repeat.
    /// </summary>
    private void ResetHeldKeys(bool keepSaveKey = false, bool keepEscapeKey = false, bool keepHistoryKeys = false)
    {
        leftRepeat.Reset();
        rightRepeat.Reset();
        upRepeat.Reset();
        downRepeat.Reset();
        if (!keepHistoryKeys)
        {
            previousZDown = false;
            previousYDown = false;
        }

        previousDeleteDown = false;
        previousDDown = false;
        previousFDown = false;

        if (!keepSaveKey)
        {
            previousSDown = false;
        }

        if (!keepEscapeKey)
        {
            previousEscapeDown = false;
        }
    }

    private void Enqueue(EditorShortcutActionKind kind) => Enqueue(new EditorShortcutAction(kind));

    private void Enqueue(Vector2 nudgeDelta) => Enqueue(new EditorShortcutAction(EditorShortcutActionKind.Nudge, nudgeDelta));

    private void Enqueue(EditorShortcutAction action)
    {
        lock (gate)
        {
            // Checked under the same lock hiding takes: nothing recognized in a tick that raced
            // with the hide is kept.
            if (!uiHidden)
            {
                pendingActions.Add(action);
            }
        }
    }

    /// <summary>
    /// Key-repeat edge detector: fires once on the initial press, then again after
    /// <see cref="InitialRepeatDelaySeconds"/>, then every <see cref="RepeatIntervalSeconds"/>
    /// while still held.
    /// </summary>
    private sealed class KeyRepeatState
    {
        private bool wasDown;
        private float heldSeconds;
        private float nextFireSeconds;

        internal bool ConsumeTrigger(bool isDown, float deltaSeconds)
        {
            if (!isDown)
            {
                Reset();
                return false;
            }

            if (!wasDown)
            {
                wasDown = true;
                heldSeconds = 0f;
                nextFireSeconds = InitialRepeatDelaySeconds;
                return true;
            }

            heldSeconds += deltaSeconds;
            if (heldSeconds < nextFireSeconds)
            {
                return false;
            }

            nextFireSeconds += RepeatIntervalSeconds;
            return true;
        }

        /// <summary>A key already held: it neither fires nor repeats until released.</summary>
        internal void HoldUntilReleased(bool isDown)
        {
            wasDown = isDown;
            heldSeconds = 0f;
            nextFireSeconds = float.PositiveInfinity;
        }

        internal void Reset()
        {
            wasDown = false;
            heldSeconds = 0f;
        }
    }
}
