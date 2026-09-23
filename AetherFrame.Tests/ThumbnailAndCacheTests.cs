using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Caching;
using AetherFrame.Services.Thumbnails;
using Xunit;

namespace AetherFrame.Tests;

public class ThumbnailTests
{
    private static readonly ProfileDocument Document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), "x", DateTime.UtcNow);

    private sealed class FakeGenerator : IPlateThumbnailGenerator
    {
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Fail { get; init; }

        internal bool Wait { get; init; }

        internal int Calls;

        public async Task GenerateAsync(ProfileDocument document, string outputPngPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Wait)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Fail)
            {
                throw new InvalidOperationException("compositor exploded");
            }

            await File.WriteAllBytesAsync(outputPngPath, TestImages.Png(16, 9), cancellationToken);
        }
    }

    private static PlateThumbnail WaitFor(Func<PlateThumbnail> get, PlateThumbnailState state)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var current = get();
            if (current.State == state)
            {
                return current;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException($"Thumbnail never reached {state}.");
    }

    [Fact]
    public void WithoutGenerator_ThumbnailIsMissing()
    {
        using var dir = new TempDirectory();
        var service = new PlateThumbnailService(dir.Path);

        Assert.Equal(PlateThumbnailState.Missing, service.Get(Guid.NewGuid(), "r1", () => Document).State);
    }

    [Fact]
    public void ExistingThumbnail_ForTheSameVersion_IsReady_ButStaleOneIsNot()
    {
        using var dir = new TempDirectory();
        var plateId = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(dir.Path, $"{plateId}.png"), TestImages.Png(16, 9));
        File.WriteAllText(Path.Combine(dir.Path, $"{plateId}.key"), "r1");
        var service = new PlateThumbnailService(dir.Path);

        var ready = service.Get(plateId, "r1", () => Document);
        Assert.Equal(PlateThumbnailState.Ready, ready.State);
        Assert.NotNull(ready.ImagePath);

        Assert.Equal(PlateThumbnailState.Missing, service.Get(plateId, "r2", () => Document).State);
    }

    [Fact]
    public void Generator_ProducesReadyThumbnail_InTheBackground()
    {
        using var dir = new TempDirectory();
        var generator = new FakeGenerator { Wait = true };
        var service = new PlateThumbnailService(dir.Path, generator);
        var plateId = Guid.NewGuid();

        var first = service.Get(plateId, "r1", () => Document);
        Assert.Equal(PlateThumbnailState.Generating, first.State);

        generator.Gate.SetResult();
        var ready = WaitFor(() => service.Get(plateId, "r1", () => Document), PlateThumbnailState.Ready);

        Assert.True(File.Exists(ready.ImagePath));
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public void Get_NeverBlocks_WhileGenerating()
    {
        using var dir = new TempDirectory();
        var generator = new FakeGenerator { Wait = true };
        var service = new PlateThumbnailService(dir.Path, generator);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 100; i++)
        {
            service.Get(Guid.NewGuid(), "r1", () => Document);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        generator.Gate.SetResult();
        service.Dispose();
    }

    [Fact]
    public void GeneratorFailure_MarksFailed_AndDoesNotRetryTheSameVersion()
    {
        using var dir = new TempDirectory();
        var generator = new FakeGenerator { Fail = true };
        var service = new PlateThumbnailService(dir.Path, generator);
        var plateId = Guid.NewGuid();

        service.Get(plateId, "r1", () => Document);
        WaitFor(() => service.Get(plateId, "r1", () => Document), PlateThumbnailState.Failed);
        service.Get(plateId, "r1", () => Document);

        Assert.Equal(1, generator.Calls);
        Assert.False(File.Exists(service.GetImagePath(plateId)));
    }

    [Fact]
    public void MissingDocument_MarksFailed()
    {
        using var dir = new TempDirectory();
        var service = new PlateThumbnailService(dir.Path, new FakeGenerator());
        var plateId = Guid.NewGuid();

        service.Get(plateId, "r1", () => null);

        WaitFor(() => service.Get(plateId, "r1", () => null), PlateThumbnailState.Failed);
    }

    [Fact]
    public void CorruptThumbnail_ReportedByDisplay_FallsBack()
    {
        using var dir = new TempDirectory();
        var plateId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir.Path, $"{plateId}.png"), "not a png");
        File.WriteAllText(Path.Combine(dir.Path, $"{plateId}.key"), "r1");
        var service = new PlateThumbnailService(dir.Path);
        Assert.Equal(PlateThumbnailState.Ready, service.Get(plateId, "r1", () => Document).State);

        service.ReportDisplayFailure(plateId, "decode failed");

        Assert.Equal(PlateThumbnailState.Failed, service.Get(plateId, "r1", () => Document).State);
    }

    [Fact]
    public void Remove_DeletesDerivedFiles_AndInvalidateForgets()
    {
        using var dir = new TempDirectory();
        var plateId = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(dir.Path, $"{plateId}.png"), TestImages.Png(4, 4));
        File.WriteAllText(Path.Combine(dir.Path, $"{plateId}.key"), "r1");
        var service = new PlateThumbnailService(dir.Path);
        service.Get(plateId, "r1", () => Document);

        service.Remove(plateId);

        Assert.False(File.Exists(Path.Combine(dir.Path, $"{plateId}.png")));
        Assert.False(File.Exists(Path.Combine(dir.Path, $"{plateId}.key")));
        Assert.Equal(PlateThumbnailState.Missing, service.Get(plateId, "r1", () => Document).State);
    }

    [Fact]
    public void VersionKey_ChangesWithRevisionAndTime()
    {
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.NotEqual(PlateThumbnailService.VersionKeyFor(1, time), PlateThumbnailService.VersionKeyFor(2, time));
        Assert.NotEqual(PlateThumbnailService.VersionKeyFor(1, time), PlateThumbnailService.VersionKeyFor(1, time.AddTicks(1)));
        Assert.Equal(PlateThumbnailService.VersionKeyFor(1, time), PlateThumbnailService.VersionKeyFor(1, time));
    }
}

public class LruCacheTests
{
    [Fact]
    public void EvictsLeastRecentlyUsed_AndReportsIt()
    {
        var evicted = new List<string>();
        var cache = new LruCache<string, int>(2, (key, _) => evicted.Add(key));

        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.TryGetValue("a", out _);
        cache.Set("c", 3);

        Assert.Equal(["b"], evicted);
        Assert.True(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("c"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Overwrite_DoesNotEvict_AndRemoveAndClearReport()
    {
        var evicted = new List<string>();
        var cache = new LruCache<string, int>(2, (key, _) => evicted.Add(key));

        cache.Set("a", 1);
        cache.Set("a", 2);
        Assert.Empty(evicted);
        Assert.True(cache.TryGetValue("a", out var value));
        Assert.Equal(2, value);

        cache.Set("b", 3);
        Assert.True(cache.Remove("a"));
        Assert.False(cache.Remove("a"));
        cache.Clear();

        Assert.Equal(["a", "b"], evicted);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void NeverExceedsCapacity()
    {
        var cache = new LruCache<int, int>(16);
        for (var i = 0; i < 1000; i++)
        {
            cache.Set(i, i);
        }

        Assert.Equal(16, cache.Count);
        Assert.True(cache.ContainsKey(999));
        Assert.False(cache.ContainsKey(0));
    }
}

public class PlateRuleTests
{
    [Theory]
    [InlineData("Showcase", new string[0], "Showcase Copy")]
    [InlineData("Showcase", new[] { "Showcase Copy" }, "Showcase Copy 2")]
    [InlineData("Showcase", new[] { "showcase copy", "SHOWCASE COPY 2" }, "Showcase Copy 3")]
    [InlineData("Showcase Copy", new[] { "Showcase Copy" }, "Showcase Copy 2")]
    [InlineData("Showcase Copy 4", new[] { "Showcase Copy" }, "Showcase Copy 2")]
    [InlineData("A Copycat", new string[0], "A Copycat Copy")]
    [InlineData("", new string[0], "New Plate Copy")]
    public void CopyNames(string source, string[] existing, string expected) =>
        Assert.Equal(expected, PlateNaming.MakeCopyName(source, existing));

    [Fact]
    public void CopyNames_StayWithinTheLengthLimit()
    {
        var name = PlateNaming.MakeCopyName(new string('x', PlateNaming.MaxNameLength), []);

        Assert.True(name.Length <= PlateNaming.MaxNameLength);
        Assert.EndsWith(" Copy", name, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueNames()
    {
        Assert.Equal("Adventure Plate", PlateNaming.MakeUniqueName("Adventure Plate", []));
        Assert.Equal("Adventure Plate 3", PlateNaming.MakeUniqueName("Adventure Plate", ["adventure plate", "Adventure Plate 2"]));
    }

    [Theory]
    [InlineData("  Hello  ", true, "Hello")]
    [InlineData("Line\nBreak", true, "Line Break")]
    [InlineData("", false, "")]
    [InlineData("    ", false, "")]
    [InlineData(null, false, "")]
    public void NormalizeName(string? input, bool valid, string expected)
    {
        Assert.Equal(valid, PlateNaming.TryNormalizeName(input, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void NormalizeName_RejectsOverlongNames()
    {
        Assert.False(PlateNaming.TryNormalizeName(new string('a', PlateNaming.MaxNameLength + 1), out _, out var error));
        Assert.NotNull(error);
        Assert.True(PlateNaming.TryNormalizeName(new string('a', PlateNaming.MaxNameLength), out _, out _));
    }

    [Fact]
    public void Ordering_InsertAndMove()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var order = new List<Guid> { a, b };

        PlateOrdering.InsertAtFront(order, c);
        Assert.Equal([c, a, b], order);

        var d = Guid.NewGuid();
        PlateOrdering.InsertAfter(order, a, d);
        Assert.Equal([c, a, d, b], order);

        var e = Guid.NewGuid();
        PlateOrdering.InsertAfter(order, Guid.NewGuid(), e);
        Assert.Equal(e, order[0]);

        Assert.True(PlateOrdering.Move(order, e, b, placeAfter: true));
        Assert.Equal([c, a, d, b, e], order);
        Assert.False(PlateOrdering.Move(order, e, e, placeAfter: true));
        Assert.False(PlateOrdering.Move(order, Guid.NewGuid(), a, placeAfter: false));
        Assert.False(PlateOrdering.Move(order, c, a, placeAfter: false));
    }

    [Fact]
    public void Ordering_ReconcileDedupesAndAppends_ButKeepsUnknownIds()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ghost = Guid.NewGuid();
        var order = new List<Guid> { ghost, a, a, Guid.Empty };

        var changed = PlateOrdering.Reconcile(order, [(a, DateTime.UtcNow), (b, DateTime.UtcNow)]);

        Assert.True(changed);
        Assert.Equal([ghost, a, b], order);
        Assert.False(PlateOrdering.Reconcile(order, [(a, DateTime.UtcNow), (b, DateTime.UtcNow)]));
        Assert.Equal([a, b], PlateOrdering.ResolveDisplayOrder(order, [(a, DateTime.UtcNow), (b, DateTime.UtcNow)]));
    }

    [Theory]
    [InlineData("show", "Showcase", null, true)]
    [InlineData("SHOW", "showcase", null, true)]
    [InlineData("alice", "Plate", "Alice Example", true)]
    [InlineData("bob", "Plate", "Alice Example", false)]
    [InlineData("", "Anything", null, true)]
    [InlineData(null, "Anything", null, true)]
    public void Search(string? query, string name, string? character, bool matches) =>
        Assert.Equal(matches, PlateSearch.Matches(query, name, character is null ? null : [character]));
}
