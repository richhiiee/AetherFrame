using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetherFrame.Services.Packages;

/// <summary>
/// Asset ids inside Plate JSON, handled at the JSON level — so an asset referenced from data this
/// build doesn't understand (a newer element type, a newer field) is found and re-pointed exactly
/// like one in a known field. Only string values are touched, and only strings that are one of
/// the package's declared asset ids; everything else is left byte-for-byte alone.
/// </summary>
internal static class PackageAssetIds
{
    private static readonly string[] GuidFormats = ["D", "N", "B", "P"];

    /// <summary>Every GUID written as a string anywhere in <paramref name="node"/>.</summary>
    internal static HashSet<Guid> CollectGuidStrings(JsonNode? node)
    {
        var found = new HashSet<Guid>();
        Walk(node, value =>
        {
            if (TryParse(value, out var id, out _))
            {
                found.Add(id);
            }

            return null;
        });
        return found;
    }

    /// <summary>
    /// Replaces every string that is a key of <paramref name="map"/> with its value, written in the
    /// same GUID format it was found in. Returns how many strings were replaced.
    /// </summary>
    internal static int Remap(JsonNode? node, IReadOnlyDictionary<Guid, Guid> map)
    {
        var replaced = 0;
        if (map.Count == 0)
        {
            return 0;
        }

        Walk(node, value =>
        {
            if (TryParse(value, out var id, out var format) && map.TryGetValue(id, out var replacement))
            {
                replaced++;
                return replacement.ToString(format);
            }

            return null;
        });
        return replaced;
    }

    private static bool TryParse(string text, out Guid id, out string format)
    {
        foreach (var candidate in GuidFormats)
        {
            if (Guid.TryParseExact(text, candidate, out id) && id != Guid.Empty)
            {
                format = candidate;
                return true;
            }
        }

        id = Guid.Empty;
        format = "D";
        return false;
    }

    /// <summary>Visits every string value; a non-null return replaces it.</summary>
    private static void Walk(JsonNode? node, Func<string, string?> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                List<(string Key, string Value)>? replacements = null;
                foreach (var (key, child) in obj)
                {
                    if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String && visit(value.GetValue<string>()) is { } replacement)
                    {
                        (replacements ??= new()).Add((key, replacement));
                    }
                    else
                    {
                        Walk(child, visit);
                    }
                }

                if (replacements is not null)
                {
                    foreach (var (key, replacement) in replacements)
                    {
                        obj[key] = replacement;
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.GetValueKind() == JsonValueKind.String && visit(value.GetValue<string>()) is { } replacement)
                    {
                        array[i] = replacement;
                    }
                    else
                    {
                        Walk(array[i], visit);
                    }
                }

                break;
        }
    }
}
