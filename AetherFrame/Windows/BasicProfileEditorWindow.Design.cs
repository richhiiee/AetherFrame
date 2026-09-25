using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Design category: the Plate as a whole, in order — layout (orientation), Theme, Pattern (both
/// first-class visual browsers), Customize Background (detailed color/mode tuning, collapsed by
/// default), the Plate Frame and decoration Components, then the layout actions that apply to every Basic section.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    private static readonly string[] OrientationLabels = ["Normal", "Mirrored"];

    private void DrawDesignCategory(ProfileDocument profile)
    {
        // Layout choice first.
        Subheading("Orientation");
        var orientation = BasicEditorSession.GetOrientation(profile);
        var clicked = EditorWidgets.Segmented("Orientation", OrientationLabels, (int)orientation);
        if (clicked >= 0)
        {
            basicEditorSession.SetOrientation((AdventurePlateOrientation)clicked);
        }

        Hint("Adventure Plate Classic: a portrait beside your details. Mirrored puts the portrait on the right.");

        // Then the two first-class visual pickers: Theme (background + every Basic text color at
        // once), then Pattern (the background's procedural texture) — both discoverable without
        // first opening Customize Background.
        ImGui.Spacing();
        Subheading("Theme");
        using (ImRaii.PushId("Theme"))
        {
            backgroundPanel.DrawThemePresets(profile, basicEditorSession.ApplyTheme);
        }

        Hint("A theme sets the background and every Basic text color at once. Each value stays editable.");

        ImGui.Spacing();
        Subheading("Pattern");
        using (ImRaii.PushId("Pattern"))
        {
            backgroundPanel.DrawPatternPresets(profile);
        }

        // Fine tuning — mode, exact colors, gradient, image — out of the way until wanted.
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Customize Background##CustomizeBackground"))
        {
            using var id = ImRaii.PushId("Background");
            backgroundPanel.Draw(profile, applyTheme: null);
        }

        DrawSectionHeadingSize(profile);

        DrawFrameAndDecorations(profile);

        DrawPlateLayoutActions(profile);
    }

    /// <summary>
    /// Section heading size: one control for every standard section heading (Home World, Favorite
    /// Job, Free Company, Playstyle, Active Hours, Message) together — a whole-Plate presentation
    /// choice, so it lives here rather than in any one section. A slider drag is one undo step.
    /// (The Advanced Editor still sizes each heading on its own.)
    /// </summary>
    private void DrawSectionHeadingSize(ProfileDocument profile)
    {
        ImGui.Spacing();
        Subheading("Text");

        var headings = BasicPlateEditor.Headings(profile);
        var size = BasicPlateEditor.HeadingSize(profile) ?? AdventurePlateClassicLayout.DefaultHeadingFontSize;

        ImGui.TextUnformatted("Section heading size");
        using (ImRaii.Disabled(headings.Count == 0))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##SectionHeadingSize", ref size, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize, "%.0f px"))
            {
                basicEditorSession.SetHeadingSize(size, continuous: true);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                basicEditorSession.CommitTextEdit();
            }
        }

        ToolTip("The small captions above your details (Home World, Free Company, Message...), all at once.");

        if (headings.Count == 0)
        {
            Hint("Your section headings appear here once your Plate has sections.");
            return;
        }

        if (BasicPlateEditor.HeadingSizesDiffer(profile))
        {
            Hint("Your headings have different sizes (set in the Advanced Editor). Changing this gives them all one size.");
        }

        // A heading larger than its row is drawn fitted to it (auto fit), as everywhere else.
        var shown = headings.Select(h => UI.Rendering.ProfileTextRenderer.GetCachedEffectiveFontSize(h)).OfType<float>().DefaultIfEmpty(size).Min();
        if (shown < size - 0.5f)
        {
            Hint($"Shown at {shown:0} px: headings shrink to fit their row.");
        }
    }

    /// <summary>Layout actions for every Basic section at once, and what needs attention.</summary>
    private void DrawPlateLayoutActions(ProfileDocument profile)
    {
        Subheading("Layout");

        var customized = BasicEditorSession.CustomizedSections(profile);
        if (customized.Count == 0)
        {
            ImGui.TextDisabled("Every section follows the Adventure Plate layout.");
        }
        else
        {
            ImGui.TextColored(CustomizedColor, customized.Count == 1
                ? "1 section is customized in the Advanced Editor."
                : $"{customized.Count} sections are customized in the Advanced Editor.");
            ToolTip(string.Join("\n", customized.Select(GroupTitle))
                + "\n\nBasic keeps customized sections exactly where you placed them, even when you change\nthe orientation. Apply Layout (here or in a section's category) moves them back.");
        }

        DrawOverlapWarnings(profile, static _ => true);

        var half = new Vector2((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f, 0f);
        using (ImRaii.Disabled(!BasicEditorSession.CanResetLayout(profile)))
        {
            if (ImGui.Button("Apply Layout", half))
            {
                basicEditorSession.ApplyLayout();
            }

            ToolTip("Moves every Basic section into the Adventure Plate Classic layout for this orientation,\nincluding sections customized in the Advanced Editor. Content and styles are kept. Undoable.");

            ImGui.SameLine();
            if (ImGui.Button("Reset Basic Layout...", half))
            {
                pendingResetLayoutConfirm = true;
            }

            ToolTip("Back to the Normal orientation with every Basic section in its default place.\nAsks first.");
        }
    }
}
