using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

internal sealed class ProfileEditorWindow : Window, IDisposable
{
    private const float CanvasChildHeight = 360f;
    private const float HandleScreenSize = 8f;

    private static readonly string[] AlignmentLabels = ["Left", "Center", "Right"];

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;

    internal ProfileEditorWindow(ProfileService profileService, EditorSession editorSession)
        : base("AetherFrame Profile Editor##ProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.editorSession = editorSession;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile is currently loaded.");
            return;
        }

        ImGui.TextUnformatted($"Editing: {profile.Name}");
        ImGui.TextUnformatted($"Elements: {profile.Elements.Count}/{ProfileDocument.MaxElementCount}");
        ImGui.Separator();

        DrawCanvas(profile);
        ImGui.Separator();

        // Snapshot before iterating: Remove below mutates the live list, and ImGui buttons
        // can fire mid-loop, which would otherwise invalidate this enumeration.
        foreach (var element in profile.Elements.ToArray())
        {
            if (element is not TextProfileElement textElement)
            {
                continue;
            }

            ImGui.PushID(textElement.Id.ToString());
            DrawTextElementInspector(textElement);
            ImGui.Separator();
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
        ImGui.TextUnformatted(string.IsNullOrEmpty(textElement.Text) ? "(empty)" : textElement.Text);

        ImGui.SameLine();
        if (ImGui.SmallButton("Remove"))
        {
            editorSession.RemoveElement(textElement.Id);
            return;
        }

        ImGui.Indent();

        // Locked stays interactive even while locked, so the user can unlock the element.
        var locked = textElement.Locked;
        if (ImGui.Checkbox("Locked", ref locked))
        {
            editorSession.UpdateElement(textElement.Id, element => element.Locked = locked);
        }

        using (ImRaii.Disabled(textElement.Locked))
        {
            var fontSize = textElement.FontSize;
            if (ImGui.SliderFloat("Font Size", ref fontSize, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize))
            {
                UpdateTextElement(textElement.Id, element => element.FontSize = fontSize);
            }

            var color = textElement.Color;
            if (ImGui.ColorEdit4("Color", ref color))
            {
                UpdateTextElement(textElement.Id, element => element.Color = color);
            }

            var alignmentIndex = (int)textElement.Alignment;
            if (ImGui.Combo("Alignment", ref alignmentIndex, AlignmentLabels, AlignmentLabels.Length))
            {
                var newAlignment = (TextAlignment)alignmentIndex;
                UpdateTextElement(textElement.Id, element => element.Alignment = newAlignment);
            }

            var wrap = textElement.Wrap;
            if (ImGui.Checkbox("Wrap", ref wrap))
            {
                UpdateTextElement(textElement.Id, element => element.Wrap = wrap);
            }

            var visible = textElement.Visible;
            if (ImGui.Checkbox("Visible", ref visible))
            {
                editorSession.UpdateElement(textElement.Id, element => element.Visible = visible);
            }
        }

        ImGui.Unindent();
    }

    /// <summary>
    /// Routes a <see cref="TextProfileElement"/>-specific edit through
    /// <see cref="EditorSession.UpdateElement"/>, which only knows about the common
    /// <see cref="ProfileElement"/> base type.
    /// </summary>
    private void UpdateTextElement(Guid elementId, Action<TextProfileElement> update)
    {
        editorSession.UpdateElement(elementId, element =>
        {
            if (element is TextProfileElement textElement)
            {
                update(textElement);
            }
        });
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

        drawList.AddRectFilled(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(new Vector4(0.09f, 0.09f, 0.09f, 1f)));
        drawList.AddRect(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        // Paint order: ascending ZIndex, ties broken by list (insertion) order, so a later
        // element paints over an earlier one. Hit testing below walks this same array in
        // reverse, so the visually topmost element is always tested (and selected) first.
        var visibleElements = profile.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToArray();

        foreach (var element in visibleElements)
        {
            DrawCanvasElement(drawList, element, canvasOrigin, zoom);
        }

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

    private static void DrawCanvasElement(ImDrawListPtr drawList, ProfileElement element, Vector2 canvasOrigin, float zoom)
    {
        var screenPos = canvasOrigin + element.Position * zoom;
        var screenSize = element.Size * zoom;

        if (element is TextProfileElement textElement)
        {
            drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 0.6f)));
            drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.8f)));

            var text = string.IsNullOrEmpty(textElement.Text) ? "(empty)" : textElement.Text;

            // Render at the element's own FontSize (scaled by zoom), not the current default
            // ImGui font size, so the Font Size control actually changes what's drawn. There's
            // no CalcTextSizeA(font, size, ...) in this binding, so approximate the rendered
            // extent by scaling the default-size measurement — glyph metrics scale linearly.
            var font = ImGui.GetFont();
            var renderedFontSize = Math.Max(1f, textElement.FontSize * zoom);
            var sizeScale = renderedFontSize / ImGui.GetFontSize();
            var textSize = ImGui.CalcTextSize(text) * sizeScale;
            var textPos = screenPos + new Vector2(4f, 4f);

            if (textElement.Alignment == TextAlignment.Center)
            {
                textPos.X = screenPos.X + Math.Max(0f, (screenSize.X - textSize.X) / 2f);
            }
            else if (textElement.Alignment == TextAlignment.Right)
            {
                textPos.X = screenPos.X + Math.Max(0f, screenSize.X - textSize.X - 4f);
            }

            drawList.PushClipRect(screenPos, screenPos + screenSize, true);
            drawList.AddText(font, renderedFontSize, textPos, ImGui.GetColorU32(textElement.Color), text);
            drawList.PopClipRect();
        }
        else
        {
            drawList.AddRectFilled(screenPos, screenPos + screenSize, ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 0.6f)));
            drawList.AddRect(screenPos, screenPos + screenSize, ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.8f)));
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
