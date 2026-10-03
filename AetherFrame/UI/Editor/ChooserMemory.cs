using System;
using System.Collections.Generic;

namespace AetherFrame.UI.Editor;

/// <summary>
/// What one chooser (a dropdown or picker list) remembers between openings, so trying several
/// nearby options doesn't mean scrolling back to the same place each time (issue #114): its search
/// text, and where its list was scrolled and what was selected when it was last open. Pure state and
/// rules, free of ImGui; <c>ChooserScroll</c> in the windows applies them.
///
/// On opening (<see cref="Open"/>):
/// <list type="bullet">
/// <item>a remembered search that would hide the current selection is cleared, so the selection
/// can always be seen in the list;</item>
/// <item>with the same selection, search and list layout as when it was last open, the list returns
/// to exactly where it was;</item>
/// <item>otherwise (the selection was changed somewhere else, the search changed, the rows were
/// reordered or are another editor's version of the list, or it never opened before) the list
/// scrolls to the selection, if there is one.</item>
/// </list>
/// ImGui clamps a restored scroll to the list's current length, so a shorter list still opens at a
/// valid position.
/// </summary>
internal sealed class ChooserMemory
{
    private bool recorded;
    private float scrollY;
    private string? recordedSelection;
    private string recordedSearch = string.Empty;
    private int recordedLayout;

    /// <summary>The chooser's search text, kept between openings. Never null.</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>
    /// Called on the frame the chooser opens, with what is selected now (null for a chooser with no
    /// selection, such as an "Add..." menu), whether a selection is shown by a given search, and the
    /// list's <paramref name="layout"/>: any number that changes when its rows are ordered differently
    /// (a pixel position only means the same rows under the same layout).
    /// </summary>
    public ChooserOpening Open(string? selection, Func<string, string, bool>? selectionMatches = null, int layout = 0)
    {
        Search ??= string.Empty;
        var searchCleared = false;
        var query = Search.Trim();
        if (query.Length > 0 && selection is not null && selectionMatches is not null && !selectionMatches(selection, query))
        {
            Search = string.Empty;
            searchCleared = true;
        }

        if (recorded && recordedLayout == layout
            && string.Equals(recordedSelection, selection, StringComparison.Ordinal) && string.Equals(recordedSearch, Search, StringComparison.Ordinal))
        {
            return new ChooserOpening(scrollY, false, searchCleared);
        }

        return new ChooserOpening(null, selection is not null, searchCleared);
    }

    /// <summary>Called on every frame the chooser is open, after its list: where the list is
    /// scrolled, what is selected (the newly picked option on the frame one is picked), and the
    /// list's layout (as given to <see cref="Open"/>).</summary>
    public void Record(float scroll, string? selection, int layout = 0)
    {
        recorded = true;
        scrollY = float.IsFinite(scroll) && scroll > 0f ? scroll : 0f;
        recordedSelection = selection;
        recordedSearch = Search ?? string.Empty;
        recordedLayout = layout;
    }

    /// <summary>Forgets everything (the search included).</summary>
    public void Reset()
    {
        recorded = false;
        scrollY = 0f;
        recordedSelection = null;
        recordedSearch = string.Empty;
        recordedLayout = 0;
        Search = string.Empty;
    }
}

/// <summary>What a chooser does as it opens: restore a scroll position, or scroll to the selection.</summary>
/// <param name="RestoreScrollY">The scroll position to return to, or null.</param>
/// <param name="ScrollToSelection">True when the selected option should be scrolled into view instead.</param>
/// <param name="SearchCleared">True when a remembered search was cleared because it hid the selection.</param>
internal readonly record struct ChooserOpening(float? RestoreScrollY, bool ScrollToSelection, bool SearchCleared);

/// <summary>
/// Every chooser's <see cref="ChooserMemory"/>, by a key naming the chooser (and, where one control
/// serves several lists, the list: each Component kind's style list has its own). Kept for the
/// plugin's session, on the framework thread only.
/// </summary>
internal static class ChooserMemories
{
    private static readonly Dictionary<string, ChooserMemory> Memories = new(StringComparer.Ordinal);

    public static ChooserMemory For(string key)
    {
        if (!Memories.TryGetValue(key, out var memory))
        {
            memory = new ChooserMemory();
            Memories[key] = memory;
        }

        return memory;
    }
}
