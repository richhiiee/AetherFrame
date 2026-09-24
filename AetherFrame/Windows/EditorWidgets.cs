using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Small, allocation-free building blocks for the Advanced editor's compact panels: a fixed-width
/// property label column, icon buttons/toggles, segmented choices, and color swatches. Purely
/// presentational; every edit still routes through <c>EditorSession</c>.
/// </summary>
internal static class EditorWidgets
{
    internal const float LabelColumnWidth = 92f;

    internal static readonly Vector4 AccentColor = new(0.30f, 0.62f, 1.00f, 1f);
    internal static readonly Vector4 ActiveToggleColor = new(0.26f, 0.46f, 0.78f, 1f);
    internal static readonly Vector4 DimTextColor = new(1f, 1f, 1f, 0.45f);
    internal static readonly Vector4 WarningColor = new(1f, 0.70f, 0.30f, 1f);
    internal static readonly Vector4 ErrorColor = new(1f, 0.42f, 0.42f, 1f);
    internal static readonly Vector4 SuccessColor = new(0.45f, 0.85f, 0.50f, 1f);

    /// <summary>
    /// Draws a dimmed property label in the fixed left column and positions the cursor for the
    /// value widget, whose width is set to fill the rest of the row (or <paramref name="width"/>).
    /// </summary>
    internal static void PropertyLabel(string label, float width = -1f)
    {
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, DimTextColor))
        {
            ImGui.TextUnformatted(label);
        }

        ImGui.SameLine(LabelColumnWidth);
        ImGui.SetNextItemWidth(width);
    }

    // Open/closed state per section label, kept here rather than in ImGui's per-ID storage so a
    // section stays collapsed (or open) as the selection moves between elements.
    private static readonly Dictionary<string, bool> SectionOpenStates = new();

    // FontAwesome glyph strings, built once per icon instead of on every draw.
    private static readonly Dictionary<FontAwesomeIcon, string> IconStrings = new();

    internal static string GetIconString(FontAwesomeIcon icon)
    {
        if (!IconStrings.TryGetValue(icon, out var text))
        {
            text = icon.ToIconString();
            IconStrings[icon] = text;
        }

        return text;
    }

    /// <summary>A collapsible Inspector section; open by default unless told otherwise.</summary>
    internal static bool Section(string label, bool defaultOpen = true)
    {
        ImGui.Spacing();

        if (!SectionOpenStates.TryGetValue(label, out var open))
        {
            open = defaultOpen;
        }

        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        open = ImGui.CollapsingHeader(label);
        SectionOpenStates[label] = open;
        return open;
    }

    /// <summary>A square icon-only button (FontAwesome) with an optional tooltip.</summary>
    internal static bool IconButton(string id, FontAwesomeIcon icon, string? tooltip = null, float size = 0f)
    {
        var buttonSize = size > 0f ? new Vector2(size, size) : new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());
        bool clicked;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            clicked = ImGui.Button($"{GetIconString(icon)}##{id}", buttonSize);
        }

        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>An icon button drawn highlighted while <paramref name="active"/>; returns true when clicked.</summary>
    internal static bool IconToggle(string id, FontAwesomeIcon icon, bool active, string? tooltip = null, float size = 0f)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, ActiveToggleColor, active);
        return IconButton(id, icon, tooltip, size);
    }

    /// <summary>A text button drawn highlighted while <paramref name="active"/>; returns true when clicked.</summary>
    internal static bool TextToggle(string label, bool active, Vector2 size = default, string? tooltip = null)
    {
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, ActiveToggleColor, active))
        {
            clicked = ImGui.Button(label, size);
        }

        Tooltip(tooltip);
        return clicked;
    }

    private static readonly (CornerMask Corner, string Label)[] CornerToggleLabels =
    [
        (CornerMask.TopLeft, "Top Left"), (CornerMask.TopRight, "Top Right"),
        (CornerMask.BottomLeft, "Bottom Left"), (CornerMask.BottomRight, "Bottom Right"),
    ];

    /// <summary>
    /// A Corner Ornament's corners as a 2x2 grid of toggles laid out like the corners themselves.
    /// Returns true when one was clicked, with the corner and its new state; the last selected corner
    /// is drawn disabled (a Corner Ornament always has at least one corner).
    /// </summary>
    internal static bool CornerToggles(CornerMask selected, out CornerMask corner, out bool enabled)
    {
        corner = CornerMask.None;
        enabled = false;
        var onlyOne = BitOperations.PopCount((uint)(selected & CornerMask.All)) <= 1;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - spacing) / 2f;
        var clicked = false;

        using var pushId = ImRaii.PushId("Corners");
        for (var i = 0; i < CornerToggleLabels.Length; i++)
        {
            var (candidate, label) = CornerToggleLabels[i];
            var active = (selected & candidate) != 0;
            if (i % 2 == 1)
            {
                ImGui.SameLine();
            }
            else if (i > 0)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + LabelColumnWidth);
            }

            using (ImRaii.Disabled(active && onlyOne))
            {
                if (TextToggle(label, active, new Vector2(width, 0f), active && onlyOne ? "At least one corner stays selected." : null) && !clicked)
                {
                    clicked = true;
                    corner = candidate;
                    enabled = !active;
                }
            }
        }

        return clicked;
    }

    /// <summary>
    /// A row of equally sized text buttons filling the available width, the one matching
    /// <paramref name="selectedIndex"/> highlighted. Returns the clicked index, or -1.
    /// </summary>
    internal static int Segmented(string id, ReadOnlySpan<string> labels, int selectedIndex)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - (spacing * (labels.Length - 1))) / labels.Length;
        var clicked = -1;

        using var pushId = ImRaii.PushId(id);
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (TextToggle(labels[i], i == selectedIndex, new Vector2(width, 0f)) && i != selectedIndex)
            {
                clicked = i;
            }
        }

        return clicked;
    }

    /// <summary>A clickable color swatch (no picker); returns true when clicked.</summary>
    internal static bool Swatch(string id, Vector4 color, float size, string? tooltip = null)
    {
        var clicked = ImGui.ColorButton(id, in color, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(size, size));
        Tooltip(tooltip);
        return clicked;
    }


    internal static void Tooltip(string? text)
    {
        if (text is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>Dimmed helper text.</summary>
    internal const string UnsupportedElementsWarning =
        "This Plate contains elements this version of AetherFrame cannot display. They will be preserved when you save.";

    /// <summary>
    /// A one-line, non-blocking notice shown only for a Plate with elements this build can't
    /// display (see <see cref="Domain.Profiles.ProfileDocument.HasUnsupportedElements"/>). Draws
    /// nothing for every other Plate.
    /// </summary>
    internal static void UnsupportedElementsNotice(Domain.Profiles.ProfileDocument profile)
    {
        if (!profile.HasUnsupportedElements)
        {
            return;
        }

        IconText(FontAwesomeIcon.ExclamationTriangle, WarningColor);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, WarningColor))
        {
            ImGui.TextWrapped(UnsupportedElementsWarning);
        }
    }

    internal static void Hint(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, DimTextColor))
        {
            ImGui.TextWrapped(text);
        }
    }

    /// <summary>A FontAwesome glyph as inline text (e.g. a type indicator).</summary>
    internal static void IconText(FontAwesomeIcon icon, Vector4 color)
    {
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextUnformatted(GetIconString(icon));
        }
    }
}
