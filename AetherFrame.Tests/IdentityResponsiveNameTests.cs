using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The Basic Identity Header puts the name first: it renders at its normal size whenever it fits
/// the region on its own, an inline title reflows under it rather than taking its width, the title
/// yields before the name, and a name too long for the region is reduced at most to 90% (then
/// wrapped) — one rule (<see cref="BasicNameFit"/>) shared by the layout and the renderer.
/// </summary>
public class IdentityResponsiveNameTests
{
    // A deterministic stand-in for the font: every character is 0.55 em wide.
    private const float EmPerChar = 0.55f;
    private const float Pad = TextProfileElement.LayoutPadding;
    private const float Inset = (2f * Pad) + 2f;

    private static float Measure(TextProfileElement element) => Measure(element.GetDisplayText(), element.FontSize);

    private static float Measure(string text, float fontSize) => text.Length * fontSize * EmPerChar;

    private static int? CountLines(TextProfileElement element, float fontSize, float maxWidth) =>
        FakeTextWrap.CountLines(element.GetDisplayText(), fontSize * EmPerChar, maxWidth);

    public static readonly string[] Names =
    [
        "Al Bo",                                                  // short
        "Hero Adventurer",                                        // medium
        "Wolfgang Aaaberg-Longshadow",                            // long: fits the region alone, not beside a title
        "Richhiiee the Extraordinarily Long Named Adventurer",    // very long: wider than the region on its own
    ];

    public const string PracticallyPelupelu = "Practically Pelupelu";

    public static readonly string?[] Titles =
    [
        null,                                                    // absent
        "the Brave",                                             // short
        PracticallyPelupelu,                                     // the in-game screenshot's title
        "Warrior of Light, Darkness and Everything In Between",  // long
    ];

    public static IEnumerable<object?[]> Matrix()
    {
        foreach (var orientation in new[] { AdventurePlateOrientation.Normal, AdventurePlateOrientation.Mirrored })
        {
            foreach (var layout in new[] { IdentityTitleLayout.InlineAfter, IdentityTitleLayout.InlineBefore, IdentityTitleLayout.Subtitle, IdentityTitleLayout.Classic })
            {
                for (var n = 0; n < Names.Length; n++)
                {
                    for (var t = 0; t < Titles.Length; t++)
                    {
                        yield return [orientation, layout, n, t];
                    }
                }
            }
        }
    }

    private static (ProfileDocument Document, TextProfileElement Name, TextProfileElement? Title) Plate(
        AdventurePlateOrientation orientation, IdentityTitleLayout layout, string name, string? title, bool titleVisible = true)
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        document.BasicPlate!.Orientation = orientation;
        var (position, width) = IdentityHeaderRules.DefaultRegion(document);
        document.BasicIdentity!.RegionPosition = position;
        document.BasicIdentity.RegionWidth = width;
        document.BasicIdentity.Layout = layout;

        var nameElement = BasicSections.FindText(document, ProfileElementRole.BasicName)!;
        nameElement.Text = name;

        TextProfileElement? titleElement = null;
        if (title is not null)
        {
            titleElement = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
            titleElement.Text = title;
            titleElement.Visible = titleVisible;
            titleElement.ZIndex = 50;
            document.Elements.Add(titleElement);
        }

        Assert.True(IdentityHeaderRules.Place(document, e => Measure(e), CountLines));
        return (document, nameElement, titleElement);
    }

    private static float Right(ProfileElement e) => e.Position.X + e.Size.X;

    private static float Bottom(ProfileElement e) => e.Position.Y + e.Size.Y;

    private static bool Overlap(ProfileElement a, ProfileElement b) =>
        a.Position.X < Right(b) - 0.01f && b.Position.X < Right(a) - 0.01f && a.Position.Y < Bottom(b) - 0.01f && b.Position.Y < Bottom(a) - 0.01f;

    /// <summary>The size the renderer draws the name at in its final box (the renderer calls exactly this rule).</summary>
    private static (float Size, bool Wrap) Rendered(TextProfileElement name) => BasicNameFit.ForBox(name, Measure(name));

    /// <summary>The rendered name's text fits its box: on one line, or wrapped within its height.</summary>
    private static void AssertNameFitsItsBox(TextProfileElement name)
    {
        var (size, wrap) = Rendered(name);
        var available = name.Size.X - (2f * Pad);
        if (!wrap)
        {
            Assert.True(Measure(name.GetDisplayText(), size) <= available + 0.01f, $"{name.Text}: one line {Measure(name.GetDisplayText(), size):0.0} > {available:0.0}");
            return;
        }

        var lines = FakeTextWrap.CountLines(name.GetDisplayText(), size * EmPerChar, available);
        var block = size + ((lines - 1) * size * name.LineSpacing);
        Assert.True(block <= name.Size.Y - (2f * Pad) + 0.01f, $"{name.Text}: {lines} lines {block:0.0} > {name.Size.Y - (2f * Pad):0.0}");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void TheNameComesFirst(AdventurePlateOrientation orientation, IdentityTitleLayout layout, int nameIndex, int titleIndex)
    {
        var (document, name, title) = Plate(orientation, layout, Names[nameIndex], Titles[titleIndex]);
        var identity = document.BasicIdentity!;
        var left = identity.RegionPosition.X;
        var right = left + identity.RegionWidth;
        var natural = Measure(name) + Inset;

        // The final rendered size: normal whenever the name fits the region on its own, and never
        // below the readable minimum (the generic auto-fit minimum plays no part).
        var (size, _) = Rendered(name);
        if (natural <= identity.RegionWidth)
        {
            Assert.Equal(name.FontSize, size);
        }

        Assert.True(size >= (name.FontSize * BasicNameFit.MinimumRatio) - 0.001f, $"{size:0.00} of {name.FontSize}");
        AssertNameFitsItsBox(name);
        Assert.True(name.Position.X >= left - 0.01f && Right(name) <= right + 0.01f);

        if (title is null)
        {
            return;
        }

        // The title never overlaps the name and never leaves the header region; it never takes
        // width from the name: inline, its own natural width (at most the region's); stacked, the region's.
        Assert.False(Overlap(name, title), $"{name.Text} / {title.Text}");
        Assert.True(title.Position.X >= left - 0.01f && Right(title) <= right + 0.01f);
        var inline = layout is IdentityTitleLayout.InlineAfter or IdentityTitleLayout.InlineBefore;
        Assert.Equal(inline ? Math.Min(Measure(title) + Inset, identity.RegionWidth) : identity.RegionWidth, title.Size.X, 3);

        var gap = IdentityHeaderLayout.InlineBoxGap(name.FontSize);
        switch (layout)
        {
            case IdentityTitleLayout.InlineAfter or IdentityTitleLayout.InlineBefore
                when natural + gap + Measure(title) + Inset <= identity.RegionWidth:
                // Comfortable room: on the name's line, one gap from it.
                Assert.True(title.Position.Y < Bottom(name) && name.Position.Y < Bottom(title));
                if (layout == IdentityTitleLayout.InlineAfter)
                {
                    Assert.Equal(Right(name) + gap, title.Position.X, 3);
                }
                else
                {
                    Assert.Equal(Right(title) + gap, name.Position.X, 3);
                }

                break;

            case IdentityTitleLayout.InlineAfter or IdentityTitleLayout.InlineBefore:
                // Not enough room: the title reflows under the name, aligned with it (the name keeps the primary line).
                Assert.Equal(identity.RegionPosition.Y, name.Position.Y, 3);
                Assert.True(title.Position.Y > Bottom(name));
                Assert.Equal(name.Position.X, title.Position.X, 3);
                break;

            case IdentityTitleLayout.Classic:
                Assert.True(Bottom(title) < name.Position.Y);
                break;

            default:
                Assert.True(title.Position.Y > Bottom(name));
                break;
        }
    }

    // ---- The in-game screenshot ------------------------------------------------------------------

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter)]
    [InlineData(AdventurePlateOrientation.Mirrored, IdentityTitleLayout.InlineAfter)]
    [InlineData(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineBefore)]
    [InlineData(AdventurePlateOrientation.Mirrored, IdentityTitleLayout.InlineBefore)]
    public void Screenshot_VeryLongName_WithPracticallyPelupelu_Inline(AdventurePlateOrientation orientation, IdentityTitleLayout layout)
    {
        foreach (var text in new[] { Names[2], Names[3] })
        {
            var (document, name, title) = Plate(orientation, layout, text, PracticallyPelupelu);
            var identity = document.BasicIdentity!;

            // Readable: at least 90% of its normal size.
            var (size, _) = Rendered(name);
            Assert.True(size >= (name.FontSize * BasicNameFit.MinimumRatio) - 0.001f);
            if (Measure(name) + Inset <= identity.RegionWidth)
            {
                Assert.Equal(name.FontSize, size); // fits alone: full size, the title simply reflows
            }

            // The title moved under the name at its own full width instead of stealing the name's.
            Assert.True(title!.Position.Y > Bottom(name));
            Assert.Equal(Measure(title) + Inset, title.Size.X, 3);
            Assert.True(size > title.FontSize); // the hierarchy holds: the name is the larger text

            Assert.False(Overlap(name, title));
            foreach (var element in new ProfileElement[] { name, title })
            {
                Assert.True(element.Position.X >= identity.RegionPosition.X - 0.01f);
                Assert.True(Right(element) <= identity.RegionPosition.X + identity.RegionWidth + 0.01f);
            }
        }
    }

    // ---- Name size and title yielding ---------------------------------------------------------------

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public void ShortNameAndShortTitle_AreUnchanged(AdventurePlateOrientation orientation)
    {
        var (document, name, title) = Plate(orientation, IdentityTitleLayout.InlineAfter, "Al Bo", "the Brave");

        // Exactly the earlier placement for a header that fits: natural boxes, one line, one gap apart.
        Assert.Equal(document.BasicIdentity!.RegionPosition.X, name.Position.X, 3);
        Assert.Equal(Measure(name) + Inset, name.Size.X, 3);
        Assert.Equal(IdentityHeaderLayout.BoxHeight(name.FontSize), name.Size.Y);
        Assert.Equal(Measure(title!) + Inset, title!.Size.X, 3);
        Assert.Equal(Right(name) + IdentityHeaderLayout.InlineBoxGap(name.FontSize), title.Position.X, 3);
        Assert.Equal(name.FontSize, Rendered(name).Size);
    }

    [Fact]
    public void ALongTitle_YieldsBeforeTheName()
    {
        // A title wider than the whole region on its own line: the title's box is the region (its
        // own auto fit reduces it); the name keeps its full size and its own box.
        var endless = string.Concat(Enumerable.Repeat("Warrior of Light ", 6)).Trim();
        var (document, name, title) = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, "Hero Adventurer", endless);

        Assert.Equal(name.FontSize, Rendered(name).Size);
        Assert.Equal(Measure(name) + Inset, name.Size.X, 3);
        Assert.Equal(document.BasicIdentity!.RegionWidth, title!.Size.X, 3);
        Assert.True(title.Position.Y > Bottom(name));
    }

    [Theory]
    [InlineData(IdentityTitleLayout.InlineAfter)]
    [InlineData(IdentityTitleLayout.Subtitle)]
    public void NoTitle_TheNameHasTheWholeRegion(IdentityTitleLayout layout)
    {
        foreach (var absent in new[] { true, false })
        {
            var (document, name, _) = Plate(AdventurePlateOrientation.Normal, layout, Names[2], absent ? null : "the Brave", titleVisible: false);
            Assert.Equal(document.BasicIdentity!.RegionPosition.Y, name.Position.Y, 3);
            Assert.Equal(name.FontSize, Rendered(name).Size);
        }
    }

    [Fact]
    public void ANameWiderThanTheRegion_IsReducedAtMostTo90Percent_ThenWraps()
    {
        var probe = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, "x", null);
        var region = probe.Document.BasicIdentity!.RegionWidth;
        var fontSize = probe.Name.FontSize;

        // Slightly too wide (between 100% and 90%): reduced continuously, by just the amount needed.
        var length = 1;
        while (Measure(new string('W', length + 1), fontSize) + Inset <= region / 0.95f)
        {
            length++;
        }

        var (_, reduced, _) = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, new string('W', length), null);
        Assert.True(Measure(reduced) + Inset > region);
        var (reducedSize, reducedWrap) = Rendered(reduced);
        Assert.False(reducedWrap);
        Assert.InRange(reducedSize, reduced.FontSize * BasicNameFit.MinimumRatio, reduced.FontSize - 0.001f);
        Assert.Equal(region - (2f * Pad), Measure(reduced.GetDisplayText(), reducedSize), 2); // exactly fills the line

        // Far too wide: 90%, word-wrapped in a taller box — never smaller, never cut.
        var (_, wrapped, _) = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, Names[3], null);
        var (wrappedSize, wrap) = Rendered(wrapped);
        Assert.True(wrap);
        Assert.Equal(wrapped.FontSize * BasicNameFit.MinimumRatio, wrappedSize, 3);
        Assert.True(wrapped.Size.Y > IdentityHeaderLayout.BoxHeight(wrapped.FontSize));
        Assert.Equal(region, wrapped.Size.X, 3);
        AssertNameFitsItsBox(wrapped);
    }

    [Fact]
    public void BasicNameFit_IsContinuous_AndFloored()
    {
        var previous = 64f;
        for (var natural = 100f; natural <= 2000f; natural += 10f)
        {
            var (size, wrap) = BasicNameFit.Resolve(64f, natural, 750f);
            Assert.True(size <= previous + 0.001f);
            Assert.True(size >= (64f * BasicNameFit.MinimumRatio) - 0.001f);
            Assert.Equal(natural * BasicNameFit.MinimumRatio > 750f, wrap);
            previous = size;
        }
    }

    // ---- The renderer: one authoritative size, no second shrink ----------------------------------

    [Fact]
    public void TheRenderer_NeverDoubleShrinksTheBasicName_EvenInAStaleBox()
    {
        // A box left over from a short name (e.g. edited without a re-layout): the generic auto fit
        // would crush a long name toward its 10 px minimum; the Basic rule keeps it at 90%.
        var (_, name, _) = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, "Al Bo", "the Brave");
        name.Text = Names[3];

        Assert.True(BasicNameFit.Applies(name));
        Assert.True(name.AutoFitMinimumSize < name.FontSize * 0.5f); // what the generic fit would have allowed
        Assert.Equal(name.FontSize * BasicNameFit.MinimumRatio, Rendered(name).Size, 3);

        // Generic text keeps the generic behavior.
        var advanced = new TextProfileElement { Text = name.Text, FontSize = name.FontSize, AutoFitText = true, Size = name.Size };
        Assert.False(BasicNameFit.Applies(advanced));

        // A name the player set to wrap, or without auto fit, keeps the generic behavior too.
        name.Wrap = true;
        Assert.False(BasicNameFit.Applies(name));
        name.Wrap = false;
        name.AutoFitText = false;
        Assert.False(BasicNameFit.Applies(name));
    }

    [Fact]
    public void ACustomizedHeader_GrowsItsNameBox_WhenABasicEditLengthensTheName()
    {
        var (document, name, title) = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, "Al Bo", "the Brave");
        name.Position += new Vector2(0f, 12f); // moved in the Advanced editor: Basic no longer places it
        Assert.True(IdentityHeaderRules.IsCustomized(document));
        var position = name.Position;
        var titleBefore = (title!.Position, title.Size);

        name.Text = Names[2];
        Assert.True(IdentityHeaderRules.KeepNameReadable(document, e => Measure(e), CountLines));

        Assert.Equal(position, name.Position);                  // never moved
        Assert.Equal(name.FontSize, Rendered(name).Size);         // full size again
        Assert.Equal(titleBefore, (title.Position, title.Size));  // nothing else touched
        Assert.False(IdentityHeaderRules.KeepNameReadable(document, e => Measure(e), CountLines)); // idempotent

        // A shorter name never shrinks the box the player has.
        var size = name.Size;
        name.Text = "Al";
        Assert.False(IdentityHeaderRules.KeepNameReadable(document, e => Measure(e), CountLines));
        Assert.Equal(size, name.Size);
    }

    [Fact]
    public void Mirrored_BehavesTheSame_InItsOwnRegion()
    {
        foreach (var text in Names)
        {
            var (normalDoc, normalName, normalTitle) = Plate(AdventurePlateOrientation.Normal, IdentityTitleLayout.InlineAfter, text, PracticallyPelupelu);
            var (mirroredDoc, name, title) = Plate(AdventurePlateOrientation.Mirrored, IdentityTitleLayout.InlineAfter, text, PracticallyPelupelu);
            var shift = mirroredDoc.BasicIdentity!.RegionPosition.X - normalDoc.BasicIdentity!.RegionPosition.X;

            Assert.Equal(normalName.Size, name.Size);
            Assert.Equal(normalTitle!.Size, title!.Size);
            Assert.True(Vector2.Distance(normalName.Position + new Vector2(shift, 0f), name.Position) < 0.01f);
            Assert.True(Vector2.Distance(normalTitle.Position + new Vector2(shift, 0f), title.Position) < 0.01f);
        }
    }

    // ---- Name Backing ----------------------------------------------------------------------------

    private static ElementRect BackingRect(ProfileDocument document, Func<TextProfileElement, float?>? measure)
    {
        document.Components ??= [];
        document.Components.Add(new PlateComponent { Kind = PlateComponentKind.NameBacking, DefinitionId = BuiltInComponentCatalog.NameBackingBar, Visible = true });
        var drawn = document.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToList();
        var plan = new List<PaintStep>();
        ComponentPaintPlan.Build(document, drawn, BuiltInComponentCatalog.Instance, plan, measure);
        return plan.Single(s => s.Component?.Kind == PlateComponentKind.NameBacking).Placement.Rect;
    }

    [Theory]
    [InlineData(AdventurePlateOrientation.Normal)]
    [InlineData(AdventurePlateOrientation.Mirrored)]
    public void NameBacking_FollowsTheFinalNameGeometry(AdventurePlateOrientation orientation)
    {
        var unit = ComponentPaintPlan.Unit(BasicDocuments.Classic(FakeCharacter.Hero));
        var pad = new Vector2(ComponentPaintPlan.NameBackingPadX, ComponentPaintPlan.NameBackingPadY) * unit;

        // Short and long names, no title: the backing is the name's text plus padding.
        foreach (var text in new[] { "Al Bo", Names[2] })
        {
            var (document, name, _) = Plate(orientation, IdentityTitleLayout.Subtitle, text, null);
            var backing = BackingRect(document, e => Measure(e));
            Assert.Equal(Measure(name) + Inset + (2f * pad.X), backing.Size.X, 2);
            Assert.Equal(name.Position.X - pad.X, backing.Position.X, 2);
        }

        // A reflowed title: one coherent block over both lines.
        var (reflowed, longName, title) = Plate(orientation, IdentityTitleLayout.InlineAfter, Names[2], PracticallyPelupelu);
        var block = BackingRect(reflowed, e => Measure(e));
        Assert.Equal(longName.Position.X - pad.X, block.Position.X, 2);
        Assert.Equal(longName.Position.Y - pad.Y, block.Position.Y, 2);
        Assert.Equal(Math.Max(Right(longName), Right(title!)) + pad.X, block.Position.X + block.Size.X, 2);
        Assert.Equal(Bottom(title!) + pad.Y, block.Position.Y + block.Size.Y, 2);

        // A wrapped name: the backing covers its whole (taller) box.
        var (wrappedDoc, wrapped, _) = Plate(orientation, IdentityTitleLayout.InlineAfter, Names[3], null);
        var wrappedBacking = BackingRect(wrappedDoc, e => Measure(e));
        Assert.Equal(wrapped.Size.X + (2f * pad.X), wrappedBacking.Size.X, 2);
        Assert.Equal(wrapped.Size.Y + (2f * pad.Y), wrappedBacking.Size.Y, 2);

        // Without a measurer (fonts not ready): the whole boxes, as before.
        var (fallbackDoc, fallbackName, _) = Plate(orientation, IdentityTitleLayout.Subtitle, "Al Bo", null);
        Assert.Equal(fallbackName.Size.X + (2f * pad.X), BackingRect(fallbackDoc, null).Size.X, 2);
    }
}
