using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Both editors' Font control: a dropdown of every family (<see cref="ProfileFontCatalog.All"/>),
/// grouped under its category, with a search box at its top that filters by name. A text whose
/// family this build doesn't know (one a newer AetherFrame saved) shows that, rather than
/// pretending to be the first family. Both editors' pickers share one <see cref="ChooserMemory"/>
/// (issue #114): the search is kept between openings, and reopening returns the list to where it was
/// (or to the chosen font, if it changed elsewhere), so trying nearby fonts needs no scrolling back.
/// The framework thread only.
/// </summary>
internal static class FontPicker
{
    private const string UnknownLabel = "A font this AetherFrame doesn't have";

    /// <summary>The fonts' list memory, shared by both editors' pickers (one list, one place in it).</summary>
    private static ChooserMemory Memory => ChooserMemories.For("Fonts");

    /// <summary>The list's tallest height, as a share of the screen's work area.</summary>
    private const float MaxListScreenShare = 0.6f;

    /// <summary>Draws the control for <paramref name="currentFamilyId"/>; true, with the family chosen, when the player picked one.</summary>
    internal static bool Draw(string id, string? currentFamilyId, out string chosen)
    {
        chosen = currentFamilyId ?? ProfileFontFamilies.DalamudDefault;
        var known = currentFamilyId is null || currentFamilyId == ProfileFontFamilies.DalamudDefault || ProfileFontCatalog.Resolve(currentFamilyId).Id == currentFamilyId;
        var preview = known ? ProfileFontCatalog.Resolve(currentFamilyId).DisplayName : UnknownLabel;
        var comboWidth = ImGui.CalcItemWidth();
        using var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLargest);
        if (!combo.Success)
        {
            return false;
        }

        // The search box is in the popup and the list scrolls in a child under it (see ChooserScroll),
        // so the box stays in view however far down the list is.
        var memory = Memory;
        var appearing = ImGui.IsWindowAppearing();
        var opening = ChooserScroll.Open(memory, appearing, chosen, (familyId, query) => Matches(ProfileFontCatalog.Resolve(familyId), query));
        if (appearing)
        {
            ImGui.SetKeyboardFocusHere();
        }

        var search = memory.Search;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##FontSearch", "Search fonts", ref search, 64, ImGuiInputTextFlags.AutoSelectAll))
        {
            memory.Search = search;
        }

        var query = search.Trim();
        var style = ImGui.GetStyle();
        var listSize = new Vector2(
            MathF.Max(comboWidth - (style.WindowPadding.X * 2f), WidestRow() + style.ScrollbarSize + (style.FramePadding.X * 2f)),
            MathF.Min(ListHeight(query), ImGui.GetMainViewport().WorkSize.Y * MaxListScreenShare));

        var changed = false;
        using (var list = ImRaii.Child("##FontList", listSize, false))
        {
            if (list.Success)
            {
                ChooserScroll.Restore(opening, appearing);
                string? heading = null;
                foreach (var family in ProfileFontCatalog.All)
                {
                    if (!Matches(family, query))
                    {
                        continue;
                    }

                    var group = GroupOf(family);
                    if (!ReferenceEquals(group, heading))
                    {
                        heading = group;
                        ImGui.Spacing();
                        ImGui.TextDisabled(group);
                    }

                    var selected = family.Id == chosen;
                    if (ImGui.Selectable(family.DisplayName + "##" + family.Id, selected))
                    {
                        chosen = family.Id;
                        changed = true;
                    }

                    ChooserScroll.ScrollHereIfOpening(opening, selected);
                }

                if (heading is null)
                {
                    ImGui.TextDisabled("No font has that name.");
                }

                ChooserScroll.End(memory, appearing, chosen);
            }
        }

        if (changed)
        {
            ImGui.CloseCurrentPopup();
        }

        return changed;
    }

    private static bool Matches(ProfileFontFamilyDescriptor family, string query) =>
        query.Length == 0 || family.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The list's full height for <paramref name="query"/>: its rows and headings (see the loop
    /// in <see cref="Draw"/>), so a short result list isn't padded out.</summary>
    private static float ListHeight(string query)
    {
        var rows = 0;
        var headings = 0;
        string? heading = null;
        foreach (var family in ProfileFontCatalog.All)
        {
            if (!Matches(family, query))
            {
                continue;
            }

            rows++;
            var group = GroupOf(family);
            if (!ReferenceEquals(group, heading))
            {
                heading = group;
                headings++;
            }
        }

        var step = ImGui.GetTextLineHeightWithSpacing();
        return rows == 0 ? step : ((rows + headings) * step) + (headings * ImGui.GetStyle().ItemSpacing.Y);
    }

    /// <summary>The widest name or heading in the whole list, so the list keeps one width while searching.</summary>
    private static float WidestRow()
    {
        var widest = ImGui.CalcTextSize(UnknownLabel).X;
        foreach (var family in ProfileFontCatalog.All)
        {
            widest = MathF.Max(widest, MathF.Max(ImGui.CalcTextSize(family.DisplayName).X, ImGui.CalcTextSize(GroupOf(family)).X));
        }

        return widest;
    }

    /// <summary>The heading a family is listed under.</summary>
    internal static string GroupOf(ProfileFontFamilyDescriptor family) => family.Id == ProfileFontFamilies.DalamudDefault
        ? "Game default"
        : family.Category switch
        {
            FontCategory.Fantasy => "Fantasy & medieval",
            FontCategory.Script => "Script & handwriting",
            FontCategory.Serif => "Serif",
            FontCategory.Sans => "Sans-serif",
            FontCategory.Display => "Display",
            FontCategory.Mono => "Monospace",
            _ => "AetherFrame",
        };
}
