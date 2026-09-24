using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Every <see cref="ProfileBackgroundTexture"/> pattern is sampled once into a 256x256 tile and then
/// repeated across the whole Plate by the GPU's texture sampler (see
/// <c>AetherFrame.UI.Rendering.ProceduralTextureCache</c>) — so a pattern whose math doesn't
/// genuinely repeat with a period dividing the tile size shows a visible seam in game, something no
/// amount of code review reliably catches. These tests assert the two invariants every periodic
/// pattern depends on: its coverage always stays a valid alpha in [0, 1], and shifting by a whole
/// tile in either direction reproduces the exact same coverage.
/// </summary>
public class ProceduralPatternMathTests
{
    // FineNoise and SubtlePaper are intentionally NOT exactly periodic: they mix in a raw per-texel
    // hash (unbounded, no wraparound) for fine grain. That's still seamless in practice because pure
    // noise has no low-frequency structure to visibly break at a tile edge, but it fails an exact
    // shift-by-one-tile equality check, so both are excluded from the periodicity assertion below.
    private static readonly ProfileBackgroundTexture[] PeriodicPatterns =
    [
        ProfileBackgroundTexture.Dots, ProfileBackgroundTexture.Grid, ProfileBackgroundTexture.DiagonalLines,
        ProfileBackgroundTexture.Crosshatch, ProfileBackgroundTexture.Checkerboard, ProfileBackgroundTexture.Stripes,
        ProfileBackgroundTexture.Waves, ProfileBackgroundTexture.Herringbone, ProfileBackgroundTexture.Honeycomb,
        ProfileBackgroundTexture.Scales, ProfileBackgroundTexture.Speckle, ProfileBackgroundTexture.Diamonds,
        ProfileBackgroundTexture.Chevron, ProfileBackgroundTexture.Sparkle, ProfileBackgroundTexture.Linen,
        ProfileBackgroundTexture.Ripples, ProfileBackgroundTexture.Quatrefoil, ProfileBackgroundTexture.Brick,
    ];

    private static readonly ProfileBackgroundTexture[] AllRealPatterns =
        Enum.GetValues<ProfileBackgroundTexture>().Where(t => t != ProfileBackgroundTexture.None).ToArray();

    public static IEnumerable<object[]> AllRealPatternsData() => AllRealPatterns.Select(t => new object[] { t });

    public static IEnumerable<object[]> PeriodicPatternsData() => PeriodicPatterns.Select(t => new object[] { t });

    [Fact]
    public void EveryPatternValue_IsCoveredByEitherList()
    {
        // A pattern added to the enum but forgotten in both lists above would silently skip both
        // invariant checks below — this keeps that impossible.
        Assert.Equal(AllRealPatterns.OrderBy(t => t), PeriodicPatterns.Concat([ProfileBackgroundTexture.FineNoise, ProfileBackgroundTexture.SubtlePaper]).OrderBy(t => t));
    }

    [Theory]
    [MemberData(nameof(AllRealPatternsData))]
    public void Coverage_StaysWithinZeroToOne_AcrossTheWholeTile(ProfileBackgroundTexture texture)
    {
        for (var y = 0; y < ProceduralPatternMath.TileTexels; y += 3)
        {
            for (var x = 0; x < ProceduralPatternMath.TileTexels; x += 3)
            {
                var coverage = ProceduralPatternMath.Sample(texture, x, y);
                Assert.True(coverage is >= 0f and <= 1f, $"{texture} at ({x},{y}) produced {coverage}, outside [0, 1].");
            }
        }
    }

    [Theory]
    [MemberData(nameof(PeriodicPatternsData))]
    public void TilesSeamlessly_HorizontallyAndVertically(ProfileBackgroundTexture texture)
    {
        const float tolerance = 0.002f;
        const int tile = ProceduralPatternMath.TileTexels;

        for (var y = 0; y < tile; y += 5)
        {
            for (var x = 0; x < tile; x += 5)
            {
                var here = ProceduralPatternMath.Sample(texture, x, y);
                var right = ProceduralPatternMath.Sample(texture, x + tile, y);
                var down = ProceduralPatternMath.Sample(texture, x, y + tile);

                Assert.True(MathF.Abs(here - right) <= tolerance,
                    $"{texture} at ({x},{y})={here} does not match one tile to the right ({x + tile},{y})={right}: a horizontal seam.");
                Assert.True(MathF.Abs(here - down) <= tolerance,
                    $"{texture} at ({x},{y})={here} does not match one tile down ({x},{y + tile})={down}: a vertical seam.");
            }
        }
    }

    // The subset of periodic patterns with a visible "direction" a rotation slider actually changes.
    // Speckle and Ripples are periodic (tested above) but isotropic — a random scatter or a set of
    // concentric rings looks identical at every angle — so rotating them is a no-op like FineNoise or
    // SubtlePaper, and SupportsRotation correctly leaves all four out.
    private static readonly ProfileBackgroundTexture[] DirectionalPatterns =
        PeriodicPatterns.Where(t => t is not (ProfileBackgroundTexture.Speckle or ProfileBackgroundTexture.Ripples)).ToArray();

    [Fact]
    public void SupportsRotation_MatchesEveryDirectionalPattern()
    {
        var directional = new HashSet<ProfileBackgroundTexture>(DirectionalPatterns);
        foreach (var texture in AllRealPatterns)
        {
            Assert.Equal(directional.Contains(texture), ProfileBackground.SupportsRotation(texture));
        }
    }

    // ---------------------------------------------------------------- stable ids (task: verify/test)

    /// <summary>
    /// Patterns are persisted as this enum's raw numeric value (see the type's own doc comment:
    /// "Persisted as its numeric value; append only"). That already IS a stable identifier scheme —
    /// this pins every current value so a future edit that reorders or renumbers a member (breaking
    /// every Plate that saved the old number) fails a test immediately instead of silently shipping.
    /// </summary>
    [Fact]
    public void EveryPatternId_IsPinnedToItsShippedNumber()
    {
        Assert.Equal(0, (int)ProfileBackgroundTexture.None);
        Assert.Equal(1, (int)ProfileBackgroundTexture.FineNoise);
        Assert.Equal(2, (int)ProfileBackgroundTexture.Dots);
        Assert.Equal(3, (int)ProfileBackgroundTexture.Grid);
        Assert.Equal(4, (int)ProfileBackgroundTexture.DiagonalLines);
        Assert.Equal(5, (int)ProfileBackgroundTexture.Crosshatch);
        Assert.Equal(6, (int)ProfileBackgroundTexture.SubtlePaper);
        Assert.Equal(7, (int)ProfileBackgroundTexture.Checkerboard);
        Assert.Equal(8, (int)ProfileBackgroundTexture.Stripes);
        Assert.Equal(9, (int)ProfileBackgroundTexture.Waves);
        Assert.Equal(10, (int)ProfileBackgroundTexture.Herringbone);
        Assert.Equal(11, (int)ProfileBackgroundTexture.Honeycomb);
        Assert.Equal(12, (int)ProfileBackgroundTexture.Scales);
        Assert.Equal(13, (int)ProfileBackgroundTexture.Speckle);
        Assert.Equal(14, (int)ProfileBackgroundTexture.Diamonds);
        Assert.Equal(15, (int)ProfileBackgroundTexture.Chevron);
        Assert.Equal(16, (int)ProfileBackgroundTexture.Sparkle);
        Assert.Equal(17, (int)ProfileBackgroundTexture.Linen);
        Assert.Equal(18, (int)ProfileBackgroundTexture.Ripples);
        Assert.Equal(19, (int)ProfileBackgroundTexture.Quatrefoil);
        Assert.Equal(20, (int)ProfileBackgroundTexture.Brick);
    }

    [Fact]
    public void EveryPatternId_IsUnique()
    {
        var ids = Enum.GetValues<ProfileBackgroundTexture>().Cast<int>().ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    /// <summary>The catalog now has 20 real patterns plus None — comfortably in the requested
    /// 18-24 range, each accepted for a genuinely distinct look (see the implementation report for
    /// what was rejected and why).</summary>
    [Fact]
    public void PatternCatalog_HasTheExpandedCount()
    {
        Assert.Equal(20, AllRealPatterns.Length);
    }

    [Fact]
    public void PatternOrdering_IsDeterministic()
    {
        // Enum.GetValues returns members in declaration order, which is what every combo/card grid
        // iterates by (int)texture — pinning the full sequence catches an accidental reorder even if
        // the numeric-id test above somehow didn't (e.g. two members swapping already-used numbers).
        ProfileBackgroundTexture[] expected =
        [
            ProfileBackgroundTexture.None, ProfileBackgroundTexture.FineNoise, ProfileBackgroundTexture.Dots,
            ProfileBackgroundTexture.Grid, ProfileBackgroundTexture.DiagonalLines, ProfileBackgroundTexture.Crosshatch,
            ProfileBackgroundTexture.SubtlePaper, ProfileBackgroundTexture.Checkerboard, ProfileBackgroundTexture.Stripes,
            ProfileBackgroundTexture.Waves, ProfileBackgroundTexture.Herringbone, ProfileBackgroundTexture.Honeycomb,
            ProfileBackgroundTexture.Scales, ProfileBackgroundTexture.Speckle, ProfileBackgroundTexture.Diamonds,
            ProfileBackgroundTexture.Chevron, ProfileBackgroundTexture.Sparkle, ProfileBackgroundTexture.Linen,
            ProfileBackgroundTexture.Ripples, ProfileBackgroundTexture.Quatrefoil, ProfileBackgroundTexture.Brick,
        ];

        Assert.Equal(expected, Enum.GetValues<ProfileBackgroundTexture>());
    }

    // ---------------------------------------------------------------- Linen (replaced Basketweave)

    /// <summary>
    /// Basketweave (a diagonal-brick weave) was found in game to be able to read as resembling
    /// extremist symbolism at Plate scale and was replaced outright — not tweaked — before ever being
    /// committed, reusing the same numeric slot (17) since nothing persisted depended on the old
    /// display name. These pin the replacement's own invariants explicitly, on top of the generic
    /// per-pattern theories above that already cover Linen once it's in <see cref="PeriodicPatterns"/>.
    /// </summary>
    [Fact]
    public void Linen_TilesSeamlessly()
    {
        const float tolerance = 0.002f;
        const int tile = ProceduralPatternMath.TileTexels;

        for (var y = 0; y < tile; y += 5)
        {
            for (var x = 0; x < tile; x += 5)
            {
                var here = ProceduralPatternMath.Sample(ProfileBackgroundTexture.Linen, x, y);
                var right = ProceduralPatternMath.Sample(ProfileBackgroundTexture.Linen, x + tile, y);
                var down = ProceduralPatternMath.Sample(ProfileBackgroundTexture.Linen, x, y + tile);
                Assert.True(MathF.Abs(here - right) <= tolerance, $"Linen at ({x},{y}) has a horizontal seam.");
                Assert.True(MathF.Abs(here - down) <= tolerance, $"Linen at ({x},{y}) has a vertical seam.");
            }
        }
    }

    [Fact]
    public void Linen_ProducesVisibleForegroundCoverage()
    {
        var maxCoverage = 0f;
        for (var y = 0; y < ProceduralPatternMath.TileTexels; y += 2)
        {
            for (var x = 0; x < ProceduralPatternMath.TileTexels; x += 2)
            {
                maxCoverage = MathF.Max(maxCoverage, ProceduralPatternMath.Sample(ProfileBackgroundTexture.Linen, x, y));
            }
        }

        Assert.True(maxCoverage > 0.3f, $"Linen's peak coverage is only {maxCoverage:0.00}.");
    }

    [Fact]
    public void Linen_IsDeterministic()
    {
        for (var y = 0; y < ProceduralPatternMath.TileTexels; y += 11)
        {
            for (var x = 0; x < ProceduralPatternMath.TileTexels; x += 11)
            {
                var first = ProceduralPatternMath.Sample(ProfileBackgroundTexture.Linen, x, y);
                var second = ProceduralPatternMath.Sample(ProfileBackgroundTexture.Linen, x, y);
                Assert.Equal(first, second);
            }
        }
    }

    [Fact]
    public void LinenId_IsTheOriginalBasketweaveSlot_Unchanged()
    {
        Assert.Equal(17, (int)ProfileBackgroundTexture.Linen);
    }

    [Fact]
    public void PatternCatalog_ExposesLinen_NotBasketweave()
    {
        var names = Enum.GetNames<ProfileBackgroundTexture>();
        Assert.Contains("Linen", names);
        Assert.DoesNotContain("Basketweave", names);
    }
}
