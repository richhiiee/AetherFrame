using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;

namespace AetherFrame.Tests;

/// <summary>A fresh directory per test, deleted afterwards.</summary>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aetherframe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FakeClock
{
    internal DateTime Now { get; set; } = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    internal DateTime Tick(double seconds = 1)
    {
        Now = Now.AddSeconds(seconds);
        return Now;
    }
}

internal sealed class TestLog : IAetherFrameLog
{
    internal List<string> Messages { get; } = new();

    public void Information(string message) => Messages.Add("I " + message);

    public void Warning(string message) => Messages.Add("W " + message);

    public void Error(Exception? exception, string message) => Messages.Add("E " + message);
}

/// <summary>
/// Plain files plus a simulated backup database, mimicking Dalamud's IReliableFileStorage read
/// semantics: when the reader throws on the primary copy, it is retried with the backup copy.
/// </summary>
internal sealed class BackupSimulatingStore : IPlateFileStore
{
    private readonly SystemFileStore files = new();

    internal Dictionary<string, string> Backups { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal int ReaderInvocations { get; private set; }

    public bool FileExists(string path) => files.FileExists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

    public async Task ReadTextAsync(string path, Action<string> reader)
    {
        try
        {
            ReaderInvocations++;
            await files.ReadTextAsync(path, reader);
        }
        catch (Exception) when (Backups.TryGetValue(path, out var backup))
        {
            ReaderInvocations++;
            reader(backup);
        }
    }

    public async Task WriteTextAsync(string path, string contents)
    {
        await files.WriteTextAsync(path, contents);
        Backups[path] = contents;
    }

    public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path) => files.DeleteFile(path);
}

/// <summary>Plain files with switchable failures, for fault injection.</summary>
internal sealed class FaultInjectingStore : IPlateFileStore
{
    private readonly SystemFileStore files = new();

    /// <summary>Writes whose path matches fail with an IOException (before anything is written).</summary>
    internal Func<string, bool>? FailWrite { get; set; }

    /// <summary>Moves whose source path matches fail with an IOException.</summary>
    internal Func<string, bool>? FailMove { get; set; }

    internal int FailedOperations { get; private set; }

    public bool FileExists(string path) => files.FileExists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

    public Task ReadTextAsync(string path, Action<string> reader) => files.ReadTextAsync(path, reader);

    public Task WriteTextAsync(string path, string contents)
    {
        if (FailWrite?.Invoke(path) == true)
        {
            FailedOperations++;
            throw new IOException($"Injected write failure: {System.IO.Path.GetFileName(path)}");
        }

        return files.WriteTextAsync(path, contents);
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        if (FailMove?.Invoke(sourcePath) == true)
        {
            FailedOperations++;
            throw new IOException($"Injected move failure: {System.IO.Path.GetFileName(sourcePath)}");
        }

        files.MoveFile(sourcePath, destinationPath);
    }

    public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path) => files.DeleteFile(path);
}

/// <summary>Builds a Plate Library over a temp directory, with helpers to seed legacy data.</summary>
internal sealed class LibraryFixture : IDisposable
{
    private readonly TempDirectory directory = new();

    internal LibraryFixture(IPlateFileStore? store = null)
    {
        Paths = new PlateStoragePaths(directory.Path);
        Store = store ?? new SystemFileStore();
    }

    internal PlateStoragePaths Paths { get; }

    internal IPlateFileStore Store { get; }

    internal FakeClock Clock { get; } = new();

    internal TestLog Log { get; } = new();

    internal string Root => directory.Path;

    internal PlateLibraryService CreateService() => new(Paths, Store, Log, () => Clock.Now);

    internal async Task<PlateLibraryService> LoadAsync()
    {
        var service = CreateService();
        await service.InitializeAsync();
        return service;
    }

    internal void WritePlateJson(Guid plateId, string json)
    {
        Directory.CreateDirectory(Paths.PlatesDirectory);
        File.WriteAllText(Paths.GetPlatePath(plateId), json, Encoding.UTF8);
    }

    internal void WriteBindingJson(ulong contentId, string json)
    {
        Directory.CreateDirectory(Paths.CharactersDirectory);
        File.WriteAllText(Paths.GetBindingPath(contentId), json, Encoding.UTF8);
    }

    internal void WriteLibraryJson(string json)
    {
        Directory.CreateDirectory(Paths.LibraryDirectory);
        File.WriteAllText(Paths.LibraryFile, json, Encoding.UTF8);
    }

    internal string ReadPlateJson(Guid plateId) => File.ReadAllText(Paths.GetPlatePath(plateId), Encoding.UTF8);

    internal string ReadBindingJson(ulong contentId) => File.ReadAllText(Paths.GetBindingPath(contentId), Encoding.UTF8);

    internal JsonElement ReadBinding(ulong contentId) => JsonDocument.Parse(ReadBindingJson(contentId)).RootElement.Clone();

    internal List<Guid> ReadLibraryOrder() =>
        JsonDocument.Parse(File.ReadAllText(Paths.LibraryFile)).RootElement.GetProperty("OrderedPlateIds").EnumerateArray().Select(e => e.GetGuid()).ToList();

    public void Dispose() => directory.Dispose();
}

/// <summary>Builds a Template Library (and the Plate Library it depends on) over a temp directory.</summary>
internal sealed class TemplateLibraryFixture : IDisposable
{
    private readonly TempDirectory directory = new();

    internal TemplateLibraryFixture(IPlateFileStore? store = null)
    {
        Paths = new PlateStoragePaths(directory.Path);
        Store = store ?? new SystemFileStore();
        PlateLibrary = new PlateLibraryService(Paths, Store, PlateLog, () => Clock.Now);
    }

    internal PlateStoragePaths Paths { get; }

    internal IPlateFileStore Store { get; }

    internal FakeClock Clock { get; } = new();

    internal TestLog Log { get; } = new();

    internal TestLog PlateLog { get; } = new();

    internal string Root => directory.Path;

    internal PlateLibraryService PlateLibrary { get; }

    internal TemplateLibraryService CreateService() => new(Paths, Store, PlateLibrary, Log, () => Clock.Now);

    /// <summary>Loads the Plate Library first (Templates depend on it), then a fresh Template Library.</summary>
    internal async Task<TemplateLibraryService> LoadAsync()
    {
        await PlateLibrary.InitializeAsync();
        var service = CreateService();
        await service.InitializeAsync();
        return service;
    }

    internal void WriteTemplateJson(Guid templateId, string json)
    {
        Directory.CreateDirectory(Paths.TemplatesDirectory);
        File.WriteAllText(Paths.GetTemplatePath(templateId), json, Encoding.UTF8);
    }

    internal string ReadTemplateJson(Guid templateId) => File.ReadAllText(Paths.GetTemplatePath(templateId), Encoding.UTF8);

    internal void WritePlateJson(Guid plateId, string json)
    {
        Directory.CreateDirectory(Paths.PlatesDirectory);
        File.WriteAllText(Paths.GetPlatePath(plateId), json, Encoding.UTF8);
    }

    public void Dispose() => directory.Dispose();
}

internal static class Characters
{
    internal static readonly CharacterContext Alice = new(1001, "Alice Example", "Twintania");
    internal static readonly CharacterContext Bob = new(2002, "Bob Sample", "Phoenix");
}

/// <summary>Legacy (single-profile era) JSON exactly as older builds wrote it.</summary>
internal static class LegacyData
{
    internal static readonly Guid PortraitAsset = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal static readonly Guid BackgroundAsset = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    /// <summary>A version-1 document: no canvas size, legacy background fields, a text and an image element.</summary>
    internal static string VersionOneDocument(Guid profileId, ulong owner, string name = "Default") => $$"""
        {
          "Version": 1,
          "ProfileId": "{{profileId}}",
          "OwnerContentId": {{owner}},
          "Name": "{{name}}",
          "Revision": 3,
          "CreatedAtUtc": "2025-01-02T03:04:05Z",
          "UpdatedAtUtc": "2025-02-03T04:05:06Z",
          "Elements": [
            {
              "elementType": "text",
              "Id": "0f0f0f0f-0000-0000-0000-000000000001",
              "Text": "Hello from the past",
              "FontSize": 22,
              "Color": { "X": 0.5, "Y": 0.25, "Z": 1, "W": 1 },
              "Position": { "X": 123.5, "Y": 456.25 },
              "Size": { "X": 300, "Y": 60 },
              "ZIndex": 0
            },
            {
              "elementType": "image",
              "Id": "0f0f0f0f-0000-0000-0000-000000000002",
              "AssetId": "{{PortraitAsset}}",
              "Position": { "X": 10, "Y": 20 },
              "Size": { "X": 400, "Y": 800 },
              "ZIndex": 1,
              "Role": 1
            }
          ],
          "BackgroundAssetId": "{{BackgroundAsset}}",
          "BackgroundFitMode": 0,
          "BackgroundOpacity": 0.8
        }
        """;

    internal static string VersionOneBinding(ulong contentId, Guid? active, params Guid[] profileIds) => $$"""
        {
          "Version": 1,
          "ContentId": {{contentId}},
          "ActiveProfileId": {{(active is { } id ? $"\"{id}\"" : "null")}},
          "ProfileIds": [{{string.Join(", ", profileIds.Select(p => $"\"{p}\""))}}],
          "CreatedAtUtc": "2025-01-02T03:04:05Z",
          "UpdatedAtUtc": "2025-01-02T03:04:05Z"
        }
        """;
}

/// <summary>Raw Template envelope JSON, for schema/migration and hardening tests.</summary>
internal static class TemplateSamples
{
    /// <summary>A Template envelope wrapping <paramref name="documentJson"/> verbatim, so the
    /// embedded document's own version can be set independently of the envelope's.</summary>
    internal static string Envelope(Guid templateId, string name, string documentJson, int version = 1, int originKind = 0) => $$"""
        {
          "Version": {{version}},
          "TemplateId": "{{templateId}}",
          "Name": "{{name}}",
          "CreatedAtUtc": "2025-01-02T03:04:05Z",
          "UpdatedAtUtc": "2025-02-03T04:05:06Z",
          "Origin": { "Kind": {{originKind}} },
          "Document": {{documentJson}}
        }
        """;
}

/// <summary>A current document with every kind of creative content, for preservation checks.</summary>
internal static class SampleDocuments
{
    internal static readonly Guid ImageAsset = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
    internal static readonly Guid BackgroundAsset = Guid.Parse("87654321-4321-4321-4321-ba0987654321");

    internal static ProfileDocument Rich(Guid plateId, string name, DateTime now)
    {
        var document = Domain.Plates.PlateFactory.Create(Domain.Plates.PlateStartingLayout.AdventurePlateClassic, plateId, name, now);
        document.CanvasWidth = 1600;
        document.CanvasHeight = 900;
        document.Background = new ProfileBackground
        {
            Mode = ProfileBackgroundMode.Image,
            ImageAssetId = BackgroundAsset,
            ImageFit = ProfileImageFit.Fit,
            Opacity = 0.7f,
            PrimaryColor = new Vector4(0.1f, 0.2f, 0.3f, 1f),
        };
        document.BasicIdentity = new BasicIdentityHeader
        {
            TitleSource = IdentityTitleSource.GameTitle,
            GameTitleId = 42,
            GameTitleIsPrefix = true,
            Layout = IdentityTitleLayout.Classic,
            RegionPosition = new Vector2(12, 34),
            RegionWidth = 560,
        };
        document.Elements.Add(new TextProfileElement
        {
            Text = "Name",
            Role = ProfileElementRole.BasicName,
            FontFamily = ProfileFontFamilies.AetherFrameSans,
            FontSize = 48,
            Bold = true,
            Position = new Vector2(100, 200),
            Size = new Vector2(500, 70),
            ZIndex = 1,
        });
        document.Elements.Add(new ImageProfileElement
        {
            AssetId = ImageAsset,
            Role = ProfileElementRole.BasicPortrait,
            Position = new Vector2(40, 40),
            Size = new Vector2(400, 640),
            RotationDegrees = 12.5f,
            Opacity = 0.9f,
            FlipX = true,
            ZIndex = 0,
        });
        return document;
    }
}

internal static class JsonAssert
{
    /// <summary>Compares two JSON documents, ignoring the listed top-level properties.</summary>
    internal static void EqualExcept(string expected, string actual, params string[] ignoredProperties)
    {
        static string Normalize(string json, string[] ignored)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            foreach (var property in ignored)
            {
                node.Remove(property);
            }

            return node.ToJsonString();
        }

        Xunit.Assert.Equal(Normalize(expected, ignoredProperties), Normalize(actual, ignoredProperties));
    }
}

/// <summary>Locations in the repository checkout the tests run from.</summary>
internal static class RepositoryPaths
{
    /// <summary>The checkout's root: the nearest directory above the test binaries holding <c>Version.props</c>.</summary>
    internal static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Version.props")))
        {
            directory = directory.Parent;
        }

        Xunit.Assert.NotNull(directory);
        return directory!;
    }

    /// <summary>
    /// The built plugin assembly: <c>AETHERFRAME_PLUGIN_ASSEMBLY</c> when set (it must exist), else this
    /// checkout's build of the same configuration, or null when that hasn't been built.
    /// </summary>
    internal static string? PluginAssembly()
    {
        var configured = Environment.GetEnvironmentVariable("AETHERFRAME_PLUGIN_ASSEMBLY");
        if (!string.IsNullOrEmpty(configured))
        {
            Xunit.Assert.True(File.Exists(configured), $"AETHERFRAME_PLUGIN_ASSEMBLY points at a missing file: {configured}");
            return configured;
        }

        // bin/<Configuration>/<tfm>/ here; the plugin builds to AetherFrame/bin/x64/<Configuration>/.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var local = Path.Combine(Root().FullName, "AetherFrame", "bin", "x64", configuration, "AetherFrame.dll");
        return File.Exists(local) ? local : null;
    }
}

/// <summary>Minimal image files: valid headers (all AetherFrame ever inspects), no real pixel data.</summary>
internal static class TestImages
{
    internal static byte[] Png(int width, int height, int? animationFrames = null)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new List<byte>();
        ihdr.AddRange(BigEndian(width));
        ihdr.AddRange(BigEndian(height));
        ihdr.AddRange([8, 6, 0, 0, 0]);
        Chunk(bytes, "IHDR", ihdr.ToArray());

        if (animationFrames is { } frames)
        {
            var actl = new List<byte>();
            actl.AddRange(BigEndian(frames));
            actl.AddRange(BigEndian(0));
            Chunk(bytes, "acTL", actl.ToArray());
        }

        Chunk(bytes, "IDAT", [0, 0, 0, 0]);
        Chunk(bytes, "IEND", []);
        return bytes.ToArray();
    }

    internal static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    internal static byte[] WebPExtended(int width, int height, int animationFrames = 0)
    {
        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());

        var vp8x = new List<byte> { (byte)(animationFrames > 0 ? 0x02 : 0x00), 0, 0, 0 };
        vp8x.AddRange(LittleEndian24(width - 1));
        vp8x.AddRange(LittleEndian24(height - 1));
        RiffChunk(body, "VP8X", vp8x.ToArray());

        for (var i = 0; i < animationFrames; i++)
        {
            RiffChunk(body, "ANMF", new byte[16]);
        }

        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(body.Count));
        bytes.AddRange(body);
        return bytes.ToArray();
    }

    internal static string Write(string directory, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Chunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(BigEndian(data.Length));
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(data);
        bytes.AddRange([0, 0, 0, 0]);
    }

    private static void RiffChunk(List<byte> bytes, string type, byte[] data)
    {
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.AddRange(BitConverter.GetBytes(data.Length));
        bytes.AddRange(data);
        if (data.Length % 2 == 1)
        {
            bytes.Add(0);
        }
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] LittleEndian24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];
}
