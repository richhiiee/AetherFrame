using System;
using System.Globalization;
using System.IO;

namespace AetherFrame.Persistence;

/// <summary>
/// Where everything lives under the plugin's config directory. Plate documents stay exactly
/// where single-profile documents always were ("Profiles/{guid}.json"), and character bindings
/// in "Characters/{contentId}.json", so migration never moves user data.
/// </summary>
internal sealed class PlateStoragePaths
{
    internal PlateStoragePaths(string rootDirectory)
    {
        Root = rootDirectory;
        PlatesDirectory = Path.Combine(rootDirectory, "Profiles");
        CharactersDirectory = Path.Combine(rootDirectory, "Characters");
        LibraryDirectory = Path.Combine(rootDirectory, "Library");
        LibraryFile = Path.Combine(LibraryDirectory, "library.json");
        PlateTrashDirectory = Path.Combine(rootDirectory, "Trash", "Plates");
        RecoveryDirectory = Path.Combine(rootDirectory, "Recovery");
        MigrationBackupDirectory = Path.Combine(rootDirectory, "Backups", "pre-plate-library");
        AssetsDirectory = Path.Combine(rootDirectory, "assets");
        AssetStagingDirectory = Path.Combine(rootDirectory, "asset-staging");
        AssetMetadataDirectory = Path.Combine(rootDirectory, "asset-metadata");
        AssetTrashDirectory = Path.Combine(rootDirectory, "asset-trash");
        ThumbnailsDirectory = Path.Combine(rootDirectory, "thumbnails");
        PackageStagingDirectory = Path.Combine(rootDirectory, "package-staging");
    }

    internal string Root { get; }

    /// <summary>Plate documents, one "{guid}.json" per Plate.</summary>
    internal string PlatesDirectory { get; }

    internal string CharactersDirectory { get; }

    internal string LibraryDirectory { get; }

    internal string LibraryFile { get; }

    /// <summary>Deleted Plates, moved here intact (never destroyed).</summary>
    internal string PlateTrashDirectory { get; }

    /// <summary>Copies of damaged files, taken before anything is written over them.</summary>
    internal string RecoveryDirectory { get; }

    /// <summary>Untouched copies of pre-Plate-Library character bindings.</summary>
    internal string MigrationBackupDirectory { get; }

    /// <summary>Managed image assets ("{guid:N}.{ext}"), unchanged from before.</summary>
    internal string AssetsDirectory { get; }

    internal string AssetStagingDirectory { get; }

    internal string AssetMetadataDirectory { get; }

    internal string AssetTrashDirectory { get; }

    internal string ThumbnailsDirectory { get; }

    /// <summary>
    /// Private scratch space for validating .aetherframe files: one folder per import, deleted when
    /// the import finishes or is cancelled. Never user data; leftovers are swept at startup.
    /// </summary>
    internal string PackageStagingDirectory { get; }

    internal string GetPlatePath(Guid plateId) => Path.Combine(PlatesDirectory, $"{plateId}.json");

    internal string GetBindingPath(ulong contentId) => Path.Combine(CharactersDirectory, $"{contentId.ToString(CultureInfo.InvariantCulture)}.json");

    internal string GetTrashPlatePath(Guid plateId, DateTime deletedUtc) =>
        Path.Combine(PlateTrashDirectory, $"{plateId}.deleted-{deletedUtc:yyyyMMdd-HHmmss-fff}.json");

    /// <summary>A recovery copy of <paramref name="originalPath"/>: same name, stamped.</summary>
    internal string GetRecoveryPath(string originalPath, DateTime nowUtc) =>
        Path.Combine(RecoveryDirectory, $"{Path.GetFileNameWithoutExtension(originalPath)}.damaged-{nowUtc:yyyyMMdd-HHmmss-fff}{Path.GetExtension(originalPath)}");

    internal static bool TryParsePlateFileName(string path, out Guid plateId) =>
        Guid.TryParse(Path.GetFileNameWithoutExtension(path), out plateId) && plateId != Guid.Empty;

    internal static bool TryParseBindingFileName(string path, out ulong contentId) =>
        ulong.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out contentId) && contentId != 0;
}
