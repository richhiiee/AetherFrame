using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Assets;

/// <summary>
/// Descriptive metadata for one managed image asset, stored beside (never inside) the asset and
/// never required for the asset to work: documents reference assets by <see cref="AssetId"/> only,
/// and metadata for assets imported before it existed is created lazily. Never records the
/// source file's path — only its bare file name, for display.
/// </summary>
public sealed class AssetMetadata
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Guid AssetId { get; set; }

    /// <summary>The imported file's own name (no directory), for display only.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>The actual media type sniffed from the file's content, e.g. "image/png" — not
    /// trusted from the file extension.</summary>
    public string MediaType { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    /// <summary>Declared animation frames (1 for still images). Only the first frame is ever displayed.</summary>
    public int FrameCount { get; set; } = 1;

    /// <summary>Lowercase hex SHA-256 of the stored file's bytes.</summary>
    public string? Sha256 { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
