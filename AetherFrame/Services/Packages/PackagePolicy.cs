using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Assets;

namespace AetherFrame.Services.Packages;

/// <summary>
/// Every safety limit for .aetherframe packages, in one place. A package is an untrusted file
/// from outside the installation, so each limit is checked as early as possible — before the
/// expensive work it protects (reading, allocating, hashing, decoding) — and a package over any of
/// them is refused outright, never truncated or "mostly" imported.
///
/// Where AetherFrame already had a limit, the package limit IS that limit (so anything a package
/// can carry is something the editor could have made, and vice versa). The rest are conservative:
/// generous for any Plate a person could build, far below anything that could hurt the game.
/// Pinned by tests so they can't silently drift.
/// </summary>
internal static class PackagePolicy
{
    /// <summary>The file extension, including the dot.</summary>
    internal const string FileExtension = ".aetherframe";

    /// <summary>The value of the manifest's "format" field: what kind of file this is.</summary>
    internal const string FormatIdentifier = "aetherframe.package";

    /// <summary>The package container version this build writes and fully understands.</summary>
    internal const int CurrentFormatVersion = 1;

    /// <summary>
    /// The one capability version 1 packages carry: "this package holds one complete Plate".
    /// Future content kinds (Templates, Theme Kits, Frames, Components...) are new capabilities,
    /// which an older build reports as Unsupported instead of misreading.
    /// </summary>
    internal const string PlateCapability = "plate";

    // ---------------------------------------------------------------- container

    /// <summary>
    /// 128 MiB for the .aetherframe file itself, checked before the archive is opened. Enough for
    /// several full-size images (which barely compress), far below anything that strains memory
    /// or disk.
    /// </summary>
    internal const long MaxPackageBytes = 128L * 1024 * 1024;

    /// <summary>
    /// 128 MiB for everything inside the package added together, counted from the bytes actually
    /// read — never from the sizes the archive declares, which a "zip bomb" can lie about.
    /// </summary>
    internal const long MaxTotalUncompressedBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Archive entries, checked from the archive's own end record before .NET reads its directory
    /// (so a directory claiming millions of entries is never loaded). Version 1 needs at most
    /// manifest + profile + preview + one per asset, plus an optional "assets/" folder entry.
    /// </summary>
    internal const int MaxEntryCount = 4 + MaxAssetCount;

    /// <summary>
    /// 1 MiB for the archive's central directory (its table of contents), also read from the end
    /// record before .NET parses it: .NET reads the whole directory region even if the entry count
    /// is understated. Version 1's few dozen entries need a few KiB, even with generous extra fields.
    /// </summary>
    internal const long MaxCentralDirectoryBytes = 1024 * 1024;

    /// <summary>The largest any one entry may be: the image limit (no entry is larger than an image).</summary>
    internal const long MaxEntryBytes = ImageSafety.MaxFileBytes;

    /// <summary>Longest archive path accepted (version 1's longest is "assets/" + 32 hex + ".webp").</summary>
    internal const int MaxEntryPathLength = 128;

    // ---------------------------------------------------------------- JSON

    /// <summary>64 KiB: the manifest only describes the package; even with every asset declared it's a few KiB.</summary>
    internal const int MaxManifestBytes = 64 * 1024;

    /// <summary>
    /// 8 MiB for profile.json. The largest real Plate — 256 elements, each with 2000 characters of
    /// text that all need escaping — is about 3 MiB.
    /// </summary>
    internal const int MaxProfileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// JSON nesting depth, enforced by the parser itself. A Plate nests about 6 deep (document →
    /// elements → element → color); 32 leaves room for newer builds' data without allowing the
    /// deep-recursion inputs that are the usual JSON attack.
    /// </summary>
    internal const int MaxJsonDepth = 32;

    // ---------------------------------------------------------------- Plate content

    /// <summary>Every element counts, including ones this build doesn't recognize. The editor's own limit.</summary>
    internal const int MaxElementCount = ProfileDocument.MaxElementCount;

    /// <summary>Distinct images one Plate may carry. Plates use a handful; 32 is generous.</summary>
    internal const int MaxAssetCount = 32;

    /// <summary>Canvas bounds. The editor offers 100 to 4096 (older Plates are 1920 x 1080); these
    /// allow any of those while refusing sizes nothing could have made.</summary>
    internal const float MinCanvasDimension = 16f;

    internal const float MaxCanvasDimension = 8192f;

    /// <summary>
    /// Positions, sizes, offsets: how far from the canvas anything may be. Elements may sit
    /// partly off-canvas, but not a hundred thousand pixels away. Also rejects values that would
    /// overflow layout math.
    /// </summary>
    internal const float MaxCoordinateMagnitude = 100_000f;

    /// <summary>Font size ceiling. The editor's slider stops at 96 but typed values can go past it;
    /// 1024 still keeps font building bounded.</summary>
    internal const float MaxFontSize = 1024f;

    /// <summary>Spacing, outline, and rotation values beyond this are nonsense rather than style.</summary>
    internal const float MaxStyleMagnitude = 10_000f;

    /// <summary>Theme ids and font family ids are short identifiers.</summary>
    internal const int MaxIdentifierLength = 64;

    /// <summary>Manifest capability list bounds.</summary>
    internal const int MaxCapabilityCount = 16;

    // ---------------------------------------------------------------- images

    // Images use AetherFrame's existing image limits unchanged (see ImageSafety): the same checks
    // run on an image whether it came from a file picker or a package.

    /// <summary>Largest image file.</summary>
    internal const long MaxImageBytes = ImageSafety.MaxFileBytes;

    /// <summary>Largest width or height, in pixels.</summary>
    internal const int MaxImageDimension = ImageSafety.MaxDimension;

    /// <summary>Largest pixel count.</summary>
    internal const long MaxImagePixelCount = ImageSafety.MaxPixelCount;

    /// <summary>Largest decoded (RGBA) size: what an image really costs in memory.</summary>
    internal const long MaxDecodedImageBytes = ImageSafety.MaxDecodedBytes;

    /// <summary>preview.png is only a picture of the Plate: 8 MiB and 4096 px per side is plenty.</summary>
    internal const long MaxPreviewBytes = 8L * 1024 * 1024;

    internal const int MaxPreviewDimension = 4096;
}
