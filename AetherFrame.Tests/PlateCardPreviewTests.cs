using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Rendering;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>My Plates card previews: the real Plate in miniature (<see cref="PlateCardPreview"/>, <see cref="PlateCardPreviewCache"/>, <see cref="TextBars"/>).</summary>
public class PlateCardPreviewTests
{
    private static readonly Vector2 Card = new(236, 133); // a 16:9 card thumbnail area

    private static ProfileDocument Plate(Vector4 background, string? cornerOrnament = null)
    {
        var document = ComponentDocuments.WithAnchors();
        document.Background = new ProfileBackground { Mode = ProfileBackgroundMode.SolidColor, PrimaryColor = background };
        if (cornerOrnament is not null)
        {
            var ornament = ComponentDocuments.Of(cornerOrnament);
            ornament.Offset = new Vector2(-60, -60);
            ornament.Scale = 2.5f;
            document.Components = [ornament];
        }

        return document;
    }

    // ---- Cache -------------------------------------------------------------------------------

    [Fact]
    public void UnchangedPlates_ReuseTheirEntry()
    {
        var cache = new PlateCardPreviewCache();
        var id = Guid.NewGuid();
        var document = Plate(new Vector4(0.2f, 0.1f, 0.3f, 1f));
        var loads = 0;

        var first = cache.Get(id, "r1", () => { loads++; return document; });
        var second = cache.Get(id, "r1", () => { loads++; return document; });

        Assert.Equal(1, loads);
        Assert.Same(first, second);
        Assert.Same(document, first!.Document);
    }

    [Fact]
    public void AChangedPlate_IsReloaded_OnItsNewVersion()
    {
        var cache = new PlateCardPreviewCache();
        var id = Guid.NewGuid();
        var before = Plate(new Vector4(0.2f, 0.1f, 0.3f, 1f));
        var after = Plate(new Vector4(0.9f, 0.8f, 0.7f, 1f), BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);

        cache.Get(id, "r1", () => before);
        var updated = cache.Get(id, "r2", () => after)!;

        Assert.Same(after, updated.Document);
        Assert.Equal("r2", updated.VersionKey);
        Assert.NotEqual(ProfileVisualBounds.Logical(after), updated.Bounds); // the new overflow is picked up
    }

    [Fact]
    public void RetainInvalidateAndClear_AreSafe()
    {
        var cache = new PlateCardPreviewCache();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var document = Plate(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        cache.Get(a, "r1", () => document);
        cache.Get(b, "r1", () => document);

        cache.Retain(new HashSet<Guid> { a });
        Assert.Equal(1, cache.Count);

        cache.Invalidate(b); // already gone: harmless
        cache.Invalidate(a);
        Assert.Equal(0, cache.Count);

        var loads = 0;
        cache.Get(a, "r1", () => { loads++; return document; });
        Assert.Equal(1, loads); // reloaded after invalidation

        cache.Clear();
        cache.Clear();
        Assert.Equal(0, cache.Count);
        cache.Retain(new HashSet<Guid>());
    }

    [Fact]
    public void AnUnloadablePlate_HasNoPreview_AndLeavesNoEntry()
    {
        var cache = new PlateCardPreviewCache();
        var id = Guid.NewGuid();
        cache.Get(id, "r1", () => Plate(Vector4.One));

        Assert.Null(cache.Get(id, "r2", () => null));
        Assert.Equal(0, cache.Count);
    }

    // ---- The preview is the real Plate --------------------------------------------------------

    [Fact]
    public void DifferentPlates_ProduceDistinctPreviews_BeyondTheirBackground()
    {
        var cache = new PlateCardPreviewCache();
        var plain = cache.Get(Guid.NewGuid(), "r1", () => Plate(new Vector4(0.2f, 0.2f, 0.3f, 1f)))!;
        var ornamented = cache.Get(Guid.NewGuid(), "r1", () => Plate(new Vector4(0.2f, 0.2f, 0.3f, 1f), BuiltInComponentCatalog.CornerOrnamentAstrolabePivot))!;

        // Same background, different Plate: the previews differ in what's drawn and how it's framed.
        Assert.NotEqual(plain.Bounds, ornamented.Bounds);
        Assert.Empty(PaintedComponents(plain.Document));
        Assert.NotEmpty(PaintedComponents(ornamented.Document));
        Assert.NotEqual(PlateCardPreview.Fit(Card, plain.Bounds), PlateCardPreview.Fit(Card, ornamented.Bounds));
    }

    [Fact]
    public void ThePreview_DrawsTheWholePlate_NotJustItsBackground()
    {
        var document = Plate(new Vector4(0.2f, 0.2f, 0.3f, 1f), BuiltInComponentCatalog.CornerOrnamentBracket);
        var entry = new PlateCardPreviewCache().Get(Guid.NewGuid(), "r1", () => document)!;

        // The same paint plan every other surface draws: the portrait, the identity and section text, and Components.
        var plan = ComponentDocuments.Plan(entry.Document);
        Assert.Contains(plan, s => s.Element?.Role == ProfileElementRole.BasicPortrait);
        Assert.Contains(plan, s => s.Element?.Role == ProfileElementRole.BasicName);
        Assert.Contains(plan, s => s.Element?.Role == ProfileElementRole.BasicWorld);
        Assert.Contains(plan, s => s.Component is not null);

        // Finished rendering (no editor chrome, the Plate's own backdrop kept), text simplified only when tiny.
        var options = PlateCardPreview.Options;
        Assert.False(options.ShowElementBounds);
        Assert.Null(options.PlaceholderProvider);
        Assert.False(options.HideCanvasBackdrop);
        Assert.Equal(PlateCardPreview.TextBarThresholdPixels, options.TextAsBarsBelowPixelSize);
        Assert.Equal(0f, ProfileRenderOptions.Finished.TextAsBarsBelowPixelSize); // every other surface draws real text
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(120f)]
    public void TheFit_KeepsTheWholePlateAndItsOverflowInsideTheCard(float overflow)
    {
        var bounds = new CanvasBounds(new Vector2(-overflow, -overflow / 2f), new Vector2(1280 + overflow, 720 + overflow));

        var fit = PlateCardPreview.Fit(Card, bounds);
        var min = fit.CanvasOffset + (bounds.Min * fit.Scale);
        var max = fit.CanvasOffset + (bounds.Max * fit.Scale);

        Assert.True(fit.Scale > 0f);
        Assert.True(min.X >= -0.01f && min.Y >= -0.01f && max.X <= Card.X + 0.01f && max.Y <= Card.Y + 0.01f);
        Assert.True(Math.Abs(max.X - min.X - Card.X) < 0.01f || Math.Abs(max.Y - min.Y - Card.Y) < 0.01f);
        Assert.Equal(bounds.Size.X / bounds.Size.Y, (max.X - min.X) / (max.Y - min.Y), 3); // never stretched
    }

    [Fact]
    public void AtCardSize_SectionTextBecomesBars_TheNameCanStayText()
    {
        var document = Plate(Vector4.One);
        var scale = PlateCardPreview.Fit(Card, ProfileVisualBounds.Compute(document)).Scale;
        var name = (TextProfileElement)document.Elements.First(e => e.Role == ProfileElementRole.BasicName);
        var world = (TextProfileElement)document.Elements.First(e => e.Role == ProfileElementRole.BasicWorld);

        Assert.True(world.FontSize * scale < PlateCardPreview.TextBarThresholdPixels);
        Assert.True(scale is > 0.1f and < 0.3f);
        Assert.True(name.FontSize >= world.FontSize);
    }

    // ---- Text bars ----------------------------------------------------------------------------

    [Fact]
    public void TextBars_StayInsideTheirBox_AndFollowAlignment()
    {
        var bars = new List<(Vector2 Min, Vector2 Max)>();
        var position = new Vector2(100, 50);
        var size = new Vector2(300, 40);

        foreach (var alignment in new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right })
        {
            TextBars.Compute(position, size, "Odin", 20f, false, alignment, TextVerticalAlignment.Top, 4f, bars);
            var bar = Assert.Single(bars);
            Assert.True(bar.Min.X >= position.X + 4f - 0.01f && bar.Max.X <= position.X + size.X - 4f + 0.01f);
            Assert.True(bar.Min.Y >= position.Y && bar.Max.Y <= position.Y + size.Y);
            Assert.Equal(40f, bar.Max.X - bar.Min.X, 3); // 4 characters x half an em

            var expectedLeft = alignment switch
            {
                TextAlignment.Center => position.X + 4f + ((292f - 40f) / 2f),
                TextAlignment.Right => position.X + 4f + 292f - 40f,
                _ => position.X + 4f,
            };
            Assert.Equal(expectedLeft, bar.Min.X, 3);
        }
    }

    [Fact]
    public void TextBars_WrapIntoLines_BoundedByTheBoxHeight()
    {
        var bars = new List<(Vector2 Min, Vector2 Max)>();
        var text = new string('x', 200);

        TextBars.Compute(Vector2.Zero, new Vector2(208, 60), text, 16f, true, TextAlignment.Left, TextVerticalAlignment.Top, 4f, bars);

        Assert.Equal(2, bars.Count); // (60 - 8) / 20 per line
        Assert.All(bars, b => Assert.True(b.Max.X <= 204.01f && b.Max.Y <= 60f));
        Assert.True(bars[1].Min.Y > bars[0].Max.Y);

        TextBars.Compute(Vector2.Zero, new Vector2(208, 60), text, 16f, false, TextAlignment.Left, TextVerticalAlignment.Top, 4f, bars);
        Assert.Single(bars); // no wrapping: one clipped line
    }

    [Fact]
    public void TextBars_NothingToShow_DrawsNothing()
    {
        var bars = new List<(Vector2 Min, Vector2 Max)> { (Vector2.Zero, Vector2.One) };

        TextBars.Compute(Vector2.Zero, new Vector2(100, 30), string.Empty, 20f, false, TextAlignment.Left, TextVerticalAlignment.Top, 4f, bars);
        Assert.Empty(bars);
        TextBars.Compute(Vector2.Zero, new Vector2(4, 30), "Odin", 20f, false, TextAlignment.Left, TextVerticalAlignment.Top, 4f, bars);
        Assert.Empty(bars);
    }

    [Fact]
    public void Previewing_NeverChangesThePlate()
    {
        var document = Plate(new Vector4(0.3f, 0.4f, 0.5f, 1f), BuiltInComponentCatalog.CornerOrnamentAstrolabePivot);
        var json = Persistence.PlateDocuments.ToJson(document).ToJsonString();

        var entry = new PlateCardPreviewCache().Get(Guid.NewGuid(), "r1", () => document)!;
        PlateCardPreview.Fit(Card, entry.Bounds);
        var bars = new List<(Vector2 Min, Vector2 Max)>();
        foreach (var text in document.Elements.OfType<TextProfileElement>())
        {
            TextBars.Compute(text.Position, text.Size, text.Text, text.FontSize, text.Wrap, text.Alignment, text.VerticalAlignment, TextProfileElement.LayoutPadding, bars);
        }

        Assert.Equal(json, Persistence.PlateDocuments.ToJson(document).ToJsonString());
    }

    private static List<PaintStep> PaintedComponents(ProfileDocument document) =>
        ComponentDocuments.Plan(document).Where(s => s.Component is not null).ToList();
}
