using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.Domain.Plates;

/// <summary>
/// Pure operations on the manual Plate order (<see cref="PlateLibraryState.OrderedPlateIds"/>).
/// Every operation tolerates ids that are missing from the list or no longer exist: ordering is
/// organizational metadata, so a damaged entry may only ever cost ordering, never a Plate.
/// </summary>
public static class PlateOrdering
{
    /// <summary>New Plates appear first.</summary>
    public static void InsertAtFront(List<Guid> order, Guid plateId)
    {
        order.Remove(plateId);
        order.Insert(0, plateId);
    }

    /// <summary>Places <paramref name="plateId"/> directly after <paramref name="anchorId"/>
    /// (a duplicate after its source); at the front when the anchor isn't in the list.</summary>
    public static void InsertAfter(List<Guid> order, Guid anchorId, Guid plateId)
    {
        order.Remove(plateId);
        var anchorIndex = order.IndexOf(anchorId);
        order.Insert(anchorIndex < 0 ? 0 : anchorIndex + 1, plateId);
    }

    /// <summary>
    /// Moves <paramref name="plateId"/> next to <paramref name="targetId"/> (after it when
    /// <paramref name="placeAfter"/>). Returns false, leaving the list untouched, when either id
    /// isn't in the list or they're the same Plate.
    /// </summary>
    public static bool Move(List<Guid> order, Guid plateId, Guid targetId, bool placeAfter)
    {
        if (plateId == targetId || !order.Contains(plateId) || !order.Contains(targetId))
        {
            return false;
        }

        var before = order.ToList();
        order.Remove(plateId);
        var targetIndex = order.IndexOf(targetId);
        order.Insert(placeAfter ? targetIndex + 1 : targetIndex, plateId);
        return !before.SequenceEqual(order);
    }

    /// <summary>
    /// Repairs the order against the Plates that actually exist: drops duplicate entries (first
    /// wins) and appends existing Plates the order doesn't mention yet, oldest-created last.
    /// Entries for Plates that don't exist are deliberately KEPT (see class doc) — they're skipped
    /// at display time. Returns true when the list changed.
    /// </summary>
    public static bool Reconcile(List<Guid> order, IReadOnlyCollection<(Guid Id, DateTime CreatedUtc)> existingPlates)
    {
        var changed = false;
        var seen = new HashSet<Guid>();

        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == Guid.Empty || !seen.Add(order[i]))
            {
                order.RemoveAt(i--);
                changed = true;
            }
        }

        foreach (var plate in existingPlates.Where(p => !seen.Contains(p.Id)).OrderByDescending(p => p.CreatedUtc).ThenBy(p => p.Id))
        {
            order.Add(plate.Id);
            changed = true;
        }

        return changed;
    }

    /// <summary>Initial order for a library built from existing documents: newest first.</summary>
    public static List<Guid> BuildInitialOrder(IEnumerable<(Guid Id, DateTime CreatedUtc)> existingPlates) =>
        existingPlates.OrderByDescending(p => p.CreatedUtc).ThenBy(p => p.Id).Select(p => p.Id).ToList();

    /// <summary>The existing Plates in display order: ordered ids that exist, then (defensively)
    /// any existing Plate the order doesn't mention.</summary>
    public static List<Guid> ResolveDisplayOrder(IReadOnlyList<Guid> order, IReadOnlyCollection<(Guid Id, DateTime CreatedUtc)> existingPlates)
    {
        var existing = existingPlates.Select(p => p.Id).ToHashSet();
        var result = new List<Guid>(existingPlates.Count);
        var seen = new HashSet<Guid>();

        foreach (var id in order)
        {
            if (existing.Contains(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }

        foreach (var plate in existingPlates.Where(p => !seen.Contains(p.Id)).OrderByDescending(p => p.CreatedUtc).ThenBy(p => p.Id))
        {
            result.Add(plate.Id);
        }

        return result;
    }
}
