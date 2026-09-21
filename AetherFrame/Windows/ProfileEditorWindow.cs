using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

internal sealed class ProfileEditorWindow : Window, IDisposable
{
    private const float CanvasChildHeight = 360f;
    private const float HandleScreenSize = 8f;

    private static readonly string[] AlignmentLabels = ["Left", "Center", "Right"];
    private static readonly string[] FitModeLabels = ["Cover", "Contain", "Stretch"];

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ImageTextureCache imageTextureCache;
    private readonly FileDialogManager fileDialogManager;

    internal ProfileEditorWindow(
        ProfileService profileService,
        EditorSession editorSession,
        KeyboardShortcutService keyboardShortcutService,
        ImageTextureCache imageTextureCache,
        FileDialogManager fileDialogManager)
        : base("AetherFrame Profile Editor##ProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.editorSession = editorSession;
        this.keyboardShortcutService = keyboardShortcutService;
        this.imageTextureCache = imageTextureCache;
        this.fileDialogManager = fileDialogManager;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Called by the window system exactly once when this window closes. Draw won't run again
    /// until it reopens, so this is the only reliable place to tell the keyboard service to
    /// stop intercepting immediately rather than leaving it stuck on stale "focused" state.
    /// </summary>
    public override void OnClose()
    {
        keyboardShortcutService.SetEditorFocusState(editorFocused: false, textInputActive: false);
        fileDialogManager.Reset();
    }

    public override void Draw()
    {
        // Drawn unconditionally so an in-progress file pick isn't stranded if the profile
        // becomes unavailable (e.g. character logs out) while the dialog is open.
        fileDialogManager.Draw();

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile is currently loaded.");
            return;
        }

        PublishKeyboardFocusState();
        ApplyPendingShortcutActions();

        ImGui.TextUnformatted($"Editing: {profile.Name}");
        ImGui.TextUnformatted($"Elements: {profile.Elements.Count}/{ProfileDocument.MaxElementCount}");

        using (ImRaii.Disabled(!editorSession.CanUndo))
        {
            if (ImGui.SmallButton("Undo"))
            {
                editorSession.Undo();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!editorSession.CanRedo))
        {
            if (ImGui.SmallButton("Redo"))
            {
                editorSession.Redo();
            }
        }

        ImGui.Separator();

        DrawCanvas(profile);
        ImGui.Separator();

        DrawBackgroundControls(profile);
        ImGui.Separator();

        // Snapshot before iterating: Remove below mutates the live list, and ImGui buttons
        // can fire mid-loop, which would otherwise invalidate this enumeration.
        foreach (var element in profile.Elements.ToArray())
        {
            ImGui.PushID(element.Id.ToString());

            switch (element)
            {
                case TextProfileElement textElement:
                    DrawTextElementInspector(textElement);
                    ImGui.Separator();
                    break;
                case ImageProfileElement imageElement:
                    DrawImageElementInspector(imageElement);
                    ImGui.Separator();
                    break;
            }

            ImGui.PopID();
        }

        ImGui.Separator();

        var buffer = editorSession.NewElementText;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline("##NewElementText", ref buffer, TextProfileElement.MaxTextLength, new Vector2(-1, 60)))
        {
            editorSession.NewElementText = buffer;
        }

        var atCapacity = profile.Elements.Count >= ProfileDocument.MaxElementCount;
        if (atCapacity)
        {
            ImGui.TextUnformatted($"Profile is at the maximum of {ProfileDocument.MaxElementCount} elements.");
        }

        if (ImGui.Button("Add Text") && !atCapacity)
        {
            editorSession.AddTextElement();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(atCapacity))
        {
            if (ImGui.Button("Add Image"))
            {
                OpenImageFileDialog("Add Image", path => editorSession.AddImageElement(path));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Save Profile"))
        {
            editorSession.SaveProfile();
        }

        if (profileService.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Saving...");
        }
        else if (editorSession.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("(unsaved changes)");
        }

        if (editorSession.ErrorMessage is { } error)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), error);
        }
    }

    private void DrawTextElementInspector(TextProfileElement textElement)
    {
        var isSelected = editorSession.SelectedElementId == textElement.Id;

        ImGui.TextUnformatted((isSelected ? "> " : string.Empty) + (string.IsNullOrEmpty(textElement.Text) ? "(empty)" : textElement.Text));

        ImGui.SameLine();
        if (ImGui.SmallButton("Remove"))
        {
            editorSession.RemoveElement(textElement.Id);
            return;
        }

        ImGui.Indent();

        DrawZOrderAndDuplicateControls(textElement.Id);

        // Locked stays interactive even while locked, so the user can unlock the element.
        var locked = textElement.Locked;
        if (ImGui.Checkbox("Locked", ref locked))
        {
            editorSession.ApplyImmediateEdit(textElement.Id, element => element.Locked = locked);
        }

        using (ImRaii.Disabled(textElement.Locked))
        {
            var fontSize = textElement.FontSize;
            if (ImGui.SliderFloat("Font Size", ref fontSize, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize))
            {
                ContinueTextEdit(textElement.Id, element => element.FontSize = fontSize);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            var color = textElement.Color;
            if (ImGui.ColorEdit4("Color", ref color))
            {
                ContinueTextEdit(textElement.Id, element => element.Color = color);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            var alignmentIndex = (int)textElement.Alignment;
            if (ImGui.Combo("Alignment", ref alignmentIndex, AlignmentLabels, AlignmentLabels.Length))
            {
                var newAlignment = (TextAlignment)alignmentIndex;
                ApplyImmediateTextEdit(textElement.Id, element => element.Alignment = newAlignment);
            }

            var wrap = textElement.Wrap;
            if (ImGui.Checkbox("Wrap", ref wrap))
            {
                ApplyImmediateTextEdit(textElement.Id, element => element.Wrap = wrap);
            }

            var visible = textElement.Visible;
            if (ImGui.Checkbox("Visible", ref visible))
            {
                editorSession.ApplyImmediateEdit(textElement.Id, element => element.Visible = visible);
            }
        }

        ImGui.Unindent();
    }

    /// <summary>Routes a discrete (checkbox/combo) <see cref="TextProfileElement"/> edit.</summary>
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

    /// <summary>Routes a live, in-progress (slider/color) <see cref="TextProfileElement"/> edit.</summary>
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

    private void DrawImageElementInspector(ImageProfileElement imageElement)
    {
        var isSelected = editorSession.SelectedElementId == imageElement.Id;

        ImGui.TextUnformatted((isSelected ? "> " : string.Empty) + "Image");

        ImGui.SameLine();
        if (ImGui.SmallButton("Remove"))
        {
            editorSession.RemoveElement(imageElement.Id);
            return;
        }

        ImGui.Indent();

        DrawZOrderAndDuplicateControls(imageElement.Id);

        // Locked stays interactive even while locked, so the user can unlock the element.
        var locked = imageElement.Locked;
        if (ImGui.Checkbox("Locked", ref locked))
        {
            editorSession.ApplyImmediateEdit(imageElement.Id, element => element.Locked = locked);
        }

        using (ImRaii.Disabled(imageElement.Locked))
        {
            var opacity = imageElement.Opacity;
            if (ImGui.SliderFloat("Opacity", ref opacity, 0f, 1f))
            {
                ContinueImageEdit(imageElement.Id, element => element.Opacity = opacity);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            var preserveAspectRatio = imageElement.PreserveAspectRatio;
            if (ImGui.Checkbox("Preserve Aspect Ratio", ref preserveAspectRatio))
            {
                ApplyImmediateImageEdit(imageElement.Id, element => element.PreserveAspectRatio = preserveAspectRatio);
            }

            var visible = imageElement.Visible;
            if (ImGui.Checkbox("Visible", ref visible))
            {
                editorSession.ApplyImmediateEdit(imageElement.Id, element => element.Visible = visible);
            }

            if (ImGui.SmallButton("Replace Image"))
            {
                var elementId = imageElement.Id;
                OpenImageFileDialog("Replace Image", path => editorSession.ReplaceImage(elementId, path));
            }
        }

        ImGui.Unindent();
    }

    /// <summary>Routes a discrete (checkbox) <see cref="ImageProfileElement"/> edit.</summary>
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

    /// <summary>
    /// Bring Forward / Send Backward / Bring to Front / Send to Back / Duplicate, shared by
    /// every element inspector. These operate on ZIndex under the hood, but that number itself
    /// is never shown to the user.
    /// </summary>
    private void DrawZOrderAndDuplicateControls(Guid elementId)
    {
        if (ImGui.SmallButton("Bring Forward"))
        {
            editorSession.BringForward(elementId);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Send Backward"))
        {
            editorSession.SendBackward(elementId);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Bring to Front"))
        {
            editorSession.BringToFront(elementId);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Send to Back"))
        {
            editorSession.SendToBack(elementId);
        }

        if (ImGui.SmallButton("Duplicate"))
        {
            editorSession.DuplicateElement(elementId);
        }
    }

    private void DrawBackgroundControls(ProfileDocument profile)
    {
        ImGui.TextUnformatted("Background: " + (profile.BackgroundAssetId is null ? "(none)" : "set"));

        if (ImGui.SmallButton("Set Background"))
        {
            OpenImageFileDialog("Set Background", path => editorSession.SetBackground(path));
        }

        if (profile.BackgroundAssetId is null)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Remove Background"))
        {
            editorSession.RemoveBackground();
        }

        var fitModeIndex = (int)profile.BackgroundFitMode;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Fit", ref fitModeIndex, FitModeLabels, FitModeLabels.Length))
        {
            var newFitMode = (BackgroundFitMode)fitModeIndex;
            editorSession.ApplyBackgroundEdit(document => document.BackgroundFitMode = newFitMode);
        }

        var opacity = profile.BackgroundOpacity;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Background Opacity", ref opacity, 0f, 1f))
        {
            editorSession.BeginOrContinueBackgroundEdit(document => document.BackgroundOpacity = opacity);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingBackgroundEdit();
        }
    }

    /// <summary>
    /// Opens a single-file picker restricted to the image formats AetherFrame currently
    /// supports, invoking <paramref name="onSelected"/> with the chosen path on success.
    /// </summary>
    private void OpenImageFileDialog(string title, Action<string> onSelected)
    {
        fileDialogManager.OpenFileDialog(title, ImageFormatSupport.BuildFileDialogFilter(), (success, path) =>
        {
            if (success)
            {
                onSelected(path);
            }
        });
    }

    /// <summary>
    /// Publishes this frame's focus/text-input state to <see cref="KeyboardShortcutService"/>
    /// so it knows whether to intercept shortcuts on the NEXT <c>Framework.Update</c> tick.
    /// Detection/suppression happen there now, not here — see that class for why.
    /// </summary>
    private void PublishKeyboardFocusState()
    {
        var io = ImGui.GetIO();
        var editorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var textInputActive = io.WantTextInput;

        keyboardShortcutService.SetEditorFocusState(editorFocused, textInputActive);

        if (editorFocused && !textInputActive)
        {
            // Claim the keyboard at the ImGui/WndProc level too, so clicks/typing route to us.
            // The actual suppression that stops FFXIV from also reacting happens earlier, in
            // KeyboardShortcutService during Framework.Update — this is a secondary measure.
            ImGui.SetNextFrameWantCaptureKeyboard(true);
        }
    }

    /// <summary>
    /// Applies shortcut actions queued by <see cref="KeyboardShortcutService"/> during
    /// <c>Framework.Update</c> (key detection/suppression already happened there; this just
    /// performs the resulting edit through the normal, render-thread-only EditorSession API).
    /// </summary>
    private void ApplyPendingShortcutActions()
    {
        foreach (var action in keyboardShortcutService.DequeuePendingActions())
        {
            switch (action.Kind)
            {
                case EditorShortcutActionKind.Undo:
                    editorSession.Undo();
                    break;
                case EditorShortcutActionKind.Redo:
                    editorSession.Redo();
                    break;
                case EditorShortcutActionKind.Delete:
                    if (editorSession.SelectedElementId is { } selectedId)
                    {
                        editorSession.RemoveElement(selectedId);
                    }

                    break;
                case EditorShortcutActionKind.Nudge:
                    editorSession.NudgeSelected(action.NudgeDelta);
                    break;
            }
        }
    }

    private void DrawCanvas(ProfileDocument profile)
    {
        var zoom = editorSession.Zoom;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Zoom", ref zoom, EditorSession.MinZoom, EditorSession.MaxZoom))
        {
            editorSession.Zoom = zoom;
        }

        using var child = ImRaii.Child(
            "##AetherFrameCanvasScroll", new Vector2(-1, CanvasChildHeight), true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child.Success)
        {
            return;
        }

        var canvasOrigin = ImGui.GetCursorScreenPos();
        var canvasScreenSize = new Vector2(ProfileDocument.CanvasWidth, ProfileDocument.CanvasHeight) * zoom;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton("##AetherFrameCanvasArea", canvasScreenSize);
        var canvasHovered = ImGui.IsItemHovered();

        // The background isn't a ProfileElement: it's always painted first (beneath every
        // element) and is deliberately excluded from hit testing below, so it can never be
        // selected, dragged, resized, or reordered like an ordinary element.
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, zoom, imageTextureCache);

        // Editor-only chrome: outlines the logical canvas bounds. Not part of the finished
        // profile's visual content, so ProfileRenderer (shared with presentation mode) doesn't
        // draw it.
        drawList.AddRect(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        // Hit testing below walks this array in reverse, so the visually topmost element (paint
        // order: ascending ZIndex, ties broken by list order) is always tested/selected first.
        var visibleElements = profile.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToArray();

        var selectedElement = editorSession.SelectedElementId is { } selectedId
            ? profile.Elements.Find(e => e.Id == selectedId)
            : null;

        Vector2 selectedScreenPos = default;
        Vector2 selectedScreenSize = default;

        if (selectedElement is not null)
        {
            selectedScreenPos = canvasOrigin + selectedElement.Position * zoom;
            selectedScreenSize = selectedElement.Size * zoom;

            drawList.AddRect(selectedScreenPos, selectedScreenPos + selectedScreenSize, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f)));

            if (!selectedElement.Locked)
            {
                DrawResizeHandles(drawList, selectedScreenPos, selectedScreenSize);
            }
        }

        HandleCanvasInput(profile, visibleElements, selectedElement, selectedScreenPos, selectedScreenSize, canvasOrigin, canvasHovered, zoom);
    }

    private void HandleCanvasInput(
        ProfileDocument profile,
        ProfileElement[] visibleElementsInPaintOrder,
        ProfileElement? selectedElement,
        Vector2 selectedScreenPos,
        Vector2 selectedScreenSize,
        Vector2 canvasOrigin,
        bool canvasHovered,
        float zoom)
    {
        var mouseScreen = ImGui.GetMousePos();
        var logicalMouse = (mouseScreen - canvasOrigin) / zoom;
        var leftClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var leftReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);

        if (editorSession.ActiveInteraction != ElementInteractionKind.None)
        {
            if (leftReleased)
            {
                editorSession.EndInteraction();
            }
            else
            {
                var mouseDelta = ImGui.GetIO().MouseDelta;
                if (mouseDelta.X != 0f || mouseDelta.Y != 0f)
                {
                    editorSession.UpdateInteraction(logicalMouse);
                }
            }

            return;
        }

        if (!canvasHovered)
        {
            return;
        }

        if (selectedElement is not null && !selectedElement.Locked
            && TryGetHoveredHandle(mouseScreen, selectedScreenPos, selectedScreenSize, out var hoveredHandle))
        {
            ImGui.SetMouseCursor(hoveredHandle is ResizeHandle.TopLeft or ResizeHandle.BottomRight
                ? ImGuiMouseCursor.ResizeNwse
                : ImGuiMouseCursor.ResizeNesw);

            if (leftClicked)
            {
                editorSession.BeginResize(selectedElement, hoveredHandle, logicalMouse);
                return;
            }
        }

        if (!leftClicked)
        {
            return;
        }

        var hitElement = HitTestElement(visibleElementsInPaintOrder, logicalMouse);
        editorSession.Select(hitElement?.Id);

        if (hitElement is not null && !hitElement.Locked)
        {
            editorSession.BeginDrag(hitElement, logicalMouse);
        }
    }

    private static void DrawResizeHandles(ImDrawListPtr drawList, Vector2 elementScreenPos, Vector2 elementScreenSize)
    {
        var color = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f));
        var half = HandleScreenSize / 2f;

        foreach (var (center, _) in GetHandleCorners(elementScreenPos, elementScreenSize))
        {
            drawList.AddRectFilled(center - new Vector2(half, half), center + new Vector2(half, half), color);
        }
    }

    private static bool TryGetHoveredHandle(
        Vector2 mouseScreen, Vector2 elementScreenPos, Vector2 elementScreenSize, out ResizeHandle handle)
    {
        var half = HandleScreenSize;

        foreach (var (center, candidate) in GetHandleCorners(elementScreenPos, elementScreenSize))
        {
            var min = center - new Vector2(half, half);
            var max = center + new Vector2(half, half);

            if (mouseScreen.X >= min.X && mouseScreen.X <= max.X && mouseScreen.Y >= min.Y && mouseScreen.Y <= max.Y)
            {
                handle = candidate;
                return true;
            }
        }

        handle = ResizeHandle.None;
        return false;
    }

    private static (Vector2 Center, ResizeHandle Handle)[] GetHandleCorners(Vector2 pos, Vector2 size) =>
    [
        (pos, ResizeHandle.TopLeft),
        (pos + new Vector2(size.X, 0f), ResizeHandle.TopRight),
        (pos + new Vector2(0f, size.Y), ResizeHandle.BottomLeft),
        (pos + size, ResizeHandle.BottomRight),
    ];

    /// <summary>Topmost-first hit test: walks the paint-ordered array backwards.</summary>
    private static ProfileElement? HitTestElement(ProfileElement[] paintOrderElements, Vector2 logicalPoint)
    {
        for (var i = paintOrderElements.Length - 1; i >= 0; i--)
        {
            var element = paintOrderElements[i];
            var min = element.Position;
            var max = element.Position + element.Size;

            if (logicalPoint.X >= min.X && logicalPoint.X <= max.X && logicalPoint.Y >= min.Y && logicalPoint.Y <= max.Y)
            {
                return element;
            }
        }

        return null;
    }
}
