using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Basic mode's Component slots: one style picker per slot ("None" or a built-in style), placed by
/// the Adventure Plate Classic layout and colored by the theme. The only transforms here are a Corner
/// Ornament's size and distance from the edge (<see cref="CornerOrnamentPlacement"/>); the rest are
/// the Advanced editor's refinements of the very same Components.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    /// <summary>Background artwork, the Plate Frame and the decorations, for the Style category.</summary>
    private void DrawFrameAndDecorations(ProfileDocument profile)
    {
        ImGui.Spacing();
        Subheading("Frame & Decorations");
        DrawComponentSlot(profile, PlateComponentKind.Background);
        DrawComponentSlot(profile, PlateComponentKind.PlateFrame);
        foreach (var kind in PlateComponentEditor.BasicDecorations)
        {
            DrawComponentSlot(profile, kind);
        }

        Hint("Colors follow your theme. Fine-tune rotation, color and every other placement in the Advanced Editor (Canvas tab, Components).");
    }

    /// <summary>One slot: a label and a style combo. Choosing is one undo step.</summary>
    private void DrawComponentSlot(ProfileDocument profile, PlateComponentKind kind)
    {
        using var id = ImRaii.PushId($"ComponentSlot{(int)kind}");

        var current = PlateComponentEditor.FindSlot(profile, kind);
        var status = current is null ? ComponentStatus.Ready : ComponentPaintPlan.Resolve(current, BuiltInComponentCatalog.Instance, out _);
        var currentDefinition = current is null ? null : BuiltInComponentCatalog.Find(current.DefinitionId);
        var preview = current is null
            ? "None"
            : status is ComponentStatus.Ready or ComponentStatus.MissingImage && currentDefinition is not null ? currentDefinition.Name : "Unavailable";

        EditorWidgets.PropertyLabel(PlateComponentEditor.KindLabel(kind));
        using (var combo = ImRaii.Combo("##Style", preview))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("None", current is null) && current is not null)
                {
                    editorSession.SetComponentSlot(kind, null);
                }

                foreach (var definition in BuiltInComponentCatalog.OfKind(kind))
                {
                    if (definition.RequiresAsset)
                    {
                        continue; // needs an image: Advanced only
                    }

                    if (ImGui.Selectable(definition.Name, current?.DefinitionId == definition.Id) && current?.DefinitionId != definition.Id)
                    {
                        editorSession.SetComponentSlot(kind, definition.Id);
                    }

                    ToolTip(definition.Description);
                }
            }
        }

        if (current is not null && status is ComponentStatus.MissingDefinition or ComponentStatus.KindMismatch)
        {
            ToolTip("Made with a newer version of AetherFrame, so it isn't shown here. It's kept unless you choose another style.");
        }
        else if (current is not null && status == ComponentStatus.MissingImage)
        {
            ToolTip("Uses an image that isn't set. Choose one in the Advanced Editor.");
        }

        // Corner Ornaments: which corners this slot's ornament is drawn in (the same instance's
        // transforms apply to every chosen corner; per-corner styles are an Advanced refinement).
        if (current is { Kind: PlateComponentKind.CornerOrnament })
        {
            EditorWidgets.PropertyLabel("Corners");
            if (EditorWidgets.CornerToggles(CornerMasks.Effective(current), out var corner, out var enabled))
            {
                editorSession.SetComponentCorner(current.Id, corner, enabled);
            }

            if (status == ComponentStatus.Ready && currentDefinition is not null)
            {
                DrawCornerOrnamentPlacement(profile, current, currentDefinition);
            }
        }
    }

    /// <summary>
    /// A Corner Ornament's size and distance from the Plate's edge (<see cref="CornerOrnamentPlacement"/>):
    /// the same Scale and Offset the Advanced editor shows, so either editor sees the other's changes.
    /// Each drag is one undo step; Reset puts both back at the defaults a new ornament gets.
    /// </summary>
    private void DrawCornerOrnamentPlacement(ProfileDocument profile, PlateComponent ornament, ComponentDefinition definition)
    {
        var ornamentId = ornament.Id;

        var size = ornament.Scale * 100f;
        EditorWidgets.PropertyLabel("Size");
        if (ImGui.SliderFloat("##OrnamentSize", ref size, CornerOrnamentPlacement.MinBasicScale * 100f, CornerOrnamentPlacement.MaxBasicScale * 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
        {
            editorSession.SetCornerOrnamentScale(ornamentId, size / 100f, continuous: true);
        }

        CommitOrnamentOnRelease();
        ToolTip("Larger or smaller. The ornament grows from its corner, so its distance from the edge stays the same.");

        var distance = CornerOrnamentPlacement.EdgeDistance(profile, ornament, definition);
        EditorWidgets.PropertyLabel("Edge distance");
        if (ImGui.SliderFloat("##OrnamentEdgeDistance", ref distance, CornerOrnamentPlacement.MinEdgeDistance, CornerOrnamentPlacement.MaxEdgeDistance, "%.0f", ImGuiSliderFlags.AlwaysClamp))
        {
            editorSession.SetCornerOrnamentEdgeDistance(ornamentId, distance, continuous: true);
        }

        CommitOrnamentOnRelease();
        ToolTip("How far each ornament sits from the Plate's edge. Lower moves it into the corner; below 0 lets it run past the edge.");

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        using (ImRaii.Disabled(CornerOrnamentPlacement.IsDefault(profile, ornament, definition)))
        {
            if (ImGui.Button("Reset Size & Distance", new Vector2(-1, 0f)))
            {
                editorSession.ResetCornerOrnamentPlacement(ornamentId);
            }
        }

        ToolTip("Back to the default size and distance from the edge. Color, corners and style are kept.");
    }

    private void CommitOrnamentOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingDocumentEdit();
        }
    }
}
