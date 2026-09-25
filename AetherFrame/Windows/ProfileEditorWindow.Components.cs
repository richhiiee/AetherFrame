using System;
using System.Numerics;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Inspector Canvas tab's Components section: the same <see cref="PlateComponent"/>s the Basic
/// editor's slots choose, with the Advanced refinements on top — style (including image styles),
/// color, opacity, offset, scale, rotation, and order within the Component's layer. Components are
/// placed by their layer and anchor (see <see cref="ComponentPaintPlan"/>), so they aren't canvas
/// elements: no selection box or Layers entry, and element Z order never moves them out of their layer.
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    // Which Component's details are expanded (runtime-only UI state).
    private Guid? expandedComponentId;

    private void DrawComponentsSection(ProfileDocument profile)
    {
        if (!EditorWidgets.Section("Components"))
        {
            return;
        }

        var components = profile.Components;
        if (components is null || components.Count == 0)
        {
            EditorWidgets.Hint("No Components yet. Add frames, backings, and decorations below; the Basic Editor uses the same ones.");
        }
        else
        {
            // A copy of the ids: an action below may change the list this frame.
            var ids = new Guid[components.Count];
            for (var i = 0; i < components.Count; i++)
            {
                ids[i] = components[i].Id;
            }

            foreach (var componentId in ids)
            {
                if (PlateComponentEditor.Find(profile, componentId) is { } component)
                {
                    DrawComponentRow(profile, component);
                }
            }
        }

        if (profile.UnrecognizedComponents is { Count: > 0 } unreadable)
        {
            EditorWidgets.Hint(unreadable.Count == 1
                ? "1 Component couldn't be read. It isn't shown, but it's kept."
                : $"{unreadable.Count} Components couldn't be read. They aren't shown, but they're kept.");
        }

        DrawAddComponentCombo(profile);
    }

    private void DrawAddComponentCombo(ProfileDocument profile)
    {
        var hasCapacity = PlateComponentEditor.HasCapacity(profile);
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.Disabled(!hasCapacity))
        using (var combo = ImRaii.Combo("##AddComponent", "Add Component..."))
        {
            if (combo.Success)
            {
                foreach (var kind in PlateComponentEditor.AdvancedOnlyKinds)
                {
                    DrawAddComponentGroup(kind);
                }

                foreach (var kind in PlateComponentEditor.BasicSlots)
                {
                    DrawAddComponentGroup(kind);
                }

                foreach (var kind in PlateComponentEditor.BasicDecorations)
                {
                    DrawAddComponentGroup(kind);
                }
            }
        }

        if (!hasCapacity)
        {
            EditorWidgets.Hint($"A Plate can have at most {PlateComponentLimits.MaxComponentCount} Components.");
        }
    }

    private void DrawAddComponentGroup(PlateComponentKind kind)
    {
        ImGui.TextDisabled(PlateComponentEditor.KindLabel(kind));
        foreach (var definition in BuiltInComponentCatalog.OfKind(kind))
        {
            if (ImGui.Selectable($"   {definition.Name}##Add{definition.Id}") && editorSession.AddComponent(definition.Id) is { } added)
            {
                expandedComponentId = added;
            }

            EditorWidgets.Tooltip(definition.Description);
        }
    }

    private void DrawComponentRow(ProfileDocument profile, PlateComponent component)
    {
        using var id = ImRaii.PushId(component.Id.ToString());

        var status = ComponentPaintPlan.Resolve(component, BuiltInComponentCatalog.Instance, out var definition);
        var styleName = definition?.Name ?? "Unavailable";
        var label = $"{PlateComponentEditor.KindLabel(component.Kind)}: {styleName}";

        var visibility = component.Visible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        if (EditorWidgets.IconButton("Visible", visibility, component.Visible ? "Hide" : "Show"))
        {
            var visible = !component.Visible;
            editorSession.EditComponent(component.Id, c => c.Visible = visible, continuous: false);
        }

        ImGui.SameLine();
        var buttons = (ImGui.GetFrameHeight() * 3f) + (ImGui.GetStyle().ItemSpacing.X * 3f);
        var expanded = expandedComponentId == component.Id;
        if (ImGui.Selectable(label, expanded, ImGuiSelectableFlags.None, new Vector2(Math.Max(40f, ImGui.GetContentRegionAvail().X - buttons), 0f)))
        {
            expandedComponentId = expanded ? null : component.Id;
        }

        if (status is not (ComponentStatus.Ready or ComponentStatus.MissingImage))
        {
            EditorWidgets.Tooltip("Made with a newer version of AetherFrame, so it isn't shown here. It's kept on save.");
        }

        ImGui.SameLine();
        if (EditorWidgets.IconButton("Down", FontAwesomeIcon.ArrowDown, "Move down within its layer"))
        {
            editorSession.MoveComponentInLayer(component.Id, -1);
        }

        ImGui.SameLine();
        if (EditorWidgets.IconButton("Up", FontAwesomeIcon.ArrowUp, "Move up within its layer"))
        {
            editorSession.MoveComponentInLayer(component.Id, +1);
        }

        ImGui.SameLine();
        if (EditorWidgets.IconButton("Remove", FontAwesomeIcon.Trash, "Remove (undoable)"))
        {
            editorSession.RemoveComponent(component.Id);
            if (expandedComponentId == component.Id)
            {
                expandedComponentId = null;
            }

            return;
        }

        if (expanded)
        {
            using (ImRaii.PushIndent())
            {
                DrawComponentDetails(profile, component, status, definition);
            }

            ImGui.Spacing();
        }
    }

    private void DrawComponentDetails(ProfileDocument profile, PlateComponent component, ComponentStatus status, ComponentDefinition? definition)
    {
        var componentId = component.Id;

        // Style: any definition of the same kind, image styles included.
        if (ComponentPaintPlan.IsKnownKind(component.Kind))
        {
            EditorWidgets.PropertyLabel("Style");
            using var combo = ImRaii.Combo("##Style", definition?.Name ?? "Unavailable");
            if (combo.Success)
            {
                foreach (var candidate in BuiltInComponentCatalog.OfKind(component.Kind))
                {
                    if (ImGui.Selectable(candidate.Name, candidate.Id == component.DefinitionId) && candidate.Id != component.DefinitionId)
                    {
                        var definitionId = candidate.Id;
                        editorSession.EditComponent(componentId, c => c.DefinitionId = definitionId, continuous: false);
                    }

                    EditorWidgets.Tooltip(candidate.Description);
                }
            }
        }

        if (definition is { RequiresAsset: true })
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
            if (ImGui.Button(component.AssetId is null ? "Choose Image..." : "Replace Image...", new Vector2(-1, 0f)))
            {
                OpenImageFileDialog("Component Image", path => editorSession.SetComponentImage(componentId, path));
            }

            if (status == ComponentStatus.MissingImage)
            {
                EditorWidgets.Hint("Choose an image to show this Component.");
            }
        }

        // Corners (Corner Ornaments): this instance's own selection; another instance can take other corners.
        if (component.Kind == PlateComponentKind.CornerOrnament)
        {
            EditorWidgets.PropertyLabel("Corners");
            if (EditorWidgets.CornerToggles(CornerMasks.Effective(component), out var corner, out var enabled))
            {
                editorSession.SetComponentCorner(componentId, corner, enabled);
            }
        }

        // Color: follows the theme until overridden.
        var hasColor = component.Color is not null;
        EditorWidgets.PropertyLabel("Color");
        if (ImGui.Checkbox("Custom##CustomColor", ref hasColor))
        {
            var start = definition?.DefaultColor(profile) ?? Vector4.One;
            editorSession.EditComponent(componentId, c => c.Color = hasColor ? start : null, continuous: false);
        }

        EditorWidgets.Tooltip("Off: the color follows the Plate's theme.");
        if (component.Color is { } color)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.ColorEdit4("##Color", ref color, ImGuiColorEditFlags.AlphaBar))
            {
                var picked = color;
                editorSession.EditComponent(componentId, c => c.Color = picked, continuous: true);
            }

            CommitComponentOnRelease();
        }

        var opacity = component.Opacity * 100f;
        EditorWidgets.PropertyLabel("Opacity");
        if (ImGui.SliderFloat("##Opacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var value = opacity / 100f;
            editorSession.EditComponent(componentId, c => c.Opacity = value, continuous: true);
        }

        CommitComponentOnRelease();

        var offset = component.Offset;
        EditorWidgets.PropertyLabel("Offset");
        if (ImGui.DragFloat2("##Offset", ref offset, 1f, -PlateComponentLimits.MaxOffset, PlateComponentLimits.MaxOffset, "%.0f"))
        {
            var value = offset;
            editorSession.EditComponent(componentId, c => c.Offset = value, continuous: true);
        }

        CommitComponentOnRelease();

        var scale = component.Scale * 100f;
        EditorWidgets.PropertyLabel("Size");
        if (ImGui.SliderFloat("##Scale", ref scale, PlateComponentLimits.MinScale * 100f, PlateComponentLimits.MaxScale * 100f, "%.0f%%"))
        {
            var value = scale / 100f;
            editorSession.EditComponent(componentId, c => c.Scale = value, continuous: true);
        }

        CommitComponentOnRelease();

        var rotation = component.RotationDegrees;
        EditorWidgets.PropertyLabel("Rotation");
        if (ImGui.SliderFloat("##Rotation", ref rotation, -180f, 180f, "%.1f deg"))
        {
            var value = rotation;
            editorSession.EditComponent(componentId, c => c.RotationDegrees = value, continuous: true);
        }

        CommitComponentOnRelease();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        if (ImGui.Button("Reset Placement & Color", new Vector2(-1, 0f)))
        {
            editorSession.ResetComponentTransform(componentId);
        }

        EditorWidgets.Tooltip("Back to the default placement, full opacity, and the theme color. Style and image are kept.");

        EditorWidgets.Hint($"Layer: {PlateComponentEditor.KindLabel(component.Kind)}. Placed by {AnchorDescription(component.Kind)}.");
    }

    private void CommitComponentOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingDocumentEdit();
        }
    }

    private static string AnchorDescription(PlateComponentKind kind) => kind switch
    {
        PlateComponentKind.Background => "the whole Plate, under the portrait and text",
        PlateComponentKind.PortraitFrame or PlateComponentKind.PortraitOverlay => "the portrait (it follows the portrait's position, size, and rotation)",
        PlateComponentKind.NameBacking => "the name and title",
        PlateComponentKind.Divider => "the space under the name and title",
        PlateComponentKind.SectionHeader => "every section heading",
        PlateComponentKind.CornerOrnament => "the Plate's corners you select (add another Corner Ornament for a different style in other corners)",
        _ => "the Plate's edges",
    };
}
