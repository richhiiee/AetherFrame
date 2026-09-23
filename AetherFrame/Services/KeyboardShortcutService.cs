using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

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

/// <summary>
/// Intercepts AetherFrame's editor keyboard shortcuts as early as possible — during
/// <see cref="IFramework.Update"/>, before FFXIV's own systems (camera, hotkeys) observe the
/// same <see cref="IKeyState"/> buffer — rather than during ImGui Draw, which runs too late to
/// stop the game from also reacting to the same key press.
///
/// Detection and suppression happen here, on the framework thread. Applying the resulting
/// editor action (Undo, nudge, etc.) is deliberately left to <c>ProfileEditorWindow</c>'s next
/// Draw: <c>EditorSession</c>/<c>ProfileService</c> mutation methods are only safe to call from
/// the render thread, so this queue (guarded by <see cref="gate"/>) is the thread-safe handoff
/// between the framework thread and the render thread.
/// </summary>
internal sealed class KeyboardShortcutService : IDisposable
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

    private DateTime lastUpdateUtc = DateTime.UtcNow;

    // Published by ProfileEditorWindow's previous Draw (or OnClose); read here on the next
    // Framework.Update tick. Volatile: written on the render thread, read on the framework
    // thread.
    private volatile bool editorFocused;
    private volatile bool textInputActive;
    private volatile bool previewActive;
    private volatile bool canvasInteractionActive;

    internal KeyboardShortcutService()
    {
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
    }

    /// <summary>
    /// Reports whether editor keyboard shortcuts should be eligible on the next
    /// <see cref="IFramework.Update"/> tick. Call once per Draw — and with both flags false as
    /// soon as the editor window closes or loses focus — so interception stops immediately.
    /// </summary>
    internal void SetEditorFocusState(bool editorFocused, bool textInputActive) =>
        SetEditorFocusState(editorFocused, textInputActive, previewActive: false, canvasInteractionActive: false);

    /// <summary>
    /// Full form of <see cref="SetEditorFocusState(bool, bool)"/>. <paramref name="previewActive"/>
    /// limits shortcuts to leaving Clean Preview (Escape) and saving; editing keys are left alone.
    /// <paramref name="canvasInteractionActive"/> (a drag/resize the editor owns) is the only time
    /// Alt — which the editor reads to bypass snapping — is kept from the game.
    /// </summary>
    internal void SetEditorFocusState(bool editorFocused, bool textInputActive, bool previewActive, bool canvasInteractionActive)
    {
        this.editorFocused = editorFocused;
        this.textInputActive = textInputActive;
        this.previewActive = previewActive;
        this.canvasInteractionActive = canvasInteractionActive;
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

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        var deltaSeconds = (float)(now - lastUpdateUtc).TotalSeconds;
        lastUpdateUtc = now;

        if (!editorFocused)
        {
            ResetHeldKeys();
            return;
        }

        var keyState = DalamudServices.KeyState;

        var ctrlDown = keyState[VirtualKey.CONTROL];
        var shiftDown = keyState[VirtualKey.SHIFT];
        var altDown = keyState[VirtualKey.MENU];

        var sDown = keyState[VirtualKey.S];
        var sPressed = sDown && !previousSDown;
        previousSDown = sDown;

        // Ctrl+S saves even mid-typing: it has no meaning inside a text field, and saving is
        // exactly what someone pressing it while editing text expects.
        if (ctrlDown && sPressed)
        {
            Enqueue(EditorShortcutActionKind.Save);
            SuppressGameKey(keyState, VirtualKey.S);
        }

        if (textInputActive)
        {
            // A text field owns every other key: release held-key state so a later return to the
            // canvas starts clean rather than treating a held key as a fresh press.
            ResetHeldKeys(keepSaveKey: true);
            return;
        }

        var escapeDown = keyState[VirtualKey.ESCAPE];
        var escapePressed = escapeDown && !previousEscapeDown;
        previousEscapeDown = escapeDown;

        if (previewActive)
        {
            // Clean Preview is view-only: Escape leaves it (and must not also reach the game,
            // where it would open the system menu or clear the target); nothing else is claimed.
            if (escapePressed)
            {
                Enqueue(EditorShortcutActionKind.ExitPreview);
                SuppressGameKey(keyState, VirtualKey.ESCAPE);
            }

            ResetHeldKeys(keepSaveKey: true, keepEscapeKey: true);
            return;
        }

        if (canvasInteractionActive && altDown)
        {
            // The editor reads Alt (via ImGui) to bypass snapping during this drag; keep the game
            // from also treating it as a held modifier. Never claimed outside such a drag.
            SuppressGameKey(keyState, VirtualKey.MENU);
        }

        var dDown = keyState[VirtualKey.D];
        var dPressed = dDown && !previousDDown;
        previousDDown = dDown;

        var fDown = keyState[VirtualKey.F];
        var fPressed = fDown && !previousFDown;
        previousFDown = fDown;

        if (ctrlDown && dPressed)
        {
            Enqueue(EditorShortcutActionKind.Duplicate);
            SuppressGameKey(keyState, VirtualKey.D);
        }

        if (fPressed && !ctrlDown && !altDown && !shiftDown)
        {
            Enqueue(EditorShortcutActionKind.FitCanvas);
            SuppressGameKey(keyState, VirtualKey.F);
        }

        var zDown = keyState[VirtualKey.Z];
        var zPressed = zDown && !previousZDown;
        previousZDown = zDown;

        var yDown = keyState[VirtualKey.Y];
        var yPressed = yDown && !previousYDown;
        previousYDown = yDown;

        var deleteDown = keyState[VirtualKey.DELETE];
        var deletePressed = deleteDown && !previousDeleteDown;
        previousDeleteDown = deleteDown;

        // Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y: recognizing one of these consumes Z or Y here and
        // nothing else interprets that same press.
        if (ctrlDown && zPressed)
        {
            Enqueue(shiftDown ? EditorShortcutActionKind.Redo : EditorShortcutActionKind.Undo);
            SuppressGameKey(keyState, VirtualKey.Z);
        }
        else if (ctrlDown && yPressed)
        {
            Enqueue(EditorShortcutActionKind.Redo);
            SuppressGameKey(keyState, VirtualKey.Y);
        }

        if (deletePressed)
        {
            Enqueue(EditorShortcutActionKind.Delete);
            SuppressGameKey(keyState, VirtualKey.DELETE);
        }

        var step = shiftDown ? 10f : 1f;

        if (leftRepeat.ConsumeTrigger(keyState[VirtualKey.LEFT], deltaSeconds))
        {
            Enqueue(new Vector2(-step, 0f));
            SuppressGameKey(keyState, VirtualKey.LEFT);
        }

        if (rightRepeat.ConsumeTrigger(keyState[VirtualKey.RIGHT], deltaSeconds))
        {
            Enqueue(new Vector2(step, 0f));
            SuppressGameKey(keyState, VirtualKey.RIGHT);
        }

        if (upRepeat.ConsumeTrigger(keyState[VirtualKey.UP], deltaSeconds))
        {
            Enqueue(new Vector2(0f, -step));
            SuppressGameKey(keyState, VirtualKey.UP);
        }

        if (downRepeat.ConsumeTrigger(keyState[VirtualKey.DOWN], deltaSeconds))
        {
            Enqueue(new Vector2(0f, step));
            SuppressGameKey(keyState, VirtualKey.DOWN);
        }
    }

    /// <summary>
    /// Releases held-key state so a later focus regain starts clean rather than treating an
    /// already-held key as a fresh press or resuming mid-repeat.
    /// </summary>
    private void ResetHeldKeys(bool keepSaveKey = false, bool keepEscapeKey = false)
    {
        leftRepeat.Reset();
        rightRepeat.Reset();
        upRepeat.Reset();
        downRepeat.Reset();
        previousZDown = false;
        previousYDown = false;
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

    private void Enqueue(EditorShortcutActionKind kind)
    {
        lock (gate)
        {
            pendingActions.Add(new EditorShortcutAction(kind));
        }
    }

    private void Enqueue(Vector2 nudgeDelta)
    {
        lock (gate)
        {
            pendingActions.Add(new EditorShortcutAction(EditorShortcutActionKind.Nudge, nudgeDelta));
        }
    }

    /// <summary>
    /// Clears a single key in the game's own key-state buffer so FFXIV doesn't also act on a
    /// key AetherFrame just consumed. Never touches any other key — no <c>ClearAll</c>, and
    /// CONTROL/SHIFT are only ever read here, never cleared.
    /// </summary>
    private static void SuppressGameKey(IKeyState keyState, VirtualKey key)
    {
        if (keyState.IsVirtualKeyValid(key))
        {
            keyState[key] = false;
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

        internal void Reset()
        {
            wasDown = false;
            heldSeconds = 0f;
        }
    }
}
