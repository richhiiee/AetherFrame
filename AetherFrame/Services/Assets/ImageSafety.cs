using System;
using System.Buffers.Binary;
using System.IO;

namespace AetherFrame.Services.Assets;

internal enum DetectedImageFormat
{
    Unknown,
    Png,
    Jpeg,
    WebP,
}

/// <summary>What an image file really is, read from its own bytes (never its extension).</summary>
internal sealed record ImageInspection(DetectedImageFormat Format, int Width, int Height, int FrameCount, long ByteLength)
{
    internal string MediaType => ImageSafety.MediaTypeOf(Format);

    /// <summary>The extension matching the actual content, e.g. ".png".</summary>
    internal string Extension => ImageSafety.ExtensionOf(Format);

    internal long PixelCount => (long)Width * Height;

    /// <summary>Memory one decoded frame needs as 32-bit RGBA (what the texture pipeline uploads).</summary>
    internal long EstimatedDecodedBytes => PixelCount * 4L;
}

/// <summary>
/// Hard limits for imported images and the header-only inspection that enforces them. Nothing
/// here decodes pixel data; every check reads at most a file's headers, so it's cheap enough to
/// run where imports already happen.
/// </summary>
internal static class ImageSafety
{
    /// <summary>
    /// 32 MiB. A Plate image is a portrait or background, not a photo archive; far larger files
    /// are almost always a mistake, and every byte is also copied into managed storage.
    /// </summary>
    internal const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>8192 px per side: well above any Plate canvas at 4x zoom, and within what every
    /// Direct3D 11 GPU the game runs on accepts as a texture dimension.</summary>
    internal const int MaxDimension = 8192;

    /// <summary>
    /// 32 megapixels (e.g. 8192 x 4096). Decoded as RGBA that is 128 MiB — the real memory
    /// cost of an image, which a small, highly compressed file can otherwise hide (a
    /// "decompression bomb").
    /// </summary>
    internal const long MaxPixelCount = 32L * 1024 * 1024;

    internal const long MaxDecodedBytes = MaxPixelCount * 4L;

    /// <summary>
    /// Only the first frame of an animated image is ever shown, but a declared frame count far
    /// beyond any legitimate animation marks a malformed or hostile file.
    /// </summary>
    internal const int MaxFrameCount = 1000;

    // Chunk-walk bounds: a well-formed header region is reached long before these.
    private const int MaxChunksWalked = 4096;

    internal static string MediaTypeOf(DetectedImageFormat format) => format switch
    {
        DetectedImageFormat.Png => "image/png",
        DetectedImageFormat.Jpeg => "image/jpeg",
        DetectedImageFormat.WebP => "image/webp",
        _ => "application/octet-stream",
    };

    internal static string ExtensionOf(DetectedImageFormat format) => format switch
    {
        DetectedImageFormat.Png => ".png",
        DetectedImageFormat.Jpeg => ".jpg",
        DetectedImageFormat.WebP => ".webp",
        _ => string.Empty,
    };

    /// <summary>Identifies a format from the first bytes of a file (its "magic number").</summary>
    internal static DetectedImageFormat Sniff(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return DetectedImageFormat.Png;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return DetectedImageFormat.Jpeg;
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return DetectedImageFormat.WebP;
        }

        return DetectedImageFormat.Unknown;
    }

    /// <summary>Inspects a file's headers, or returns null when it can't be read at all.</summary>
    internal static ImageInspection? Inspect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;

            Span<byte> header = stackalloc byte[32];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var format = Sniff(header[..read]);
            if (format == DetectedImageFormat.Unknown)
            {
                return new ImageInspection(DetectedImageFormat.Unknown, 0, 0, 0, length);
            }

            var dimensions = ImageDimensionReader.TryReadDimensions(path);
            var frames = format switch
            {
                DetectedImageFormat.Png => CountPngFrames(stream),
                DetectedImageFormat.WebP => CountWebPFrames(stream),
                _ => 1,
            };

            return new ImageInspection(format, dimensions?.Width ?? 0, dimensions?.Height ?? 0, frames, length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The player-facing reason an inspected image may not be imported, or null when it's within
    /// every limit. <paramref name="isDecoderSupported"/> takes an extension such as ".webp".
    /// </summary>
    internal static string? Validate(ImageInspection? inspection, Func<string, bool>? isDecoderSupported = null)
    {
        if (inspection is null)
        {
            return "The image file couldn't be read.";
        }

        if (inspection.Format == DetectedImageFormat.Unknown)
        {
            return "This file isn't a PNG, JPEG, or WebP image.";
        }

        if (isDecoderSupported is not null && !isDecoderSupported(inspection.Extension))
        {
            return $"{inspection.Extension.TrimStart('.').ToUpperInvariant()} images aren't supported on this system.";
        }

        if (inspection.ByteLength <= 0)
        {
            return "The image file is empty.";
        }

        if (inspection.ByteLength > MaxFileBytes)
        {
            return $"The image is too large ({inspection.ByteLength / (1024 * 1024)} MB). The limit is {MaxFileBytes / (1024 * 1024)} MB.";
        }

        if (inspection.Width <= 0 || inspection.Height <= 0)
        {
            return "The image's size couldn't be read; the file may be damaged.";
        }

        if (inspection.Width > MaxDimension || inspection.Height > MaxDimension)
        {
            return $"The image is {inspection.Width} x {inspection.Height} pixels. The limit is {MaxDimension} pixels per side.";
        }

        if (inspection.PixelCount > MaxPixelCount || inspection.EstimatedDecodedBytes > MaxDecodedBytes)
        {
            return $"The image has too many pixels ({inspection.PixelCount / 1_000_000.0:0.#} megapixels). The limit is {MaxPixelCount / 1_000_000.0:0.#} megapixels.";
        }

        if (inspection.FrameCount < 1 || inspection.FrameCount > MaxFrameCount)
        {
            return "The image's animation data is invalid.";
        }

        return null;
    }

    /// <summary>
    /// A stricter, still header-level check for files from an untrusted package: the file must be
    /// structurally whole — not truncated, not padded with an unrelated payload. PNG: a chunk walk
    /// from IHDR to IEND that stays inside the file and ends exactly at its end. JPEG: an
    /// end-of-image marker near the end (cameras may legitimately append small trailers). WebP:
    /// the RIFF size matches the file. Pixels are still never decoded (the game's decoder does that,
    /// within <see cref="Validate"/>'s limits). Null when whole, else the player-facing reason.
    /// </summary>
    internal static string? CheckStructure(string path, DetectedImageFormat format)
    {
        const string damaged = "The image file is incomplete or damaged.";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ok = format switch
            {
                DetectedImageFormat.Png => IsWholePng(stream),
                DetectedImageFormat.Jpeg => IsWholeJpeg(stream),
                DetectedImageFormat.WebP => IsWholeWebP(stream),
                _ => false,
            };

            return ok ? null : damaged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The image file couldn't be read.";
        }
    }

    private static bool IsWholePng(Stream stream)
    {
        Span<byte> chunkHeader = stackalloc byte[8];
        stream.Position = 8;
        for (var i = 0; i < MaxChunksWalked * 16; i++)
        {
            if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            var type = chunkHeader[4..8];
            if (i == 0 && !type.SequenceEqual("IHDR"u8))
            {
                return false;
            }

            foreach (var b in type)
            {
                if (!char.IsAsciiLetter((char)b))
                {
                    return false;
                }
            }

            var next = stream.Position + length + 4; // payload + CRC
            if (length > int.MaxValue || next > stream.Length)
            {
                return false;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return length == 0 && next == stream.Length;
            }

            stream.Position = next;
        }

        return false;
    }

    private static bool IsWholeJpeg(Stream stream)
    {
        const int trailerWindow = 1024 * 1024;
        var windowLength = (int)Math.Min(stream.Length, trailerWindow);
        var window = new byte[windowLength];
        stream.Position = stream.Length - windowLength;
        stream.ReadExactly(window);
        return window.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]) >= 0 && stream.Length > 4;
    }

    private static bool IsWholeWebP(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
        {
            return false;
        }

        var declared = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8;
        return stream.Length == declared || stream.Length == declared + 1;
    }

    /// <summary>APNG declares its frame count in an acTL chunk before the first IDAT.</summary>
    private static int CountPngFrames(Stream stream)
    {
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> actl = stackalloc byte[4];
        stream.Position = 8;

        for (var i = 0; i < MaxChunksWalked; i++)
        {
            if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8)
            {
                return 1;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            var type = chunkHeader[4..8];

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                return 1;
            }

            if (type.SequenceEqual("acTL"u8))
            {
                if (length < 4 || stream.ReadAtLeast(actl, 4, throwOnEndOfStream: false) < 4)
                {
                    return 0;
                }

                var frames = BinaryPrimitives.ReadUInt32BigEndian(actl);
                return frames > int.MaxValue ? int.MaxValue : (int)frames;
            }

            var next = stream.Position + length + 4; // payload + CRC
            if (length > int.MaxValue || next > stream.Length)
            {
                return 1;
            }

            stream.Position = next;
        }

        return 1;
    }

    /// <summary>An animated WebP (VP8X with the animation flag) holds one ANMF chunk per frame.</summary>
    private static int CountWebPFrames(Stream stream)
    {
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> flags = stackalloc byte[1];

        stream.Position = 12;
        if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8 || !chunkHeader[..4].SequenceEqual("VP8X"u8))
        {
            return 1;
        }

        if (stream.ReadAtLeast(flags, 1, throwOnEndOfStream: false) < 1 || (flags[0] & 0x02) == 0)
        {
            return 1;
        }

        var frames = 0;
        stream.Position = 12;
        for (var i = 0; i < MaxChunksWalked * 4; i++)
        {
            if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }

            if (chunkHeader[..4].SequenceEqual("ANMF"u8))
            {
                frames++;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            var next = stream.Position + size + (size & 1); // payloads are padded to even length
            if (next > stream.Length)
            {
                break;
            }

            stream.Position = next;
        }

        return Math.Max(1, frames);
    }
}
