using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.Services.Packages;

/// <summary>What one archive entry is, once its name has been validated against version 1's layout.</summary>
internal enum PackageEntryKind
{
    Manifest,
    Profile,
    Preview,

    /// <summary>The optional "assets/" folder entry some ZIP tools add. Carries no data.</summary>
    AssetsFolder,

    Asset,
}

/// <summary>A validated version 1 entry name, and for assets the id and extension its name declares.</summary>
internal readonly record struct PackageEntryName(PackageEntryKind Kind, string Path, Guid AssetId = default, string? Extension = null);

/// <summary>
/// Archive entry names are hostile input. Nothing here ever turns an entry name into a file
/// system path — the importer never extracts to a name the archive chose — but names are still
/// validated strictly, because an ambiguous name (two spellings of one file) could otherwise make
/// validation look at one entry while something else reads another.
///
/// <para><b>Rule:</b> an unsafe or unusual name is REJECTED, never normalized into a safe one.
/// There is no ".." resolution, separator conversion, trimming, or case folding that could make a
/// hostile name look acceptable; the only names accepted are plain, lowercase-ASCII relative
/// paths. Duplicates are compared case-insensitively (as Windows would see them), and any
/// duplicate is an error — never first-wins or last-wins.</para>
///
/// <para>Two passes: <see cref="CheckSafety"/> applies to every entry of every package version
/// (so even a newer package can't carry a traversal name), then <see cref="ClassifyVersion1"/>
/// allows exactly version 1's layout: manifest.json, profile.json, preview.png, and
/// assets/&lt;32 lowercase hex&gt;.(png|jpg|webp). Future content gets new names through a new
/// package version — never a wildcard now.</para>
/// </summary>
internal static class PackagePaths
{
    internal const string ManifestPath = "manifest.json";
    internal const string ProfilePath = "profile.json";
    internal const string PreviewPath = "preview.png";
    internal const string AssetsFolder = "assets/";

    /// <summary>Image extensions a version 1 asset may have: exactly the managed asset formats.</summary>
    internal static readonly IReadOnlyList<string> AssetExtensions = [".png", ".jpg", ".webp"];

    /// <summary>The package path an asset is stored at: stable, derived only from its id and real format.</summary>
    internal static string AssetPath(Guid assetId, string extension) => AssetsFolder + assetId.ToString("N") + extension;

    /// <summary>
    /// Null when <paramref name="name"/> is a safe, plain relative entry name; otherwise why not
    /// (for the log — never shown with the name to the player).
    /// </summary>
    internal static string? CheckSafety(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "empty name";
        }

        if (name.Length > PackagePolicy.MaxEntryPathLength)
        {
            return "name too long";
        }

        foreach (var c in name)
        {
            // Printable ASCII only: no control characters (NUL included), no Unicode look-alikes,
            // no characters whose meaning differs between file systems.
            if (c < 0x21 || c > 0x7E)
            {
                return "non-printable or non-ASCII character";
            }

            switch (c)
            {
                case '\\':
                    return "backslash separator";
                case ':':
                    return "drive or stream separator";
                case '*' or '?' or '"' or '<' or '>' or '|':
                    return "reserved character";
            }
        }

        if (name[0] == '/')
        {
            return "absolute path";
        }

        // Directory entries end in '/', so the last segment of one is empty; nothing else may be.
        var isDirectory = name.EndsWith('/');
        var segments = (isDirectory ? name[..^1] : name).Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return "empty path segment";
            }

            if (segment is "." or "..")
            {
                return "dot segment";
            }

            // Windows silently drops trailing dots and spaces, so "profile.json." would be another
            // spelling of "profile.json".
            if (segment.EndsWith('.'))
            {
                return "trailing dot";
            }
        }

        return null;
    }

    /// <summary>The key two names collide under: Windows treats names differing only in case as one file.</summary>
    internal static string CollisionKey(string safeName) => safeName.ToLowerInvariant();

    /// <summary>
    /// Classifies a name that already passed <see cref="CheckSafety"/> against version 1's exact
    /// layout (case-sensitive), or returns false for anything else.
    /// </summary>
    internal static bool ClassifyVersion1(string safeName, out PackageEntryName entry)
    {
        entry = default;
        switch (safeName)
        {
            case ManifestPath:
                entry = new PackageEntryName(PackageEntryKind.Manifest, safeName);
                return true;
            case ProfilePath:
                entry = new PackageEntryName(PackageEntryKind.Profile, safeName);
                return true;
            case PreviewPath:
                entry = new PackageEntryName(PackageEntryKind.Preview, safeName);
                return true;
            case AssetsFolder:
                entry = new PackageEntryName(PackageEntryKind.AssetsFolder, safeName);
                return true;
        }

        if (!safeName.StartsWith(AssetsFolder, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = safeName[AssetsFolder.Length..];
        var dot = fileName.IndexOf('.');
        if (dot != 32)
        {
            return false;
        }

        var extension = fileName[dot..];
        var id = fileName[..dot];
        if (!AssetExtensions.Contains(extension) || !IsLowercaseHex(id) || !Guid.TryParseExact(id, "N", out var assetId) || assetId == Guid.Empty)
        {
            return false;
        }

        entry = new PackageEntryName(PackageEntryKind.Asset, safeName, assetId, extension);
        return true;
    }

    private static bool IsLowercaseHex(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A lowercase hex SHA-256 string (64 characters).</summary>
    internal static bool IsSha256Hex(string? text) =>
        text is { Length: 64 } && IsLowercaseHex(text);

    /// <summary>A file name safe to offer in a save dialog, from a Plate name the player wrote.</summary>
    internal static string SuggestFileName(string plateName)
    {
        var chars = new List<char>(plateName.Length);
        foreach (var c in plateName.Trim())
        {
            chars.Add(char.IsControl(c) || Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : c);
        }

        // No leading dots (a hidden file) or trailing dots (which Windows drops).
        var name = new string(chars.ToArray()).Trim().Trim('.').Trim();
        if (name.Length == 0)
        {
            name = "Plate";
        }

        return name.Length > 64 ? name[..64] : name;
    }
}
