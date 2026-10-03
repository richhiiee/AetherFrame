using System;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;

namespace AetherFrame.Windows;

/// <summary>
/// Applies a <see cref="ChooserMemory"/> to the ImGui list it belongs to. Every combo shares ImGui's
/// one combo popup window, so its own scroll can't be relied on between different choosers: this is
/// what keeps each chooser's place.
///
/// A list that is its own window (a combo without a search box) calls <see cref="Begin"/> first thing
/// inside it and <see cref="End"/> last. A list under a search box scrolls in a child window of its
/// own, and calls <see cref="Open"/> in the popup before the search box (which may clear the search),
/// then <see cref="Restore"/> and <see cref="End"/> inside the child, passing the popup's appearing
/// flag: the search box takes keyboard focus as the popup appears, and ImGui then scrolls the window
/// holding it to the box on the next frame, which would undo a scroll restored in that same window.
/// Either way, the selected row is scrolled into view with <see cref="ScrollHereIfOpening"/> when
/// <see cref="ChooserOpening.ScrollToSelection"/> says so.
/// </summary>
internal static class ChooserScroll
{
    /// <summary>For a list that is the current window: on the frame it appears, restores its scroll or
    /// asks for the selection to be shown.</summary>
    internal static ChooserOpening Begin(ChooserMemory memory, string? selection, Func<string, string, bool>? selectionMatches = null, int layout = 0)
    {
        var appearing = ImGui.IsWindowAppearing();
        var opening = Open(memory, appearing, selection, selectionMatches, layout);
        Restore(opening, appearing);
        return opening;
    }

    /// <summary>On the frame the chooser appears, decides how its list opens (see <see cref="ChooserMemory.Open"/>);
    /// nothing on any other frame.</summary>
    internal static ChooserOpening Open(ChooserMemory memory, bool appearing, string? selection, Func<string, string, bool>? selectionMatches = null, int layout = 0) =>
        appearing ? memory.Open(selection, selectionMatches, layout) : default;

    /// <summary>In the window that scrolls, on the frame the chooser appears: returns it to its place,
    /// or to the top (the selected row then scrolls itself into view).</summary>
    internal static void Restore(in ChooserOpening opening, bool appearing)
    {
        if (appearing)
        {
            ImGui.SetScrollY(opening.RestoreScrollY ?? 0f);
        }
    }

    /// <summary>Scrolls the row just drawn into the middle of the list, when it is the selected row of a list that is opening.</summary>
    internal static void ScrollHereIfOpening(in ChooserOpening opening, bool selected)
    {
        if (opening.ScrollToSelection && selected)
        {
            ImGui.SetScrollHereY(0.5f);
        }
    }

    /// <summary>For a list that is the current window: remembers where it is and what is selected (see
    /// <see cref="End(ChooserMemory, bool, string?, int)"/>).</summary>
    internal static void End(ChooserMemory memory, string? selection, int layout = 0) =>
        End(memory, ImGui.IsWindowAppearing(), selection, layout);

    /// <summary>Remembers where the list is and what is selected; call after the list, inside the window
    /// that scrolls, every frame it is open. Not on the frame it appears: the scroll set then only
    /// applies from the next frame, so the window's scroll is still the last chooser's (combos share
    /// one window).</summary>
    internal static void End(ChooserMemory memory, bool appearing, string? selection, int layout = 0)
    {
        if (!appearing)
        {
            memory.Record(ImGui.GetScrollY(), selection, layout);
        }
    }
}
