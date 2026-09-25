using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using AetherFrame.Domain.Components;

namespace AetherFrame.Domain.Rendering;

/// <summary>One RGBA level of bundled artwork: <see cref="Width"/> x <see cref="Height"/> texels, straight (non-premultiplied) alpha.</summary>
public sealed record ArtLevel(int Width, int Height, byte[] Rgba)
{
    /// <summary>The longer side, in texels: what level selection compares with the on-screen size.</summary>
    public int LongSide => Math.Max(Width, Height);
}

/// <summary>
/// Decodes AetherFrame's own bundled artwork PNGs and builds their downsampled levels. Pure logic
/// (no Dalamud), so the bundled files and the level math are unit tested.
///
/// <para><b>Why levels.</b> Dalamud textures have no mipmaps and ImGui samples them bilinearly, so
/// one large texture drawn far smaller than its size skips texels and breaks up fine lines (a
/// Corner Ornament is usually drawn at a fraction of its runtime size). Building a few halved
/// levels once, and drawing the smallest level at least as large as the on-screen size (see
/// <see cref="SelectLevel"/>), keeps thin lines continuous at every size from one bundled PNG.</para>
///
/// <para>The decoder only accepts what the bundled art is required to be — 8-bit RGBA or RGB,
/// non-interlaced, at most <see cref="MaxSize"/> on either side — and rejects anything else instead
/// of guessing. Square power-of-two art (tinted line art) halves exactly at every level; other sizes
/// (full-color artwork kept at its approved resolution) round each level up. It is never used for
/// user images (those go through Dalamud's decoders and <c>ImageSafety</c>).</para>
/// </summary>
public static class BundledArtImage
{
    /// <summary>Largest bundled art dimension accepted, on either side.</summary>
    public const int MaxSize = 4096;

    /// <summary>Smallest level built, on the shorter side (below this, a mark is a few pixels anyway).</summary>
    public const int MinLevelSize = 32;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Everything the texture cache uploads for <paramref name="art"/>, from its bundled PNG: the
    /// decoded image (which must be exactly the catalog's size) and its levels, with transparent
    /// edges prepared for the way the art is drawn (see <see cref="BleedIntoTransparentTexels"/>).
    /// Throws <see cref="InvalidDataException"/> when the PNG isn't what the catalog says.
    /// </summary>
    public static IReadOnlyList<ArtLevel> LoadLevels(ReadOnlySpan<byte> png, BuiltInArtAsset art)
    {
        var top = DecodePng(png);
        if (top.Width != art.PixelWidth || top.Height != art.PixelHeight)
        {
            throw new InvalidDataException($"expected {art.PixelWidth}x{art.PixelHeight}px, found {top.Width}x{top.Height}px");
        }

        var levels = BuildLevels(top);
        if (!art.Tintable)
        {
            foreach (var level in levels)
            {
                BleedIntoTransparentTexels(level);
            }
        }

        return levels;
    }

    /// <summary>Decodes an 8-bit RGBA or RGB, non-interlaced PNG of at most <see cref="MaxSize"/> per side
    /// into RGBA (RGB becomes fully opaque). Throws <see cref="InvalidDataException"/> otherwise.</summary>
    public static ArtLevel DecodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }

        var width = 0;
        var height = 0;
        var channels = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        var position = Signature.Length;

        while (!sawEnd)
        {
            if (png.Length - position < 12)
            {
                throw new InvalidDataException("truncated chunk");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(png[position..]);
            if (length < 0 || length > png.Length - position - 12)
            {
                throw new InvalidDataException("bad chunk length");
            }

            var type = png.Slice(position + 4, 4);
            var data = png.Slice(position + 8, length);
            position += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13)
                {
                    throw new InvalidDataException("bad header");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                var bitDepth = data[8];
                channels = data[9] switch
                {
                    6 => 4, // RGBA
                    2 => 3, // RGB
                    _ => 0,
                };
                if (bitDepth != 8 || channels == 0 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new InvalidDataException("bundled art must be 8-bit RGBA or RGB, non-interlaced");
                }

                if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
                {
                    throw new InvalidDataException($"bundled art must be 1 to {MaxSize} pixels on each side");
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader)
                {
                    throw new InvalidDataException("image data before header");
                }

                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
            }
        }

        if (!sawHeader)
        {
            throw new InvalidDataException("no header");
        }

        var bytesPerPixel = channels;
        var stride = width * bytesPerPixel;
        var raw = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            inflater.ReadExactly(raw);
            if (inflater.ReadByte() != -1)
            {
                throw new InvalidDataException("excess image data");
            }
        }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = raw.AsSpan((y * (stride + 1)) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var previous = y == 0 ? Span<byte>.Empty : pixels.AsSpan((y - 1) * stride, stride);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = y > 0 ? previous[i] : 0;
                int upLeft = y > 0 && i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                row[i] = (byte)(source[i] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("bad row filter"),
                });
            }
        }

        return new ArtLevel(width, height, bytesPerPixel == 4 ? pixels : ExpandRgb(pixels, width * height));
    }

    /// <summary>
    /// <paramref name="top"/> followed by successively halved levels, until the shorter side reaches
    /// <see cref="MinLevelSize"/> (an odd side rounds up, its last texel counted twice).
    /// Each texel averages its 2x2 source texels weighted by alpha (premultiplied), so soft glows keep
    /// their brightness and never pick up the color of fully transparent texels; a texel with no
    /// coverage at all is stored as transparent white, so bilinear filtering never darkens a tint.
    /// </summary>
    public static IReadOnlyList<ArtLevel> BuildLevels(ArtLevel top)
    {
        var levels = new List<ArtLevel> { top };
        var current = top;
        while (Math.Min(current.Width, current.Height) / 2 >= MinLevelSize)
        {
            var width = (current.Width + 1) / 2;
            var height = (current.Height + 1) / 2;
            var source = current.Rgba;
            var sourceStride = current.Width * 4;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var sy = Math.Min((y * 2) + dy, current.Height - 1);
                            var sx = Math.Min((x * 2) + dx, current.Width - 1);
                            var s = (sy * sourceStride) + (sx * 4);
                            var alpha = source[s + 3];
                            r += source[s] * alpha;
                            g += source[s + 1] * alpha;
                            b += source[s + 2] * alpha;
                            a += alpha;
                        }
                    }

                    var d = ((y * width) + x) * 4;
                    if ((a + 2) / 4 == 0)
                    {
                        pixels[d] = pixels[d + 1] = pixels[d + 2] = 255;
                        pixels[d + 3] = 0;
                        continue;
                    }

                    pixels[d] = (byte)((r + (a / 2)) / a);
                    pixels[d + 1] = (byte)((g + (a / 2)) / a);
                    pixels[d + 2] = (byte)((b + (a / 2)) / a);
                    pixels[d + 3] = (byte)((a + 2) / 4);
                }
            }

            current = new ArtLevel(width, height, pixels);
            levels.Add(current);
        }

        return levels;
    }

    /// <summary>
    /// For artwork drawn in its own colors: gives every fully transparent texel next to drawn texels
    /// the alpha-weighted color of those neighbors (in place; no alpha changes, so nothing visible
    /// moves). Those texels are invisible themselves, but bilinear filtering blends their color into
    /// the artwork's edges: black (as the art is exported) would outline every stroke dark, white a
    /// light halo. Tinted art keeps its white instead (see <see cref="BuildLevels"/>).
    /// </summary>
    public static void BleedIntoTransparentTexels(ArtLevel level)
    {
        var width = level.Width;
        var height = level.Height;
        var pixels = level.Rgba;
        var source = (byte[])pixels.Clone();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = ((y * width) + x) * 4;
                if (source[d + 3] != 0)
                {
                    continue;
                }

                int r = 0, g = 0, b = 0, a = 0;
                for (var ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                {
                    for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                    {
                        var s = ((ny * width) + nx) * 4;
                        var alpha = source[s + 3];
                        r += source[s] * alpha;
                        g += source[s + 1] * alpha;
                        b += source[s + 2] * alpha;
                        a += alpha;
                    }
                }

                if (a > 0)
                {
                    pixels[d] = (byte)((r + (a / 2)) / a);
                    pixels[d + 1] = (byte)((g + (a / 2)) / a);
                    pixels[d + 2] = (byte)((b + (a / 2)) / a);
                }
            }
        }
    }

    /// <summary>
    /// Index into <paramref name="levelSizes"/> (each level's <see cref="ArtLevel.LongSide"/>, largest
    /// first, as <see cref="BuildLevels"/> returns them) of the smallest level whose longer side is
    /// still at least <paramref name="screenPixels"/> (the drawn longer side), so the
    /// texture is never magnified unless even the largest level is too small, and never minified by
    /// more than 2x.
    /// </summary>
    public static int SelectLevel(ReadOnlySpan<int> levelSizes, float screenPixels)
    {
        var chosen = 0;
        for (var i = 1; i < levelSizes.Length; i++)
        {
            if (!(levelSizes[i] >= screenPixels))
            {
                break;
            }

            chosen = i;
        }

        return chosen;
    }

    /// <summary>RGB texels as fully opaque RGBA.</summary>
    private static byte[] ExpandRgb(byte[] rgb, int texels)
    {
        var rgba = new byte[texels * 4];
        for (var i = 0; i < texels; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[(i * 4) + 1] = rgb[(i * 3) + 1];
            rgba[(i * 4) + 2] = rgb[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        return rgba;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
