using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Basic mode's Component slots: one style picker per slot ("None" or a built-in style), placed by
/// the Adventure Plate Classic layout and colored by the theme. No transforms here — those are the
/// Advanced editor's refinements of the very same Components.
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    /// <summary>Plate Frame and the decorations, for the Style category.</summary>
    private void DrawFrameAndDecorations(ProfileDocument profile)
    {
        ImGui.Spacing();
        Subheading("Frame & Decorations");
        DrawComponentSlot(profile, PlateComponentKind.PlateFrame);
        foreach (var kind in PlateComponentEditor.BasicDecorations)
        {
            DrawComponentSlot(profile, kind);
        }

        Hint("Colors follow your theme. Fine-tune placement and color in the Advanced Editor (Canvas tab, Components).");
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
        }
    }
}
