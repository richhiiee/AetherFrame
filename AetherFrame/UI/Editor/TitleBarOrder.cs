using System;
using System.Collections.Generic;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Title bar button order for AetherFrame's windows, left to right:
/// Dalamud's Window Options (Settings) | Minimize | Close — all of them Dalamud's own buttons.
///
/// <para><b>Minimize and Close</b> are ImGui's native collapse and close buttons (a window keeps
/// them by not setting <c>NoCollapse</c> and by showing its close button, <c>ShowCloseButton</c>).
/// Dalamud's standard style puts the collapse button on the right (<c>WindowMenuButtonPosition =
/// Right</c>), so ImGui draws Close far right with Minimize just left of it, and Dalamud reserves
/// both native slots before laying any other title bar button out to their left (read from
/// Dalamud's <c>DrawTitleBarButtons</c>). They need no priority, and collapsing is ImGui's own
/// state — restored by the same button or a title bar double-click, like any Dalamud window.</para>
///
/// <para><b>Other buttons</b> (read from Dalamud's <c>WindowHost.DrawInternal</c>): the window's
/// <c>TitleBarButtons</c> plus its own menu button (<see cref="DalamudMenu"/>, <c>int.MinValue</c>)
/// are sorted with <c>(a, b) =&gt; b.Priority - a.Priority</c> — highest priority first — and drawn
/// from the right leftward, so a higher priority sits further right. That comparison is a plain
/// subtraction: any priority of 0 or more overflows against the menu's <c>int.MinValue</c>, the sort
/// goes inconsistent, and buttons land in the wrong order. So any custom button's priority must be a
/// small negative number; it then sits between Window Options and the native Minimize.</para>
/// </summary>
internal static class TitleBarOrder
{
    /// <summary>Dalamud's own Window Options button (fixed by Dalamud).</summary>
    internal const int DalamudMenu = int.MinValue;

    /// <summary>Dalamud's comparison, reproduced exactly (including its unchecked subtraction).</summary>
    internal static int DalamudCompare(int a, int b) => unchecked(b - a);

    /// <summary>
    /// The left-to-right order Dalamud draws custom buttons with these priorities in, followed by the
    /// window's native buttons in their fixed far-right slots: <paramref name="nativeMinimize"/> (when
    /// the window can collapse), then <paramref name="nativeClose"/> (when it shows its close button).
    /// </summary>
    internal static List<T> LeftToRight<T>(
        IEnumerable<(T Button, int Priority)> buttons,
        T? nativeClose = default,
        bool showsNativeClose = false,
        T? nativeMinimize = default,
        bool showsNativeMinimize = false)
    {
        var sorted = new List<(T Button, int Priority)>(buttons);
        sorted.Sort((a, b) => DalamudCompare(a.Priority, b.Priority)); // first = rightmost
        var result = new List<T>(sorted.Count + 2);
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            result.Add(sorted[i].Button);
        }

        if (showsNativeMinimize)
        {
            result.Add(nativeMinimize!);
        }

        if (showsNativeClose)
        {
            result.Add(nativeClose!);
        }

        return result;
    }
}
