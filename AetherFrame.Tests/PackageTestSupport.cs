using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>A real Plate Library, managed asset storage, and package service over one temp directory.</summary>
internal sealed class PackageFixture : IDisposable
{
    internal const ulong PrivateContentId = 987_654_321_987_654UL;
    internal const string PrivateCharacterName = "Secretive Binding Name";
    internal const string PrivateWorld = "Hiddenworld";

    internal static readonly CharacterContext Owner = new(PrivateContentId, PrivateCharacterName, PrivateWorld);

    private readonly LibraryFixture library;
    private readonly TempDirectory outside = new();

    internal PackageFixture(Func<string, bool>? decoder = null, IPlateFileStore? store = null)
    {
        library = new LibraryFixture(store);
        Assets = new AssetStorageService(Paths.AssetsDirectory, Paths.AssetStagingDirectory, new AssetMetadataStore(Paths.AssetMetadataDirectory), decoder);
        Decoder = decoder;
        Directory.CreateDirectory(ExportDirectory);
    }

    internal PlateStoragePaths Paths => library.Paths;

    internal LibraryFixture Library => library;

    internal FakeClock Clock => library.Clock;

    internal TestLog Log => library.Log;

    internal AssetStorageService Assets { get; }

    internal Func<string, bool>? Decoder { get; }

    /// <summary>Where exported files go: outside the installation, like a real Documents folder.</summary>
    internal string ExportDirectory => Path.Combine(outside.Path, "Exports");

    internal string SourceDirectory => Path.Combine(outside.Path, "Pictures");

    internal PlatePackageService CreatePackages(PlateLibraryService service) =>
        new(service, Assets, Paths, "AetherFrame Tests", Decoder, Log, () => Clock.Now);

    internal async Task<(PlateLibraryService Library, PlatePackageService Packages)> LoadAsync()
    {
        var service = await library.LoadAsync();
        return (service, CreatePackages(service));
    }

    internal Guid AddImage(byte[] bytes, string fileName = "picture.png") =>
        Assets.ImportImage(TestImages.Write(SourceDirectory, Guid.NewGuid().ToString("N") + "-" + fileName, bytes));

    /// <summary>
    /// The full round-trip Plate: an Adventure Plate Classic for <see cref="Owner"/> (so it has a
    /// binding and is Active) with Basic metadata, a Theme, a Pattern, a portrait, a background
    /// image, and Advanced-only text and image elements.
    /// </summary>
    internal async Task<(Guid PlateId, Guid PortraitAsset, Guid BackgroundAsset, Guid ExtraAsset)> CreateRichPlateAsync(PlateLibraryService service, string name = "Traveler's Plate")
    {
        var created = await service.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Owner, name, new PlateStarterContent(BasicTestCharacter));
        Assert.True(created.BecameActive);

        var portrait = AddImage(TestImages.Png(400, 640), "portrait.png");
        var background = AddImage(TestImages.Jpeg(1280, 720), "background.jpg");
        var extra = AddImage(TestImages.WebPExtended(64, 64), "sticker.webp");

        var document = service.OpenDocumentForEditing(created.PlateId);
        var editor = BasicDocuments.Editor(document);
        editor.ApplyTheme(ProfileThemePresets.All[3]);
        editor.SetLevel(90);
        editor.SetPlaystyles(["Casual", "Roleplay"]);

        document.Background!.Mode = ProfileBackgroundMode.TexturedFill;
        document.Background.Texture = ProfileBackgroundTexture.Honeycomb;
        document.Background.TextureScale = 32f;
        document.Background.ImageAssetId = background;

        var portraitElement = document.Elements.OfType<ImageProfileElement>().FirstOrDefault(e => e.Role == ProfileElementRole.BasicPortrait);
        if (portraitElement is null)
        {
            portraitElement = new ImageProfileElement { Role = ProfileElementRole.BasicPortrait, Position = new Vector2(20, 20), Size = new Vector2(300, 480) };
            document.Elements.Add(portraitElement);
        }

        portraitElement.AssetId = portrait;

        document.Elements.Add(new TextProfileElement
        {
            Text = "Advanced-only caption: visit https://example.invalid/me",
            FontFamily = ProfileFontFamilies.AetherFrameSerif,
            FontSize = 30,
            Italic = true,
            Color = new Vector4(0.9f, 0.8f, 0.2f, 1f),
            Position = new Vector2(700, 600),
            Size = new Vector2(500, 60),
            ZIndex = 50,
        });
        document.Elements.Add(new ImageProfileElement
        {
            AssetId = extra,
            Position = new Vector2(1100, 40),
            Size = new Vector2(96, 96),
            RotationDegrees = 15f,
            FlipY = true,
            ZIndex = 51,
        });

        await service.SavePlateDocumentAsync(document);
        return (created.PlateId, portrait, background, extra);
    }

    internal static readonly BasicCharacterInfo BasicTestCharacter = new("Visible Hero", "Phoenix", "Light", 19, "Paladin", 100, "ABC");

    /// <summary>Exports a Plate and asserts it worked.</summary>
    internal string Export(PlatePackageService packages, Guid plateId, string fileName = "plate.aetherframe")
    {
        var path = Path.Combine(ExportDirectory, fileName);
        var result = packages.Export(plateId, path, overwrite: false);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return path;
    }

    /// <summary>Every file in the installation (hash by relative path), staging excluded: for "nothing changed" checks.</summary>
    internal Dictionary<string, string> SnapshotInstallation()
    {
        var root = library.Root;
        var staging = Path.GetFullPath(Paths.PackageStagingDirectory) + Path.DirectorySeparatorChar;
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetFullPath(p).StartsWith(staging, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
    }

    /// <summary>True when no import left anything in staging.</summary>
    internal bool StagingIsEmpty =>
        !Directory.Exists(Paths.PackageStagingDirectory) || Directory.GetFileSystemEntries(Paths.PackageStagingDirectory).Length == 0;

    public void Dispose()
    {
        library.Dispose();
        outside.Dispose();
    }
}

/// <summary>One archive entry, for building hostile packages.</summary>
internal sealed class ZipSpec
{
    internal ZipSpec(string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal, int? externalAttributes = null)
    {
        Name = name;
        Bytes = bytes;
        Level = level;
        ExternalAttributes = externalAttributes;
    }

    internal string Name { get; set; }

    internal byte[] Bytes { get; set; }

    internal CompressionLevel Level { get; set; }

    internal int? ExternalAttributes { get; set; }
}

/// <summary>Reads, edits, and rewrites packages — including into forms no exporter would ever produce.</summary>
internal static class PackageFiles
{
    internal static List<ZipSpec> Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new ZipSpec(e.FullName, buffer.ToArray(), CompressionLevel.Optimal, e.ExternalAttributes);
        }).ToList();
    }

    internal static byte[] Build(IEnumerable<ZipSpec> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var spec in entries)
            {
                var entry = archive.CreateEntry(spec.Name, spec.Level);
                if (spec.ExternalAttributes is { } attributes)
                {
                    entry.ExternalAttributes = attributes;
                }

                using var stream = entry.Open();
                stream.Write(spec.Bytes);
            }
        }

        return buffer.ToArray();
    }

    internal static string Write(string directory, IEnumerable<ZipSpec> entries, string fileName = "crafted.aetherframe")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Build(entries));
        return path;
    }

    /// <summary>Copies a package with its entries edited; <paramref name="reseal"/> recomputes the manifest's sizes and hashes afterwards.</summary>
    internal static string Rewrite(string source, Action<List<ZipSpec>> edit, bool reseal = true, string? fileName = null)
    {
        var entries = Read(source);
        edit(entries);
        if (reseal)
        {
            Reseal(entries);
        }

        return Write(Path.GetDirectoryName(source)!, entries, fileName ?? $"edited-{Guid.NewGuid():N}.aetherframe");
    }

    internal static ZipSpec Entry(List<ZipSpec> entries, string name) => entries.Single(e => e.Name == name);

    internal static JsonObject Json(ZipSpec entry) => JsonNode.Parse(entry.Bytes)!.AsObject();

    internal static void SetJson(ZipSpec entry, JsonNode json) => entry.Bytes = Encoding.UTF8.GetBytes(json.ToJsonString());

    internal static void EditManifest(List<ZipSpec> entries, Action<JsonObject> edit)
    {
        var entry = Entry(entries, PackagePaths.ManifestPath);
        var json = Json(entry);
        edit(json);
        SetJson(entry, json);
    }

    internal static void EditProfile(List<ZipSpec> entries, Action<JsonObject> edit)
    {
        var entry = Entry(entries, PackagePaths.ProfilePath);
        var json = Json(entry);
        edit(json);
        SetJson(entry, json);
    }

    /// <summary>Makes the manifest's declared sizes and hashes match the (edited) entries, as a careful attacker would.</summary>
    internal static void Reseal(List<ZipSpec> entries)
    {
        var manifestEntry = entries.FirstOrDefault(e => e.Name == PackagePaths.ManifestPath);
        if (manifestEntry is null)
        {
            return;
        }

        var manifest = Json(manifestEntry);
        void Seal(JsonObject? declaration)
        {
            if (declaration?["path"]?.GetValue<string>() is { } path && entries.FirstOrDefault(e => e.Name == path) is { } entry)
            {
                declaration["byteLength"] = entry.Bytes.Length;
                declaration["sha256"] = Convert.ToHexStringLower(SHA256.HashData(entry.Bytes));
            }
        }

        Seal(manifest["profile"] as JsonObject);
        Seal(manifest["preview"] as JsonObject);
        if (manifest["assets"] is JsonArray assets)
        {
            foreach (var asset in assets)
            {
                Seal(asset as JsonObject);
            }
        }

        SetJson(manifestEntry, manifest);
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
