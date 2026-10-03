using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace AetherFrame.Tests;

/// <summary>
/// The Plate font cache's rules (issue #117), against a fake font system that counts what each
/// atlas rebuild rasterizes: one atlas per family, nothing in use let go, bounded browsing, batches
/// and load times; and a before/after measurement of browsing fonts.
/// </summary>
public class FontHandleCacheTests(ITestOutputHelper output)
{
    private static readonly string[] Library = FontLibrary.Families.Select(f => f.Id).ToArray();

    private sealed class FakeAtlas(string name)
    {
        public string Name { get; } = name;

        public List<FakeHandle> Handles { get; } = [];

        public bool Disposed { get; set; }

        public int Suppressions { get; set; }

        public bool RebuildHeld { get; set; }

        /// <summary>A rebuild is due: it runs at the next <see cref="FakeFonts.CompleteBuilds"/>.</summary>
        public bool Dirty { get; set; }

        public int Rebuilds { get; set; }
    }

    private sealed class FakeHandle(FontCacheKey key, FakeAtlas atlas)
    {
        public FontCacheKey Key { get; } = key;

        public FakeAtlas Atlas { get; } = atlas;

        public bool Available { get; set; }

        public bool Disposed { get; set; }

        /// <summary>Called once when the face is first built (<see cref="IFontAtlasBackend{TAtlas, THandle}.WhenAvailable"/>).</summary>
        public List<Action> Waiters { get; } = [];
    }

    /// <summary>A font system like Dalamud's asynchronous atlases: a new or removed face makes its
    /// atlas due for a rebuild unless held back; requests made before the rebuild runs share it
    /// (a newer request supersedes an older build); a rebuild rasterizes every face in the atlas;
    /// faces appear once it completes; a disposed atlas never builds.</summary>
    private sealed class FakeFonts(bool oneSharedAtlas = false) : IFontAtlasBackend<FakeAtlas, FakeHandle>
    {
        private FakeAtlas? shared;

        public List<FakeAtlas> Atlases { get; } = [];

        /// <summary>The glyph surface each rebuild rasterized, in order.</summary>
        public List<long> Rebuilt { get; } = [];

        public FakeAtlas CreateAtlas(string familyId)
        {
            if (oneSharedAtlas)
            {
                return shared ??= Add(new FakeAtlas("shared"));
            }

            return Add(new FakeAtlas(familyId));
        }

        public FakeHandle CreateHandle(FakeAtlas atlas, FontCacheKey key)
        {
            Assert.False(atlas.Disposed);
            var handle = new FakeHandle(key, atlas);
            atlas.Handles.Add(handle);
            RequestRebuild(atlas);
            return handle;
        }

        public bool IsAvailable(FakeHandle handle) => handle.Available;

        public void WhenAvailable(FakeHandle handle, Action available)
        {
            if (handle.Available)
            {
                available();
            }
            else
            {
                handle.Waiters.Add(available);
            }
        }

        public IDisposable SuppressRebuild(FakeAtlas atlas)
        {
            atlas.Suppressions++;
            return new Release(() =>
            {
                atlas.Suppressions--;
                if (atlas.Suppressions == 0 && atlas.RebuildHeld)
                {
                    atlas.RebuildHeld = false;
                    RequestRebuild(atlas);
                }
            });
        }

        public void DisposeHandle(FakeHandle handle)
        {
            handle.Disposed = true;
            handle.Atlas.Handles.Remove(handle);
            RequestRebuild(handle.Atlas);
        }

        public void DisposeAtlas(FakeAtlas atlas)
        {
            if (!oneSharedAtlas)
            {
                atlas.Disposed = true;
            }
        }

        /// <summary>Every rebuild due runs and completes: its faces can be drawn.</summary>
        public void CompleteBuilds()
        {
            foreach (var atlas in Atlases.Where(a => !a.Disposed && a.Dirty))
            {
                atlas.Dirty = false;
                atlas.Rebuilds++;
                Rebuilt.Add(atlas.Handles.Sum(h => FontTierPolicy.EstimatedSurfacePixels(h.Key.FamilyId, h.Key.SizePx)));
                foreach (var handle in atlas.Handles)
                {
                    handle.Available = true;
                    foreach (var waiter in handle.Waiters)
                    {
                        waiter();
                    }

                    handle.Waiters.Clear();
                }
            }
        }

        public FakeAtlas AtlasOf(string familyId) => Atlases.Single(a => a.Name == familyId && !a.Disposed);

        private FakeAtlas Add(FakeAtlas atlas)
        {
            Atlases.Add(atlas);
            return atlas;
        }

        private static void RequestRebuild(FakeAtlas atlas)
        {
            if (atlas.Suppressions > 0)
            {
                atlas.RebuildHeld = true;
                return;
            }

            atlas.Dirty = true;
        }

        private sealed class Release(Action release) : IDisposable
        {
            private bool done;

            public void Dispose()
            {
                if (!done)
                {
                    done = true;
                    release();
                }
            }
        }
    }

    private sealed class Clock
    {
        public long Now { get; set; } = 1_000_000;

        public void Advance(long milliseconds) => Now += milliseconds;
    }

    private static int Tier(float size) => Array.IndexOf(FontTierPolicy.SizeLadder.ToArray(), size);

    private static FontCacheKey Key(string family, float size) => new(family, size, false, false);

    private static (FontHandleCache<FakeAtlas, FakeHandle> Cache, FakeFonts Fonts, Clock Clock) NewCache(bool oneSharedAtlas = false, long budget = FontTierPolicy.AtlasBudgetPixels)
    {
        var fonts = new FakeFonts(oneSharedAtlas);
        var clock = new Clock();
        return (new FontHandleCache<FakeAtlas, FakeHandle>(fonts, () => clock.Now, budget), fonts, clock);
    }

    // ---------------------------------------------------------------- one atlas per family

    [Fact]
    public void EachFamily_HasItsOwnAtlas_AndItsSizesShareIt()
    {
        var (cache, fonts, _) = NewCache();

        var a16 = cache.GetOrCreate(Key(Library[0], 16f));
        var a32 = cache.GetOrCreate(Key(Library[0], 32f));
        var b16 = cache.GetOrCreate(Key(Library[1], 16f));

        Assert.Same(a16.Atlas, a32.Atlas);
        Assert.NotSame(a16.Atlas, b16.Atlas);
        Assert.Equal(2, fonts.Atlases.Count);
        Assert.Equal(2, cache.FamilyCount);
        Assert.Equal(3, cache.HandleCount);
    }

    [Fact]
    public void ANewFont_RebuildsOnlyItsOwnFaces()
    {
        var (cache, fonts, _) = NewCache();
        foreach (var size in FontTierPolicy.CommonEditorSizes)
        {
            cache.GetOrCreate(Key(ProfileFontFamilies.AetherFrameSans, size));
        }

        fonts.CompleteBuilds();
        var built = fonts.Rebuilt.Count;

        cache.GetOrCreate(Key(Library[0], 20f));
        fonts.CompleteBuilds();

        Assert.Equal(built + 1, fonts.Rebuilt.Count);
        Assert.Equal(FontTierPolicy.EstimatedSurfacePixels(Library[0], 20f), fonts.Rebuilt[^1]);
        Assert.Equal(1, fonts.AtlasOf(ProfileFontFamilies.AetherFrameSans).Rebuilds);
    }

    [Fact]
    public void ReselectingAFontAlreadyLoaded_BuildsNothing()
    {
        var (cache, fonts, clock) = NewCache();
        cache.Get(Library[0], Tier(32f), false, false);
        cache.Get(Library[1], Tier(32f), false, false);
        fonts.CompleteBuilds();
        var rebuilds = fonts.Rebuilt.Count;

        clock.Advance(10_000); // long since used
        var again = cache.Get(Library[0], Tier(32f), false, false);

        Assert.NotNull(again);
        Assert.True(again.Available);
        Assert.Equal(rebuilds, fonts.Rebuilt.Count);
        Assert.Equal(2, cache.HandleCount);
    }

    // ---------------------------------------------------------------- what is drawn while loading

    [Fact]
    public void Get_IsNullWhileTheFontLoads_ThenTheRequestedTier()
    {
        var (cache, fonts, _) = NewCache();

        Assert.Null(cache.Get(Library[0], Tier(32f), false, false));
        fonts.CompleteBuilds();

        var handle = cache.Get(Library[0], Tier(32f), false, false);
        Assert.NotNull(handle);
        Assert.Equal(32f, handle.Key.SizePx);
    }

    [Fact]
    public void WhileATierLoads_ALargerBuiltTierIsUsed_NeverASmallerOne()
    {
        var (cache, fonts, _) = NewCache();
        cache.GetOrCreate(Key(Library[0], 16f));
        cache.GetOrCreate(Key(Library[0], 48f));
        fonts.CompleteBuilds();

        var handle = cache.Get(Library[0], Tier(32f), false, false);

        Assert.NotNull(handle);
        Assert.Equal(48f, handle.Key.SizePx);
        Assert.Contains(fonts.AtlasOf(Library[0]).Handles, h => h.Key.SizePx == 32f); // and the ideal tier is on its way
    }

    [Fact]
    public void SwitchingQuickly_NeverDrawsTheNewFontWithAnEarlierOne()
    {
        var (cache, fonts, _) = NewCache();
        cache.Get(Library[0], Tier(32f), false, false);
        fonts.CompleteBuilds();

        // Switched to Library[1], then Library[2], before either loaded.
        Assert.Null(cache.Get(Library[1], Tier(32f), false, false));
        Assert.Null(cache.Get(Library[2], Tier(32f), false, false));
        fonts.CompleteBuilds();

        Assert.Equal(Library[2], cache.Get(Library[2], Tier(32f), false, false)!.Key.FamilyId);
        Assert.Equal(Library[1], cache.Get(Library[1], Tier(32f), false, false)!.Key.FamilyId);
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public void BrowsingManyFonts_KeepsAtMostTheFamilyCap_PlusThoseInUse()
    {
        var (cache, fonts, clock) = NewCache();

        // The Plate's own font, drawn every frame throughout.
        cache.Get(ProfileFontFamilies.AetherFrameSans, Tier(32f), false, false);
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(3000);
            cache.Get(ProfileFontFamilies.AetherFrameSans, Tier(32f), false, false);
            cache.Get(Library[i], Tier(20f), false, false);
            cache.Get(Library[i], Tier(32f), false, false);
            fonts.CompleteBuilds();

            Assert.True(cache.FamilyCount <= FontHandleCache<FakeAtlas, FakeHandle>.MaxFamilyAtlases, $"after font {i}: {cache.FamilyCount} families");
        }

        // The Plate's font was never let go, and the last fonts tried are still there.
        Assert.Single(fonts.Atlases, a => a.Name == ProfileFontFamilies.AetherFrameSans);
        Assert.False(fonts.AtlasOf(ProfileFontFamilies.AetherFrameSans).Disposed);
        Assert.False(fonts.AtlasOf(Library[39]).Disposed);
        Assert.False(fonts.AtlasOf(Library[38]).Disposed);
        Assert.True(fonts.Atlases.Single(a => a.Name == Library[0]).Disposed);

        // What was let go is gone for good: every disposed atlas had no faces left.
        Assert.All(fonts.Atlases.Where(a => a.Disposed), a => Assert.Empty(a.Handles));
    }

    [Fact]
    public void FamiliesInUse_AreNeverLetGo_EvenBeyondTheCap()
    {
        var (cache, fonts, clock) = NewCache();
        var count = FontHandleCache<FakeAtlas, FakeHandle>.MaxFamilyAtlases + 5;

        // A Plate with more families than the cap, all drawn every frame.
        for (var frame = 0; frame < 3; frame++)
        {
            for (var i = 0; i < count; i++)
            {
                cache.Get(Library[i], Tier(32f), false, false);
            }

            fonts.CompleteBuilds();
            clock.Advance(16);
        }

        Assert.Equal(count, cache.FamilyCount);
        Assert.DoesNotContain(fonts.Atlases, a => a.Disposed);
        Assert.Equal(count, fonts.Rebuilt.Count); // each built once, never rebuilt again
    }

    [Fact]
    public void OverTheBudget_IdleFacesGoFirst_AndFacesInUseNeverDo()
    {
        var (cache, fonts, clock) = NewCache(budget: 1);

        cache.GetOrCreate(Key(Library[0], 64f));
        cache.GetOrCreate(Key(Library[1], 64f));
        clock.Advance(5000);
        cache.GetOrCreate(Key(Library[2], 64f)); // in use from here on
        cache.GetOrCreate(Key(Library[3], 64f));
        cache.GetOrCreate(Key(Library[4], 64f));

        Assert.True(fonts.Atlases.Single(a => a.Name == Library[0]).Disposed);
        Assert.True(fonts.Atlases.Single(a => a.Name == Library[1]).Disposed);
        Assert.Equal(3, cache.HandleCount); // over the budget only by what is in use
        Assert.All([Library[2], Library[3], Library[4]], id => Assert.False(fonts.AtlasOf(id).Disposed));
    }

    // ---------------------------------------------------------------- batches

    [Fact]
    public void ABatch_CostsOneRebuildPerAtlas()
    {
        var (cache, fonts, _) = NewCache();

        using (cache.Batch())
        {
            foreach (var size in FontTierPolicy.CommonEditorSizes)
            {
                cache.GetOrCreate(Key(ProfileFontFamilies.AetherFrameSans, size));
                cache.GetOrCreate(Key(ProfileFontFamilies.AetherFrameSerif, size));
            }

            Assert.All(fonts.Atlases, a => Assert.True(a.RebuildHeld && !a.Dirty));
        }

        Assert.All(fonts.Atlases, a => Assert.True(a.Dirty && !a.RebuildHeld));
        fonts.CompleteBuilds();
        Assert.Equal(2, fonts.Rebuilt.Count);
        Assert.Equal(1, fonts.AtlasOf(ProfileFontFamilies.AetherFrameSans).Rebuilds);
        Assert.Equal(1, fonts.AtlasOf(ProfileFontFamilies.AetherFrameSerif).Rebuilds);
        Assert.All(fonts.Atlases, a => Assert.Equal(0, a.Suppressions));
    }

    [Fact]
    public void AnAtlasEmptiedInsideABatch_IsDisposed_WithItsHeldRebuildReleased()
    {
        var oneTier = FontTierPolicy.EstimatedSurfacePixels(Library[0], 64f);
        var (cache, fonts, clock) = NewCache(budget: oneTier);
        cache.GetOrCreate(Key(Library[0], 64f));
        clock.Advance(5000);

        using (cache.Batch())
        {
            cache.GetOrCreate(Key(Library[1], 64f)); // over the budget: Library[0] goes
            var gone = fonts.Atlases.Single(a => a.Name == Library[0]);
            Assert.True(gone.Disposed);
            Assert.Equal(0, gone.Suppressions);
        }

        Assert.Equal(1, cache.FamilyCount);
        Assert.All(fonts.Atlases, a => Assert.Equal(0, a.Suppressions));
    }

    // ---------------------------------------------------------------- measurements

    [Fact]
    public void LoadTimes_AreRecordedOnce_FromTheRequestToWhenTheFontIsBuilt()
    {
        var (cache, fonts, clock) = NewCache();
        cache.Get(Library[0], Tier(32f), false, false);
        clock.Advance(120);
        cache.Get(Library[0], Tier(32f), false, false);
        fonts.CompleteBuilds();
        clock.Advance(30);
        cache.Get(Library[0], Tier(32f), false, false);
        clock.Advance(500);
        cache.Get(Library[0], Tier(32f), false, false);

        var stats = cache.Stats;
        Assert.Equal(1, stats.Loads);
        Assert.Equal([120L], stats.RecentLoadMilliseconds);
        Assert.Equal(120L, stats.LongestLoadMilliseconds);
        Assert.Equal(1, stats.Families);
        Assert.Equal(1, stats.Handles);
        Assert.Contains("1 loaded", stats.Describe(), StringComparison.Ordinal);
        Assert.Contains("120 ms", stats.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFontBuiltAheadOfUse_CountsOnlyItsBuild_HoweverLateItIsFirstDrawn()
    {
        var (cache, fonts, clock) = NewCache();
        cache.GetOrCreate(Key(Library[0], 24f)); // warmed at start
        clock.Advance(40);
        fonts.CompleteBuilds();
        clock.Advance(180_000);

        // Minutes later: the 20 px tier isn't built, so the built 24 px one is drawn instead.
        Assert.Equal(24f, cache.Get(Library[0], Tier(20f), false, false)!.Key.SizePx);

        var stats = cache.Stats;
        Assert.Equal(1, stats.Loads);
        Assert.Equal([40L], stats.RecentLoadMilliseconds);
        Assert.Equal(40L, stats.LongestLoadMilliseconds);
    }

    [Fact]
    public void AFontLetGoBeforeItIsBuilt_IsNotCounted()
    {
        var (cache, fonts, clock) = NewCache(budget: 1);
        cache.GetOrCreate(Key(Library[0], 32f));
        clock.Advance(FontHandleCache<FakeAtlas, FakeHandle>.InUseMilliseconds + 1);
        cache.GetOrCreate(Key(Library[1], 32f)); // over the budget: the idle first face goes, unbuilt
        fonts.CompleteBuilds();

        Assert.Equal(1, cache.HandleCount);
        Assert.Equal(1, cache.Stats.Loads);
    }

    [Fact]
    public void Dispose_LetsGoOfEverything()
    {
        var (cache, fonts, _) = NewCache();
        cache.GetOrCreate(Key(Library[0], 32f));
        cache.GetOrCreate(Key(Library[1], 32f));

        cache.Dispose();

        Assert.All(fonts.Atlases, a => Assert.True(a.Disposed));
        Assert.Equal(0, cache.HandleCount);
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrCreate(Key(Library[0], 32f)));
    }

    /// <summary>
    /// Kim's case: one name in the Basic editor (32 px, live view at 60%), switched through 40
    /// library fonts in turn; each font builds its live view tier and its smallest tier (for
    /// measuring). Before: one atlas for every family. After: an atlas per family. What each switch
    /// rebuilds is the measure: before, every font tried so far; after, the new font's own faces.
    /// </summary>
    [Fact]
    public void BrowsingFonts_EachNewFontCostsItsOwnFaces_NotEveryFontBeforeIt()
    {
        var before = Browse(oneSharedAtlas: true);
        var after = Browse(oneSharedAtlas: false);

        output.WriteLine("Font tried | before: glyph px rebuilt | after: glyph px rebuilt");
        foreach (var i in new[] { 0, 4, 9, 19, 29, 39 })
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i + 1} | {before[i]:N0} | {after[i]:N0}"));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Total over 40 | {before.Sum():N0} | {after.Sum():N0}"));

        // After: each switch costs exactly the new font's own two faces, however many came before.
        // Before: every font tried so far, rebuilt again.
        long tried = 0;
        for (var i = 0; i < after.Count; i++)
        {
            var own = FontTierPolicy.EstimatedSurfacePixels(Library[i], 20f) + FontTierPolicy.EstimatedSurfacePixels(Library[i], 10f);
            tried += own;
            Assert.Equal(own, after[i]);
            Assert.Equal(tried, before[i]);
        }
    }

    /// <summary>The glyph surface rebuilt by each font switch.</summary>
    private static List<long> Browse(bool oneSharedAtlas)
    {
        var (cache, fonts, clock) = NewCache(oneSharedAtlas);
        var perSwitch = new List<long>();

        // The model of the old cache never let a face go in a session this short (its only bounds
        // were 160 faces and the surface budget), so its clock stands still: nothing goes idle.
        for (var i = 0; i < 40; i++)
        {
            var rebuiltBefore = fonts.Rebuilt.Sum();
            cache.Get(Library[i], FontTierPolicy.FindTierIndex(Library[i], 32f * 0.6f), false, false); // the live view
            cache.Get(Library[i], 0, false, false); // measuring: the smallest tier
            fonts.CompleteBuilds();
            perSwitch.Add(fonts.Rebuilt.Sum() - rebuiltBefore);
            if (!oneSharedAtlas)
            {
                clock.Advance(3000);
            }
        }

        return perSwitch;
    }
}
