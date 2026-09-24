using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Fonts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Basic editor's text styling, in two tiers so typography never competes with content:
/// <b>Appearance</b> (font, size, color, opacity) and <b>Advanced Styling</b> (bold, italic,
/// outline, shadow, alignment), each a collapsed group at the bottom of its category. Both edit the
/// same shared text properties the Advanced Inspector does. Sliders and colors are one undo step
/// per drag; toggles and combos one step per click.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    private static readonly string[] AlignmentLabels = ["Left", "Center", "Right"];
    private static readonly string[] FontLabels = ProfileFontCatalog.All.Select(f => f.DisplayName).ToArray();

    [Flags]
    private enum StyleControls
    {
        Common = 0,
        Bold = 1,
        Italic = 2,
        Effects = 4,
        Name = Bold | Italic | Effects,
        Title = Bold | Italic | Effects,
        Value = Bold | Italic | Effects,
    }

    /// <summary>One text element an inspector can style, and how its edits are applied.</summary>
    /// <param name="Label">Its name in the styling groups ("Character Name", "Home World"...).</param>
    /// <param name="Role">Its role (also its ImGui id scope).</param>
    /// <param name="Element">The element.</param>
    /// <param name="Edit">Applies a change; continuous edits coalesce until <paramref name="Commit"/>.</param>
    /// <param name="Commit">Ends a continuous edit.</param>
    /// <param name="Controls">Which advanced controls apply.</param>
    private readonly record struct StyleTarget(
        string Label, ProfileElementRole Role, TextProfileElement Element, Action<Action<TextProfileElement>, bool> Edit, Action Commit, StyleControls Controls);

    /// <summary>A style target for a section value edited through <see cref="UI.Editor.BasicEditorSession"/>.</summary>
    private StyleTarget? SectionStyle(ProfileDocument profile, string label, ProfileElementRole role) =>
        Domain.Basic.BasicSections.FindText(profile, role) is { } element
            ? new StyleTarget(label, role, element, (change, continuous) => basicEditorSession.EditSectionStyle(role, change, continuous), basicEditorSession.CommitTextEdit, StyleControls.Value)
            : null;

    /// <summary>The collapsed "Appearance" group: font, size, color, and opacity for each target.</summary>
    private static void DrawAppearance(IEnumerable<StyleTarget?> targets)
    {
        var list = targets.OfType<StyleTarget>().ToList();
        if (list.Count == 0 || !ImGui.CollapsingHeader("Appearance##Appearance"))
        {
            return;
        }

        using var indent = ImRaii.PushIndent();
        foreach (var target in list)
        {
            using var id = ImRaii.PushId((int)target.Role);
            if (list.Count > 1)
            {
                ImGui.TextDisabled(target.Label);
            }

            DrawAppearanceControls(target);
            ImGui.Spacing();
        }
    }

    /// <summary>The collapsed "Advanced Styling" group: bold, italic, outline, shadow, and alignment for each target.</summary>
    private static void DrawAdvancedStyling(IEnumerable<StyleTarget?> targets, Action? extra = null)
    {
        var list = targets.OfType<StyleTarget>().ToList();
        if (list.Count == 0 || !ImGui.CollapsingHeader("Advanced Styling##AdvancedStyling"))
        {
            return;
        }

        using var indent = ImRaii.PushIndent();
        foreach (var target in list)
        {
            using var id = ImRaii.PushId((int)target.Role);
            if (list.Count > 1)
            {
                ImGui.TextDisabled(target.Label);
            }

            DrawAdvancedControls(target);
            ImGui.Spacing();
        }

        extra?.Invoke();
    }

    private static void StyleLabel(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        ImGui.SameLine(EditorWidgets.LabelColumnWidth * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(-1);
    }

    private static void CommitOnRelease(StyleTarget target)
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            target.Commit();
        }
    }

    private static void DrawAppearanceControls(StyleTarget target)
    {
        var element = target.Element;

        var familyIndex = 0;
        for (var i = 0; i < ProfileFontCatalog.All.Count; i++)
        {
            if (ProfileFontCatalog.All[i].Id == element.FontFamily)
            {
                familyIndex = i;
                break;
            }
        }

        StyleLabel("Font");
        if (ImGui.Combo("##Font", ref familyIndex, FontLabels, FontLabels.Length))
        {
            var family = ProfileFontCatalog.All[familyIndex].Id;
            target.Edit(e => e.FontFamily = family, false);
        }

        var size = element.FontSize;
        StyleLabel("Size");
        if (ImGui.SliderFloat("##Size", ref size, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize, "%.0f px"))
        {
            var value = size;
            target.Edit(e => e.FontSize = value, true);
        }

        CommitOnRelease(target);

        var color = element.Color;
        StyleLabel("Color");
        if (ImGui.ColorEdit4("##Color", ref color, ImGuiColorEditFlags.NoAlpha))
        {
            var rgb = color;
            target.Edit(e => e.Color = rgb with { W = e.Color.W }, true);
        }

        CommitOnRelease(target);

        var opacity = element.Color.W * 100f;
        StyleLabel("Opacity");
        if (ImGui.SliderFloat("##Opacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var alpha = opacity / 100f;
            target.Edit(e => e.Color = e.Color with { W = alpha }, true);
        }

        CommitOnRelease(target);
    }

    private static void DrawAdvancedControls(StyleTarget target)
    {
        var element = target.Element;

        // Style toggles (Bold/Italic only for a family with the real face).
        var descriptor = ProfileFontCatalog.Resolve(element.FontFamily);
        StyleLabel("Style");
        if ((target.Controls & StyleControls.Bold) != 0)
        {
            using (ImRaii.Disabled(!descriptor.SupportsBold))
            {
                var bold = element.Bold && descriptor.SupportsBold;
                if (ImGui.Checkbox("Bold", ref bold))
                {
                    target.Edit(e => e.Bold = bold, false);
                }
            }

            ImGui.SameLine();
        }

        using (ImRaii.Disabled(!descriptor.SupportsItalic))
        {
            var italic = element.Italic && descriptor.SupportsItalic;
            if (ImGui.Checkbox("Italic", ref italic))
            {
                target.Edit(e => e.Italic = italic, false);
            }
        }

        if ((target.Controls & StyleControls.Effects) != 0)
        {
            StyleLabel("Outline");
            var outline = element.OutlineEnabled;
            if (ImGui.Checkbox("##Outline", ref outline))
            {
                target.Edit(e => e.OutlineEnabled = outline, false);
            }

            if (element.OutlineEnabled)
            {
                ImGui.SameLine();
                var outlineColor = element.OutlineColor;
                if (ImGui.ColorEdit4("##OutlineColor", ref outlineColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
                {
                    var rgb = outlineColor;
                    target.Edit(e => e.OutlineColor = rgb with { W = 1f }, true);
                }

                CommitOnRelease(target);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var thickness = element.OutlineThickness;
                if (ImGui.SliderFloat("##OutlineThickness", ref thickness, 0.5f, TextProfileElement.MaxOutlineThickness, "%.1f px"))
                {
                    var value = thickness;
                    target.Edit(e => e.OutlineThickness = value, true);
                }

                CommitOnRelease(target);
            }

            StyleLabel("Shadow");
            var shadow = element.ShadowEnabled;
            if (ImGui.Checkbox("##Shadow", ref shadow))
            {
                target.Edit(e => e.ShadowEnabled = shadow, false);
            }

            if (element.ShadowEnabled)
            {
                ImGui.SameLine();
                var shadowColor = element.ShadowColor;
                if (ImGui.ColorEdit4("##ShadowColor", ref shadowColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
                {
                    var rgb = shadowColor;
                    target.Edit(e => e.ShadowColor = rgb with { W = 1f }, true);
                }

                CommitOnRelease(target);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var shadowOpacity = element.ShadowOpacity * 100f;
                if (ImGui.SliderFloat("##ShadowOpacity", ref shadowOpacity, 0f, 100f, "%.0f%%"))
                {
                    var value = shadowOpacity / 100f;
                    target.Edit(e => e.ShadowOpacity = value, true);
                }

                CommitOnRelease(target);
            }
        }

        StyleLabel("Align");
        var alignmentClicked = EditorWidgets.Segmented("Align", AlignmentLabels, (int)element.Alignment);
        if (alignmentClicked >= 0)
        {
            var alignment = (TextAlignment)alignmentClicked;
            target.Edit(e => e.Alignment = alignment, false);
        }
    }
}
