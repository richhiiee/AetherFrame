using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Inspector panel: an Element tab (the selected element's properties in compact, collapsible
/// sections) and a Canvas tab (canvas size and background — see <c>.CanvasSettings.cs</c>).
///
/// Every control follows one of two undo patterns: discrete controls (checkbox, toggle, combo,
/// button) apply immediately as one history entry; continuous controls (slider, drag, color,
/// typing) apply live and commit one entry when the widget is released.
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    private static readonly string[] FontFamilyLabels = ProfileFontCatalog.All.Select(f => f.DisplayName).ToArray();
    private static readonly string[] VerticalAlignmentLabels = ["Top", "Middle", "Bottom"];
    private static readonly string[] DisplayModeLabels = ["Fit", "Fill", "Stretch"];
    private static readonly ProfileImageFit[] DisplayModeOrder = [ProfileImageFit.Fit, ProfileImageFit.Fill, ProfileImageFit.Stretch];

    private string nameEditBuffer = string.Empty;
    private Guid nameEditElementId;
    private bool nameFieldWasActive;
    private int textContentSelectAllFrames;

    private void DrawInspectorPanel(ProfileDocument profile, Vector2 size)
    {
        using var panel = ImRaii.Child("##AetherFrameInspectorPanel", size, true);
        if (!panel.Success)
        {
            return;
        }

        // A new selection (from the canvas or Layers) brings the Element tab forward.
        if (editorSession.SelectedElementId != lastInspectedElementId)
        {
            lastInspectedElementId = editorSession.SelectedElementId;
            if (lastInspectedElementId is not null)
            {
                selectElementTabPending = true;
            }
        }

        using var tabBar = ImRaii.TabBar("##AetherFrameInspectorTabs");
        if (!tabBar.Success)
        {
            return;
        }

        var elementTabFlags = selectElementTabPending ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectElementTabPending = false;

        using (var elementTab = ImRaii.TabItem("Element", elementTabFlags))
        {
            if (elementTab.Success)
            {
                using var scroll = ImRaii.Child("##ElementInspectorScroll", new Vector2(-1, -1), false);
                if (scroll.Success)
                {
                    DrawSelectedElementInspector(profile);
                }
            }
        }

        using (var canvasTab = ImRaii.TabItem("Canvas"))
        {
            if (canvasTab.Success)
            {
                using var scroll = ImRaii.Child("##CanvasInspectorScroll", new Vector2(-1, -1), false);
                if (scroll.Success)
                {
                    DrawCanvasSettings(profile);
                }
            }
        }
    }

    private void DrawSelectedElementInspector(ProfileDocument profile)
    {
        var selected = GetSelectedElement(profile);
        if (selected is null)
        {
            ImGui.Spacing();
            EditorWidgets.Hint("Select an element on the canvas or in the Layers panel to edit it.");
            ImGui.Spacing();
            EditorWidgets.Hint("The Canvas tab holds the canvas size and background.");
            return;
        }

        using var id = ImRaii.PushId(selected.Id.GetHashCode());

        DrawGeneralSection(selected);

        // Locking only freezes the transform (the Transform section); content, typography,
        // appearance, and image display settings stay editable while locked.
        switch (selected)
        {
            case TextProfileElement text:
                DrawTextContentSection(text);
                DrawTransformSection(profile, text);
                DrawTypographySection(text);
                DrawTextAppearanceSection(text);
                break;

            case ImageProfileElement image:
                DrawTransformSection(profile, image);
                DrawImageSection(image);
                break;
        }

        if (selected.Locked)
        {
            ImGui.Spacing();
            EditorWidgets.Hint("Locked: position, size, rotation, and alignment can't change until it's unlocked (General, or the lock in Layers). Other properties stay editable.");
        }
    }

    // ---------------------------------------------------------------- shared sections

    /// <summary>Name / Visible / Locked — always editable, even while locked (so it can be unlocked).</summary>
    private void DrawGeneralSection(ProfileElement element)
    {
        if (!EditorWidgets.Section("General"))
        {
            return;
        }

        // The field edits a scratch buffer, resynced whenever the selection changes or the name
        // changes from elsewhere (Layers rename, undo) while the field isn't being typed in.
        if (nameEditElementId != element.Id || (!nameFieldWasActive && nameEditBuffer != element.Name))
        {
            nameEditElementId = element.Id;
            nameEditBuffer = element.Name;
        }

        EditorWidgets.PropertyLabel("Name");
        ImGui.InputTextWithHint("##ElementName", ProfileElementNames.GetAutomaticName(element), ref nameEditBuffer, ProfileElement.MaxNameLength);
        nameFieldWasActive = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.RenameElement(element.Id, nameEditBuffer);
        }

        EditorWidgets.PropertyLabel("Visible", 0f);
        var visible = element.Visible;
        if (ImGui.Checkbox("##Visible", ref visible))
        {
            editorSession.SetElementVisible(element.Id, visible);
        }

        ImGui.SameLine(LabelColumnOffset(2));
        using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.DimTextColor))
        {
            ImGui.TextUnformatted("Locked");
        }

        ImGui.SameLine();
        var locked = element.Locked;
        if (ImGui.Checkbox("##Locked", ref locked))
        {
            editorSession.SetElementLocked(element.Id, locked);
        }

        // Only elements Basic mode still owns (a legacy tagline is ordinary Advanced content now).
        if (ProfileElementNames.GetRoleLabel(element.Role) is { } role && Domain.Basic.BasicSections.SectionOf(element.Role) is not null)
        {
            EditorWidgets.Hint($"Basic editor: {role}");
        }
    }

    private void DrawTransformSection(ProfileDocument profile, ProfileElement element)
    {
        if (!EditorWidgets.Section("Transform"))
        {
            return;
        }

        using var lockedScope = ImRaii.Disabled(element.Locked);

        var halfWidth = (ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
        var canvas = new Vector2(profile.CanvasWidth, profile.CanvasHeight);

        // Position (clamped exactly like dragging on the canvas).
        var position = element.Position;
        EditorWidgets.PropertyLabel("Position", halfWidth);
        var positionChanged = ImGui.DragFloat("##PosX", ref position.X, 1f, -canvas.X, canvas.X * 2f, "X %.0f");
        var positionDone = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(halfWidth);
        positionChanged |= ImGui.DragFloat("##PosY", ref position.Y, 1f, -canvas.Y, canvas.Y * 2f, "Y %.0f");
        positionDone |= ImGui.IsItemDeactivatedAfterEdit();

        if (positionChanged)
        {
            var clamped = editorSession.ClampElementPosition(element, position);
            editorSession.BeginOrContinueEdit(element.Id, e => e.Position = clamped);
        }

        if (positionDone)
        {
            editorSession.CommitPendingEdit();
        }

        // Size (an image with Preserve Ratio keeps its current aspect).
        var size = element.Size;
        var lockedAspect = element is ImageProfileElement { PreserveAspectRatio: true } && size.Y > 0f ? size.X / size.Y : (float?)null;
        EditorWidgets.PropertyLabel("Size", halfWidth);
        var widthChanged = ImGui.DragFloat("##SizeW", ref size.X, 1f, EditorSession.MinElementWidth, canvas.X, "W %.0f", ImGuiSliderFlags.AlwaysClamp);
        var sizeDone = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(halfWidth);
        var heightChanged = ImGui.DragFloat("##SizeH", ref size.Y, 1f, EditorSession.MinElementHeight, canvas.Y, "H %.0f", ImGuiSliderFlags.AlwaysClamp);
        sizeDone |= ImGui.IsItemDeactivatedAfterEdit();

        if (widthChanged || heightChanged)
        {
            if (lockedAspect is { } aspect)
            {
                size = widthChanged ? new Vector2(size.X, size.X / aspect) : new Vector2(size.Y * aspect, size.Y);
            }

            var newSize = Vector2.Max(size, new Vector2(EditorSession.MinElementWidth, EditorSession.MinElementHeight));
            editorSession.BeginOrContinueEdit(element.Id, e => e.Size = newSize);
        }

        if (sizeDone)
        {
            editorSession.CommitPendingEdit();
        }

        if (element is ImageProfileElement image)
        {
            DrawRotationRow(image);
        }

        DrawAlignmentRow();
    }

    private void DrawRotationRow(ImageProfileElement image)
    {
        // Precise, arbitrary rotation; the buttons are the quick 90-degree steps. A full slider
        // drag is one history entry. Ctrl+Click the slider to type an exact value.
        var buttonSize = ImGui.GetFrameHeight();
        var sliderWidth = ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ((buttonSize + 2f) * 3f) - 4f;

        var rotation = image.RotationDegrees;
        EditorWidgets.PropertyLabel("Rotation", sliderWidth);
        if (ImGui.SliderFloat("##Rotation", ref rotation, 0f, 359.9f, "%.1f deg"))
        {
            var normalized = RotationGeometry.NormalizeDegrees(rotation);
            ContinueImageEdit(image.Id, element => element.RotationDegrees = normalized);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingEdit();
        }

        ImGui.SameLine(0f, 4f);
        if (EditorWidgets.IconButton("RotLeft", FontAwesomeIcon.UndoAlt, "Rotate left 90", buttonSize))
        {
            RotateImageBy(image.Id, -90f);
        }

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconButton("RotRight", FontAwesomeIcon.RedoAlt, "Rotate right 90", buttonSize))
        {
            RotateImageBy(image.Id, 90f);
        }

        ImGui.SameLine(0f, 2f);
        using (ImRaii.Disabled(image.RotationDegrees == 0f))
        {
            if (EditorWidgets.IconButton("RotReset", FontAwesomeIcon.Times, "Reset rotation", buttonSize))
            {
                ApplyImmediateImageEdit(image.Id, element => element.RotationDegrees = 0f);
            }
        }
    }

    /// <summary>Align the selected element to the canvas (by its visual, rotation-aware bounds).</summary>
    private void DrawAlignmentRow()
    {
        var buttonSize = ImGui.GetFrameHeight();

        EditorWidgets.PropertyLabel("Align", 0f);
        AlignButton("AlignLeft", FontAwesomeIcon.AlignLeft, "Align left", CanvasAlignment.Left);
        ImGui.SameLine(0f, 2f);
        AlignButton("AlignHCenter", FontAwesomeIcon.AlignCenter, "Align horizontal center", CanvasAlignment.HorizontalCenter);
        ImGui.SameLine(0f, 2f);
        AlignButton("AlignRight", FontAwesomeIcon.AlignRight, "Align right", CanvasAlignment.Right);
        ImGui.SameLine(0f, 10f);
        AlignButton("AlignTop", FontAwesomeIcon.ArrowUp, "Align top", CanvasAlignment.Top);
        ImGui.SameLine(0f, 2f);
        AlignButton("AlignVCenter", FontAwesomeIcon.ArrowsAltV, "Align vertical center", CanvasAlignment.VerticalCenter);
        ImGui.SameLine(0f, 2f);
        AlignButton("AlignBottom", FontAwesomeIcon.ArrowDown, "Align bottom", CanvasAlignment.Bottom);

        void AlignButton(string id, FontAwesomeIcon icon, string tooltip, CanvasAlignment alignment)
        {
            if (EditorWidgets.IconButton(id, icon, tooltip + " (to canvas)", buttonSize))
            {
                editorSession.AlignSelected(alignment);
            }
        }
    }

    // ---------------------------------------------------------------- text

    private void DrawTextContentSection(TextProfileElement text)
    {
        if (!EditorWidgets.Section("Text"))
        {
            return;
        }

        // Deferred edit: every keystroke updates the live profile immediately (so the canvas and
        // Profile View reflect it as you type), but only one history entry is recorded for the
        // whole typing session, once the widget deactivates. While this widget holds keyboard
        // focus, ImGui reports WantTextInput, which already makes KeyboardShortcutService stand
        // down — so Ctrl+Z/Ctrl+Y here stay ordinary text-field undo/redo.
        if (focusTextContentPending)
        {
            ImGui.SetKeyboardFocusHere();
            focusTextContentPending = false;

            // Select-all only for this programmatic focus (e.g. right after + Text, so typing
            // replaces the placeholder content) — never for an ordinary click into the box.
            textContentSelectAllFrames = 2;
        }

        var contentFlags = textContentSelectAllFrames > 0 ? ImGuiInputTextFlags.AutoSelectAll : ImGuiInputTextFlags.None;
        if (textContentSelectAllFrames > 0)
        {
            textContentSelectAllFrames--;
        }

        var content = text.Text;
        if (ImGui.InputTextMultiline("##TextContent", ref content, TextProfileElement.MaxTextLength, new Vector2(-1, 72f), contentFlags))
        {
            ContinueTextEdit(text.Id, element => element.Text = content);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingEdit();
        }

        // Decorations drawn around the text (stored separately; the text itself never contains them).
        var halfWidth = (ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
        var prefix = text.Prefix;
        EditorWidgets.PropertyLabel("Decoration", halfWidth);
        if (ImGui.InputTextWithHint("##Prefix", "Prefix", ref prefix, TextProfileElement.MaxAffixLength))
        {
            ContinueTextEdit(text.Id, element => element.Prefix = prefix);
        }

        CommitOnRelease();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(halfWidth);
        var suffix = text.Suffix;
        if (ImGui.InputTextWithHint("##Suffix", "Suffix", ref suffix, TextProfileElement.MaxAffixLength))
        {
            ContinueTextEdit(text.Id, element => element.Suffix = suffix);
        }

        CommitOnRelease();
    }

    private void DrawTypographySection(TextProfileElement text)
    {
        if (!EditorWidgets.Section("Typography"))
        {
            return;
        }

        var familyIndex = 0;
        for (var i = 0; i < ProfileFontCatalog.All.Count; i++)
        {
            if (ProfileFontCatalog.All[i].Id == text.FontFamily)
            {
                familyIndex = i;
                break;
            }
        }

        EditorWidgets.PropertyLabel("Font");
        if (ImGui.Combo("##Family", ref familyIndex, FontFamilyLabels, FontFamilyLabels.Length))
        {
            var newFamily = ProfileFontCatalog.All[familyIndex].Id;
            ApplyImmediateTextEdit(text.Id, element => element.FontFamily = newFamily);
        }

        var fontSize = text.FontSize;
        EditorWidgets.PropertyLabel("Size");
        if (ImGui.SliderFloat("##FontSize", ref fontSize, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize, "%.0f px"))
        {
            ContinueTextEdit(text.Id, element => element.FontSize = fontSize);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingEdit();
        }

        // Bold/Italic only for a family with the real face to back them (see ProfileFontCatalog);
        // Underline/Strikethrough are plain line draws, available for every family.
        var descriptor = ProfileFontCatalog.Resolve(text.FontFamily);
        EditorWidgets.PropertyLabel("Style", 0f);

        using (ImRaii.Disabled(!descriptor.SupportsBold))
        {
            if (EditorWidgets.IconToggle("Bold", FontAwesomeIcon.Bold, text.Bold && descriptor.SupportsBold, descriptor.SupportsBold ? "Bold" : "This font has no bold face"))
            {
                ApplyImmediateTextEdit(text.Id, element => element.Bold = !element.Bold);
            }
        }

        ImGui.SameLine(0f, 2f);
        using (ImRaii.Disabled(!descriptor.SupportsItalic))
        {
            if (EditorWidgets.IconToggle("Italic", FontAwesomeIcon.Italic, text.Italic && descriptor.SupportsItalic, descriptor.SupportsItalic ? "Italic" : "This font has no italic face"))
            {
                ApplyImmediateTextEdit(text.Id, element => element.Italic = !element.Italic);
            }
        }

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconToggle("Underline", FontAwesomeIcon.Underline, text.Underline, "Underline"))
        {
            ApplyImmediateTextEdit(text.Id, element => element.Underline = !element.Underline);
        }

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconToggle("Strike", FontAwesomeIcon.Strikethrough, text.Strikethrough, "Strikethrough"))
        {
            ApplyImmediateTextEdit(text.Id, element => element.Strikethrough = !element.Strikethrough);
        }

        EditorWidgets.PropertyLabel("Horizontal", 0f);
        HorizontalAlignButton("HLeft", FontAwesomeIcon.AlignLeft, TextAlignment.Left, "Left");
        ImGui.SameLine(0f, 2f);
        HorizontalAlignButton("HCenter", FontAwesomeIcon.AlignCenter, TextAlignment.Center, "Center");
        ImGui.SameLine(0f, 2f);
        HorizontalAlignButton("HRight", FontAwesomeIcon.AlignRight, TextAlignment.Right, "Right");

        EditorWidgets.PropertyLabel("Vertical", 0f);
        var verticalClicked = EditorWidgets.Segmented("VAlign", VerticalAlignmentLabels, (int)text.VerticalAlignment);
        if (verticalClicked >= 0)
        {
            var newAlignment = (TextVerticalAlignment)verticalClicked;
            ApplyImmediateTextEdit(text.Id, element => element.VerticalAlignment = newAlignment);
        }

        // Legacy text (saved before layout versions) never wrapped, so its checkbox shows the
        // effective state. Changing Wrap or Auto Fit is the explicit opt-in to the current layout.
        EditorWidgets.PropertyLabel("Wrap", 0f);
        var wrap = text.EffectiveWrap;
        if (ImGui.Checkbox("##Wrap", ref wrap))
        {
            ApplyImmediateTextEdit(text.Id, element =>
            {
                element.Wrap = wrap;
                element.LayoutVersion = TextProfileElement.CurrentLayoutVersion;
            });
        }

        var letterSpacing = text.LetterSpacing;
        EditorWidgets.PropertyLabel("Letter Spacing");
        if (ImGui.SliderFloat("##LetterSpacing", ref letterSpacing, TextProfileElement.MinLetterSpacing, TextProfileElement.MaxLetterSpacing, "%.1f px"))
        {
            ContinueTextEdit(text.Id, element => element.LetterSpacing = letterSpacing);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingEdit();
        }

        var lineSpacing = text.LineSpacing;
        EditorWidgets.PropertyLabel("Line Spacing");
        if (ImGui.SliderFloat("##LineSpacing", ref lineSpacing, TextProfileElement.MinLineSpacing, TextProfileElement.MaxLineSpacing, "%.2fx"))
        {
            ContinueTextEdit(text.Id, element => element.LineSpacing = lineSpacing);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingEdit();
        }

        EditorWidgets.PropertyLabel("Auto Fit", 0f);
        var autoFit = text.EffectiveAutoFit;
        if (ImGui.Checkbox("##AutoFit", ref autoFit))
        {
            ApplyImmediateTextEdit(text.Id, element =>
            {
                element.AutoFitText = autoFit;
                element.LayoutVersion = TextProfileElement.CurrentLayoutVersion;
            });
        }

        EditorWidgets.Tooltip("Shrinks the text (never below the minimum) until it fits its box.\nThe text and its font size setting are never changed.");

        if (text.EffectiveAutoFit)
        {
            ImGui.SameLine();
            if (ProfileTextRenderer.GetCachedEffectiveFontSize(text) is { } effective && effective < text.FontSize - 0.05f)
            {
                ImGui.TextColored(EditorWidgets.DimTextColor, $"fitted to {effective:0.#} px");
            }

            var minimum = Math.Min(text.AutoFitMinimumSize, text.FontSize);
            EditorWidgets.PropertyLabel("Minimum");
            if (ImGui.SliderFloat("##AutoFitMin", ref minimum, TextProfileElement.MinFontSize, Math.Max(TextProfileElement.MinFontSize, text.FontSize), "%.0f px"))
            {
                ContinueTextEdit(text.Id, element => element.AutoFitMinimumSize = minimum);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }
        }

        if (text.UsesLegacyLayout)
        {
            ImGui.Spacing();
            EditorWidgets.Hint("Legacy layout: this text keeps its original look (no wrapping, original padding and alignment).");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
            if (ImGui.Button("Use Current Layout", new Vector2(-1, 0f)))
            {
                ApplyImmediateTextEdit(text.Id, element => element.LayoutVersion = TextProfileElement.CurrentLayoutVersion);
            }

            EditorWidgets.Tooltip(text.Wrap
                ? "Switches to the current text layout: real word wrapping (Wrap is on),\npadding that scales with the canvas, and per-line alignment. Undoable."
                : "Switches to the current text layout: padding that scales with the canvas\nand per-line alignment. Undoable.");
        }

        void HorizontalAlignButton(string id, FontAwesomeIcon icon, TextAlignment alignment, string tooltip)
        {
            if (EditorWidgets.IconToggle(id, icon, text.Alignment == alignment, tooltip) && text.Alignment != alignment)
            {
                ApplyImmediateTextEdit(text.Id, element => element.Alignment = alignment);
            }
        }
    }

    private void DrawTextAppearanceSection(TextProfileElement text)
    {
        if (!EditorWidgets.Section("Appearance"))
        {
            return;
        }

        // Color edits RGB only; Opacity (the color's alpha) fades the whole text — fill,
        // outline, and shadow together.
        var color = text.Color;
        EditorWidgets.PropertyLabel("Color");
        if (ImGui.ColorEdit4("##TextColor", ref color, ImGuiColorEditFlags.NoAlpha))
        {
            var rgb = color;
            ContinueTextEdit(text.Id, element => element.Color = rgb with { W = element.Color.W });
        }

        CommitOnRelease();

        // A Basic Plate's character name follows its theme until given a custom color; this puts it
        // back under the theme (one undo step; opacity kept).
        if (text.Role == ProfileElementRole.BasicName && profileService.CurrentProfile is { BasicPlate: not null } plate)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
            if (Domain.Basic.BasicNameColor.IsAutomatic(plate))
            {
                ImGui.TextDisabled("Follows the theme");
            }
            else if (ImGui.SmallButton("Use Theme Color"))
            {
                editorSession.ApplyDocumentEdit(() => Domain.Basic.BasicNameColor.Reset(plate));
            }

            EditorWidgets.Tooltip("The name's color follows the Basic theme until you pick your own; a custom color stays when the theme changes.");
        }

        var opacity = text.Color.W * 100f;
        EditorWidgets.PropertyLabel("Opacity");
        if (ImGui.SliderFloat("##TextOpacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var alpha = opacity / 100f;
            ContinueTextEdit(text.Id, element => element.Color = element.Color with { W = alpha });
        }

        CommitOnRelease();

        // Outline
        ImGui.Spacing();
        EditorWidgets.PropertyLabel("Outline", 0f);
        var outlineEnabled = text.OutlineEnabled;
        if (ImGui.Checkbox("##OutlineEnabled", ref outlineEnabled))
        {
            ApplyImmediateTextEdit(text.Id, element => element.OutlineEnabled = outlineEnabled);
        }

        if (text.OutlineEnabled)
        {
            ImGui.SameLine();
            var outlineColor = text.OutlineColor;
            if (ImGui.ColorEdit4("##OutlineColor", ref outlineColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
            {
                var rgb = outlineColor;
                ContinueTextEdit(text.Id, element => element.OutlineColor = rgb with { W = 1f });
            }

            CommitOnRelease();

            var thickness = text.OutlineThickness;
            EditorWidgets.PropertyLabel("  Thickness");
            if (ImGui.SliderFloat("##OutlineThickness", ref thickness, 0.5f, TextProfileElement.MaxOutlineThickness, "%.1f px"))
            {
                ContinueTextEdit(text.Id, element => element.OutlineThickness = thickness);
            }

            CommitOnRelease();

            var outlineOpacity = text.OutlineOpacity * 100f;
            EditorWidgets.PropertyLabel("  Opacity");
            if (ImGui.SliderFloat("##OutlineOpacity", ref outlineOpacity, 0f, 100f, "%.0f%%"))
            {
                var value = outlineOpacity / 100f;
                ContinueTextEdit(text.Id, element => element.OutlineOpacity = value);
            }

            CommitOnRelease();
        }

        // Shadow
        ImGui.Spacing();
        EditorWidgets.PropertyLabel("Shadow", 0f);
        var shadowEnabled = text.ShadowEnabled;
        if (ImGui.Checkbox("##ShadowEnabled", ref shadowEnabled))
        {
            ApplyImmediateTextEdit(text.Id, element => element.ShadowEnabled = shadowEnabled);
        }

        if (text.ShadowEnabled)
        {
            ImGui.SameLine();
            var shadowColor = text.ShadowColor;
            if (ImGui.ColorEdit4("##ShadowColor", ref shadowColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
            {
                var rgb = shadowColor;
                ContinueTextEdit(text.Id, element => element.ShadowColor = rgb with { W = 1f });
            }

            CommitOnRelease();

            var shadowOpacity = text.ShadowOpacity * 100f;
            EditorWidgets.PropertyLabel("  Opacity");
            if (ImGui.SliderFloat("##ShadowOpacity", ref shadowOpacity, 0f, 100f, "%.0f%%"))
            {
                var value = shadowOpacity / 100f;
                ContinueTextEdit(text.Id, element => element.ShadowOpacity = value);
            }

            CommitOnRelease();

            var halfWidth = (ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ImGui.GetStyle().ItemSpacing.X) / 2f;
            var offset = new Vector2(text.ShadowOffsetX, text.ShadowOffsetY);
            EditorWidgets.PropertyLabel("  Offset", halfWidth);
            var offsetChanged = ImGui.DragFloat("##ShadowX", ref offset.X, 0.25f, -TextProfileElement.MaxShadowOffset, TextProfileElement.MaxShadowOffset, "X %.1f", ImGuiSliderFlags.AlwaysClamp);
            var offsetDone = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(halfWidth);
            offsetChanged |= ImGui.DragFloat("##ShadowY", ref offset.Y, 0.25f, -TextProfileElement.MaxShadowOffset, TextProfileElement.MaxShadowOffset, "Y %.1f", ImGuiSliderFlags.AlwaysClamp);
            offsetDone |= ImGui.IsItemDeactivatedAfterEdit();

            if (offsetChanged)
            {
                ContinueTextEdit(text.Id, element =>
                {
                    element.ShadowOffsetX = offset.X;
                    element.ShadowOffsetY = offset.Y;
                });
            }

            if (offsetDone)
            {
                editorSession.CommitPendingEdit();
            }
        }
    }

    // ---------------------------------------------------------------- image

    private void DrawImageSection(ImageProfileElement image)
    {
        if (!EditorWidgets.Section("Image"))
        {
            return;
        }

        EditorWidgets.PropertyLabel("Display", 0f);
        var modeClicked = EditorWidgets.Segmented("DisplayMode", DisplayModeLabels, Array.IndexOf(DisplayModeOrder, image.DisplayMode));
        if (modeClicked >= 0)
        {
            var newMode = DisplayModeOrder[modeClicked];
            ApplyImmediateImageEdit(image.Id, element => element.DisplayMode = newMode);
        }

        EditorWidgets.PropertyLabel("Keep Ratio", 0f);
        var preserveAspectRatio = image.PreserveAspectRatio;
        if (ImGui.Checkbox("##PreserveRatio", ref preserveAspectRatio))
        {
            ApplyImmediateImageEdit(image.Id, element => element.PreserveAspectRatio = preserveAspectRatio);
        }

        EditorWidgets.Tooltip("Keep the box's proportions while resizing");

        EditorWidgets.PropertyLabel("Flip", 0f);
        if (EditorWidgets.TextToggle("Flip X", image.FlipX, tooltip: "Mirror horizontally"))
        {
            ApplyImmediateImageEdit(image.Id, element => element.FlipX = !element.FlipX);
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Flip Y", image.FlipY, tooltip: "Mirror vertically"))
        {
            ApplyImmediateImageEdit(image.Id, element => element.FlipY = !element.FlipY);
        }

        var opacity = image.Opacity * 100f;
        EditorWidgets.PropertyLabel("Opacity");
        if (ImGui.SliderFloat("##ImageOpacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            var value = opacity / 100f;
            ContinueImageEdit(image.Id, element => element.Opacity = value);
        }

        CommitOnRelease();

        EditorWidgets.PropertyLabel("Native", 0f);
        var native = renderResources.Images.GetNativeSize(image.AssetId);
        ImGui.TextUnformatted(native is { } n ? $"{n.Width} x {n.Height} px" : "Unavailable");

        ImGui.Spacing();
        var halfButton = new Vector2((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f, 0f);
        if (ImGui.Button("Replace Image...", halfButton))
        {
            var elementId = image.Id;
            OpenImageFileDialog("Replace Image", path => editorSession.ReplaceImage(elementId, path));
        }

        EditorWidgets.Tooltip("Keeps position, size, rotation, flips, and display mode");

        ImGui.SameLine();
        using (ImRaii.Disabled(native is null || image.Locked))
        {
            if (ImGui.Button("Native Ratio", halfButton))
            {
                editorSession.ResetImageToNativeAspect(image.Id);
            }
        }

        EditorWidgets.Tooltip("Reset the box to the image's native aspect ratio (keeps width and center)");
    }

    // ---------------------------------------------------------------- edit routing helpers

    /// <summary>Commits the pending continuous edit when the widget just drawn is released.</summary>
    private void CommitOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingEdit();
        }
    }

    /// <summary>Routes a discrete (checkbox/combo/toggle) <see cref="TextProfileElement"/> edit.</summary>
    private void ApplyImmediateTextEdit(Guid elementId, Action<TextProfileElement> update)
    {
        editorSession.ApplyImmediateEdit(elementId, element =>
        {
            if (element is TextProfileElement textElement)
            {
                update(textElement);
            }
        });
    }

    /// <summary>Routes a live, in-progress (slider/color/typing) <see cref="TextProfileElement"/> edit.</summary>
    private void ContinueTextEdit(Guid elementId, Action<TextProfileElement> update)
    {
        editorSession.BeginOrContinueEdit(elementId, element =>
        {
            if (element is TextProfileElement textElement)
            {
                update(textElement);
            }
        });
    }

    /// <summary>Routes a discrete (checkbox/toggle/button) <see cref="ImageProfileElement"/> edit.</summary>
    private void ApplyImmediateImageEdit(Guid elementId, Action<ImageProfileElement> update)
    {
        editorSession.ApplyImmediateEdit(elementId, element =>
        {
            if (element is ImageProfileElement imageElement)
            {
                update(imageElement);
            }
        });
    }

    /// <summary>Routes a live, in-progress (slider) <see cref="ImageProfileElement"/> edit.</summary>
    private void ContinueImageEdit(Guid elementId, Action<ImageProfileElement> update)
    {
        editorSession.BeginOrContinueEdit(elementId, element =>
        {
            if (element is ImageProfileElement imageElement)
            {
                update(imageElement);
            }
        });
    }

    /// <summary>X offset for a second label/value pair on the same row (e.g. Visible | Locked).</summary>
    private static float LabelColumnOffset(int column) =>
        EditorWidgets.LabelColumnWidth + ((column - 1) * 56f);

}
