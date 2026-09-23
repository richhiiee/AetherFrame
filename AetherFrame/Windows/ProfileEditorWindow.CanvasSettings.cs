using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Inspector's Canvas tab: canvas size (presets / custom, applied through the resize-choice
/// popup) and the shared <see cref="ProfileBackground"/> — mode, theme presets, colors,
/// gradient, texture, image, and opacity. Every mode's settings are kept while switching modes.
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    private const float MinCanvasDimension = 100f;
    private const float MaxCanvasDimension = 4096f;

    private static readonly string[] BackgroundModeLabels = ["None", "Solid Color", "Linear Gradient", "Textured Fill", "Image"];
    private static readonly string[] TextureLabels = ["None", "Fine Noise", "Dots", "Grid", "Diagonal Lines", "Crosshatch", "Subtle Paper"];
    private static readonly string[] ImageFitLabels = ["Fit", "Fill", "Stretch"];
    private static readonly ProfileImageFit[] ImageFitOrder = [ProfileImageFit.Fit, ProfileImageFit.Fill, ProfileImageFit.Stretch];

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

        if (profile.Background is not { } background)
        {
            EditorWidgets.Hint("Background unavailable.");
            return;
        }

        var modeIndex = (int)background.Mode;
        EditorWidgets.PropertyLabel("Mode");
        if (ImGui.Combo("##BackgroundMode", ref modeIndex, BackgroundModeLabels, BackgroundModeLabels.Length))
        {
            var newMode = (ProfileBackgroundMode)modeIndex;
            editorSession.ApplyBackgroundEdit(style => style.Mode = newMode);
        }

        if (background.Mode != ProfileBackgroundMode.Image)
        {
            DrawThemePresets();
        }

        switch (background.Mode)
        {
            case ProfileBackgroundMode.None:
                EditorWidgets.Hint("No background. Choose a mode or a theme above.");
                return;

            case ProfileBackgroundMode.SolidColor:
                DrawBackgroundColor("Color", "##BgPrimary", background.PrimaryColor, primary: true);
                DrawSolidSwatches();
                break;

            case ProfileBackgroundMode.LinearGradient:
                DrawGradientControls(background);
                break;

            case ProfileBackgroundMode.TexturedFill:
                DrawTextureControls(background);
                break;

            case ProfileBackgroundMode.Image:
                DrawBackgroundImageControls(background);
                break;
        }

        ImGui.Spacing();
        var opacity = background.Opacity * 100f;
        EditorWidgets.PropertyLabel("Opacity");
        if (ImGui.SliderFloat("##BgOpacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var value = opacity / 100f;
            editorSession.BeginOrContinueBackgroundEdit(style => style.Opacity = value);
        }

        CommitBackgroundOnRelease();
    }

    /// <summary>Two-color starting points; applying one only copies values (everything stays editable).</summary>
    private void DrawThemePresets()
    {
        EditorWidgets.PropertyLabel("Theme", 0f);

        var spacing = 4f;
        var perRow = 4;
        var swatchWidth = (ImGui.GetContentRegionAvail().X - (spacing * (perRow - 1))) / perRow;
        var swatchSize = new Vector2(swatchWidth, ImGui.GetFrameHeight());
        var rowStartX = ImGui.GetCursorPosX();

        for (var i = 0; i < ProfileThemePresets.All.Length; i++)
        {
            var preset = ProfileThemePresets.All[i];
            if (i > 0)
            {
                if (i % perRow == 0)
                {
                    ImGui.SetCursorPosX(rowStartX);
                }
                else
                {
                    ImGui.SameLine(0f, spacing);
                }
            }

            if (EditorWidgets.GradientSwatch($"##Theme{i}", preset.PrimaryColor, preset.SecondaryColor, swatchSize, $"{preset.Name} theme"))
            {
                editorSession.ApplyBackgroundEdit(preset.ApplyTo);
            }
        }
    }

    private void DrawSolidSwatches()
    {
        EditorWidgets.PropertyLabel("Swatches", 0f);

        const int perRow = 8;
        var spacing = 3f;
        var size = MathF.Floor((ImGui.GetContentRegionAvail().X - (spacing * (perRow - 1))) / perRow);
        var rowStartX = ImGui.GetCursorPosX();

        for (var i = 0; i < ProfileThemePresets.SolidSwatches.Length; i++)
        {
            if (i > 0)
            {
                if (i % perRow == 0)
                {
                    ImGui.SetCursorPosX(rowStartX);
                }
                else
                {
                    ImGui.SameLine(0f, spacing);
                }
            }

            var swatch = ProfileThemePresets.SolidSwatches[i];
            if (EditorWidgets.Swatch($"##Swatch{i}", swatch, size))
            {
                editorSession.ApplyBackgroundEdit(style => style.PrimaryColor = swatch);
            }
        }
    }

    private void DrawGradientControls(ProfileBackground background)
    {
        DrawBackgroundColor("From", "##BgPrimary", background.PrimaryColor, primary: true);
        DrawBackgroundColor("To", "##BgSecondary", background.SecondaryColor, primary: false);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        if (ImGui.Button("Swap Colors", new Vector2(-1, 0f)))
        {
            editorSession.ApplyBackgroundEdit(style => (style.PrimaryColor, style.SecondaryColor) = (style.SecondaryColor, style.PrimaryColor));
        }

        var angle = background.GradientAngle;
        var buttonSize = ImGui.GetFrameHeight();
        EditorWidgets.PropertyLabel("Angle", ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ((buttonSize + 2f) * 2f) - 2f);
        if (ImGui.SliderFloat("##GradientAngle", ref angle, 0f, 360f, "%.0f deg"))
        {
            var value = angle;
            editorSession.BeginOrContinueBackgroundEdit(style => style.GradientAngle = value);
        }

        CommitBackgroundOnRelease();

        ImGui.SameLine(0f, 4f);
        if (EditorWidgets.IconButton("AngleMinus", FontAwesomeIcon.UndoAlt, "Rotate -45", buttonSize))
        {
            editorSession.ApplyBackgroundEdit(style => style.GradientAngle = WrapAngle(style.GradientAngle - 45f));
        }

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconButton("AnglePlus", FontAwesomeIcon.RedoAlt, "Rotate +45", buttonSize))
        {
            editorSession.ApplyBackgroundEdit(style => style.GradientAngle = WrapAngle(style.GradientAngle + 45f));
        }
    }

    private void DrawTextureControls(ProfileBackground background)
    {
        var textureIndex = (int)background.Texture;
        EditorWidgets.PropertyLabel("Texture");
        if (ImGui.Combo("##Texture", ref textureIndex, TextureLabels, TextureLabels.Length))
        {
            var newTexture = (ProfileBackgroundTexture)textureIndex;
            editorSession.ApplyBackgroundEdit(style => style.Texture = newTexture);
        }

        DrawBackgroundColor("Base", "##BgPrimary", background.PrimaryColor, primary: true);
        DrawBackgroundColor("Pattern", "##BgSecondary", background.SecondaryColor, primary: false);

        if (background.Texture == ProfileBackgroundTexture.None)
        {
            return;
        }

        var intensity = background.TextureIntensity * 100f;
        EditorWidgets.PropertyLabel("Intensity");
        if (ImGui.SliderFloat("##TextureIntensity", ref intensity, 0f, 100f, "%.0f%%"))
        {
            var value = intensity / 100f;
            editorSession.BeginOrContinueBackgroundEdit(style => style.TextureIntensity = value);
        }

        CommitBackgroundOnRelease();

        var scale = background.TextureScale;
        EditorWidgets.PropertyLabel("Scale");
        if (ImGui.SliderFloat("##TextureScale", ref scale, ProfileBackground.MinTextureScale, ProfileBackground.MaxTextureScale, "%.0f px", ImGuiSliderFlags.AlwaysClamp))
        {
            var value = scale;
            editorSession.BeginOrContinueBackgroundEdit(style => style.TextureScale = value);
        }

        CommitBackgroundOnRelease();

        if (ProfileBackground.SupportsRotation(background.Texture))
        {
            var rotation = background.TextureRotation;
            EditorWidgets.PropertyLabel("Rotation");
            if (ImGui.SliderFloat("##TextureRotation", ref rotation, 0f, 360f, "%.0f deg"))
            {
                var value = rotation;
                editorSession.BeginOrContinueBackgroundEdit(style => style.TextureRotation = value);
            }

            CommitBackgroundOnRelease();
        }
    }

    private void DrawBackgroundImageControls(ProfileBackground background)
    {
        var halfButton = new Vector2((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f, 0f);

        if (ImGui.Button(background.ImageAssetId is null ? "Choose Image..." : "Replace Image...", halfButton))
        {
            OpenImageFileDialog("Background Image", path => editorSession.SetBackground(path));
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(background.ImageAssetId is null))
        {
            if (ImGui.Button("Remove Image", halfButton))
            {
                editorSession.RemoveBackground();
            }
        }

        if (background.ImageAssetId is not { } assetId)
        {
            EditorWidgets.Hint("No image chosen yet.");
            return;
        }

        if (renderResources.Images.GetNativeSize(assetId) is { } native)
        {
            EditorWidgets.PropertyLabel("Native", 0f);
            ImGui.TextUnformatted($"{native.Width} x {native.Height} px");
        }

        EditorWidgets.PropertyLabel("Fit", 0f);
        var fitClicked = EditorWidgets.Segmented("BgFit", ImageFitLabels, Array.IndexOf(ImageFitOrder, background.ImageFit));
        if (fitClicked >= 0)
        {
            var newFit = ImageFitOrder[fitClicked];
            editorSession.ApplyBackgroundEdit(style => style.ImageFit = newFit);
        }

        EditorWidgets.PropertyLabel("Flip", 0f);
        if (EditorWidgets.TextToggle("Flip X##Bg", background.ImageFlipX, tooltip: "Mirror horizontally"))
        {
            editorSession.ApplyBackgroundEdit(style => style.ImageFlipX = !style.ImageFlipX);
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Flip Y##Bg", background.ImageFlipY, tooltip: "Mirror vertically"))
        {
            editorSession.ApplyBackgroundEdit(style => style.ImageFlipY = !style.ImageFlipY);
        }
    }

    /// <summary>A background color picker (RGB; the background's own Opacity controls transparency).</summary>
    private void DrawBackgroundColor(string label, string id, Vector4 current, bool primary)
    {
        var color = current;
        EditorWidgets.PropertyLabel(label);
        if (ImGui.ColorEdit4(id, ref color, ImGuiColorEditFlags.NoAlpha))
        {
            var value = color with { W = 1f };
            editorSession.BeginOrContinueBackgroundEdit(style =>
            {
                if (primary)
                {
                    style.PrimaryColor = value;
                }
                else
                {
                    style.SecondaryColor = value;
                }
            });
        }

        CommitBackgroundOnRelease();
    }

    private void CommitBackgroundOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingBackgroundEdit();
        }
    }

    private static float WrapAngle(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
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
