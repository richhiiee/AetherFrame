using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The canvas panel: viewport (fit, wheel zoom around the cursor, middle-drag pan), the shared
/// profile rendering, editor-only chrome on top of it (canvas border, hover/selection outlines,
/// resize handles, snap guides), and mouse input (select, drag, resize, context menu).
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    // Screen-space sizes below are in unscaled pixels, at Dalamud's global UI scale when used, so
    // handles stay grabbable and snapping feels the same on a high-DPI screen.
    private static float HandleScreenSize => EditorWidgets.Scaled(8f);

    // Snapping pulls within this many SCREEN pixels, at any zoom — close enough to feel helpful,
    // small enough not to fight deliberate placement.
    private static float SnapThresholdScreenPixels => EditorWidgets.Scaled(6f);

    private const float WheelZoomStep = 1.15f;

    // How much of the canvas must stay inside the panel when panning.
    private static float PanKeepVisiblePixels => EditorWidgets.Scaled(48f);

    private static readonly Vector4 SelectionColor = new(1f, 0.85f, 0.2f, 1f);
    private static readonly Vector4 HoverColor = new(0.45f, 0.72f, 1f, 0.75f);
    private static readonly Vector4 SnapGuideColor = new(1f, 0.28f, 0.62f, 0.95f);
    private static readonly Vector4 CanvasBorderColor = new(0.4f, 0.4f, 0.4f, 1f);

    // Fit-to-window viewport state — all runtime only, never persisted with the profile.
    // lastCanvasPanelSize starts at a sentinel that can never match a real panel size, so Auto
    // Fit's size-change check always fires once on the first Draw after (re)opening.
    private Vector2 lastCanvasPanelSize = new(-1f, -1f);
    private bool isPanning;

    /// <summary>Fit: re-enable Auto Fit and fit the canvas to the panel right away (button, F key).</summary>
    private void FitCanvas()
    {
        editorSession.AutoFit = true;
        if (lastCanvasPanelSize.X > 0f)
        {
            editorSession.ApplyFitZoom(lastCanvasPanelSize);
        }
    }

    private void DrawCanvasPanel(ProfileDocument profile, Vector2 size)
    {
        using var child = ImRaii.Child("##AetherFrameCanvasPanel", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child.Success)
        {
            return;
        }

        // The interior content region (post-border/padding), not the outer `size` passed in —
        // this is what the canvas actually has to fit inside.
        var panelSize = ImGui.GetContentRegionAvail();
        var panelMin = ImGui.GetCursorScreenPos();
        var panelCenter = panelMin + (panelSize / 2f);

        if (editorSession.AutoFit && Vector2.DistanceSquared(panelSize, lastCanvasPanelSize) > 0.25f)
        {
            // Covers both the initial Fit-to-Window on open (lastCanvasPanelSize starts at an
            // impossible sentinel) and continuous re-fitting while the panel is being resized.
            editorSession.ApplyFitZoom(panelSize);
        }

        lastCanvasPanelSize = panelSize;
        editorSession.ClampPan(panelSize, PanKeepVisiblePixels);

        var zoom = editorSession.Zoom;
        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * zoom;
        var canvasOrigin = panelCenter - (canvasScreenSize / 2f) + editorSession.PanOffset;

        // One interaction surface over the whole panel: clicks beside the canvas still deselect,
        // and wheel/pan work anywhere in the panel.
        ImGui.InvisibleButton("##AetherFrameCanvasSurface", Vector2.Max(panelSize, Vector2.One), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var panelHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(panelMin, panelMin + panelSize, true);

        var showGuides = editorSession.ShowGuides;
        var renderOptions = showGuides ? EditorPlaceholders.CanvasOptions : EditorPlaceholders.CanvasOptionsWithoutGuides;
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, zoom, renderResources, renderOptions);

        if (showGuides)
        {
            // Editor-only chrome: outlines the logical canvas bounds.
            drawList.AddRect(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(CanvasBorderColor));
        }

        // Hit testing walks paint order in reverse, so the visually topmost element wins.
        ProfilePaintOrder.Fill(profile, paintOrderBuffer, includeHidden: false);

        var selectedElement = GetSelectedElement(profile);
        Vector2[]? selectedScreenCorners = null;

        var logicalMouse = (ImGui.GetMousePos() - canvasOrigin) / zoom;
        var hoverTarget = panelHovered && editorSession.ActiveInteraction == ElementInteractionKind.None && !isPanning
            ? ProfilePaintOrder.HitTest(paintOrderBuffer, logicalMouse)
            : null;

        if (showGuides && hoverTarget is not null && hoverTarget.Id != selectedElement?.Id)
        {
            var hoverCorners = GetScreenCorners(hoverTarget, canvasOrigin, zoom);
            drawList.AddQuad(hoverCorners[0], hoverCorners[1], hoverCorners[2], hoverCorners[3], ImGui.GetColorU32(HoverColor), 1.5f);
        }

        if (selectedElement is not null)
        {
            // Rotated corners (identity for rotation 0), not an axis-aligned rect, so the
            // outline and handles always match what's actually drawn/hit-tested.
            selectedScreenCorners = GetScreenCorners(selectedElement, canvasOrigin, zoom);

            if (showGuides)
            {
                var outlineColor = ImGui.GetColorU32(selectedElement.Locked ? SelectionColor with { W = 0.45f } : SelectionColor);
                drawList.AddQuad(selectedScreenCorners[0], selectedScreenCorners[1], selectedScreenCorners[2], selectedScreenCorners[3], outlineColor, 1.5f);

                if (!selectedElement.Locked && selectedElement.Visible)
                {
                    DrawResizeHandles(drawList, selectedScreenCorners);
                }
            }
        }

        DrawSnapGuides(drawList, canvasOrigin, zoom);
        drawList.PopClipRect();

        HandleNavigation(panelHovered, canvasOrigin, panelCenter);

        // Guides off also disables resize-handle interaction (nothing is drawn to grab) by
        // simply not handing HandleCanvasInput any corners to hit-test against; plain click-to-
        // select and drag-to-move on the canvas stay fully functional either way.
        HandleCanvasInput(selectedElement, showGuides && selectedElement is { Visible: true } ? selectedScreenCorners : null, hoverTarget, logicalMouse, panelHovered, zoom);

        paintOrderBuffer.Clear();
    }

    private static Vector2[] GetScreenCorners(ProfileElement element, Vector2 canvasOrigin, float zoom)
    {
        var corners = RotationGeometry.GetRotatedCorners(element.Position, element.Size, RotationGeometry.GetRotationDegrees(element));
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = canvasOrigin + (corners[i] * zoom);
        }

        return corners;
    }

    /// <summary>Editor-only alignment guides for the current snapped drag/resize.</summary>
    private void DrawSnapGuides(ImDrawListPtr drawList, Vector2 canvasOrigin, float zoom)
    {
        var guides = editorSession.SnapGuides;
        if (guides.Count == 0)
        {
            return;
        }

        var color = ImGui.GetColorU32(SnapGuideColor);
        foreach (var guide in guides)
        {
            Vector2 from, to;
            if (guide.Vertical)
            {
                from = canvasOrigin + (new Vector2(guide.Position, guide.SpanStart) * zoom);
                to = canvasOrigin + (new Vector2(guide.Position, guide.SpanEnd) * zoom);
            }
            else
            {
                from = canvasOrigin + (new Vector2(guide.SpanStart, guide.Position) * zoom);
                to = canvasOrigin + (new Vector2(guide.SpanEnd, guide.Position) * zoom);
            }

            drawList.AddLine(from, to, color, 1f);
        }
    }

    /// <summary>Mouse-wheel zoom around the cursor and middle-mouse-drag panning.</summary>
    private void HandleNavigation(bool panelHovered, Vector2 canvasOrigin, Vector2 panelCenter)
    {
        var io = ImGui.GetIO();

        if (panelHovered && io.MouseWheel != 0f && editorSession.ActiveInteraction == ElementInteractionKind.None)
        {
            editorSession.ZoomAround(MathF.Pow(WheelZoomStep, io.MouseWheel), ImGui.GetMousePos(), canvasOrigin, panelCenter);
        }

        if (panelHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            isPanning = true;
        }

        if (isPanning)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                if (io.MouseDelta != Vector2.Zero)
                {
                    editorSession.PanBy(io.MouseDelta);
                }
            }
            else
            {
                isPanning = false;
            }
        }
    }

    private void HandleCanvasInput(
        ProfileElement? selectedElement,
        Vector2[]? selectedScreenCorners,
        ProfileElement? hoverTarget,
        Vector2 logicalMouse,
        bool panelHovered,
        float zoom)
    {
        var io = ImGui.GetIO();
        var mouseScreen = ImGui.GetMousePos();
        var leftClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var leftReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        if (editorSession.ActiveInteraction != ElementInteractionKind.None)
        {
            if (leftReleased || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                editorSession.EndInteraction();
            }
            else
            {
                // Every frame (not just on mouse movement), so pressing/releasing Alt mid-drag
                // takes effect immediately.
                var snap = editorSession.SnapEnabled && !io.KeyAlt;
                editorSession.UpdateInteraction(logicalMouse, snap, SnapThresholdScreenPixels / zoom);

                if (editorSession.ActiveInteraction == ElementInteractionKind.Dragging)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                }
            }

            return;
        }

        if (!panelHovered || isPanning)
        {
            return;
        }

        if (selectedElement is not null && !selectedElement.Locked && selectedScreenCorners is not null
            && TryGetHoveredHandle(mouseScreen, selectedScreenCorners, out var hoveredHandle))
        {
            ImGui.SetMouseCursor(GetResizeCursor(selectedScreenCorners, hoveredHandle));

            if (leftClicked)
            {
                editorSession.BeginResize(selectedElement, hoveredHandle, logicalMouse);
                return;
            }
        }
        else if (hoverTarget is { Locked: false })
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        // A locked element is selected like any other (click, right-click menu), but never
        // transformed from the canvas: BeginDrag is a no-op for it, and it gets no resize handles.
        if (rightClicked)
        {
            if (hoverTarget is not null)
            {
                RequestElementContextMenu(hoverTarget.Id);
            }

            return;
        }

        if (!leftClicked)
        {
            return;
        }

        editorSession.Select(hoverTarget?.Id);

        if (hoverTarget is null)
        {
            return;
        }

        selectElementTabPending = true;

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && hoverTarget is TextProfileElement)
        {
            // Double-click text: jump straight to editing its content.
            focusTextContentPending = true;
            return;
        }

        editorSession.BeginDrag(hoverTarget, logicalMouse);
    }

    private static void DrawResizeHandles(ImDrawListPtr drawList, Vector2[] screenCorners)
    {
        var fill = ImGui.GetColorU32(SelectionColor);
        var border = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f));
        var half = HandleScreenSize / 2f;

        foreach (var corner in screenCorners)
        {
            drawList.AddRectFilled(corner - new Vector2(half, half), corner + new Vector2(half, half), fill);
            drawList.AddRect(corner - new Vector2(half, half), corner + new Vector2(half, half), border);
        }
    }

    /// <summary>
    /// <paramref name="screenCorners"/> is in the same perimeter order as
    /// <see cref="RotationGeometry.GetRotatedCorners"/> (TopLeft, TopRight, BottomRight,
    /// BottomLeft) — <paramref name="handle"/> identifies which LOCAL corner of the element was
    /// hit, not which corner it currently appears at on screen (rotation can move that visually).
    /// </summary>
    private static bool TryGetHoveredHandle(Vector2 mouseScreen, Vector2[] screenCorners, out ResizeHandle handle)
    {
        var half = HandleScreenSize;

        ReadOnlySpan<ResizeHandle> handleForCornerIndex = [ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomRight, ResizeHandle.BottomLeft];

        for (var i = 0; i < screenCorners.Length; i++)
        {
            var center = screenCorners[i];
            var min = center - new Vector2(half, half);
            var max = center + new Vector2(half, half);

            if (mouseScreen.X >= min.X && mouseScreen.X <= max.X && mouseScreen.Y >= min.Y && mouseScreen.Y <= max.Y)
            {
                handle = handleForCornerIndex[i];
                return true;
            }
        }

        handle = ResizeHandle.None;
        return false;
    }

    /// <summary>
    /// Picks the diagonal resize cursor (NW-SE vs NE-SW) that matches the CURRENT visual angle
    /// between the hovered corner and its opposite — not <paramref name="handle"/>'s local
    /// identity, which points at a different visual diagonal once the element is rotated. At 90
    /// degrees, for example, the local TopLeft/BottomRight pair (the NW-SE diagonal at rotation
    /// 0) visually sits on the NE-SW diagonal instead.
    /// </summary>
    private static ImGuiMouseCursor GetResizeCursor(Vector2[] screenCorners, ResizeHandle handle)
    {
        var index = handle switch
        {
            ResizeHandle.TopLeft => 0,
            ResizeHandle.TopRight => 1,
            ResizeHandle.BottomRight => 2,
            _ => 3, // BottomLeft
        };
        var oppositeIndex = (index + 2) % 4;

        // Same-signed X/Y offset to the opposite corner means it's visually down-right (or
        // up-left) of the hovered one, i.e. the NW-SE diagonal; opposite signs mean NE-SW.
        var diff = screenCorners[oppositeIndex] - screenCorners[index];
        return diff.X * diff.Y >= 0f ? ImGuiMouseCursor.ResizeNwse : ImGuiMouseCursor.ResizeNesw;
    }
}
