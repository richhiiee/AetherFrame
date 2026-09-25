using System;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Where the shared editor action bar's three groups sit on its row: the left group (My Plates,
/// the Basic / Advanced switch) at the start, the history group (Undo, Redo) centered in the row,
/// and the document group (save state, Preview, Revert, Save) against the end — the same positions
/// in both editors. The Plate's name fills whatever space is left between the left group and the
/// history group. When the row is too narrow to center, the groups keep their order and never
/// overlap: the history group follows the left group, the document group follows it.
/// </summary>
internal static class EditorActionBarLayout
{
    /// <param name="rowStart">The row's first usable x.</param>
    /// <param name="rowEnd">The row's last usable x.</param>
    /// <param name="leftEnd">Where the left group ends.</param>
    /// <param name="centerWidth">The history group's width.</param>
    /// <param name="rightWidth">The document group's width.</param>
    /// <param name="spacing">The gap kept between groups.</param>
    /// <returns>The history group's start, the document group's start, and the room for the Plate's name (0 when none).</returns>
    internal static (float CenterX, float RightX, float NameWidth) Arrange(
        float rowStart, float rowEnd, float leftEnd, float centerWidth, float rightWidth, float spacing)
    {
        var earliestCenter = leftEnd + spacing;
        var rightX = Math.Max(earliestCenter + centerWidth + spacing, rowEnd - rightWidth);
        var centered = rowStart + ((rowEnd - rowStart - centerWidth) / 2f);
        var centerX = Math.Clamp(centered, earliestCenter, Math.Max(earliestCenter, rightX - spacing - centerWidth));
        var nameWidth = Math.Max(0f, centerX - spacing - earliestCenter);
        return (centerX, rightX, nameWidth);
    }
}
