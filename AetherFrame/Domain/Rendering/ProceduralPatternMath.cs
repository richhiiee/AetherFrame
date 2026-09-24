using System;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Rendering;

/// <summary>
/// Pure per-texel sampling for every <see cref="ProfileBackgroundTexture"/> pattern: given a texel
/// coordinate within one tile, returns its alpha coverage in [0, 1]. Kept Dalamud-free (unlike its
/// only caller, <c>AetherFrame.UI.Rendering.ProceduralTextureCache</c>, which turns a sampled tile
/// into a GPU texture) so the seamless-tiling and range invariants every pattern depends on can be
/// unit tested directly.
/// </summary>
internal static class ProceduralPatternMath
{
    internal const int TileTexels = 256;

    /// <summary>Pattern periods across one tile, for the periodic patterns (dots, grid, lines).
    /// Also fixes a tile's logical size: <see cref="ProfileBackground.TextureScale"/> x this.</summary>
    internal const int PeriodsPerTile = 8;

    internal const int Period = TileTexels / PeriodsPerTile;

    /// <summary>The coverage (alpha, in [0, 1]) of <paramref name="texture"/> at texel (x, y) within one tile.</summary>
    internal static float Sample(ProfileBackgroundTexture texture, int x, int y)
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

            case ProfileBackgroundTexture.Checkerboard:
            {
                var cx = (int)MathF.Floor(px / Period);
                var cy = (int)MathF.Floor(py / Period);
                return (cx + cy) % 2 == 0 ? 1f : 0f;
            }

            case ProfileBackgroundTexture.Stripes:
            {
                var cy = (int)MathF.Floor(py / Period);
                return cy % 2 == 0 ? 1f : 0f;
            }

            case ProfileBackgroundTexture.Waves:
            {
                // A sinusoidal band: an integer number of cycles across the tile keeps the phase
                // (and its derivative) matching at x=0 and x=TileTexels, so it tiles seamlessly.
                const int cycles = 4;
                var wave = MathF.Sin(px / TileTexels * cycles * MathF.Tau) * (Period * 0.35f);
                var distance = DistanceToLattice(py + wave, Period);
                return Coverage(distance, Period * 0.16f);
            }

            case ProfileBackgroundTexture.Herringbone:
            {
                // A brick of blocks (each two periods square, dividing the tile evenly) alternating
                // which diagonal direction its lines run — the alternation itself draws the zigzag.
                var blockSize = Period * 2f;
                var bx = (int)MathF.Floor(px / blockSize);
                var by = (int)MathF.Floor(py / blockSize);
                var value = (bx + by) % 2 == 0 ? px + py : px - py + TileTexels;
                var distance = DistanceToLattice(value, Period) / MathF.Sqrt(2f);
                return Coverage(distance, Period * 0.11f);
            }

            case ProfileBackgroundTexture.Honeycomb:
            {
                // An offset grid of hexagon outlines: a Chebyshev-style hexagon metric evaluated
                // relative to each cell's center, with alternating rows offset by half a cell.
                var spacing = Period;
                var row = (int)MathF.Floor(py / spacing);
                var rowParity = ((row % 2) + 2) % 2;
                var offsetX = rowParity == 1 ? spacing / 2f : 0f;
                var cx = Wrap(px - offsetX, spacing) - (spacing / 2f);
                var cy = Wrap(py, spacing) - (spacing / 2f);
                var metric = MathF.Max(MathF.Abs(cy), MathF.Max(MathF.Abs((0.5f * cy) + (0.8660254f * cx)), MathF.Abs((0.5f * cy) - (0.8660254f * cx))));
                var hexRadius = spacing * 0.4f;
                return Coverage(MathF.Abs(metric - hexRadius), Period * 0.045f);
            }

            case ProfileBackgroundTexture.Scales:
            {
                // Overlapping filled arcs in offset brick rows — each row's arcs are centered on
                // its own top edge, so they read as shingles/fish-scales spilling into the row above.
                var spacingX = Period;
                var spacingY = Period / 2f;
                var row = (int)MathF.Floor(py / spacingY);
                var rowParity = ((row % 2) + 2) % 2;
                var offsetX = rowParity == 1 ? spacingX / 2f : 0f;
                var cx = Wrap(px - offsetX, spacingX) - (spacingX / 2f);
                var cy = Wrap(py, spacingY);
                var distance = MathF.Sqrt((cx * cx) + (cy * cy));
                return Coverage(distance, spacingX * 0.32f);
            }

            case ProfileBackgroundTexture.Speckle:
            {
                // Organic scattered dots of varying size and offset (cellular/Worley-style): each
                // cell owns one jittered point, found via its own and its eight tiled neighbors.
                var cellSize = Period;
                var cellsAcross = (int)(TileTexels / cellSize);
                var cx0 = (int)MathF.Floor(px / cellSize);
                var cy0 = (int)MathF.Floor(py / cellSize);
                var coverage = 0f;

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var ccx = cx0 + dx;
                        var ccy = cy0 + dy;
                        var hcx = ((ccx % cellsAcross) + cellsAcross) % cellsAcross;
                        var hcy = ((ccy % cellsAcross) + cellsAcross) % cellsAcross;

                        var jitterX = (Hash(hcx, hcy, 31) * cellSize * 0.7f) + (cellSize * 0.15f);
                        var jitterY = (Hash(hcx, hcy, 37) * cellSize * 0.7f) + (cellSize * 0.15f);
                        var radius = (Hash(hcx, hcy, 41) * 0.35f + 0.15f) * cellSize * 0.5f;

                        var centerX = (ccx * cellSize) + jitterX;
                        var centerY = (ccy * cellSize) + jitterY;
                        var distance = MathF.Sqrt(((px - centerX) * (px - centerX)) + ((py - centerY) * (py - centerY)));
                        coverage = MathF.Max(coverage, Coverage(distance, radius));
                    }
                }

                return coverage;
            }

            case ProfileBackgroundTexture.Diamonds:
            {
                // An L1 (Manhattan) distance from each lattice point naturally forms a diamond;
                // an outline (not filled) reads as a lattice rather than a solid rhombus tiling.
                var cx = Wrap(px, Period) - (Period / 2f);
                var cy = Wrap(py, Period) - (Period / 2f);
                var metric = MathF.Abs(cx) + MathF.Abs(cy);
                return Coverage(MathF.Abs(metric - (Period * 0.3f)), Period * 0.05f);
            }

            case ProfileBackgroundTexture.Chevron:
            {
                // A zigzag ribbon: a triangle wave in x sets the "V" path's vertical offset, then
                // the usual periodic-line coverage follows that offset path instead of a flat one.
                var chevronPeriod = Period * 2f;
                var t = Wrap(px, chevronPeriod) / chevronPeriod;
                var triangle = t < 0.5f ? t * 2f : 2f - (t * 2f);
                var zigzagY = triangle * Period;
                var distance = DistanceToLattice(py - zigzagY, Period);
                return Coverage(distance, Period * 0.12f);
            }

            case ProfileBackgroundTexture.Sparkle:
            {
                // A small 4-pointed star per lattice point: the minimum of two skewed diamond
                // metrics (one favoring the vertical axis, one the horizontal) pinches into points.
                var cx = Wrap(px, Period) - (Period / 2f);
                var cy = Wrap(py, Period) - (Period / 2f);
                var arm1 = MathF.Abs(cx) + (MathF.Abs(cy) * 0.35f);
                var arm2 = MathF.Abs(cy) + (MathF.Abs(cx) * 0.35f);
                return Coverage(MathF.Min(arm1, arm2), Period * 0.12f);
            }

            case ProfileBackgroundTexture.Linen:
            {
                // A plain-weave fabric look: straight horizontal and vertical thread bands only (no
                // diagonals, bends, or rotation of any kind), with which band reads as "on top"
                // alternating per grid cell — the same over/under illusion a woven textile has.
                var threadHalfWidth = Period * 0.18f;
                var horizontalCoverage = Coverage(DistanceToLattice(py, Period), threadHalfWidth);
                var verticalCoverage = Coverage(DistanceToLattice(px, Period), threadHalfWidth);

                var cellX = (int)MathF.Floor(px / Period);
                var cellY = (int)MathF.Floor(py / Period);
                var horizontalOnTop = (cellX + cellY) % 2 == 0;

                return horizontalOnTop
                    ? MathF.Max(horizontalCoverage, verticalCoverage * 0.3f)
                    : MathF.Max(verticalCoverage, horizontalCoverage * 0.3f);
            }

            case ProfileBackgroundTexture.Ripples:
            {
                // Concentric ring outlines around each lattice point, isotropic (no rotation control)
                // since a ring looks identical at every angle.
                var cx = Wrap(px, Period) - (Period / 2f);
                var cy = Wrap(py, Period) - (Period / 2f);
                var distance = MathF.Sqrt((cx * cx) + (cy * cy));
                var ringSpacing = Period * 0.22f;
                var ringPhase = distance % ringSpacing;
                var ringDistance = MathF.Min(ringPhase, ringSpacing - ringPhase);
                return Coverage(ringDistance, Period * 0.04f);
            }

            case ProfileBackgroundTexture.Quatrefoil:
            {
                // A four-lobed clover/medallion: the nearest of four circles offset from the lattice
                // point along each axis, unioned by taking the minimum distance.
                var cx = Wrap(px, Period) - (Period / 2f);
                var cy = Wrap(py, Period) - (Period / 2f);
                var lobeOffset = Period * 0.22f;
                var lobeRadius = Period * 0.26f;
                var d1 = MathF.Sqrt(((cx - lobeOffset) * (cx - lobeOffset)) + (cy * cy));
                var d2 = MathF.Sqrt(((cx + lobeOffset) * (cx + lobeOffset)) + (cy * cy));
                var d3 = MathF.Sqrt((cx * cx) + ((cy - lobeOffset) * (cy - lobeOffset)));
                var d4 = MathF.Sqrt((cx * cx) + ((cy + lobeOffset) * (cy + lobeOffset)));
                var distance = MathF.Min(MathF.Min(d1, d2), MathF.Min(d3, d4));
                return Coverage(distance, lobeRadius);
            }

            case ProfileBackgroundTexture.Brick:
            {
                // Running-bond brick outlines: rows twice as wide as tall, alternating rows offset by
                // half a brick — the same offset-row technique as Honeycomb and Scales.
                var brickWidth = Period * 2f;
                var brickHeight = Period;
                var row = (int)MathF.Floor(py / brickHeight);
                var rowParity = ((row % 2) + 2) % 2;
                var offsetX = rowParity == 1 ? brickWidth / 2f : 0f;
                var localX = Wrap(px - offsetX, brickWidth);
                var localY = Wrap(py, brickHeight);
                var distX = MathF.Min(localX, brickWidth - localX);
                var distY = MathF.Min(localY, brickHeight - localY);
                return Coverage(MathF.Min(distX, distY), Period * 0.05f);
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
