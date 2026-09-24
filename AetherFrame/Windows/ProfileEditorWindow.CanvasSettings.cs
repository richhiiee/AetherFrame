using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Inspector's Canvas tab: canvas size (presets / custom, applied through the resize-choice
/// popup) and the shared <see cref="ProfileBackground"/> (through the shared <see cref="BackgroundStylePanel"/>).
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    private const float MinCanvasDimension = 100f;
    private const float MaxCanvasDimension = 4096f;

    // Runtime-only scratch buffers for the custom width/height fields — resynced from the
    // profile's actual canvas size (see lastSyncedCustomCanvasSize) only when it changes from
    // outside the fields themselves (preset, undo/redo, profile switch), so in-progress typing is
    // never clobbered by the per-frame redraw.
    private float customCanvasWidthInput = ProfileDocument.DefaultCanvasWidth;
    private float customCanvasHeightInput = ProfileDocument.DefaultCanvasHeight;
    private Vector2 lastSyncedCustomCanvasSize = new(-1f, -1f);

    private void DrawCanvasSettings(ProfileDocument profile)
    {
        DrawCanvasSizeSection(profile);
        DrawBackgroundSection(profile);
    }

    /// <summary>
    /// Preset/custom canvas size. Never resizes on its own — every action here just requests the
    /// resize-choice popup (see <see cref="DrawCanvasResizePromptPopup"/>), which is what actually
    /// calls <c>EditorSession.ApplyCanvasResize</c> once the user picks how contents should behave.
    /// </summary>
    private void DrawCanvasSizeSection(ProfileDocument profile)
    {
        if (!EditorWidgets.Section("Canvas"))
        {
            return;
        }

        var currentSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight);
        if (currentSize != lastSyncedCustomCanvasSize)
        {
            customCanvasWidthInput = profile.CanvasWidth;
            customCanvasHeightInput = profile.CanvasHeight;
            lastSyncedCustomCanvasSize = currentSize;
        }

        var matchedPreset = ProfileCanvasPreset.Match(profile.CanvasWidth, profile.CanvasHeight);
        var presetLabel = matchedPreset is null ? "Custom" : $"{matchedPreset.Name} ({matchedPreset.Width:0}x{matchedPreset.Height:0})";

        EditorWidgets.PropertyLabel("Preset");
        using (var combo = ImRaii.Combo("##CanvasPreset", presetLabel))
        {
            if (combo.Success)
            {
                foreach (var preset in ProfileCanvasPreset.All)
                {
                    if (ImGui.Selectable($"{preset.Name} ({preset.Width:0}x{preset.Height:0})", preset == matchedPreset) && preset != matchedPreset)
                    {
                        pendingCanvasResizeOpenRequest = (preset.Width, preset.Height);
                    }
                }
            }
        }

        var halfWidth = (ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
        EditorWidgets.PropertyLabel("Size", halfWidth);
        ImGui.InputFloat("##CustomCanvasWidth", ref customCanvasWidthInput, 0f, 0f, "W %.0f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(halfWidth);
        ImGui.InputFloat("##CustomCanvasHeight", ref customCanvasHeightInput, 0f, 0f, "H %.0f");

        var validCustomSize = customCanvasWidthInput is >= MinCanvasDimension and <= MaxCanvasDimension
            && customCanvasHeightInput is >= MinCanvasDimension and <= MaxCanvasDimension;
        var changed = customCanvasWidthInput != profile.CanvasWidth || customCanvasHeightInput != profile.CanvasHeight;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        using (ImRaii.Disabled(!validCustomSize || !changed))
        {
            if (ImGui.Button("Apply Size", new Vector2(-1, 0f)))
            {
                pendingCanvasResizeOpenRequest = (customCanvasWidthInput, customCanvasHeightInput);
            }
        }

        if (!validCustomSize)
        {
            EditorWidgets.Hint($"Width and height must each be between {MinCanvasDimension:0} and {MaxCanvasDimension:0}.");
        }
    }

    private void DrawBackgroundSection(ProfileDocument profile)
    {
        if (!EditorWidgets.Section("Background"))
        {
            return;
        }

        backgroundPanel.Draw(profile, preset => editorSession.ApplyBackgroundEdit(preset.ApplyTo));
    }

    /// <summary>
    /// Opens (if requested this frame) and draws the "how should the canvas resize?" popup,
    /// mirroring <see cref="DrawElementContextMenuPopup"/>'s deferred-open pattern so it keeps
    /// rendering across frames regardless of which Inspector tab is active.
    /// </summary>
    private void DrawCanvasResizePromptPopup()
    {
        if (pendingCanvasResizeOpenRequest is { } requested)
        {
            canvasResizePromptTarget = requested;
            ImGui.OpenPopup(CanvasResizePopupId);
            pendingCanvasResizeOpenRequest = null;
        }

        using var popup = ImRaii.Popup(CanvasResizePopupId);
        if (!popup.Success)
        {
            return;
        }

        var target = canvasResizePromptTarget;
        const float popupContentWidth = 280f;

        ImGui.TextUnformatted($"Resize canvas to {target.Width:0} x {target.Height:0}?");
        ImGui.Spacing();

        if (ImGui.Button("Resize Canvas Only", new Vector2(popupContentWidth, 0f)))
        {
            editorSession.ApplyCanvasResize(target.Width, target.Height, scaleContentsProportionally: false);
            ImGui.CloseCurrentPopup();
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + popupContentWidth);
        ImGui.TextDisabled("Keeps every element's position and size exactly as-is; only the canvas bounds change.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        if (ImGui.Button("Scale Contents Proportionally", new Vector2(popupContentWidth, 0f)))
        {
            editorSession.ApplyCanvasResize(target.Width, target.Height, scaleContentsProportionally: true);
            ImGui.CloseCurrentPopup();
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + popupContentWidth);
        ImGui.TextDisabled("Scales every element's position and size to match the new canvas proportions.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Cancel", new Vector2(popupContentWidth, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }
    }
}
