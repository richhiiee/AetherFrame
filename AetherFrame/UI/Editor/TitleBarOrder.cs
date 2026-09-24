using System;
using System.Collections.Generic;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Title bar button order for AetherFrame's windows, left to right:
/// Dalamud's Window Options menu | Minimize | (future Maximize/Restore) | Close — Close always far right.
///
/// <para><b>Close</b> is the standard native title bar close every AetherFrame window uses
/// (<c>ShowCloseButton</c>). Dalamud reserves the far-right slot for it and lays every other title
/// bar button out to its left (read from Dalamud's <c>DrawTitleBarButtons</c>), so it needs no
/// priority and is always rightmost, collapsed or not.</para>
///
/// <para><b>The other buttons</b> (read from Dalamud's <c>WindowHost.DrawInternal</c>): the window's
/// <c>TitleBarButtons</c> plus its own menu button (<see cref="DalamudMenu"/>, <c>int.MinValue</c>)
/// are sorted with <c>(a, b) =&gt; b.Priority - a.Priority</c> — highest priority first — and drawn
/// from the right leftward, so a higher priority sits further right. That comparison is a plain
/// subtraction: any priority of 0 or more overflows against the menu's <c>int.MinValue</c>, the sort
/// goes inconsistent, and buttons land in the wrong order (a custom Close at <c>int.MaxValue</c> once
/// produced "Minimize | Close | Menu"). So every priority here is a small negative number.</para>
/// </summary>
internal static class TitleBarOrder
{
    /// <summary>Dalamud's own Window Options button (fixed by Dalamud).</summary>
    internal const int DalamudMenu = int.MinValue;

    internal const int Minimize = -3;

    /// <summary>Reserved for a future Maximize/Restore button, just left of Close.</summary>
    internal const int MaximizeRestore = -2;

    /// <summary>Dalamud's comparison, reproduced exactly (including its unchecked subtraction).</summary>
    internal static int DalamudCompare(int a, int b) => unchecked(b - a);

    /// <summary>
    /// The left-to-right order Dalamud draws buttons with these priorities in, followed by
    /// <paramref name="nativeClose"/> in the far-right slot when the window shows its native close.
    /// </summary>
    internal static List<T> LeftToRight<T>(IEnumerable<(T Button, int Priority)> buttons, T? nativeClose = default, bool showsNativeClose = false)
    {
        var sorted = new List<(T Button, int Priority)>(buttons);
        sorted.Sort((a, b) => DalamudCompare(a.Priority, b.Priority)); // first = rightmost
        var result = new List<T>(sorted.Count + 1);
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            result.Add(sorted[i].Button);
        }

        if (showsNativeClose)
        {
            result.Add(nativeClose!);
        }

        return result;
    }
}
