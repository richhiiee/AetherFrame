using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Regression coverage for the "Components V1 flattens Theme backgrounds" report. Diagnosis found
/// no code path where Components could reach this: <see cref="ComponentPaintPlan.Build"/> never
/// reads or writes <see cref="ProfileDocument.Background"/> (the background is composed entirely by
/// <c>ProfileBackgroundRenderer</c>, called once, before the element/Component paint loop, unchanged
/// by Components V1), and every built-in Plate Frame / Portrait Component is either an edge-only
/// border, artwork whose interior is transparent, or scoped to the portrait's own rect — never a
/// canvas-covering fill. (A Background Component is the one deliberate canvas-covering kind, and it
/// paints under everything but still never touches the Plate's own background.) These tests lock in
/// both guarantees at the one level Components' render planning is testable without a live ImGui
/// context (<c>Windows/</c> and <c>UI/Rendering/</c> proper aren't compiled into this project).
/// </summary>
public class ComponentBackgroundRegressionTests
{
    private static ProfileThemePreset GradientTheme => ProfileThemePresets.Find("Warm")!;

    private static ProfileDocument GradientDocument()
    {
        var document = ComponentDocuments.WithAnchors();
        BasicDocuments.Editor(document).ApplyTheme(GradientTheme);
        return document;
    }

    // ---------------------------------------------------------------- the paint plan never touches Background

    [Fact]
    public void GradientTheme_ThroughFullPaintPlan_RemainsGradient()
    {
        var document = GradientDocument();
        var before = document.Background!.Clone();

        ComponentDocuments.Plan(document);

        Assert.True(document.Background!.ContentEquals(before));
        Assert.Equal(ProfileBackgroundMode.LinearGradient, document.Background.Mode);
    }

    [Fact]
    public void GradientThemePlusPattern_ThroughFullPaintPlan_RemainsGradientPlusOverlay()
    {
        var document = GradientDocument();
        document.Background!.Texture = ProfileBackgroundTexture.Dots;
        var before = document.Background.Clone();

        ComponentDocuments.Plan(document);

        Assert.True(document.Background!.ContentEquals(before));
        Assert.Equal(ProfileBackgroundMode.LinearGradient, document.Background.Mode);
        Assert.Equal(ProfileBackgroundTexture.Dots, document.Background.Texture);
    }

    [Theory]
    [MemberData(nameof(EachBuiltInDefinitionId))]
    public void ComponentPresent_DoesNotChangeBackgroundMode(string definitionId)
    {
        var document = GradientDocument();
        var before = document.Background!.Clone();
        document.Components = [ComponentDocuments.Of(definitionId)];

        ComponentDocuments.Plan(document);

        Assert.True(document.Background!.ContentEquals(before));
    }

    [Fact]
    public void EveryComponentKindAtOnce_DoesNotChangeBackgroundMode()
    {
        var document = GradientDocument();
        var before = document.Background!.Clone();
        document.Components = ComponentDocuments.OneOfEach();

        ComponentDocuments.Plan(document);

        Assert.True(document.Background!.ContentEquals(before));
    }

    [Fact]
    public void NoComponents_MatchesAcceptedRendererBehavior_BackgroundAndElementOrderUnchanged()
    {
        var withComponents = GradientDocument();
        withComponents.Components = ComponentDocuments.OneOfEach();
        var withoutComponents = GradientDocument();
        withoutComponents.Components = null;

        var planWithout = ComponentDocuments.Plan(withoutComponents);

        // No Components: the plan is exactly the element order (the pre-Components behavior),
        // and the background is whatever ApplyTheme produced — neither touched by planning.
        Assert.Equal(withoutComponents.Elements.OrderBy(e => e.ZIndex).Select(e => e.Id), planWithout.Select(s => s.Element!.Id));
        Assert.Equal(ProfileBackgroundMode.LinearGradient, withoutComponents.Background!.Mode);
    }

    public static IEnumerable<object[]> EachBuiltInDefinitionId() =>
        BuiltInComponentCatalog.All.Select(d => new object[] { d.Id });

    // ---------------------------------------------------------------- geometry never covers the canvas center

    [Theory]
    [MemberData(nameof(PlateFrameDefinitionIds))]
    public void PlateFrame_NeverCoversTheCanvasCenter(string definitionId)
    {
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(definitionId)];

        var steps = ComponentDocuments.Plan(document);
        var frameStep = Assert.Single(steps, s => !s.IsElement);

        AssertNoPrimitiveCoversCenter(document, frameStep);
    }

    [Theory]
    [MemberData(nameof(PortraitComponentDefinitionIds))]
    public void PortraitComponent_NeverCoversTheCanvasCenter(string definitionId)
    {
        // WithAnchors' portrait spans x:[40,440] — well clear of the canvas center (640, 360) on a
        // 1280x720 canvas — so a Portrait Frame/Overlay scoped to the portrait can never reach it.
        var document = ComponentDocuments.WithAnchors();
        document.Components = [ComponentDocuments.Of(definitionId)];

        var steps = ComponentDocuments.Plan(document);
        var componentSteps = steps.Where(s => !s.IsElement).ToList();
        Assert.NotEmpty(componentSteps);

        foreach (var step in componentSteps)
        {
            AssertNoPrimitiveCoversCenter(document, step);
        }
    }

    public static IEnumerable<object[]> PlateFrameDefinitionIds() =>
        BuiltInComponentCatalog.OfKind(PlateComponentKind.PlateFrame).Select(d => new object[] { d.Id });

    public static IEnumerable<object[]> PortraitComponentDefinitionIds() =>
        BuiltInComponentCatalog.All
            .Where(d => d.Kind is PlateComponentKind.PortraitFrame or PlateComponentKind.PortraitOverlay)
            .Where(d => !d.RequiresAsset) // Custom Image needs an AssetId; without one it's MissingImage and never drawn at all.
            .Select(d => new object[] { d.Id });

    private static void AssertNoPrimitiveCoversCenter(ProfileDocument document, PaintStep step)
    {
        var primitives = new List<ComponentPrimitive>();
        ComponentGeometry.Build(document, step.Component!, step.Definition!, step.Placement, primitives);

        var center = new Vector2(document.CanvasWidth, document.CanvasHeight) / 2f;
        foreach (var primitive in primitives)
        {
            if (primitive.Kind == ComponentPrimitiveKind.Art && step.Definition!.Art is { } art)
            {
                // Artwork spans its box; what reaches the center is its (transparent) interior.
                Assert.True(ArtAlphaAt(art, primitive, center) <= 1, $"{art.Id} is not see-through at the canvas center");
                continue;
            }

            Assert.False(BoundsContain(primitive, center));
        }
    }

    /// <summary>The largest alpha of <paramref name="art"/>'s texels within 8 texels of where
    /// <paramref name="point"/> falls in the (unrotated) quad it is drawn over; 0 outside it.</summary>
    private static int ArtAlphaAt(BuiltInArtAsset art, ComponentPrimitive quad, Vector2 point)
    {
        if (!BoundsContain(quad, point))
        {
            return 0;
        }

        using var stream = typeof(ComponentBackgroundRegressionTests).Assembly.GetManifestResourceStream(art.ResourceName);
        Assert.NotNull(stream);
        using var buffer = new System.IO.MemoryStream();
        stream!.CopyTo(buffer);
        var image = Domain.Rendering.BundledArtImage.DecodePng(buffer.ToArray());

        var u = (point.X - quad.A.X) / (quad.B.X - quad.A.X);
        var v = (point.Y - quad.A.Y) / (quad.D.Y - quad.A.Y);
        var cx = (int)(u * image.Width);
        var cy = (int)(v * image.Height);
        var max = 0;
        for (var y = Math.Max(0, cy - 8); y <= Math.Min(image.Height - 1, cy + 8); y++)
        {
            for (var x = Math.Max(0, cx - 8); x <= Math.Min(image.Width - 1, cx + 8); x++)
            {
                max = Math.Max(max, image.Rgba[(((y * image.Width) + x) * 4) + 3]);
            }
        }

        return max;
    }

    /// <summary>Axis-aligned bounding box of the primitive's four corners contains <paramref name="point"/>.
    /// A loose (bounding-box, not exact-shape) check — sufficient here since every failure mode this
    /// guards against (a fill reaching the canvas center) would fail even this generous a test.</summary>
    private static bool BoundsContain(ComponentPrimitive p, Vector2 point)
    {
        var minX = Math.Min(Math.Min(p.A.X, p.B.X), Math.Min(p.C.X, p.D.X));
        var maxX = Math.Max(Math.Max(p.A.X, p.B.X), Math.Max(p.C.X, p.D.X));
        var minY = Math.Min(Math.Min(p.A.Y, p.B.Y), Math.Min(p.C.Y, p.D.Y));
        var maxY = Math.Max(Math.Max(p.A.Y, p.B.Y), Math.Max(p.C.Y, p.D.Y));
        return point.X > minX && point.X < maxX && point.Y > minY && point.Y < maxY;
    }
}
