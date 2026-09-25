using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace AetherFrame.Services;

/// <summary>
/// Intercepts AetherFrame's editor keyboard shortcuts as early as possible — during
/// <see cref="IFramework.Update"/>, before FFXIV's own systems (camera, hotkeys) observe the
/// same <see cref="IKeyState"/> buffer — rather than during ImGui Draw, which runs too late to
/// stop the game from also reacting to the same key press. Which keys mean what is decided by
/// <see cref="EditorShortcutInterpreter"/>; this is its connection to Dalamud.
///
/// Detection and suppression happen here, on the framework thread. Applying the resulting
/// editor action (Undo, nudge, etc.) is deliberately left to the open editor window's next Draw:
/// <c>EditorSession</c>/<c>ProfileService</c> mutation methods are only safe to call from the
/// render thread, so the interpreter's queue is the thread-safe handoff between the framework
/// thread and the render thread.
///
/// <para>Both editors publish their focus here (only one is ever open). The Advanced editor gets
/// every shortcut; the Basic editor only the document ones its shared action bar offers —
/// Ctrl+S, Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z (see <see cref="SetDocumentShortcutFocusState"/>) — so
/// no other key is ever taken from the game while Basic is focused.</para>
///
/// <para>While Dalamud hides plugin UI (<see cref="Dalamud.Interface.IUiBuilder.HideUi"/>: cutscenes,
/// gpose, the player hiding the UI…) the editors aren't drawn and can't publish focus, so no
/// shortcut is recognized, queued or taken from the game until the UI is shown again and an editor
/// has focus again.</para>
/// </summary>
internal sealed class KeyboardShortcutService : IDisposable
{
    private readonly EditorShortcutInterpreter interpreter = new();
    private readonly GameKeyboard keyboard = new();

    private DateTime lastUpdateUtc = DateTime.UtcNow;

    internal KeyboardShortcutService()
    {
        DalamudServices.Framework.Update += OnFrameworkUpdate;
        DalamudServices.PluginInterface.UiBuilder.HideUi += interpreter.OnUiHidden;
        DalamudServices.PluginInterface.UiBuilder.ShowUi += interpreter.OnUiShown;
    }

    public void Dispose()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        DalamudServices.PluginInterface.UiBuilder.HideUi -= interpreter.OnUiHidden;
        DalamudServices.PluginInterface.UiBuilder.ShowUi -= interpreter.OnUiShown;
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
    internal void SetEditorFocusState(bool editorFocused, bool textInputActive, bool previewActive, bool canvasInteractionActive) =>
        interpreter.SetEditorFocusState(editorFocused, textInputActive, previewActive, canvasInteractionActive);

    /// <summary>
    /// The Basic editor's form of <see cref="SetEditorFocusState(bool, bool)"/>: only the shared
    /// action bar's document shortcuts — Save (even mid-typing, as in Advanced), Undo and Redo (not
    /// while typing, where they stay the text field's own) — are recognized and kept from the game.
    /// </summary>
    internal void SetDocumentShortcutFocusState(bool editorFocused, bool textInputActive) =>
        interpreter.SetDocumentShortcutFocusState(editorFocused, textInputActive);

    /// <summary>Drains and returns any shortcut actions queued since the last call.</summary>
    internal List<EditorShortcutAction> DequeuePendingActions() => interpreter.DequeuePendingActions();

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        var deltaSeconds = (float)(now - lastUpdateUtc).TotalSeconds;
        lastUpdateUtc = now;

        interpreter.Update(keyboard, deltaSeconds);
    }

    /// <summary>The game's own key-state buffer.</summary>
    private sealed class GameKeyboard : IShortcutKeyboard
    {
        public bool IsDown(ShortcutKey key) => DalamudServices.KeyState[ToVirtualKey(key)];

        /// <summary>
        /// Clears a single key in the game's own key-state buffer so FFXIV doesn't also act on a
        /// key AetherFrame just consumed. Never touches any other key — no <c>ClearAll</c>, and
        /// CONTROL/SHIFT are only ever read, never cleared.
        /// </summary>
        public void Suppress(ShortcutKey key)
        {
            var virtualKey = ToVirtualKey(key);
            var keyState = DalamudServices.KeyState;
            if (keyState.IsVirtualKeyValid(virtualKey))
            {
                keyState[virtualKey] = false;
            }
        }

        private static VirtualKey ToVirtualKey(ShortcutKey key) => key switch
        {
            ShortcutKey.Control => VirtualKey.CONTROL,
            ShortcutKey.Shift => VirtualKey.SHIFT,
            ShortcutKey.Alt => VirtualKey.MENU,
            ShortcutKey.Escape => VirtualKey.ESCAPE,
            ShortcutKey.Delete => VirtualKey.DELETE,
            ShortcutKey.Left => VirtualKey.LEFT,
            ShortcutKey.Right => VirtualKey.RIGHT,
            ShortcutKey.Up => VirtualKey.UP,
            ShortcutKey.Down => VirtualKey.DOWN,
            ShortcutKey.S => VirtualKey.S,
            ShortcutKey.Z => VirtualKey.Z,
            ShortcutKey.Y => VirtualKey.Y,
            ShortcutKey.D => VirtualKey.D,
            ShortcutKey.F => VirtualKey.F,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
    }
}
