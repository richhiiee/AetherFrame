using System;
using System.Collections.Generic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// The one definition of paint order — ascending <see cref="ProfileElement.ZIndex"/>, ties broken
/// by list (insertion) order, so a later element paints over an earlier one — shared by rendering,
/// canvas hit testing, and the Layers panel. Fills a caller-owned, reused buffer instead of
/// allocating a sorted copy (LINQ OrderBy) every frame.
/// </summary>
internal static class ProfilePaintOrder
{
    /// <summary>
    /// Clears <paramref name="buffer"/> and fills it with <paramref name="profile"/>'s elements in
    /// paint order (bottom first). Hidden elements are skipped unless <paramref name="includeHidden"/>.
    /// </summary>
    internal static void Fill(ProfileDocument profile, List<ProfileElement> buffer, bool includeHidden)
    {
        buffer.Clear();

        var elements = profile.Elements;
        var alreadySorted = true;
        var previousZ = int.MinValue;

        foreach (var element in elements)
        {
            if (!includeHidden && !element.Visible)
            {
                continue;
            }

            if (element.ZIndex < previousZ)
            {
                alreadySorted = false;
            }

            previousZ = element.ZIndex;
            buffer.Add(element);
        }

        if (alreadySorted)
        {
            // The overwhelmingly common case (elements appended with increasing ZIndex, or
            // renumbered compactly by any reorder): list order already is paint order.
            return;
        }

        // Stable insertion sort — List.Sort is unstable, and ties must keep list order. Cheap for
        // the element counts a profile allows (<= 256) and for the nearly-sorted input it sees.
        for (var i = 1; i < buffer.Count; i++)
        {
            var current = buffer[i];
            var j = i - 1;
            while (j >= 0 && buffer[j].ZIndex > current.ZIndex)
            {
                buffer[j + 1] = buffer[j];
                j--;
            }

            buffer[j + 1] = current;
        }
    }

    /// <summary>Topmost-first hit test over a paint-ordered buffer, skipping elements <paramref name="skip"/> rejects.</summary>
    internal static ProfileElement? HitTest(List<ProfileElement> paintOrder, System.Numerics.Vector2 logicalPoint, Func<ProfileElement, bool>? skip = null)
    {
        for (var i = paintOrder.Count - 1; i >= 0; i--)
        {
            var element = paintOrder[i];
            if (skip is not null && skip(element))
            {
                continue;
            }

            var rotationDegrees = RotationGeometry.GetRotationDegrees(element);
            var testPoint = rotationDegrees == 0f
                ? logicalPoint
                : RotationGeometry.RotatePoint(logicalPoint, RotationGeometry.GetCenter(element.Position, element.Size), -rotationDegrees);

            var min = element.Position;
            var max = element.Position + element.Size;

            if (testPoint.X >= min.X && testPoint.X <= max.X && testPoint.Y >= min.Y && testPoint.Y <= max.Y)
            {
                return element;
            }
        }

        return null;
    }
}
