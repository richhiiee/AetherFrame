using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Identity layouts own their look, never the title's content: switching layouts swaps one
/// layout's (recorded, reversible) style for another's, and the title's text, prefix, and suffix
/// are always exactly what the user made them.
/// </summary>
public class IdentityLayoutStyleTests
{
    private static readonly IdentityTitleLayout[] Layouts = Enum.GetValues<IdentityTitleLayout>();

    private static async Task<BasicHarness> ClassicWithTitleAsync(string title = "the Warrior of Light")
    {
        var harness = await BasicHarness.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, new PlateStarterContent(FakeCharacter.Hero));
        harness.Identity.SetCustomTitle(title);
        harness.Identity.Commit();
        harness.Identity.SetLayout(IdentityTitleLayout.Classic);
        return harness;
    }

    private static TextProfileElement Title(BasicHarness harness) => BasicSections.FindText(harness.Document, ProfileElementRole.BasicTitle)!;

    // ---------------------------------------------------------------- the reported bug

    [Fact]
    public async Task ClassicToAccentToClassic_RestoresTheTitleExactly()
    {
        using var harness = await ClassicWithTitleAsync();
        var before = Title(harness).Clone();

        harness.Identity.SetLayout(IdentityTitleLayout.Accent);
        Assert.True(Title(harness).Italic);
        Assert.Equal(string.Empty, Title(harness).Prefix);
        Assert.Equal(string.Empty, Title(harness).Suffix);

        harness.Identity.SetLayout(IdentityTitleLayout.Classic);

        Assert.True(Title(harness).ContentEquals(before));
        Assert.Null(harness.Document.BasicIdentity!.LayoutStyle);
    }

    [Theory]
    [InlineData((int)IdentityTitleLayout.Subtitle)]
    [InlineData((int)IdentityTitleLayout.Badge)]
    [InlineData((int)IdentityTitleLayout.InlineBefore)]
    [InlineData((int)IdentityTitleLayout.InlineAfter)]
    [InlineData((int)IdentityTitleLayout.Classic)]
    public async Task LeavingAccent_LeavesNoAccentDecorationOrStyle(int targetValue)
    {
        var target = (IdentityTitleLayout)targetValue;
        using var harness = await ClassicWithTitleAsync();

        // Where the title ends up going to the target directly (from Classic to Classic changes
        // nothing and records no undo step)...
        var start = harness.Json();
        harness.Identity.SetLayout(target);
        var direct = Title(harness).Clone();
        if (harness.Json() != start)
        {
            harness.Session.Undo();
        }

        Assert.Equal(start, harness.Json());

        // ...is exactly where it ends up going there through Accent.
        harness.Identity.SetLayout(IdentityTitleLayout.Accent);
        harness.Identity.SetLayout(target);

        var title = Title(harness);
        Assert.True(title.ContentEquals(direct), $"Accent -> {target} left: italic={title.Italic} spacing={title.LetterSpacing} prefix='{title.Prefix}' suffix='{title.Suffix}'");
        Assert.Equal(string.Empty, title.Prefix);
        Assert.Equal(string.Empty, title.Suffix);
    }

    [Fact]
    public void Accent_IsItalicAndSpacing_WithNoDecorationGlyphs()
    {
        var look = IdentityHeaderRules.LayoutLook(IdentityTitleLayout.Accent, 43f)!;

        Assert.True(look.Italic);
        Assert.True(look.LetterSpacing > 0f);
        Assert.Null(look.FontSize);
        Assert.Null(look.Bold);
        Assert.Null(IdentityHeaderRules.LayoutLook(IdentityTitleLayout.Classic, 43f));
        Assert.Null(IdentityHeaderRules.LayoutLook(IdentityTitleLayout.Subtitle, 43f));
        Assert.Null(IdentityHeaderRules.LayoutLook(IdentityTitleLayout.InlineBefore, 43f));
        Assert.Null(IdentityHeaderRules.LayoutLook(IdentityTitleLayout.InlineAfter, 43f));
    }

    // ---------------------------------------------------------------- every transition

    [Fact]
    public async Task EveryLayoutTransition_KeepsTheTitleText_ByteForByte_AndNeverLeaksStyle()
    {
        const string text = "the «Wandering» Poet — of Æther";
        using var harness = await ClassicWithTitleAsync(text);

        // Back to the Classic starting point (a no-op transition records no undo step, so undo until there).
        var baseline = harness.Json();
        void BackToBaseline()
        {
            for (var i = 0; i < 4 && harness.Json() != baseline; i++)
            {
                harness.Session.Undo();
            }

            Assert.Equal(baseline, harness.Json());
        }

        // Reference: the title's state in each layout, reached directly from Classic.
        var reference = new Dictionary<IdentityTitleLayout, ProfileElement>();
        foreach (var layout in Layouts)
        {
            harness.Identity.SetLayout(layout);
            reference[layout] = Title(harness).Clone();
            BackToBaseline();
        }

        foreach (var from in Layouts)
        {
            foreach (var to in Layouts)
            {
                harness.Identity.SetLayout(from);
                harness.Identity.SetLayout(to);

                var title = Title(harness);
                Assert.Equal(text, title.Text);
                Assert.True(title.ContentEquals(reference[to]), $"{from} -> {to} carried style over");

                BackToBaseline();
            }
        }
    }

    [Fact]
    public async Task UserAuthoredPrefixAndSuffix_SurviveEveryLayout()
    {
        using var harness = await ClassicWithTitleAsync();
        harness.Identity.SetPrefix("«");
        harness.Identity.SetSuffix("»");

        foreach (var layout in Layouts.Concat(Layouts.Reverse()).Append(IdentityTitleLayout.Accent).Append(IdentityTitleLayout.Classic))
        {
            harness.Identity.SetLayout(layout);
            Assert.Equal("«", Title(harness).Prefix);
            Assert.Equal("»", Title(harness).Suffix);
        }

        harness.Basic.ResetSection(BasicSection.Identity);
        Assert.Equal("«", Title(harness).Prefix);
        Assert.Equal("»", Title(harness).Suffix);
    }

    [Fact]
    public async Task UserChangesMadeWhileInALayout_SurviveLeavingIt()
    {
        using var harness = await ClassicWithTitleAsync();
        var plain = Title(harness).Clone() as TextProfileElement;

        harness.Identity.SetLayout(IdentityTitleLayout.Badge);
        harness.Identity.EditStyle(ProfileElementRole.BasicTitle, e => e.FontSize = 30, continuous: false);
        harness.Identity.SetLayout(IdentityTitleLayout.Subtitle);

        Assert.Equal(30f, Title(harness).FontSize);             // the user's
        Assert.Equal(plain!.Bold, Title(harness).Bold);          // the layout's, undone
        Assert.Equal(plain.LetterSpacing, Title(harness).LetterSpacing);

        harness.Identity.SetLayout(IdentityTitleLayout.Accent);
        harness.Identity.EditStyle(ProfileElementRole.BasicTitle, e => e.Italic = false, continuous: false);
        harness.Identity.EditStyle(ProfileElementRole.BasicTitle, e => e.Color = new Vector4(1, 0, 0, 1), continuous: false);
        harness.Identity.SetLayout(IdentityTitleLayout.Classic);

        Assert.False(Title(harness).Italic);
        Assert.Equal(new Vector4(1, 0, 0, 1), Title(harness).Color);
        Assert.Equal(plain.LetterSpacing, Title(harness).LetterSpacing);
    }

    [Fact]
    public async Task LayoutChanges_NeverTouchAlignmentColorOutlineOrShadow()
    {
        using var harness = await ClassicWithTitleAsync();
        harness.Identity.EditStyle(ProfileElementRole.BasicTitle, e =>
        {
            e.Alignment = TextAlignment.Center;
            e.Color = new Vector4(0.2f, 0.9f, 0.4f, 0.8f);
            e.OutlineEnabled = true;
            e.OutlineThickness = 3f;
            e.ShadowEnabled = true;
            e.FontFamily = ProfileFontFamilies.AetherFrameSerif;
        }, continuous: false);
        var before = Title(harness).Clone() as TextProfileElement;

        foreach (var layout in Layouts)
        {
            harness.Identity.SetLayout(layout);
            var title = Title(harness);
            Assert.Equal(before!.Alignment, title.Alignment);
            Assert.Equal(before.Color, title.Color);
            Assert.Equal(before.OutlineEnabled, title.OutlineEnabled);
            Assert.Equal(before.OutlineThickness, title.OutlineThickness);
            Assert.Equal(before.ShadowEnabled, title.ShadowEnabled);
            Assert.Equal(before.FontFamily, title.FontFamily);
        }
    }

    // ---------------------------------------------------------------- undo, redo, dirty state

    [Fact]
    public async Task AccentTransitions_AreOneUndoStepEach_AndRedoExactly()
    {
        using var harness = await ClassicWithTitleAsync();
        var classic = harness.Json();

        harness.Identity.SetLayout(IdentityTitleLayout.Accent);
        var accent = harness.Json();
        harness.Identity.SetLayout(IdentityTitleLayout.Badge);
        var badge = harness.Json();

        harness.Session.Undo();
        Assert.Equal(accent, harness.Json());
        harness.Session.Undo();
        Assert.Equal(classic, harness.Json());
        harness.Session.Redo();
        Assert.Equal(accent, harness.Json());
        harness.Session.Redo();
        Assert.Equal(badge, harness.Json());
    }

    [Fact]
    public async Task AccentAndBack_AfterASave_LeavesNoPhantomDirtyState()
    {
        using var harness = await ClassicWithTitleAsync();
        await harness.Session.SaveProfileAsync();
        harness.Session.SyncWithCurrentProfile();
        Assert.False(harness.Session.IsDirty);

        harness.Identity.SetLayout(IdentityTitleLayout.Accent);
        Assert.True(harness.Session.IsDirty);
        harness.Identity.SetLayout(IdentityTitleLayout.Classic);

        Assert.False(harness.Session.IsDirty);
    }

    // ---------------------------------------------------------------- Plates the bug already touched

    /// <summary>A Plate an earlier build saved in Accent: italic title with the "✦" decoration, no look record.</summary>
    private static ProfileDocument PlateSavedInOldAccent()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        title.Text = "the Wanderer";
        title.Italic = true;
        title.Prefix = "✦";
        title.Suffix = "✦";
        title.ZIndex = 50;
        document.Elements.Add(title);
        document.BasicIdentity!.Layout = IdentityTitleLayout.Accent;
        document.BasicIdentity.TitleSource = IdentityTitleSource.Custom;
        document.BasicIdentity.CustomTitle = "the Wanderer";
        document.BasicIdentity.LayoutStyle = null;
        IdentityHeaderRules.Place(document, _ => 100f);
        return document;
    }

    [Fact]
    public async Task AnAffectedPlate_OpensUnchanged_AndItsSymbolsAreFlaggedButKept()
    {
        var document = PlateSavedInOldAccent();
        var json = JsonSerializer.Serialize(document, JsonOptions.Default);
        using var harness = await BasicHarness.OpenJsonAsync(json, document.ProfileId);

        harness.SimulateBasicFrame();
        Assert.Equal(json, harness.Fixture.ReadPlateJson(document.ProfileId));
        Assert.False(harness.Session.IsDirty);
        Assert.False(IdentityHeaderRules.IsDrawableDecoration(Title(harness).Prefix));

        // Leaving Accent undoes its italic as it always did, but the symbols can't be proven to be
        // the layout's (a user could have picked them), so they stay until removed explicitly.
        harness.Identity.SetLayout(IdentityTitleLayout.Classic);
        Assert.False(Title(harness).Italic);
        Assert.Equal("✦", Title(harness).Prefix);
        Assert.Equal("the Wanderer", Title(harness).Text);

        harness.Identity.ClearDecoration();
        Assert.Equal(string.Empty, Title(harness).Prefix);
        Assert.Equal(string.Empty, Title(harness).Suffix);
        harness.Session.Undo();
        Assert.Equal("✦", Title(harness).Prefix);
    }

    [Fact]
    public async Task APlateSavedInOldBadge_LeavesBadgeLikeBefore_ButKeepsAnyUserChange()
    {
        var document = BasicDocuments.Classic(FakeCharacter.Hero);
        var title = IdentityHeaderRules.Create(ProfileElementRole.BasicTitle, document, null);
        var nameSize = BasicSections.FindText(document, ProfileElementRole.BasicName)!.FontSize;
        var badge = IdentityHeaderRules.LayoutLook(IdentityTitleLayout.Badge, nameSize)!;
        badge.ApplyTo(title);
        title.Text = "the Brave";
        title.Color = new Vector4(1, 0, 0, 1);
        title.ZIndex = 50;
        document.Elements.Add(title);
        document.BasicIdentity!.Layout = IdentityTitleLayout.Badge;
        document.BasicIdentity.LayoutStyle = null;
        using var harness = await BasicHarness.OpenDocumentAsync(document);

        harness.Identity.SetLayout(IdentityTitleLayout.Subtitle);

        var plainSize = MathF.Round(nameSize * IdentityHeaderRules.TitleSizeRatio);
        Assert.Equal(plainSize, Title(harness).FontSize);
        Assert.False(Title(harness).Bold);
        Assert.Equal(0f, Title(harness).LetterSpacing);
        Assert.Equal(new Vector4(1, 0, 0, 1), Title(harness).Color);
    }

    // ---------------------------------------------------------------- glyph coverage

    [Fact]
    public void EveryOfferedDecoration_IsInEveryBundledFont()
    {
        var fonts = BundledFonts();
        Assert.Equal(12, fonts.Count);

        foreach (var symbol in IdentityHeaderRules.DecorationSymbols.Where(s => s.Length > 0))
        {
            Assert.True(IdentityHeaderRules.IsDrawableDecoration(symbol));
            foreach (var (name, bytes) in fonts)
            {
                Assert.All(symbol, c => Assert.True(TrueType.HasGlyph(bytes, c), $"{name} lacks U+{(int)c:X4} ({c})"));
            }
        }
    }

    [Fact]
    public void TheOldAccentSymbol_IsMissingFromTheBundledFonts_WhichIsWhyItShowedAsAQuestionMark()
    {
        foreach (var (name, bytes) in BundledFonts())
        {
            Assert.False(TrueType.HasGlyph(bytes, '✦'), $"{name} unexpectedly has U+2726");
            Assert.True(TrueType.HasGlyph(bytes, 'A'), $"{name}: cmap reader sanity check");
        }

        Assert.False(IdentityHeaderRules.IsDrawableDecoration("✦"));
        Assert.True(IdentityHeaderRules.IsDrawableDecoration(string.Empty));
    }

    private static List<(string Name, byte[] Bytes)> BundledFonts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AetherFrame", "Fonts")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Directory.GetFiles(Path.Combine(directory!.FullName, "AetherFrame", "Fonts"), "*.ttf")
            .Select(path => (Path.GetFileName(path), File.ReadAllBytes(path)))
            .ToList();
    }

    /// <summary>Just enough of the TrueType "cmap" table (formats 4 and 12) to ask whether a font maps a character.</summary>
    private static class TrueType
    {
        internal static bool HasGlyph(byte[] font, int codepoint)
        {
            var tableCount = U16(font, 4);
            var cmap = -1;
            for (var i = 0; i < tableCount; i++)
            {
                var record = 12 + (16 * i);
                if (font[record] == 'c' && font[record + 1] == 'm' && font[record + 2] == 'a' && font[record + 3] == 'p')
                {
                    cmap = (int)U32(font, record + 8);
                }
            }

            Assert.True(cmap >= 0, "no cmap table");
            var subtables = U16(font, cmap + 2);
            for (var i = 0; i < subtables; i++)
            {
                var record = cmap + 4 + (8 * i);
                var platform = U16(font, record);
                var subtable = cmap + (int)U32(font, record + 4);
                var format = U16(font, subtable);
                if (platform is 0 or 3 && format == 4 && Format4(font, subtable, codepoint))
                {
                    return true;
                }

                if (platform is 0 or 3 && format == 12 && Format12(font, subtable, codepoint))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Format4(byte[] font, int table, int codepoint)
        {
            if (codepoint > 0xFFFF)
            {
                return false;
            }

            var segX2 = U16(font, table + 6);
            var ends = table + 14;
            var starts = ends + segX2 + 2;
            var deltas = starts + segX2;
            var rangeOffsets = deltas + segX2;
            for (var s = 0; s < segX2 / 2; s++)
            {
                var end = U16(font, ends + (2 * s));
                if (end < codepoint)
                {
                    continue;
                }

                var start = U16(font, starts + (2 * s));
                if (start > codepoint)
                {
                    return false;
                }

                var delta = U16(font, deltas + (2 * s));
                var rangeOffsetAt = rangeOffsets + (2 * s);
                var rangeOffset = U16(font, rangeOffsetAt);
                if (rangeOffset == 0)
                {
                    return ((codepoint + delta) & 0xFFFF) != 0;
                }

                var glyph = U16(font, rangeOffsetAt + rangeOffset + (2 * (codepoint - start)));
                return glyph != 0 && ((glyph + delta) & 0xFFFF) != 0;
            }

            return false;
        }

        private static bool Format12(byte[] font, int table, int codepoint)
        {
            var groups = U32(font, table + 12);
            for (var g = 0; g < groups; g++)
            {
                var group = table + 16 + (12 * g);
                if (codepoint >= U32(font, group) && codepoint <= U32(font, group + 4))
                {
                    return U32(font, group + 8) + (codepoint - U32(font, group)) != 0;
                }
            }

            return false;
        }

        private static int U16(byte[] b, int at) => (b[at] << 8) | b[at + 1];

        private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
    }
}
