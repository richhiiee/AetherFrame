using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Identity category: Character Name and Title (source, FFXIV title picker or custom text)
/// first; then the identity layout; then Appearance and Advanced Styling (collapsed); then
/// the header's layout status. Every control routes through <see cref="BasicIdentitySession"/>;
/// styling edits the same shared text properties the Advanced Inspector does, so there is no
/// Basic-only renderer or styling path.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    private const string TitlePickerPopupId = "##AetherFrameTitlePicker";

    // BeginPopup always auto-resizes to its content, so every size inside the picker is fixed:
    // a -1 / fill size there would be derived from the popup's own size and feed back into it.
    private const float TitlePickerWidth = 380f;
    private const float TitlePickerListHeight = 320f;

    private static readonly string[] TitleSourceLabels = ["None", "FFXIV Title", "Custom"];
    private static readonly string[] DecorationLabels =
        BasicIdentitySession.DecorationSymbols.Select(s => s.Length == 0 ? "None" : s).ToArray();

    // Badge and Accent are retired from Basic's picker (no longer offered to choose), but the enum
    // values, their look, and IdentityHeaderRules' revert/legacy logic all stay: a Plate saved with
    // either opens unchanged, keeps rendering exactly as it did, and is only ever changed by an
    // explicit pick of one of the four layouts still offered here.
    private static readonly (IdentityTitleLayout Layout, string Label, string Tooltip)[] TitleLayouts =
    [
        (IdentityTitleLayout.Classic, "Classic", "Title above the character name"),
        (IdentityTitleLayout.Subtitle, "Subtitle", "Character name above the title"),
        (IdentityTitleLayout.InlineBefore, "Inline Before", "Title, then the character name, on one line"),
        (IdentityTitleLayout.InlineAfter, "Inline After", "Character name, then the title, on one line"),
    ];

    private bool pendingTitlePicker;
    private string titleSearch = string.Empty;
    private readonly List<(GameTitle Title, bool? Unlocked)> titlePickerRows = new();
    private bool titlePickerUnlockKnown;

    private void DrawIdentityCategory(ProfileDocument profile)
    {
        var identity = basicEditorSession.Identity;

        if (BasicIdentitySession.HasNoHeader(profile))
        {
            Hint("Your character's name and title, designed as one header.");
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

        DrawNameControls(profile, identity);
        DrawTitleControls(profile, identity);
        DrawIdentityLayoutChoice(profile, identity);

        ImGui.Spacing();
        Subheading("Name Backing");
        DrawComponentSlot(profile, Domain.Components.PlateComponentKind.NameBacking);

        // Fine tuning, out of the way until wanted.
        ImGui.Spacing();
        StyleTarget? Target(string label, ProfileElementRole role, StyleControls controls) =>
            BasicIdentitySession.Find(profile, role) is { } element
                ? new StyleTarget(label, role, element, (change, continuous) => identity.EditStyle(role, change, continuous), identity.Commit, controls)
                : null;

        var targets = new[]
        {
            Target("Character Name", ProfileElementRole.BasicName, StyleControls.Name),
            Target("Title", ProfileElementRole.BasicTitle, StyleControls.Title),
        };
        DrawAppearance(targets);
        var title = BasicIdentitySession.Find(profile, ProfileElementRole.BasicTitle);
        DrawAdvancedStyling(targets, title is null ? null : () => DrawDecoration(title, identity));

        DrawLayoutBlock(profile, new LayoutRow("Identity Header", [BasicSection.Identity], "Reset Identity"));
        DrawTitlePickerPopup(profile);
    }

    // ---------------------------------------------------------------- name

    private void DrawNameControls(ProfileDocument profile, BasicIdentitySession identity)
    {
        var name = BasicIdentitySession.Find(profile, ProfileElementRole.BasicName);

        Subheading("Character Name");
        var visible = name?.Visible ?? false;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ShowToggleWidth()));
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
    }

    /// <summary>Width of a right-aligned "Show" checkbox.</summary>
    private static float ShowToggleWidth(string label = "Show") =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    // ---------------------------------------------------------------- title

    private void DrawTitleControls(ProfileDocument profile, BasicIdentitySession identity)
    {
        Subheading("Title");

        var source = BasicIdentitySession.GetTitleSource(profile);
        var clicked = EditorWidgets.Segmented("TitleSource", TitleSourceLabels, (int)source);
        if (clicked >= 0)
        {
            identity.SetTitleSource((IdentityTitleSource)clicked);
            source = (IdentityTitleSource)clicked;
        }

        if (source == IdentityTitleSource.GameTitle)
        {
            var chosen = profile.BasicIdentity is { GameTitleId: > 0 } settings ? titleCatalog.Find(settings.GameTitleId) : null;
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

        DrawUndrawableDecorationWarning(profile, identity);
    }

    /// <summary>
    /// A title decoration the Plate's fonts can't draw (it shows as "?") — e.g. the symbols earlier
    /// builds added when choosing Accent — is called out where it's seen, with an explicit fix.
    /// Never removed on its own: it can't be told apart from a symbol the user picked.
    /// </summary>
    private static void DrawUndrawableDecorationWarning(ProfileDocument profile, BasicIdentitySession identity)
    {
        if (BasicIdentitySession.Find(profile, ProfileElementRole.BasicTitle) is not { } title
            || (IdentityHeaderRules.IsDrawableDecoration(title.Prefix) && IdentityHeaderRules.IsDrawableDecoration(title.Suffix)))
        {
            return;
        }

        ImGui.TextColored(EditorWidgets.WarningColor, $"The title's decoration ({title.Prefix} ... {title.Suffix}) can't be drawn and shows as \"?\".");
        if (ImGui.SmallButton("Remove Decoration"))
        {
            identity.ClearDecoration();
        }

        ToolTip("Removes the symbols before and after the title (undoable). You can pick new ones\nunder Advanced Styling > Title decoration.");
    }

    /// <summary>The curated title layouts (only meaningful while a title is shown): one row of four.</summary>
    private static void DrawIdentityLayoutChoice(ProfileDocument profile, BasicIdentitySession identity)
    {
        if (BasicIdentitySession.GetTitleSource(profile) == IdentityTitleSource.None)
        {
            return;
        }

        Subheading("Identity Layout");
        var current = BasicIdentitySession.GetLayout(profile);
        var customized = BasicIdentitySession.IsCustomized(profile);

        if (!customized && current is IdentityTitleLayout.Badge or IdentityTitleLayout.Accent)
        {
            DrawLegacyLayoutConversion(current, identity);
        }

        var buttonWidth = (ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().ItemSpacing.X * (TitleLayouts.Length - 1))) / TitleLayouts.Length;
        for (var i = 0; i < TitleLayouts.Length; i++)
        {
            var (layout, layoutLabel, tooltip) = TitleLayouts[i];
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (EditorWidgets.TextToggle($"{layoutLabel}##Layout{i}", layout == current && !customized, new Vector2(buttonWidth, 0f), tooltip))
            {
                identity.SetLayout(layout);
            }
        }
    }

    /// <summary>
    /// A legacy Badge or Accent layout, called out explicitly with a dedicated migration action
    /// rather than leaving the user to notice that clicking Classic (one of the four regular buttons
    /// below) happens to also do it. Nothing here runs on its own: the Plate keeps rendering exactly
    /// as it always has — unchanged, not dirtied — until this button is pressed, and pressing it is
    /// one undo step (the same <see cref="BasicIdentitySession.SetLayout"/> the regular buttons use).
    /// </summary>
    private static void DrawLegacyLayoutConversion(IdentityTitleLayout current, BasicIdentitySession identity)
    {
        ImGui.TextColored(EditorWidgets.WarningColor, $"This Plate uses the retired \"{current}\" layout.");
        if (ImGui.SmallButton("Convert to Classic"))
        {
            identity.SetLayout(IdentityTitleLayout.Classic);
        }

        ToolTip("Switches to the Classic layout and updates the title's style to match (undoable).\nUntil you do this, the title keeps looking exactly as it always has.");
    }

    /// <summary>The title's prefix/suffix decoration (inside Advanced Styling).</summary>
    private static void DrawDecoration(TextProfileElement title, BasicIdentitySession identity)
    {
        ImGui.TextDisabled("Title decoration");
        var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        DecorationCombo("Prefix", title.Prefix, half, identity.SetPrefix);
        ImGui.SameLine();
        DecorationCombo("Suffix", title.Suffix, half, identity.SetSuffix);

        Hint("Symbols drawn before and after the title. They're yours: changing the layout never adds or removes them.");

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
            RebuildTitlePickerRows(titleCatalog);
            ImGui.OpenPopup(TitlePickerPopupId);
        }

        using var popup = ImRaii.Popup(TitlePickerPopupId);
        if (!popup.Success)
        {
            return;
        }

        // The list may arrive from the server while the picker is open.
        if (titleCatalog.IsUnlockStateKnown != titlePickerUnlockKnown)
        {
            RebuildTitlePickerRows(titleCatalog);
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
                    ImGui.TextDisabled(titleCatalog.Titles.Count == 0 ? "No title data available." : "No titles match.");
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
}
