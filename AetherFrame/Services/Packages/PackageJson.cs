using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetherFrame.Services.Packages;

/// <summary>
/// The only way package JSON is parsed: strict (no comments, no trailing commas), depth-limited
/// by the parser itself (so deep nesting fails before it can recurse), and into plain JSON nodes
/// — never into types named by the data. Typed reading happens afterwards, through the
/// document's own fixed, compile-time list of element types.
/// </summary>
internal static class PackageJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = PackagePolicy.MaxJsonDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    /// <summary>Parses UTF-8 JSON that must be an object. Never throws for bad content.</summary>
    internal static bool TryParseObject(ReadOnlySpan<byte> utf8Json, out JsonObject? result, out string? error)
    {
        result = null;
        error = null;

        // A UTF-8 byte order mark is tolerated (some editors add one); anything else is JSON's job.
        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8Json = utf8Json[3..];
        }

        try
        {
            var node = JsonNode.Parse(utf8Json, NodeOptions, DocumentOptions);
            if (node is not JsonObject obj)
            {
                error = "not a JSON object";
                return false;
            }

            // JsonNode keeps the last of two same-named properties; an ambiguous object is refused.
            if (FindDuplicateProperty(utf8Json) is { } duplicate)
            {
                error = $"duplicate property \"{PackageManifest.SafeForLog(duplicate)}\"";
                return false;
            }

            // JsonObject builds itself lazily; build it here, inside the try, not on first use later.
            _ = obj.Count;
            result = obj;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"malformed JSON at line {ex.LineNumber}, byte {ex.BytePositionInLine}";
            return false;
        }
        catch (ArgumentException)
        {
            error = "malformed JSON";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = "malformed JSON";
            return false;
        }
    }

    /// <summary>The first property name repeated within one object, or null. One forward pass.</summary>
    private static string? FindDuplicateProperty(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = PackagePolicy.MaxJsonDepth });
        var scopes = new System.Collections.Generic.Stack<System.Collections.Generic.HashSet<string>?>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (scopes.Peek() is { } names && !names.Add(name))
                    {
                        return name;
                    }

                    break;
            }
        }

        return null;
    }
}
