using System;
using System.IO;

namespace AetherFrame.Services;

/// <summary>
/// Reads just the pixel width/height a supported image file declares in its own header, without
/// decoding pixels or touching Dalamud's (async, GPU-backed) texture pipeline — so a freshly
/// imported image's native aspect ratio is available synchronously, in time to size its new
/// <see cref="AetherFrame.Domain.Profiles.ImageProfileElement"/> correctly on the same frame
/// it's added. Supports exactly the formats <see cref="ImageFormatSupport"/> can import (PNG,
/// JPEG, WEBP); anything else (or a truncated/corrupt header) returns null, and the caller falls
/// back to its own default.
/// </summary>
internal static class ImageDimensionReader
{
    /// <summary>Reads the pixel width/height from a file's header, or null if the format isn't
    /// recognized or the header is malformed/truncated.</summary>
    internal static (int Width, int Height)? TryReadDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);

            Span<byte> header = stackalloc byte[32];
            var read = stream.Read(header);
            var peeked = read < header.Length ? header[..read] : header;

            if (TryReadPng(peeked, out var pngSize))
            {
                return pngSize;
            }

            if (TryReadWebP(peeked, out var webpSize))
            {
                return webpSize;
            }

            // JPEG has variable-length segments before its dimensions, so it walks the stream
            // itself rather than working from the fixed-size peek above.
            stream.Position = 0;
            return TryReadJpeg(stream, out var jpegSize) ? jpegSize : null;
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

    private static bool TryReadPng(ReadOnlySpan<byte> header, out (int Width, int Height) size)
    {
        size = default;

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (header.Length < 24 || !header[..8].SequenceEqual(signature))
        {
            return false;
        }

        // IHDR is always the very first chunk: 4-byte length, 4-byte type "IHDR", then width and
        // height as 4-byte big-endian integers.
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
        {
            return false;
        }

        var width = ReadUInt32BigEndian(header[16..20]);
        var height = ReadUInt32BigEndian(header[20..24]);
        if (width == 0 || height == 0)
        {
            return false;
        }

        size = ((int)width, (int)height);
        return true;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> header, out (int Width, int Height) size)
    {
        size = default;

        if (header.Length < 16
            || header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F'
            || header[8] != (byte)'W' || header[9] != (byte)'E' || header[10] != (byte)'B' || header[11] != (byte)'P')
        {
            return false;
        }

        var fourCc = header[12..16];

        // Each sub-format needs a different amount of the header past the common 16-byte
        // RIFF/WEBP/FourCC prefix checked above — checked per branch rather than one shared
        // minimum, since VP8L's payload is shorter than VP8X/VP8's.
        if (fourCc.SequenceEqual("VP8X"u8) && header.Length >= 30)
        {
            // Extended format: 1 byte flags, 3 bytes reserved, then 24-bit little-endian
            // (width - 1) and (height - 1).
            var w = 1 + (header[24] | (header[25] << 8) | (header[26] << 16));
            var h = 1 + (header[27] | (header[28] << 8) | (header[29] << 16));
            size = (w, h);
            return true;
        }

        if (fourCc.SequenceEqual("VP8 "u8) && header.Length >= 30)
        {
            // Lossy: 3-byte frame tag, then a 3-byte sync code (0x9D 0x01 0x2A), then 14-bit
            // little-endian width/height (the top 2 bits of each word are an unrelated scale
            // factor, masked off).
            if (header[23] != 0x9D || header[24] != 0x01 || header[25] != 0x2A)
            {
                return false;
            }

            var w = (header[26] | (header[27] << 8)) & 0x3FFF;
            var h = (header[28] | (header[29] << 8)) & 0x3FFF;
            size = (w, h);
            return w > 0 && h > 0;
        }

        if (fourCc.SequenceEqual("VP8L"u8) && header.Length >= 25)
        {
            // Lossless: 1 signature byte (0x2F), then a little-endian bitstream packing a 14-bit
            // (width - 1) followed by a 14-bit (height - 1).
            if (header[20] != 0x2F)
            {
                return false;
            }

            var bits = header[21] | (header[22] << 8) | (header[23] << 16) | (header[24] << 24);
            var w = 1 + (bits & 0x3FFF);
            var h = 1 + ((bits >> 14) & 0x3FFF);
            size = (w, h);
            return true;
        }

        return false;
    }

    private static bool TryReadJpeg(Stream stream, out (int Width, int Height) size)
    {
        size = default;

        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
        {
            return false; // Missing SOI marker: not a JPEG.
        }

        while (true)
        {
            // Find the next marker: a 0xFF byte followed by a non-0xFF byte. Extra 0xFF padding
            // bytes between markers are legal and just skipped over.
            int b;
            do
            {
                b = stream.ReadByte();
                if (b < 0)
                {
                    return false;
                }
            }
            while (b != 0xFF);

            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0)
                {
                    return false;
                }
            }
            while (marker == 0xFF);

            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))
            {
                // Byte-stuffing / restart markers: no length field, no payload.
                continue;
            }

            if (marker == 0xD9) // EOI, reached without ever finding a SOF segment.
            {
                return false;
            }

            var lengthHi = stream.ReadByte();
            var lengthLo = stream.ReadByte();
            if (lengthHi < 0 || lengthLo < 0)
            {
                return false;
            }

            var segmentLength = (lengthHi << 8) | lengthLo;
            if (segmentLength < 2)
            {
                return false;
            }

            // SOF0-SOF15 carry the image dimensions, except DHT (0xC4), JPG (0xC8), and DAC
            // (0xCC), which reuse the same numeric range for unrelated segment types.
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

            if (isStartOfFrame)
            {
                var precision = stream.ReadByte();
                var heightHi = stream.ReadByte();
                var heightLo = stream.ReadByte();
                var widthHi = stream.ReadByte();
                var widthLo = stream.ReadByte();
                if (precision < 0 || heightHi < 0 || heightLo < 0 || widthHi < 0 || widthLo < 0)
                {
                    return false;
                }

                var height = (heightHi << 8) | heightLo;
                var width = (widthHi << 8) | widthLo;
                if (width <= 0 || height <= 0)
                {
                    return false;
                }

                size = (width, height);
                return true;
            }

            var remaining = segmentLength - 2;
            if (remaining > 0)
            {
                stream.Position += remaining;
            }
        }
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
}
