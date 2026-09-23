using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Plates;

/// <summary>Simple case-insensitive Plate search over the display name and the last-known names
/// of characters the Plate is associated with. No tags, filters, or indexing.</summary>
public static class PlateSearch
{
    public static bool Matches(string? query, string displayName, IEnumerable<string>? characterNames = null)
    {
        var needle = query?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        if (displayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (characterNames is not null)
        {
            foreach (var name in characterNames)
            {
                if (!string.IsNullOrEmpty(name) && name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
