using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The bundled-art texture load (the Celestial Sakura UI hitch investigation): every artwork is
/// decoded and uploaded at most once, on the thread pool, never inside Draw; Draw only looks up
/// what is ready; every texture is released exactly once.
/// </summary>
public class BuiltInArtLoaderTests
{
    private static readonly BuiltInArtAsset Frame = BuiltInArtCatalog.CelestialSakuraPlateFrameArt;
    private static readonly BuiltInArtAsset Corner = BuiltInArtCatalog.CelestialSakuraCornerOrnamentArt;

    [Fact]
    public void FirstRequest_StartsTheOnlyLoad_OffTheDrawThread_AndReturnsAtOnce()
    {
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        var loadThread = -1;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            loadThread = Environment.CurrentManagedThreadId;
            gate.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(Levels(512, 256));
        });

        // Frames while it loads: nothing drawn, nothing blocked, no second load.
        for (var frame = 0; frame < 100; frame++)
        {
            Assert.Null(loader.GetLevelOrNull(Frame, 300f));
        }

        Assert.Equal(1, loader.LoadsStarted);
        gate.Set();
        var level = WaitForLevel(loader, Frame, 300f);

        Assert.Equal(512, level.LongSide);
        Assert.NotEqual(Environment.CurrentManagedThreadId, loadThread);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void AfterLoading_DrawIsALookup_NoFurtherLoadOrPixelWork()
    {
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Levels(512, 256, 128));
        });

        var first = WaitForLevel(loader, Frame, 100f);
        for (var frame = 0; frame < 1000; frame++)
        {
            Assert.Same(first, loader.GetLevelOrNull(Frame, 100f));
        }

        Assert.Equal(1, calls);
        Assert.Equal(1, loader.LoadsStarted);
        Assert.Equal(512, loader.GetLevelOrNull(Frame, 400f)!.LongSide); // level choice still follows the drawn size
        Assert.Equal(128, loader.GetLevelOrNull(Frame, 20f)!.LongSide);
    }

    [Fact]
    public void EachArtwork_LoadsOnce_Independently()
    {
        var loaded = new List<string>();
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            lock (loaded)
            {
                loaded.Add(art.Id);
            }

            return Task.FromResult(Levels(64));
        });

        WaitForLevel(loader, Frame, 10f);
        WaitForLevel(loader, Corner, 10f);
        WaitForLevel(loader, Frame, 10f);

        Assert.Equal(2, loader.LoadsStarted);
        Assert.Equal([Corner.Id, Frame.Id], loaded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFailedLoad_IsNotRetried_AndIsNeverDrawn()
    {
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidDataException("broken");
        });

        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);
        for (var frame = 0; frame < 10; frame++)
        {
            Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        }

        Assert.Equal(1, loader.LoadsStarted);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_ReleasesEveryLoadedTexture_Once_AndStopsLoading()
    {
        var textures = Levels(128, 64);
        var loader = new BuiltInArtLoader<FakeTexture>((art, _) => Task.FromResult(textures));
        WaitForLevel(loader, Frame, 10f);

        loader.Dispose();
        loader.Dispose();

        Assert.All(textures.Levels, t => Assert.Equal(1, t.DisposeCount));
        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        Assert.Null(loader.GetLevelOrNull(Corner, 10f));
        Assert.Equal(1, loader.LoadsStarted);
    }

    [Fact]
    public void Dispose_WhileLoading_CancelsIt_AndReleasesWhatItFinishesWith()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var textures = Levels(128, 64);
        var cancelled = false;
        var loader = new BuiltInArtLoader<FakeTexture>((art, token) =>
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            cancelled = token.IsCancellationRequested;
            return Task.FromResult(textures);
        });
        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        loader.Dispose();
        gate.Set();

        Assert.True(SpinWait.SpinUntil(() => textures.Levels.All(t => t.DisposeCount == 1), TimeSpan.FromSeconds(10)));
        Assert.True(cancelled);
        Thread.Sleep(50);
        Assert.All(textures.Levels, t => Assert.Equal(1, t.DisposeCount));
    }

    [Fact]
    public void Dispose_InTheSameFrameAsTheFirstRequest_NeverLeaksOrDraws()
    {
        // The load is either cancelled while still queued (nothing is made) or runs and has what it
        // made released as it finishes: either way nothing leaks and nothing is drawn.
        var made = new List<FakeTexture>();
        var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            var levels = Levels(64);
            lock (made)
            {
                made.AddRange(levels.Levels);
            }

            return Task.FromResult(levels);
        });

        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        loader.Dispose();
        Thread.Sleep(100);

        Assert.Null(loader.GetLevelOrNull(Frame, 10f));
        lock (made)
        {
            Assert.All(made, t => Assert.Equal(1, t.DisposeCount));
        }
    }

    [Fact]
    public void TheRealFamily_LoadsThroughTheLoader_EachOnce()
    {
        // The texture cache's own CPU path (resource bytes -> LoadLevels) per artwork, run by the loader.
        var calls = 0;
        using var loader = new BuiltInArtLoader<FakeTexture>((art, _) =>
        {
            Interlocked.Increment(ref calls);
            using var stream = typeof(BuiltInArtLoaderTests).Assembly.GetManifestResourceStream(art.ResourceName)!;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var levels = BundledArtImage.LoadLevels(bytes, art);
            return Task.FromResult(new LoadedArt<FakeTexture>(levels.Select(l => new FakeTexture(l.LongSide)).ToList(), levels.Select(l => l.LongSide).ToArray()));
        });

        var family = BuiltInArtCatalog.OfFamily(BuiltInArtCatalog.CelestialSakuraFamily).ToList();
        foreach (var art in family)
        {
            Assert.Null(loader.GetLevelOrNull(art, 5000f)); // every piece requested in the same frame: none blocks
        }

        foreach (var art in family)
        {
            Assert.Equal(Math.Max(art.PixelWidth, art.PixelHeight), WaitForLevel(loader, art, 5000f).LongSide);
        }

        Assert.Equal(family.Count, calls);
        Assert.Equal(family.Count, loader.LoadsStarted);
    }

    private static FakeTexture WaitForLevel(BuiltInArtLoader<FakeTexture> loader, BuiltInArtAsset art, float screenPixels)
    {
        FakeTexture? level = null;
        Assert.True(SpinWait.SpinUntil(() => (level = loader.GetLevelOrNull(art, screenPixels)) is not null, TimeSpan.FromSeconds(30)), $"{art.Id} never loaded");
        return level!;
    }

    private static LoadedArt<FakeTexture> Levels(params int[] longSides) => new(longSides.Select(s => new FakeTexture(s)).ToList(), longSides);

    private sealed class FakeTexture(int longSide) : IDisposable
    {
        private int disposeCount;

        public int LongSide { get; } = longSide;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }
}
