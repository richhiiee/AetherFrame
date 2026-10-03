using System;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A chooser's memory between openings (issue #114): reopening returns the list to where it was,
/// unless the selection changed elsewhere, when it shows the selection instead; the search is kept,
/// unless it would hide the selection.
/// </summary>
public class ChooserMemoryTests
{
    private static readonly Func<string, string, bool> NameContains = (selection, query) => selection.Contains(query, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TheFirstOpening_ScrollsToTheSelection()
    {
        var memory = new ChooserMemory();

        Assert.Equal(new ChooserOpening(null, true, false), memory.Open("Garamond"));
    }

    [Fact]
    public void TheFirstOpening_OfAChooserWithNoSelection_StartsAtTheTop()
    {
        Assert.Equal(new ChooserOpening(null, false, false), new ChooserMemory().Open(null));
    }

    /// <summary>Kim's case: scroll a long list, pick a font, reopen to try the next one.</summary>
    [Fact]
    public void ReopeningAfterPicking_ReturnsToWhereTheListWas()
    {
        var memory = new ChooserMemory();
        memory.Open("Arial");
        memory.Record(120f, "Arial");
        memory.Record(1480f, "Arial");
        memory.Record(1480f, "Garamond"); // the frame the player picks Garamond; the menu closes

        Assert.Equal(new ChooserOpening(1480f, false, false), memory.Open("Garamond"));
    }

    [Fact]
    public void ReopeningWithoutPicking_ReturnsToWhereTheListWas()
    {
        var memory = new ChooserMemory();
        memory.Open("Arial");
        memory.Record(900f, "Arial");

        Assert.Equal(900f, memory.Open("Arial").RestoreScrollY);
    }

    [Fact]
    public void ASelectionChangedElsewhere_IsShownInstead()
    {
        var memory = new ChooserMemory();
        memory.Record(900f, "Arial");

        // Another text element, or undo, or the other editor chose differently.
        Assert.Equal(new ChooserOpening(null, true, false), memory.Open("Papyrus"));
    }

    [Fact]
    public void TheSearch_IsKeptBetweenOpenings_WithItsPlace()
    {
        var memory = new ChooserMemory { Search = "goth" };
        memory.Record(64f, "Gothic Blackletter");

        var opening = memory.Open("Gothic Blackletter", NameContains);

        Assert.Equal("goth", memory.Search);
        Assert.Equal(new ChooserOpening(64f, false, false), opening);
    }

    [Fact]
    public void ASearchThatWouldHideTheSelection_IsCleared_AndTheSelectionShown()
    {
        var memory = new ChooserMemory { Search = "goth" };
        memory.Record(64f, "Gothic Blackletter");

        var opening = memory.Open("Papyrus", NameContains);

        Assert.Equal(string.Empty, memory.Search);
        Assert.Equal(new ChooserOpening(null, true, true), opening);
    }

    [Fact]
    public void AChangedSearch_ShowsTheSelection_RatherThanAnOldPlaceInAnotherList()
    {
        var memory = new ChooserMemory();
        memory.Record(1480f, "Garamond");
        memory.Search = "gar"; // typed, then the menu closed before another frame was recorded

        Assert.Equal(new ChooserOpening(null, true, false), memory.Open("Garamond", NameContains));
    }

    [Fact]
    public void AChooserWithNoSelection_AlwaysReturnsToItsPlace()
    {
        var memory = new ChooserMemory();
        memory.Open(null);
        memory.Record(300f, null);

        Assert.Equal(new ChooserOpening(300f, false, false), memory.Open(null));
    }

    [Fact]
    public void AnotherLayout_ScrollsToTheSelectionInstead()
    {
        // Rows reordered (or the other editor's version of the list): the old pixel position would
        // point at another row, so the selection is found again.
        var memory = new ChooserMemory();
        memory.Open("Garamond", layout: 1);
        memory.Record(640f, "Garamond", layout: 1);

        Assert.Equal(new ChooserOpening(null, true, false), memory.Open("Garamond", layout: 2));
        memory.Record(96f, "Garamond", layout: 2);
        Assert.Equal(new ChooserOpening(96f, false, false), memory.Open("Garamond", layout: 2));
    }

    [Fact]
    public void ABadScrollValue_IsRememberedAsTheTop()
    {
        var memory = new ChooserMemory();
        memory.Record(float.NaN, "a");
        Assert.Equal(0f, memory.Open("a").RestoreScrollY);
        memory.Record(-5f, "a");
        Assert.Equal(0f, memory.Open("a").RestoreScrollY);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var memory = new ChooserMemory { Search = "goth" };
        memory.Record(64f, "a");

        memory.Reset();

        Assert.Equal(string.Empty, memory.Search);
        Assert.Equal(new ChooserOpening(null, true, false), memory.Open("a"));
    }

    [Fact]
    public void EachChooser_HasItsOwnMemory()
    {
        var fonts = ChooserMemories.For("ChooserMemoryTests.Fonts");
        var ornaments = ChooserMemories.For("ChooserMemoryTests.Ornaments");

        Assert.Same(fonts, ChooserMemories.For("ChooserMemoryTests.Fonts"));
        Assert.NotSame(fonts, ornaments);
    }
}
