using System;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Lazily generates and owns one small, seamlessly tileable GPU texture per
/// <see cref="ProfileBackgroundTexture"/> pattern — never a bundled bitmap, never a canvas-sized
/// texture, and never anything per frame.
///
/// Each tile is a pure MASK: white RGB with the pattern in alpha, sampled texel by texel from
/// <see cref="ProceduralPatternMath"/> (kept Dalamud-free so its tiling math can be unit tested).
/// Everything the user can edit (colors, intensity, opacity, scale, rotation) is applied at draw
/// time through the vertex tint and quad geometry, so dragging any of those sliders never
/// regenerates a texture. A pattern is generated the first time a profile actually uses it and kept
/// until the plugin unloads (at most a handful of 256x256 tiles, ~256 KB each).
/// </summary>
internal sealed class ProceduralTextureCache : IDisposable
{
    internal const int TileTexels = ProceduralPatternMath.TileTexels;

    /// <summary>Pattern periods across one tile, for the periodic patterns (dots, grid, lines).
    /// Also fixes a tile's logical size: <see cref="ProfileBackground.TextureScale"/> x this.</summary>
    internal const int PeriodsPerTile = ProceduralPatternMath.PeriodsPerTile;

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
                var coverage = Math.Clamp(ProceduralPatternMath.Sample(texture, x, y), 0f, 1f);
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
}
