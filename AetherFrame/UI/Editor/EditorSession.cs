using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

internal enum ElementInteractionKind
{
    None,
    Dragging,
    Resizing,
}

internal enum ResizeHandle
{
    None,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Holds transient editor UI state and translates ImGui interactions into
/// <see cref="ProfileService"/> calls, surfacing failures as an inline error message
/// instead of letting exceptions escape ImGui Draw. Also owns runtime-only canvas
/// interaction state (selection, drag/resize in progress) that is never persisted.
/// </summary>
internal sealed class EditorSession
{
    internal const float MinZoom = 0.25f;
    internal const float MaxZoom = 4f;

    internal const float MinElementWidth = 20f;
    internal const float MinElementHeight = 20f;

    private readonly ProfileService profileService;

    // Active drag/resize interaction. Runtime only; never persisted.
    private Guid interactingElementId;
    private Vector2 dragStartMousePosition;
    private Vector2 dragOriginalPosition;
    private Vector2 dragOriginalSize;

    internal EditorSession(ProfileService profileService)
    {
        this.profileService = profileService;
    }

    internal string NewElementText { get; set; } = string.Empty;

    internal string? ErrorMessage { get; private set; }

    internal bool IsDirty { get; set; }

    internal float Zoom { get; set; } = 1f;

    internal Guid? SelectedElementId { get; private set; }

    internal ElementInteractionKind ActiveInteraction { get; private set; } = ElementInteractionKind.None;

    internal ResizeHandle ActiveResizeHandle { get; private set; } = ResizeHandle.None;

    internal void AddTextElement()
    {
        ErrorMessage = null;

        try
        {
            profileService.AddTextElement(NewElementText);
            NewElementText = string.Empty;
            IsDirty = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Applies an in-place edit to a property of an existing element (e.g. a text element's
    /// font size, color, alignment, wrap, visibility, or lock state) via
    /// <see cref="ProfileService.UpdateElement"/>, which validates and mutates under its own
    /// lock. Marks the session dirty on success.
    /// </summary>
    internal void UpdateElement(Guid elementId, Action<ProfileElement> update)
    {
        ErrorMessage = null;

        try
        {
            profileService.UpdateElement(elementId, update);
            IsDirty = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    internal void RemoveElement(Guid elementId)
    {
        ErrorMessage = null;

        try
        {
            profileService.RemoveElement(elementId);
            IsDirty = true;

            if (SelectedElementId == elementId)
            {
                SelectedElementId = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    internal async void SaveProfile()
    {
        ErrorMessage = null;

        try
        {
            await profileService.SaveCurrentProfileAsync().ConfigureAwait(false);
            IsDirty = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            DalamudServices.Log.Error(ex, "AetherFrame failed to save the current profile.");
        }
    }

    /// <summary>Selects an element on the canvas, or clears the selection when null.</summary>
    internal void Select(Guid? elementId) => SelectedElementId = elementId;

    /// <summary>Starts dragging a selected, unlocked element. No-op for locked elements.</summary>
    internal void BeginDrag(ProfileElement element, Vector2 mouseCanvasPosition)
    {
        if (element.Locked)
        {
            return;
        }

        ActiveInteraction = ElementInteractionKind.Dragging;
        ActiveResizeHandle = ResizeHandle.None;
        interactingElementId = element.Id;
        dragStartMousePosition = mouseCanvasPosition;
        dragOriginalPosition = element.Position;
        dragOriginalSize = element.Size;
    }

    /// <summary>Starts resizing a selected, unlocked element from the given corner handle.</summary>
    internal void BeginResize(ProfileElement element, ResizeHandle handle, Vector2 mouseCanvasPosition)
    {
        if (element.Locked || handle == ResizeHandle.None)
        {
            return;
        }

        ActiveInteraction = ElementInteractionKind.Resizing;
        ActiveResizeHandle = handle;
        interactingElementId = element.Id;
        dragStartMousePosition = mouseCanvasPosition;
        dragOriginalPosition = element.Position;
        dragOriginalSize = element.Size;
    }

    /// <summary>
    /// Advances the active drag or resize interaction using the mouse's current logical
    /// canvas position. Safe to call every frame; a no-op when nothing is active.
    /// </summary>
    internal void UpdateInteraction(Vector2 mouseCanvasPosition)
    {
        if (ActiveInteraction == ElementInteractionKind.None)
        {
            return;
        }

        var delta = mouseCanvasPosition - dragStartMousePosition;
        var elementId = interactingElementId;

        if (ActiveInteraction == ElementInteractionKind.Dragging)
        {
            var newPosition = ClampPosition(dragOriginalPosition + delta, dragOriginalSize);
            UpdateElement(elementId, element => element.Position = newPosition);
        }
        else
        {
            var (newPosition, newSize) = ComputeResize(dragOriginalPosition, dragOriginalSize, ActiveResizeHandle, delta);
            UpdateElement(elementId, element =>
            {
                element.Position = newPosition;
                element.Size = newSize;
            });
        }
    }

    /// <summary>Ends the active drag or resize interaction (e.g. on mouse release).</summary>
    internal void EndInteraction()
    {
        ActiveInteraction = ElementInteractionKind.None;
        ActiveResizeHandle = ResizeHandle.None;
    }

    private static Vector2 ClampPosition(Vector2 position, Vector2 size)
    {
        var maxX = Math.Max(0f, ProfileDocument.CanvasWidth - size.X);
        var maxY = Math.Max(0f, ProfileDocument.CanvasHeight - size.Y);
        return new Vector2(Math.Clamp(position.X, 0f, maxX), Math.Clamp(position.Y, 0f, maxY));
    }

    private static (Vector2 Position, Vector2 Size) ComputeResize(
        Vector2 originalPosition, Vector2 originalSize, ResizeHandle handle, Vector2 delta)
    {
        var left = originalPosition.X;
        var top = originalPosition.Y;
        var right = originalPosition.X + originalSize.X;
        var bottom = originalPosition.Y + originalSize.Y;

        switch (handle)
        {
            case ResizeHandle.TopLeft:
                left += delta.X;
                top += delta.Y;
                break;
            case ResizeHandle.TopRight:
                right += delta.X;
                top += delta.Y;
                break;
            case ResizeHandle.BottomLeft:
                left += delta.X;
                bottom += delta.Y;
                break;
            case ResizeHandle.BottomRight:
                right += delta.X;
                bottom += delta.Y;
                break;
        }

        // Keep every edge inside the canvas before enforcing minimum size.
        left = Math.Clamp(left, 0f, ProfileDocument.CanvasWidth);
        top = Math.Clamp(top, 0f, ProfileDocument.CanvasHeight);
        right = Math.Clamp(right, 0f, ProfileDocument.CanvasWidth);
        bottom = Math.Clamp(bottom, 0f, ProfileDocument.CanvasHeight);

        // Enforce a minimum size by holding the edge opposite the dragged handle in place,
        // which also rules out negative width/height.
        if (right - left < MinElementWidth)
        {
            if (handle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft)
            {
                left = right - MinElementWidth;
            }
            else
            {
                right = left + MinElementWidth;
            }
        }

        if (bottom - top < MinElementHeight)
        {
            if (handle is ResizeHandle.TopLeft or ResizeHandle.TopRight)
            {
                top = bottom - MinElementHeight;
            }
            else
            {
                bottom = top + MinElementHeight;
            }
        }

        return (new Vector2(left, top), new Vector2(right - left, bottom - top));
    }
}
