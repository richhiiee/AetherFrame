using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Lazily generates and owns one small, seamlessly tileable GPU texture per
/// <see cref="ProfileBackgroundTexture"/> pattern — never a bundled bitmap, never a canvas-sized
/// texture, and never anything per frame.
///
/// Each tile is a pure MASK: white RGB with the pattern in alpha. Everything the user can edit
/// (colors, intensity, opacity, scale, rotation) is applied at draw time through the vertex tint
/// and quad geometry, so dragging any of those sliders never regenerates a texture. A pattern is
/// generated the first time a profile actually uses it and kept until the plugin unloads (at most
/// six 256x256 tiles, ~256 KB each).
/// </summary>
internal sealed class ProceduralTextureCache : IDisposable
{
    internal const int TileTexels = 256;

    /// <summary>Pattern periods across one tile, for the periodic patterns (dots, grid, lines).
    /// Also fixes a tile's logical size: <see cref="ProfileBackground.TextureScale"/> x this.</summary>
    internal const int PeriodsPerTile = 8;

    private const int Period = TileTexels / PeriodsPerTile;

    private readonly IDalamudTextureWrap?[] tiles = new IDalamudTextureWrap?[Enum.GetValues<ProfileBackgroundTexture>().Length];
    private readonly float[] averageCoverage = new float[Enum.GetValues<ProfileBackgroundTexture>().Length];
    private readonly bool[] failed = new bool[Enum.GetValues<ProfileBackgroundTexture>().Length];

    /// <summary>
    /// The tile for <paramref name="texture"/>, generating it on first use, or null for
    /// <see cref="ProfileBackgroundTexture.None"/> or if creation failed (logged once).
    /// </summary>
    internal IDalamudTextureWrap? GetTile(ProfileBackgroundTexture texture, out float meanCoverage)
    {
        var index = (int)texture;
        meanCoverage = 0f;

        if (texture == ProfileBackgroundTexture.None || index < 0 || index >= tiles.Length || failed[index])
        {
            return null;
        }

        if (tiles[index] is null)
        {
            try
            {
                var pixels = Generate(texture, out averageCoverage[index]);
                tiles[index] = DalamudServices.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(TileTexels, TileTexels), pixels, $"AetherFrame.Texture.{texture}");
            }
            catch (Exception ex)
            {
                failed[index] = true;
                DalamudServices.Log.Warning(ex, $"AetherFrame could not create the {texture} background texture.");
                return null;
            }
        }

        meanCoverage = averageCoverage[index];
        return tiles[index];
    }

    public void Dispose()
    {
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i]?.Dispose();
            tiles[i] = null;
        }
    }

    private static byte[] Generate(ProfileBackgroundTexture texture, out float meanCoverage)
    {
        var pixels = new byte[TileTexels * TileTexels * 4];
        double total = 0;

        for (var y = 0; y < TileTexels; y++)
        {
            for (var x = 0; x < TileTexels; x++)
            {
                var coverage = Math.Clamp(Sample(texture, x, y), 0f, 1f);
                total += coverage;

                var offset = ((y * TileTexels) + x) * 4;
                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = (byte)MathF.Round(coverage * 255f);
            }
        }

        meanCoverage = (float)(total / (TileTexels * TileTexels));
        return pixels;
    }

    private static float Sample(ProfileBackgroundTexture texture, int x, int y)
    {
        // Texel centers, so every pattern is symmetric and tiles seamlessly across tile edges
        // (every periodic pattern below has a period that divides TileTexels exactly).
        var px = x + 0.5f;
        var py = y + 0.5f;

        switch (texture)
        {
            case ProfileBackgroundTexture.FineNoise:
                // Independent per-texel grain: trivially seamless.
                return Hash(x, y, 17);

            case ProfileBackgroundTexture.Dots:
            {
                var cx = Wrap(px, Period) - (Period / 2f);
                var cy = Wrap(py, Period) - (Period / 2f);
                var distance = MathF.Sqrt((cx * cx) + (cy * cy));
                return Coverage(distance, Period * 0.2f);
            }

            case ProfileBackgroundTexture.Grid:
            {
                var dx = DistanceToLattice(px, Period);
                var dy = DistanceToLattice(py, Period);
                return Coverage(MathF.Min(dx, dy), Period * 0.045f);
            }

            case ProfileBackgroundTexture.DiagonalLines:
            {
                // Lines along x + y = k * Period (45 degrees), measured perpendicular to the line.
                var distance = DistanceToLattice(px + py, Period) / MathF.Sqrt(2f);
                return Coverage(distance, Period * 0.12f);
            }

            case ProfileBackgroundTexture.Crosshatch:
            {
                var a = DistanceToLattice(px + py, Period) / MathF.Sqrt(2f);
                var b = DistanceToLattice(px - py + TileTexels, Period) / MathF.Sqrt(2f);
                return Coverage(MathF.Min(a, b), Period * 0.05f);
            }

            case ProfileBackgroundTexture.SubtlePaper:
            {
                // Tileable fractal value noise (soft mottling) plus fine grain and faint horizontal
                // fibers — a quiet, paper-like surface rather than a visible pattern.
                var mottling = (ValueNoise(px, py, 8, 8, 3) * 0.5f) + (ValueNoise(px, py, 16, 16, 5) * 0.3f) + (ValueNoise(px, py, 32, 32, 7) * 0.2f);
                var grain = Hash(x, y, 29);

                // Few cells across, many down: long, thin horizontal streaks.
                var fiber = ValueNoise(px, py, 4, 64, 11);
                var value = (mottling * 0.55f) + (grain * 0.25f) + (fiber * 0.2f);
                return Math.Clamp((value - 0.3f) * 1.6f, 0f, 1f);
            }

            default:
                return 0f;
        }
    }

    /// <summary>Anti-aliased coverage of a shape whose edge is <paramref name="halfWidth"/> from its center line/point.</summary>
    private static float Coverage(float distance, float halfWidth) =>
        Math.Clamp(halfWidth + 0.5f - distance, 0f, 1f);

    private static float Wrap(float value, float period)
    {
        var wrapped = value % period;
        return wrapped < 0f ? wrapped + period : wrapped;
    }

    private static float DistanceToLattice(float value, float period)
    {
        var wrapped = Wrap(value, period);
        return MathF.Min(wrapped, period - wrapped);
    }

    /// <summary>Deterministic per-texel hash in [0, 1).</summary>
    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)((x * 374761393) + (y * 668265263) + (seed * 1274126177));
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>Smoothly interpolated lattice noise with the given cell counts across the tile,
    /// wrapping at the tile edge (so it tiles seamlessly, since both counts divide the tile).</summary>
    private static float ValueNoise(float px, float py, int cellsX, int cellsY, int seed)
    {
        var fx = Wrap(px, TileTexels) / (TileTexels / (float)cellsX);
        var fy = Wrap(py, TileTexels) / (TileTexels / (float)cellsY);

        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = SmoothStep(fx - x0);
        var ty = SmoothStep(fy - y0);

        var x1 = (x0 + 1) % cellsX;
        var y1 = (y0 + 1) % cellsY;
        x0 %= cellsX;
        y0 %= cellsY;

        var top = Lerp(Hash(x0, y0, seed), Hash(x1, y0, seed), tx);
        var bottom = Lerp(Hash(x0, y1, seed), Hash(x1, y1, seed), tx);
        return Lerp(top, bottom, ty);
    }

    private static float SmoothStep(float t) => t * t * (3f - (2f * t));

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
