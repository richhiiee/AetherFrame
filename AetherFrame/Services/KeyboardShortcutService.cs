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

    private DateTime lastUpdateUtc = DateTime.UtcNow;

    // Published by ProfileEditorWindow's previous Draw (or OnClose); read here on the next
    // Framework.Update tick. Volatile: written on the render thread, read on the framework
    // thread.
    private volatile bool editorFocused;
    private volatile bool textInputActive;

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
    internal void SetEditorFocusState(bool editorFocused, bool textInputActive)
    {
        this.editorFocused = editorFocused;
        this.textInputActive = textInputActive;
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

        if (!editorFocused || textInputActive)
        {
            // Not our turn: release held-key state so a later focus regain starts clean rather
            // than treating an already-held key as a fresh press or resuming mid-repeat.
            leftRepeat.Reset();
            rightRepeat.Reset();
            upRepeat.Reset();
            downRepeat.Reset();
            previousZDown = false;
            previousYDown = false;
            previousDeleteDown = false;
            return;
        }

        var keyState = DalamudServices.KeyState;

        var ctrlDown = keyState[VirtualKey.CONTROL];
        var shiftDown = keyState[VirtualKey.SHIFT];

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
