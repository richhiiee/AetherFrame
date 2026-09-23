using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Basic editor's Identity section: Character Name, Title (source, FFXIV title picker or
/// custom text, curated layout, style, decoration), and Tagline. Every control routes through
/// <see cref="BasicIdentitySession"/>; styling edits the same shared text properties the Advanced
/// Inspector does, so there is no Basic-only renderer or styling path.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    private const string TitlePickerPopupId = "##AetherFrameTitlePicker";

    // BeginPopup always auto-resizes to its content, so every size inside the picker is fixed:
    // a -1 / fill size there would be derived from the popup's own size and feed back into it.
    private const float TitlePickerWidth = 380f;
    private const float TitlePickerListHeight = 320f;

    private static readonly string[] TitleSourceLabels = ["None", "FFXIV Title", "Custom"];
    private static readonly string[] AlignmentLabels = ["Left", "Center", "Right"];
    private static readonly string[] IdentityFontLabels = ProfileFontCatalog.All.Select(f => f.DisplayName).ToArray();
    private static readonly string[] DecorationLabels =
        BasicIdentitySession.DecorationSymbols.Select(s => s.Length == 0 ? "None" : s).ToArray();

    private static readonly (IdentityTitleLayout Layout, string Label, string Tooltip)[] TitleLayouts =
    [
        (IdentityTitleLayout.Classic, "Classic", "Title above the character name"),
        (IdentityTitleLayout.Subtitle, "Subtitle", "Character name above the title"),
        (IdentityTitleLayout.Badge, "Badge", "Title as a small, spaced, bold line under the name\n(sets the title's size and weight; adjust afterwards)"),
        (IdentityTitleLayout.InlineBefore, "Inline Before", "Title, then the character name, on one line"),
        (IdentityTitleLayout.InlineAfter, "Inline After", "Character name, then the title, on one line"),
        (IdentityTitleLayout.Accent, "Accent", "Title in italics, decorated with symbols\n(adds ✦ … ✦ if no decoration is set; adjust afterwards)"),
    ];

    private bool pendingTitlePicker;
    private string titleSearch = string.Empty;
    private readonly List<(GameTitle Title, bool? Unlocked)> titlePickerRows = new();
    private bool titlePickerUnlockKnown;

    private void DrawIdentitySection(ProfileDocument profile)
    {
        if (!ImGui.CollapsingHeader("Identity", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var identity = basicEditorSession.Identity;
        using var id = ImRaii.PushId("Identity");

        if (BasicIdentitySession.HasNoHeader(profile))
        {
            Hint("Your character's name, title, and an optional tagline, designed as one header.");
            if (ImGui.Button("Add Identity Header"))
            {
                identity.CreateHeader();
            }

            if (identity.CharacterName is { } characterName)
            {
                ImGui.SameLine();
                Hint($"Uses \"{characterName}\"");
            }

            DrawTitlePickerPopup(profile);
            return;
        }

        if (BasicIdentitySession.IsCustomized(profile))
        {
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 0.8f), "Customized in Advanced Editor");
            ToolTip("The header's placement was changed outside Basic mode (or predates it), so Basic\nkeeps it exactly where it is. Choosing a layout or Apply Layout re-places it.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply Layout"))
            {
                identity.ApplyLayout();
            }
        }

        DrawThemeColors(identity);
        ImGui.Spacing();

        DrawNameControls(profile, identity);
        ImGui.Spacing();
        ImGui.Separator();

        DrawTitleControls(profile, identity);
        ImGui.Spacing();
        ImGui.Separator();

        DrawTaglineControls(profile, identity);
        DrawTitlePickerPopup(profile);
    }

    private void DrawThemeColors(BasicIdentitySession identity)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Theme colors");
        ToolTip("Fills the name, title, and tagline colors from a theme.\nEach color stays editable afterwards.");

        for (var i = 0; i < ProfileThemePresets.All.Length; i++)
        {
            var preset = ProfileThemePresets.All[i];
            ImGui.SameLine(0f, i == 0 ? 8f : 4f);
            if (EditorWidgets.GradientSwatch($"##ThemeColors{i}", preset.TextColor, preset.AccentTextColor, new Vector2(26f, ImGui.GetFrameHeight()), $"{preset.Name}: name, title, and tagline colors"))
            {
                identity.ApplyThemeColors(preset);
            }
        }
    }

    // ---------------------------------------------------------------- name

    private void DrawNameControls(ProfileDocument profile, BasicIdentitySession identity)
    {
        var name = BasicIdentitySession.Find(profile, ProfileElementRole.BasicName);

        ImGui.TextUnformatted("Character Name");
        ImGui.SameLine();
        var visible = name?.Visible ?? false;
        if (ImGui.Checkbox("Show##Name", ref visible))
        {
            identity.SetNameVisible(visible);
        }

        var buffer = name?.Text ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##NameText", identity.CharacterName ?? "No character loaded", ref buffer, 64))
        {
            identity.SetNameText(buffer);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            identity.Commit();
        }

        if (identity.CharacterName is { Length: > 0 } characterName && name?.Text != characterName)
        {
            if (ImGui.SmallButton($"Use \"{characterName}\""))
            {
                identity.UseCharacterName();
            }
        }

        if (name is not null)
        {
            DrawStyleTree("Name style", ProfileElementRole.BasicName, name, identity, StyleControls.Name);
        }
    }

    // ---------------------------------------------------------------- title

    private void DrawTitleControls(ProfileDocument profile, BasicIdentitySession identity)
    {
        ImGui.TextUnformatted("Title");

        var source = BasicIdentitySession.GetTitleSource(profile);
        var clicked = EditorWidgets.Segmented("TitleSource", TitleSourceLabels, (int)source);
        if (clicked >= 0)
        {
            identity.SetTitleSource((IdentityTitleSource)clicked);
            source = (IdentityTitleSource)clicked;
        }

        var title = BasicIdentitySession.Find(profile, ProfileElementRole.BasicTitle);

        if (source == IdentityTitleSource.GameTitle)
        {
            var chosen = profile.BasicIdentity is { GameTitleId: > 0 } settings ? identity.Titles.Find(settings.GameTitleId) : null;
            var label = chosen?.GetText(GameTitleCatalog.UseFeminineForms) ?? "Choose a title...";
            if (ImGui.Button($"{label}##ChooseTitle", new Vector2(-1, 0f)))
            {
                pendingTitlePicker = true;
            }

            if (chosen is not null)
            {
                Hint(chosen.IsPrefix ? "In game, this title is shown before (above) the name." : "In game, this title is shown after (below) the name.");
                var gameLayout = chosen.IsPrefix ? IdentityTitleLayout.Classic : IdentityTitleLayout.Subtitle;
                if (BasicIdentitySession.GetLayout(profile) != gameLayout)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Use game placement"))
                    {
                        identity.UseGamePlacement();
                    }
                }
            }

            if (identity.IsGameTitleTextEdited(profile))
            {
                Hint("The title text was edited in the Advanced Editor.");
            }
        }
        else if (source == IdentityTitleSource.Custom)
        {
            var buffer = BasicIdentitySession.GetCustomTitle(profile);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##CustomTitle", "Custom title", ref buffer, BasicIdentityHeader.MaxCustomTitleLength))
            {
                identity.SetCustomTitle(buffer);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                identity.Commit();
            }
        }

        if (source == IdentityTitleSource.None)
        {
            return;
        }

        // Layout presets: two rows of three.
        ImGui.Spacing();
        ImGui.TextUnformatted("Layout");
        var current = BasicIdentitySession.GetLayout(profile);
        var customized = BasicIdentitySession.IsCustomized(profile);
        var buttonWidth = (ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().ItemSpacing.X * 2f)) / 3f;
        for (var i = 0; i < TitleLayouts.Length; i++)
        {
            var (layout, layoutLabel, tooltip) = TitleLayouts[i];
            if (i % 3 != 0)
            {
                ImGui.SameLine();
            }

            if (EditorWidgets.TextToggle($"{layoutLabel}##Layout{i}", layout == current && !customized, new Vector2(buttonWidth, 0f), tooltip))
            {
                identity.SetLayout(layout);
            }
        }

        if (title is null)
        {
            return;
        }

        DrawStyleTree("Title style", ProfileElementRole.BasicTitle, title, identity, StyleControls.Title);
        DrawDecoration(title, identity);
    }

    private static void DrawDecoration(TextProfileElement title, BasicIdentitySession identity)
    {
        if (!ImGui.TreeNode("Decoration##TitleDecoration"))
        {
            return;
        }

        var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        DecorationCombo("Prefix", title.Prefix, half, identity.SetPrefix);
        ImGui.SameLine();
        DecorationCombo("Suffix", title.Suffix, half, identity.SetSuffix);

        // Honest about glyph coverage: the curated fonts are currently built with the Latin range
        // only, and there's no glyph fallback yet, so most symbols can't be drawn by them.
        Hint("Symbols need glyph fallback, which isn't available yet: most won't show in the\nAetherFrame fonts until it is. The choice is saved and will display once supported.");
        ImGui.TreePop();

        static void DecorationCombo(string label, string current, float width, Action<string> apply)
        {
            ImGui.SetNextItemWidth(width);
            var preview = current.Length == 0 ? $"{label}: None" : $"{label}: {current}";
            using var combo = ImRaii.Combo($"##{label}", preview);
            if (!combo.Success)
            {
                return;
            }

            for (var i = 0; i < BasicIdentitySession.DecorationSymbols.Length; i++)
            {
                var symbol = BasicIdentitySession.DecorationSymbols[i];
                if (ImGui.Selectable(DecorationLabels[i], symbol == current) && symbol != current)
                {
                    apply(symbol);
                }
            }
        }
    }

    // ---------------------------------------------------------------- tagline

    private void DrawTaglineControls(ProfileDocument profile, BasicIdentitySession identity)
    {
        var tagline = BasicIdentitySession.Find(profile, ProfileElementRole.BasicTagline);

        ImGui.TextUnformatted("Tagline");
        ImGui.SameLine();
        var visible = tagline is { Visible: true };
        if (ImGui.Checkbox("Show##Tagline", ref visible))
        {
            identity.SetTaglineVisible(visible);
        }

        if (tagline is not { Visible: true })
        {
            return;
        }

        var buffer = tagline.Text;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##TaglineText", "Add a tagline", ref buffer, BasicIdentityHeader.MaxTaglineLength))
        {
            identity.SetTaglineText(buffer);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            identity.Commit();
        }

        DrawStyleTree("Tagline style", ProfileElementRole.BasicTagline, tagline, identity, StyleControls.Tagline);
    }

    // ---------------------------------------------------------------- shared style controls

    [Flags]
    private enum StyleControls
    {
        Common = 0,
        Bold = 1,
        Italic = 2,
        Effects = 4,
        Name = Bold | Italic | Effects,
        Title = Bold | Italic | Effects,
        Tagline = Italic,
    }

    /// <summary>
    /// A compact, collapsed-by-default style block over the shared text properties: Font, Size,
    /// Color, Opacity, (Bold,) Italic, (Outline, Shadow,) Alignment. Sliders and colors are one
    /// undo step per drag; toggles and combos are one step per click.
    /// </summary>
    private void DrawStyleTree(string label, ProfileElementRole role, TextProfileElement element, BasicIdentitySession identity, StyleControls controls)
    {
        if (!ImGui.TreeNode($"{label}##{role}"))
        {
            return;
        }

        using var styleId = ImRaii.PushId((int)role);
        const float labelWidth = 70f;

        void Label(string text)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(text);
            ImGui.SameLine(labelWidth + ImGui.GetCursorStartPos().X + ImGui.GetStyle().IndentSpacing);
            ImGui.SetNextItemWidth(-1);
        }

        void Apply(Action<TextProfileElement> change) => identity.EditStyle(role, change, continuous: false);

        void Continue(Action<TextProfileElement> change) => identity.EditStyle(role, change, continuous: true);

        void CommitOnRelease()
        {
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                identity.Commit();
            }
        }

        // Font family
        var familyIndex = 0;
        for (var i = 0; i < ProfileFontCatalog.All.Count; i++)
        {
            if (ProfileFontCatalog.All[i].Id == element.FontFamily)
            {
                familyIndex = i;
                break;
            }
        }

        Label("Font");
        if (ImGui.Combo("##Font", ref familyIndex, IdentityFontLabels, IdentityFontLabels.Length))
        {
            var family = ProfileFontCatalog.All[familyIndex].Id;
            Apply(e => e.FontFamily = family);
        }

        var size = element.FontSize;
        Label("Size");
        if (ImGui.SliderFloat("##Size", ref size, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize, "%.0f px"))
        {
            var value = size;
            Continue(e => e.FontSize = value);
        }

        CommitOnRelease();

        var color = element.Color;
        Label("Color");
        if (ImGui.ColorEdit4("##Color", ref color, ImGuiColorEditFlags.NoAlpha))
        {
            var rgb = color;
            Continue(e => e.Color = rgb with { W = e.Color.W });
        }

        CommitOnRelease();

        var opacity = element.Color.W * 100f;
        Label("Opacity");
        if (ImGui.SliderFloat("##Opacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var alpha = opacity / 100f;
            Continue(e => e.Color = e.Color with { W = alpha });
        }

        CommitOnRelease();

        // Style toggles (Bold/Italic only for a family with the real face).
        var descriptor = ProfileFontCatalog.Resolve(element.FontFamily);
        Label("Style");
        if ((controls & StyleControls.Bold) != 0)
        {
            using (ImRaii.Disabled(!descriptor.SupportsBold))
            {
                var bold = element.Bold && descriptor.SupportsBold;
                if (ImGui.Checkbox("Bold", ref bold))
                {
                    Apply(e => e.Bold = bold);
                }
            }

            ImGui.SameLine();
        }

        using (ImRaii.Disabled(!descriptor.SupportsItalic))
        {
            var italic = element.Italic && descriptor.SupportsItalic;
            if (ImGui.Checkbox("Italic", ref italic))
            {
                Apply(e => e.Italic = italic);
            }
        }

        if ((controls & StyleControls.Effects) != 0)
        {
            Label("Outline");
            var outline = element.OutlineEnabled;
            if (ImGui.Checkbox("##Outline", ref outline))
            {
                Apply(e => e.OutlineEnabled = outline);
            }

            if (element.OutlineEnabled)
            {
                ImGui.SameLine();
                var outlineColor = element.OutlineColor;
                if (ImGui.ColorEdit4("##OutlineColor", ref outlineColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
                {
                    var rgb = outlineColor;
                    Continue(e => e.OutlineColor = rgb with { W = 1f });
                }

                CommitOnRelease();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var thickness = element.OutlineThickness;
                if (ImGui.SliderFloat("##OutlineThickness", ref thickness, 0.5f, TextProfileElement.MaxOutlineThickness, "%.1f px"))
                {
                    var value = thickness;
                    Continue(e => e.OutlineThickness = value);
                }

                CommitOnRelease();
            }

            Label("Shadow");
            var shadow = element.ShadowEnabled;
            if (ImGui.Checkbox("##Shadow", ref shadow))
            {
                Apply(e => e.ShadowEnabled = shadow);
            }

            if (element.ShadowEnabled)
            {
                ImGui.SameLine();
                var shadowColor = element.ShadowColor;
                if (ImGui.ColorEdit4("##ShadowColor", ref shadowColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
                {
                    var rgb = shadowColor;
                    Continue(e => e.ShadowColor = rgb with { W = 1f });
                }

                CommitOnRelease();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var shadowOpacity = element.ShadowOpacity * 100f;
                if (ImGui.SliderFloat("##ShadowOpacity", ref shadowOpacity, 0f, 100f, "%.0f%%"))
                {
                    var value = shadowOpacity / 100f;
                    Continue(e => e.ShadowOpacity = value);
                }

                CommitOnRelease();
            }
        }

        Label("Align");
        var alignmentClicked = EditorWidgets.Segmented("Align", AlignmentLabels, (int)element.Alignment);
        if (alignmentClicked >= 0)
        {
            var alignment = (TextAlignment)alignmentClicked;
            Apply(e => e.Alignment = alignment);
        }

        ImGui.TreePop();
    }

    // ---------------------------------------------------------------- FFXIV title picker

    /// <summary>
    /// Searchable list of FFXIV titles from game data. When the game has the character's title
    /// list, unlocked titles come first and locked ones are marked; otherwise all titles are shown
    /// with no unlock claims at all (never guessed).
    /// </summary>
    private void DrawTitlePickerPopup(ProfileDocument profile)
    {
        var identity = basicEditorSession.Identity;

        if (pendingTitlePicker)
        {
            pendingTitlePicker = false;
            titleSearch = string.Empty;
            RebuildTitlePickerRows(identity.Titles);
            ImGui.OpenPopup(TitlePickerPopupId);
        }

        using var popup = ImRaii.Popup(TitlePickerPopupId);
        if (!popup.Success)
        {
            return;
        }

        // The list may arrive from the server while the picker is open.
        if (identity.Titles.IsUnlockStateKnown != titlePickerUnlockKnown)
        {
            RebuildTitlePickerRows(identity.Titles);
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var pickerWidth = TitlePickerWidth * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(pickerWidth);
        ImGui.InputTextWithHint("##TitleSearch", "Search titles...", ref titleSearch, 64);

        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + pickerWidth))
        {
            if (titlePickerUnlockKnown)
            {
                Hint("Your unlocked titles are listed first.");
            }
            else
            {
                Hint("Unlocked titles aren't known yet: open Character > Titles in game once this session\nto load them. Until then every title is listed, unlocked or not.");
            }
        }

        var feminine = GameTitleCatalog.UseFeminineForms;
        var selectedId = profile.BasicIdentity is { TitleSource: IdentityTitleSource.GameTitle } settings ? settings.GameTitleId : 0u;
        var search = titleSearch.Trim();

        using (var list = ImRaii.Child("##TitleList", new Vector2(pickerWidth, TitlePickerListHeight * ImGuiHelpers.GlobalScale), true))
        {
            if (list.Success)
            {
                var shown = 0;
                foreach (var (title, unlocked) in titlePickerRows)
                {
                    if (search.Length > 0 && !title.Matches(search))
                    {
                        continue;
                    }

                    shown++;
                    var text = title.GetText(feminine);
                    using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 0.45f), unlocked == false))
                    {
                        if (ImGui.Selectable($"{text}##Title{title.Id}", title.Id == selectedId))
                        {
                            identity.SelectGameTitle(title);
                            ImGui.CloseCurrentPopup();
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        var placement = title.IsPrefix ? "Shown before (above) the name in game." : "Shown after (below) the name in game.";
                        var state = unlocked switch
                        {
                            true => "Unlocked",
                            false => "Not unlocked on this character",
                            _ => "Unlock state unknown",
                        };
                        ImGui.SetTooltip($"{placement}\n{state}");
                    }

                    ImGui.SameLine(ImGui.GetContentRegionMax().X - 64f);
                    ImGui.TextDisabled(title.IsPrefix ? "before" : "after");
                    if (unlocked == false)
                    {
                        ImGui.SameLine();
                        EditorWidgets.IconText(FontAwesomeIcon.Lock, new Vector4(1f, 1f, 1f, 0.35f));
                    }
                }

                if (shown == 0)
                {
                    ImGui.TextDisabled(identity.Titles.Titles.Count == 0 ? "No title data available." : "No titles match.");
                }
            }
        }
    }

    private void RebuildTitlePickerRows(GameTitleCatalog catalog)
    {
        titlePickerUnlockKnown = catalog.IsUnlockStateKnown;
        titlePickerRows.Clear();

        foreach (var title in catalog.Titles)
        {
            titlePickerRows.Add((title, catalog.IsUnlocked(title)));
        }

        // Unlocked first (when known); otherwise the game's own list order. Stable within groups.
        if (titlePickerUnlockKnown)
        {
            var ordered = titlePickerRows.OrderBy(r => r.Unlocked == true ? 0 : 1).ToList();
            titlePickerRows.Clear();
            titlePickerRows.AddRange(ordered);
        }
    }

    private static void Hint(string text) => EditorWidgets.Hint(text);

    private static void ToolTip(string text) => EditorWidgets.Tooltip(text);
}
