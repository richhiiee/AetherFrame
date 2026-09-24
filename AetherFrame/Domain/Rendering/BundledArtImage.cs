using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AetherFrame.Domain.Rendering;

/// <summary>One RGBA level of bundled artwork: <see cref="Size"/> x <see cref="Size"/> texels, straight (non-premultiplied) alpha.</summary>
public sealed record ArtLevel(int Size, byte[] Rgba);

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
/// <para>The decoder only accepts what the bundled art is required to be — square, power-of-two,
/// 8-bit RGBA, non-interlaced — and rejects anything else instead of guessing. It is never used for
/// user images (those go through Dalamud's decoders and <c>ImageSafety</c>).</para>
/// </summary>
public static class BundledArtImage
{
    /// <summary>Largest bundled art dimension accepted.</summary>
    public const int MaxSize = 2048;

    /// <summary>Smallest level built (below this, a corner mark is a few pixels anyway).</summary>
    public const int MinLevelSize = 32;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes a square, power-of-two, 8-bit RGBA, non-interlaced PNG. Throws <see cref="InvalidDataException"/> otherwise.</summary>
    public static ArtLevel DecodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }

        var width = 0;
        var height = 0;
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
                var colorType = data[9];
                if (bitDepth != 8 || colorType != 6 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new InvalidDataException("bundled art must be 8-bit RGBA, non-interlaced");
                }

                if (width != height || width < 1 || width > MaxSize || (width & (width - 1)) != 0)
                {
                    throw new InvalidDataException("bundled art must be square with a power-of-two size");
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

        const int bytesPerPixel = 4;
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

        return new ArtLevel(width, pixels);
    }

    /// <summary>
    /// <paramref name="top"/> followed by successively halved levels down to <see cref="MinLevelSize"/>.
    /// Each texel averages its 2x2 source texels weighted by alpha (premultiplied), so soft glows keep
    /// their brightness and never pick up the color of fully transparent texels; a texel with no
    /// coverage at all is stored as transparent white, so bilinear filtering never darkens a tint.
    /// </summary>
    public static IReadOnlyList<ArtLevel> BuildLevels(ArtLevel top)
    {
        var levels = new List<ArtLevel> { top };
        var current = top;
        while (current.Size / 2 >= MinLevelSize)
        {
            var size = current.Size / 2;
            var source = current.Rgba;
            var sourceStride = current.Size * 4;
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var s = (((y * 2) + dy) * sourceStride) + (((x * 2) + dx) * 4);
                            var alpha = source[s + 3];
                            r += source[s] * alpha;
                            g += source[s + 1] * alpha;
                            b += source[s + 2] * alpha;
                            a += alpha;
                        }
                    }

                    var d = ((y * size) + x) * 4;
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

            current = new ArtLevel(size, pixels);
            levels.Add(current);
        }

        return levels;
    }

    /// <summary>
    /// Index into <paramref name="levelSizes"/> (largest first, as <see cref="BuildLevels"/> returns
    /// them) of the smallest level still at least <paramref name="screenPixels"/> across, so the
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

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
