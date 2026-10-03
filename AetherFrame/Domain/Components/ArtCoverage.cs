using System;

namespace AetherFrame.Domain.Components;

/// <summary>
/// Where an artwork is actually drawn, as a <see cref="GridSize"/> × <see cref="GridSize"/> grid over
/// its whole image, measured from its pixels by tools/art/measure_coverage.py
/// (<see cref="ArtCoverageData"/>). The editors hit test a Corner Ornament's artwork by it, so a
/// click on a clear part of its large box reaches what is underneath (issue #115).
/// </summary>
internal sealed class ArtCoverage(uint[] rows)
{
    internal const int GridSize = 32;

    /// <summary>The grid, one row per entry, bit x for column x.</summary>
    internal uint[] Rows { get; } = rows;

    /// <summary>The coverage of a bundled artwork, or null when it was never measured (it is then
    /// hit by its whole box).</summary>
    internal static ArtCoverage? For(BuiltInArtAsset art) =>
        ArtCoverageData.ByAssetPath.TryGetValue(art.AssetPath, out var coverage) ? coverage : null;

    /// <summary>Whether the drawing covers the point (<paramref name="u"/>, <paramref name="v"/>) of its
    /// image, each 0 to 1 from the image's top left as stored (before mirroring).</summary>
    internal bool Covers(float u, float v)
    {
        if (!(u >= 0f && u <= 1f && v >= 0f && v <= 1f) || Rows.Length != GridSize)
        {
            return false;
        }

        var x = Math.Min(GridSize - 1, (int)(u * GridSize));
        var y = Math.Min(GridSize - 1, (int)(v * GridSize));
        return (Rows[y] & (1u << x)) != 0;
    }
}
