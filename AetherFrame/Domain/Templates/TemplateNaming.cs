using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AetherFrame.Domain.Templates;

/// <summary>Display-name rules for Templates. Names are labels only: duplicates are allowed and a
/// name never identifies a Template (its Guid does). A deliberate near-duplicate of
/// <c>PlateNaming</c> — kept separate so a Template naming error never says "Plate".</summary>
public static class TemplateNaming
{
    public const int MaxNameLength = 64;

    private const string CopySuffix = " Copy";

    /// <summary>
    /// Trims the name and folds control characters (e.g. a pasted newline) to spaces. Returns
    /// false with a player-facing <paramref name="error"/> for an empty or over-long result.
    /// </summary>
    public static bool TryNormalizeName(string? input, out string normalized, out string? error)
    {
        var builder = new StringBuilder(input?.Length ?? 0);
        foreach (var c in input ?? string.Empty)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        normalized = builder.ToString().Trim();

        if (normalized.Length == 0)
        {
            error = "A Template needs a name.";
            return false;
        }

        if (normalized.Length > MaxNameLength)
        {
            error = $"Template names can be at most {MaxNameLength} characters.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// "Name Copy", or "Name Copy 2", "Name Copy 3", ... when taken (case-insensitively). Copying
    /// a copy doesn't stack suffixes: "Name Copy" duplicates to "Name Copy 2".
    /// </summary>
    public static string MakeCopyName(string sourceName, IEnumerable<string> existingNames)
    {
        var baseName = StripCopySuffix(string.IsNullOrWhiteSpace(sourceName) ? "Template" : sourceName.Trim());
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var candidate = Fit(baseName, CopySuffix);
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = Fit(baseName, $"{CopySuffix} {n}");
        }

        return candidate;
    }

    /// <summary>"Name", or "Name 2", "Name 3", ... when taken (case-insensitively).</summary>
    public static string MakeUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "Template" : baseName.Trim();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var candidate = Fit(name, string.Empty);
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = Fit(name, $" {n}");
        }

        return candidate;
    }

    private static string StripCopySuffix(string name)
    {
        var index = name.LastIndexOf(CopySuffix, StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
        {
            return name;
        }

        var rest = name[(index + CopySuffix.Length)..];
        var isCopySuffix = rest.Length == 0 || (rest[0] == ' ' && rest.Length > 1 && rest[1..].All(char.IsAsciiDigit));
        return isCopySuffix ? name[..index] : name;
    }

    /// <summary>Appends the suffix, shortening the base so the result stays within the length cap.</summary>
    private static string Fit(string baseName, string suffix)
    {
        var room = MaxNameLength - suffix.Length;
        var trimmedBase = baseName.Length > room ? baseName[..room].TrimEnd() : baseName;
        return trimmedBase + suffix;
    }
}
