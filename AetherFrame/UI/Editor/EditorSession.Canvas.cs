using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>Canvas-relative alignment commands for the selected element.</summary>
internal enum CanvasAlignment
{
    Left,
    HorizontalCenter,
    Right,
    Top,
    VerticalCenter,
    Bottom,
}

/// <summary>
/// Canvas interaction half of <see cref="EditorSession"/>: runtime viewport/preview state, drag
/// and resize (with snapping), keyboard nudging, alignment commands, and the geometry behind them.
/// Nothing here is persisted except the element positions/sizes the interactions produce.
/// </summary>
internal sealed partial class EditorSession
{
    // Small visual margin around the fitted canvas so it doesn't touch the panel's edges.
    private const float FitPaddingPixels = 16f;

    // Active drag/resize interaction. Runtime only; never persisted.
    private Guid interactingElementId;
    private Vector2 dragStartMousePosition;
    private Vector2 dragOriginalPosition;
    private Vector2 dragOriginalSize;
    private ProfileElement? interactionBeforeSnapshot;

    private readonly SnapEngine snapEngine = new();

    internal float Zoom { get; set; } = 1f;

    /// <summary>
    /// Runtime-only viewport preference — never persisted with the profile. While true, the
    /// editor canvas recalculates <see cref="Zoom"/> to fit its available panel size (and is
    /// centered, <see cref="PanOffset"/> zero); any manual zoom or pan sets it false until the
    /// user asks for Fit again.
    /// </summary>
    internal bool AutoFit { get; set; } = true;

    /// <summary>Runtime-only screen-space pan of the canvas from its centered position.</summary>
    internal Vector2 PanOffset { get; set; }

    /// <summary>
    /// Runtime-only editor chrome preference — never persisted with the profile. While true (the
    /// default), the canvas draws element bounds, the selection outline, and resize handles;
    /// while false, the canvas draws the finished profile plus only the selection outline.
    /// Selection state itself is unaffected either way.
    /// </summary>
    internal bool ShowGuides { get; set; } = true;

    /// <summary>Runtime-only: snapping while moving/resizing (default on; Alt bypasses it temporarily).</summary>
    internal bool SnapEnabled { get; set; } = true;

    /// <summary>
    /// Runtime-only Clean Preview state: the editor shows only the finished profile, exactly as
    /// Profile View renders it. Never persisted in the <see cref="ProfileDocument"/>.
    /// </summary>
    internal bool PreviewActive { get; set; }

    internal ElementInteractionKind ActiveInteraction { get; private set; } = ElementInteractionKind.None;

    internal ResizeHandle ActiveResizeHandle { get; private set; } = ResizeHandle.None;

    /// <summary>Editor-only alignment guides for the current snapped drag/resize (empty otherwise).</summary>
    internal IReadOnlyList<SnapGuide> SnapGuides => snapEngine.Guides;

    /// <summary>
    /// The current profile's logical canvas size, or the legacy 1920x1080 size if no profile is
    /// loaded (matching <see cref="ProfileDocument.NormalizeLegacyCanvasSize"/>'s fallback, so
    /// bounds-clamping helpers here never divide by, or clamp against, zero).
    /// </summary>
    private Vector2 CurrentCanvasSize => profileService.CurrentProfile is { } profile
        ? new Vector2(profile.CanvasWidth, profile.CanvasHeight)
        : new Vector2(ProfileDocument.LegacyCanvasWidth, ProfileDocument.LegacyCanvasHeight);

    /// <summary>
    /// Sets <see cref="Zoom"/> so the full logical canvas fits inside a panel of
    /// <paramref name="availablePanelSize"/> screen pixels, preserving aspect ratio, with a
    /// small padding margin, clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>], and
    /// re-centers it. Does not touch <see cref="AutoFit"/> — callers decide whether this was an
    /// auto or manual fit.
    /// </summary>
    internal void ApplyFitZoom(Vector2 availablePanelSize)
    {
        // Frames the Plate's visual bounds (canvas plus any intentional Component overflow), so Fit
        // shows oversized decorations too; with no overflow this is exactly the canvas, centered.
        var canvasSize = CurrentCanvasSize;
        var bounds = profileService.CurrentProfile is { } profile ? ProfileVisualBounds.Compute(profile) : new CanvasBounds(Vector2.Zero, canvasSize);
        var boundsSize = bounds.Size.X > 0f && bounds.Size.Y > 0f ? bounds.Size : canvasSize;
        var usableWidth = Math.Max(1f, availablePanelSize.X - (FitPaddingPixels * 2f));
        var usableHeight = Math.Max(1f, availablePanelSize.Y - (FitPaddingPixels * 2f));

        var fitZoom = Math.Min(usableWidth / boundsSize.X, usableHeight / boundsSize.Y);
        Zoom = Math.Clamp(fitZoom, MinZoom, MaxZoom);

        // The layout centers the canvas; this shift centers the visual bounds instead.
        PanOffset = ((canvasSize / 2f) - bounds.Center) * Zoom;
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> around a screen point (e.g. the mouse), keeping the
    /// canvas point under it fixed. <paramref name="canvasOrigin"/>/<paramref name="panelCenter"/>
    /// describe the current layout so the matching pan can be solved for.
    /// </summary>
    internal void ZoomAround(float factor, Vector2 screenPoint, Vector2 canvasOrigin, Vector2 panelCenter)
    {
        var oldZoom = Zoom;
        var newZoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);
        if (newZoom.Equals(oldZoom))
        {
            return;
        }

        var logicalPoint = (screenPoint - canvasOrigin) / oldZoom;
        var canvasScreenSize = CurrentCanvasSize * newZoom;

        // The layout places the canvas at: panelCenter - canvasScreenSize / 2 + PanOffset.
        var newOrigin = screenPoint - (logicalPoint * newZoom);
        PanOffset = newOrigin - (panelCenter - (canvasScreenSize / 2f));
        Zoom = newZoom;
        AutoFit = false;
    }

    /// <summary>Sets an explicit zoom level (e.g. a zoom preset), keeping the canvas centered on the same point.</summary>
    internal void SetZoom(float zoom)
    {
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Zoom > 0f)
        {
            PanOffset *= newZoom / Zoom;
        }

        Zoom = newZoom;
        AutoFit = false;
    }

    /// <summary>Pans the canvas by a screen-space delta (middle-mouse drag).</summary>
    internal void PanBy(Vector2 screenDelta)
    {
        PanOffset += screenDelta;
        AutoFit = false;
    }

    /// <summary>Keeps at least a margin of the canvas inside the panel so it can't be lost off-screen.</summary>
    internal void ClampPan(Vector2 panelSize, float keepVisiblePixels)
    {
        var canvasScreenSize = CurrentCanvasSize * Zoom;
        var limit = ((panelSize + canvasScreenSize) / 2f) - new Vector2(keepVisiblePixels);
        limit = Vector2.Max(limit, Vector2.Zero);
        PanOffset = Vector2.Clamp(PanOffset, -limit, limit);
    }

    /// <summary>Starts dragging a selected, unlocked element. No-op for locked elements.</summary>
    internal void BeginDrag(ProfileElement element, Vector2 mouseCanvasPosition)
    {
        if (element.Locked)
        {
            return;
        }

        CommitPendingEdits();
        BeginInteraction(ElementInteractionKind.Dragging, ResizeHandle.None, element, mouseCanvasPosition);
    }

    /// <summary>Starts resizing a selected, unlocked element from the given corner handle.</summary>
    internal void BeginResize(ProfileElement element, ResizeHandle handle, Vector2 mouseCanvasPosition)
    {
        if (element.Locked || handle == ResizeHandle.None)
        {
            return;
        }

        CommitPendingEdits();
        BeginInteraction(ElementInteractionKind.Resizing, handle, element, mouseCanvasPosition);
    }

    /// <summary>
    /// Advances the active drag or resize interaction using the mouse's current logical canvas
    /// position. <paramref name="snap"/> is false when snapping is off or bypassed (Alt);
    /// <paramref name="snapThreshold"/> is in logical units (a fixed screen distance / zoom, so
    /// snapping feels the same at every zoom level). Safe to call every frame; a no-op when
    /// nothing is active. Does not record history — see <see cref="EndInteraction"/>.
    /// </summary>
    internal void UpdateInteraction(Vector2 mouseCanvasPosition, bool snap, float snapThreshold)
    {
        if (ActiveInteraction == ElementInteractionKind.None)
        {
            return;
        }

        var elementId = interactingElementId;
        var rotationDegrees = interactionBeforeSnapshot is null ? 0f : RotationGeometry.GetRotationDegrees(interactionBeforeSnapshot);
        var canvasSize = CurrentCanvasSize;

        if (!snap)
        {
            snapEngine.ClearGuides();
        }

        try
        {
            if (ActiveInteraction == ElementInteractionKind.Dragging)
            {
                // Translation is invariant under rotation: moving a rotated element just moves
                // its (still unrotated) Position/Size by the same screen-space delta.
                var delta = mouseCanvasPosition - dragStartMousePosition;
                var proposed = dragOriginalPosition + delta;

                if (snap)
                {
                    var (min, max) = RotationGeometry.GetVisualBounds(proposed, dragOriginalSize, rotationDegrees);
                    proposed += snapEngine.SnapMove(min, max, snapThreshold);
                }

                var newPosition = ClampPositionForRotation(canvasSize, proposed, dragOriginalSize, rotationDegrees);

                if (snap)
                {
                    var (min, max) = RotationGeometry.GetVisualBounds(newPosition, dragOriginalSize, rotationDegrees);
                    snapEngine.UpdateGuides(min, max, EdgeMask.All);
                }

                profileService.UpdateElement(elementId, element => element.Position = newPosition);
            }
            else
            {
                var lockedAspectRatio = interactionBeforeSnapshot is ImageProfileElement { PreserveAspectRatio: true } && dragOriginalSize.Y > 0f
                    ? dragOriginalSize.X / dragOriginalSize.Y
                    : (float?)null;

                Vector2 newPosition, newSize;
                if (rotationDegrees == 0f)
                {
                    var delta = mouseCanvasPosition - dragStartMousePosition;

                    if (snap)
                    {
                        // Snap the dragged corner itself: its edges land on nearby targets, and
                        // ComputeResize then treats the snapped delta like any other.
                        var corner = GetHandleCorner(dragOriginalPosition, dragOriginalSize, ActiveResizeHandle) + delta;
                        delta += new Vector2(snapEngine.SnapX(corner.X, snapThreshold), snapEngine.SnapY(corner.Y, snapThreshold));
                    }

                    (newPosition, newSize) = ComputeResize(canvasSize, dragOriginalPosition, dragOriginalSize, ActiveResizeHandle, delta, lockedAspectRatio);

                    if (snap)
                    {
                        snapEngine.UpdateGuides(newPosition, newPosition + newSize, GetMovingEdges(ActiveResizeHandle));
                    }
                }
                else
                {
                    // Resizing a rotated element keeps the VISIBLE corner opposite the one being
                    // dragged fixed in canvas space — see ComputeRotatedResize for why the naive
                    // "hold the opposite LOCAL corner's coordinates fixed" approach (used above
                    // for rotation 0) doesn't generalize to a rotated element. Its edges aren't
                    // axis-aligned, so it isn't snapped.
                    snapEngine.ClearGuides();
                    (newPosition, newSize) = ComputeRotatedResize(
                        canvasSize, dragOriginalPosition, dragOriginalSize, ActiveResizeHandle, mouseCanvasPosition, lockedAspectRatio, rotationDegrees);
                }

                profileService.UpdateElement(elementId, element =>
                {
                    element.Position = newPosition;
                    element.Size = newSize;
                });
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
        }
    }

    /// <summary>
    /// Ends the active drag or resize interaction (e.g. on mouse release), recording exactly
    /// one history entry for the whole interaction if the element actually moved or resized.
    /// </summary>
    internal void EndInteraction()
    {
        if (ActiveInteraction != ElementInteractionKind.None && interactionBeforeSnapshot is { } before)
        {
            var elementId = interactingElementId;

            try
            {
                var after = profileService.CloneElement(elementId);
                if (after.Position != before.Position || after.Size != before.Size)
                {
                    RecordHistory(
                        undo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(before)),
                        redo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(after)));
                }
            }
            catch
            {
                // Element no longer exists; nothing to record.
            }
        }

        ResetInteraction();
    }

    /// <summary>
    /// Abandons an in-progress drag/resize, restoring the element to where it started (nothing is
    /// recorded). Used when something else (undo, revert, a document switch) takes over.
    /// </summary>
    internal void CancelInteraction()
    {
        if (ActiveInteraction != ElementInteractionKind.None && interactionBeforeSnapshot is { } before)
        {
            try
            {
                profileService.UpdateElement(interactingElementId, element => element.CopyFrom(before));
            }
            catch
            {
                // Element gone or profile no longer editable; nothing to restore.
            }
        }

        ResetInteraction();
    }

    /// <summary>
    /// Moves the selected unlocked element by a logical canvas offset (e.g. an arrow-key
    /// nudge), clamped to canvas bounds. Records one history entry; a no-op that hits the
    /// canvas edge (no actual movement) records nothing.
    /// </summary>
    internal void NudgeSelected(Vector2 delta)
    {
        if (SelectedElementId is not { } elementId)
        {
            return;
        }

        ErrorMessage = null;
        CommitPendingEdits();

        ProfileElement before;
        try
        {
            before = profileService.CloneElement(elementId);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        if (before.Locked)
        {
            return;
        }

        var newPosition = ClampPositionForRotation(CurrentCanvasSize, before.Position + delta, before.Size, RotationGeometry.GetRotationDegrees(before));
        if (newPosition == before.Position)
        {
            return;
        }

        try
        {
            profileService.UpdateElement(elementId, element => element.Position = newPosition);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        var oldPosition = before.Position;
        RecordHistory(
            undo: () => profileService.UpdateElement(elementId, element => element.Position = oldPosition),
            redo: () => profileService.UpdateElement(elementId, element => element.Position = newPosition));
    }

    /// <summary>
    /// Aligns the selected (unlocked) element's visual bounds — rotation-aware, so a rotated image
    /// aligns by what's actually visible — to an edge or center of the canvas. One history entry.
    /// </summary>
    internal void AlignSelected(CanvasAlignment alignment)
    {
        var profile = profileService.CurrentProfile;
        if (profile is null || SelectedElementId is not { } elementId || profile.Elements.Find(e => e.Id == elementId) is not { } element || element.Locked)
        {
            return;
        }

        var (min, max) = RotationGeometry.GetVisualBounds(element);
        var center = (min + max) / 2f;
        var canvas = new Vector2(profile.CanvasWidth, profile.CanvasHeight);

        var offset = alignment switch
        {
            CanvasAlignment.Left => new Vector2(-min.X, 0f),
            CanvasAlignment.HorizontalCenter => new Vector2((canvas.X / 2f) - center.X, 0f),
            CanvasAlignment.Right => new Vector2(canvas.X - max.X, 0f),
            CanvasAlignment.Top => new Vector2(0f, -min.Y),
            CanvasAlignment.VerticalCenter => new Vector2(0f, (canvas.Y / 2f) - center.Y),
            _ => new Vector2(0f, canvas.Y - max.Y),
        };

        if (offset == Vector2.Zero)
        {
            return;
        }

        var newPosition = element.Position + offset;
        ApplyImmediateEdit(elementId, e => e.Position = newPosition);
    }

    /// <summary>
    /// Resizes an image element to its source image's native aspect ratio, keeping its width and
    /// its center (and scaling down only if the result would no longer fit the canvas). One
    /// history entry; reports an error if the image's size isn't known.
    /// </summary>
    internal void ResetImageToNativeAspect(Guid elementId)
    {
        var profile = profileService.CurrentProfile;
        if (profile?.Elements.Find(e => e.Id == elementId) is not ImageProfileElement image || image.Locked)
        {
            return;
        }

        if (imageTextureCache.GetNativeSize(image.AssetId) is not { Width: > 0, Height: > 0 } native)
        {
            ErrorMessage = "The image's native size isn't available.";
            return;
        }

        var aspect = native.Width / (float)native.Height;
        var canvas = new Vector2(profile.CanvasWidth, profile.CanvasHeight);
        var size = new Vector2(Math.Max(MinElementWidth, image.Size.X), Math.Max(MinElementWidth, image.Size.X) / aspect);

        var fit = Math.Min(1f, Math.Min(canvas.X / size.X, canvas.Y / size.Y));
        size = Vector2.Max(size * fit, new Vector2(MinElementWidth, MinElementHeight));

        var center = RotationGeometry.GetCenter(image.Position, image.Size);
        var position = ClampPositionForRotation(canvas, center - (size / 2f), size, image.RotationDegrees);

        ApplyImmediateEdit(elementId, e =>
        {
            e.Position = position;
            e.Size = size;
        });
    }

    /// <summary>
    /// Clamps a proposed Position for <paramref name="element"/> (e.g. typed into the Inspector)
    /// so its visual bounds stay on the canvas, exactly as dragging does.
    /// </summary>
    internal Vector2 ClampElementPosition(ProfileElement element, Vector2 position) =>
        ClampPositionForRotation(CurrentCanvasSize, position, element.Size, RotationGeometry.GetRotationDegrees(element));

    private void BeginInteraction(ElementInteractionKind kind, ResizeHandle handle, ProfileElement element, Vector2 mouseCanvasPosition)
    {
        ActiveInteraction = kind;
        ActiveResizeHandle = handle;
        interactingElementId = element.Id;
        dragStartMousePosition = mouseCanvasPosition;
        dragOriginalPosition = element.Position;
        dragOriginalSize = element.Size;
        interactionBeforeSnapshot = element.Clone();

        if (profileService.CurrentProfile is { } profile)
        {
            snapEngine.Begin(profile, element.Id);
        }
    }

    private void ResetInteraction()
    {
        interactionBeforeSnapshot = null;
        ActiveInteraction = ElementInteractionKind.None;
        ActiveResizeHandle = ResizeHandle.None;
        snapEngine.Clear();
    }

    private static Vector2 GetHandleCorner(Vector2 position, Vector2 size, ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => position,
        ResizeHandle.TopRight => position + new Vector2(size.X, 0f),
        ResizeHandle.BottomLeft => position + new Vector2(0f, size.Y),
        _ => position + size,
    };

    private static EdgeMask GetMovingEdges(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => EdgeMask.Left | EdgeMask.Top,
        ResizeHandle.TopRight => EdgeMask.Right | EdgeMask.Top,
        ResizeHandle.BottomLeft => EdgeMask.Left | EdgeMask.Bottom,
        _ => EdgeMask.Right | EdgeMask.Bottom,
    };

    /// <summary>
    /// Picks a newly imported image's initial canvas size: its native aspect ratio, scaled down
    /// (never up) so its longer side fits <see cref="ImageProfileElement.DefaultSize"/> — a
    /// square image keeps exactly the old default footprint, while a tall or wide one starts
    /// correctly proportioned instead of stretched into a square. Falls back to the old fixed
    /// square default if the file's dimensions can't be read (unrecognized/corrupt header).
    /// </summary>
    private static Vector2 ComputeDefaultImportSize(string sourceFilePath)
    {
        if (ImageDimensionReader.TryReadDimensions(sourceFilePath) is not { } native || native.Width <= 0 || native.Height <= 0)
        {
            return new Vector2(ImageProfileElement.DefaultSize, ImageProfileElement.DefaultSize);
        }

        var scale = Math.Min(1f, Math.Min(ImageProfileElement.DefaultSize / native.Width, ImageProfileElement.DefaultSize / native.Height));
        var size = new Vector2(native.Width * scale, native.Height * scale);

        // Guards an extreme aspect ratio (e.g. a very wide banner) from shrinking to a sliver
        // too small to see or grab, at the cost of slightly distorting its proportions in that
        // edge case only.
        return new Vector2(Math.Max(MinElementWidth, size.X), Math.Max(MinElementHeight, size.Y));
    }

    private static Vector2 ClampPosition(Vector2 canvasSize, Vector2 position, Vector2 size)
    {
        var maxX = Math.Max(0f, canvasSize.X - size.X);
        var maxY = Math.Max(0f, canvasSize.Y - size.Y);
        return new Vector2(Math.Clamp(position.X, 0f, maxX), Math.Clamp(position.Y, 0f, maxY));
    }

    /// <summary>
    /// Rotation-aware version of <see cref="ClampPosition"/>: keeps the element's full rotated
    /// visual bounds (not just its unrotated Position/Size box) inside the canvas, by clamping
    /// the rotated bounding box's center rather than the unrotated corner.
    /// </summary>
    private static Vector2 ClampPositionForRotation(Vector2 canvasSize, Vector2 position, Vector2 size, float rotationDegrees)
    {
        if (rotationDegrees == 0f)
        {
            return ClampPosition(canvasSize, position, size);
        }

        var aabbSize = RotationGeometry.GetRotatedAabbSize(size, rotationDegrees);
        var center = RotationGeometry.GetCenter(position, size);

        var maxCenterX = Math.Max(aabbSize.X / 2f, canvasSize.X - (aabbSize.X / 2f));
        var maxCenterY = Math.Max(aabbSize.Y / 2f, canvasSize.Y - (aabbSize.Y / 2f));

        var clampedCenter = new Vector2(
            Math.Clamp(center.X, aabbSize.X / 2f, maxCenterX),
            Math.Clamp(center.Y, aabbSize.Y / 2f, maxCenterY));

        return clampedCenter - (size / 2f);
    }

    /// <summary>
    /// Axis-aligned resize (rotation 0 only — see <see cref="ComputeRotatedResize"/> for a
    /// rotated element). Unchanged from before rotation existed: the opposite edge/corner is
    /// simply held at its original canvas coordinate while the dragged edge/corner moves by
    /// <paramref name="delta"/>.
    /// </summary>
    private static (Vector2 Position, Vector2 Size) ComputeResize(
        Vector2 canvasSize, Vector2 originalPosition, Vector2 originalSize, ResizeHandle handle, Vector2 delta, float? lockedAspectRatio)
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
        left = Math.Clamp(left, 0f, canvasSize.X);
        top = Math.Clamp(top, 0f, canvasSize.Y);
        right = Math.Clamp(right, 0f, canvasSize.X);
        bottom = Math.Clamp(bottom, 0f, canvasSize.Y);

        if (lockedAspectRatio is { } aspectRatio && aspectRatio > 0f)
        {
            // Fit the largest box of the locked aspect ratio that stays within the raw
            // (unconstrained) drag bounds just computed, anchored at the corner opposite the
            // dragged handle.
            var rawWidth = Math.Max(0f, right - left);
            var rawHeight = Math.Max(0f, bottom - top);

            float width, height;
            if (rawHeight <= 0f || rawWidth / aspectRatio <= rawHeight)
            {
                width = rawWidth;
                height = rawWidth / aspectRatio;
            }
            else
            {
                height = rawHeight;
                width = rawHeight * aspectRatio;
            }

            switch (handle)
            {
                case ResizeHandle.TopLeft:
                    left = right - width;
                    top = bottom - height;
                    break;
                case ResizeHandle.TopRight:
                    right = left + width;
                    top = bottom - height;
                    break;
                case ResizeHandle.BottomLeft:
                    left = right - width;
                    bottom = top + height;
                    break;
                case ResizeHandle.BottomRight:
                    right = left + width;
                    bottom = top + height;
                    break;
            }
        }

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

    /// <summary>
    /// Resize for a rotated element, keeping the VISIBLE corner diagonally opposite the one
    /// being dragged exactly fixed in canvas space for the whole drag.
    ///
    /// This is deliberately not a rotation-aware variant of <see cref="ComputeResize"/>'s
    /// "hold the opposite corner's local coordinates fixed, then re-derive the rotation center"
    /// approach. That approach is correct at rotation 0 (where local space IS canvas space,
    /// so an unmoved local coordinate trivially stays visually fixed too) but NOT once the
    /// element is rotated: <see cref="RotationGeometry.GetCenter"/> recomputes the rotation
    /// pivot from Position/Size on every use, and resizing by moving only two of four local
    /// edges shifts that center — so rotating the new box around its new (shifted) center no
    /// longer puts the "fixed" corner back where it visually was. The drift is small near
    /// rotation 0 but becomes obviously wrong near 90/270 degrees, where a corner can end up a
    /// full side-length away from where the user grabbed it.
    ///
    /// Instead, this solves directly for the new center that keeps the anchor's canvas-space
    /// position exactly at its drag-start value, for whatever size the drag implies:
    /// 1. anchorWorld: the anchor corner's fixed canvas position, from the ORIGINAL box.
    /// 2. xAxis/yAxis: canvas-space unit vectors for the element's local +X/+Y directions —
    ///    fixed for the whole drag, since rotation doesn't change while resizing.
    /// 3. The current mouse position, projected onto xAxis/yAxis relative to anchorWorld, gives
    ///    the new local width/height (this is what "drag follows the local axes" means for a
    ///    rotated element — equivalent to the plain delta.X/delta.Y projection ComputeResize
    ///    uses at rotation 0).
    /// 4. The new center is whatever makes the anchor corner (at its fixed local offset from
    ///    that center) land back on anchorWorld when rotated — solved directly below, not
    ///    iterated or approximated.
    /// </summary>
    private static (Vector2 Position, Vector2 Size) ComputeRotatedResize(
        Vector2 canvasSize, Vector2 originalPosition, Vector2 originalSize, ResizeHandle handle, Vector2 mouseCanvasPosition, float? lockedAspectRatio, float rotationDegrees)
    {
        var originalCenter = RotationGeometry.GetCenter(originalPosition, originalSize);

        var xAxis = RotationGeometry.RotatePoint(Vector2.UnitX, Vector2.Zero, rotationDegrees);
        var yAxis = RotationGeometry.RotatePoint(Vector2.UnitY, Vector2.Zero, rotationDegrees);

        // Local +X/+Y direction, per axis, from the anchor corner towards the dragged (handle)
        // corner — e.g. dragging TopLeft (anchor BottomRight) grows the box towards local -X,-Y.
        var cornerSign = handle switch
        {
            ResizeHandle.TopLeft => new Vector2(-1f, -1f),
            ResizeHandle.TopRight => new Vector2(1f, -1f),
            ResizeHandle.BottomLeft => new Vector2(-1f, 1f),
            _ => new Vector2(1f, 1f), // BottomRight
        };

        var anchorLocal = new Vector2(
            originalPosition.X + ((cornerSign.X > 0f ? 0f : 1f) * originalSize.X),
            originalPosition.Y + ((cornerSign.Y > 0f ? 0f : 1f) * originalSize.Y));
        var anchorWorld = RotationGeometry.RotatePoint(anchorLocal, originalCenter, rotationDegrees);

        var mouseOffset = mouseCanvasPosition - anchorWorld;
        var along = Vector2.Dot(mouseOffset, xAxis) * cornerSign.X;
        var up = Vector2.Dot(mouseOffset, yAxis) * cornerSign.Y;

        var rawWidth = Math.Max(0f, along);
        var rawHeight = Math.Max(0f, up);

        float width, height;
        if (lockedAspectRatio is { } aspectRatio && aspectRatio > 0f)
        {
            // Same "fit the largest box of the locked aspect ratio within the raw drag bounds"
            // rule as ComputeResize, just expressed in the rotated local width/height instead
            // of canvas-space edges.
            if (rawHeight <= 0f || rawWidth / aspectRatio <= rawHeight)
            {
                width = rawWidth;
                height = rawWidth / aspectRatio;
            }
            else
            {
                height = rawHeight;
                width = rawHeight * aspectRatio;
            }
        }
        else
        {
            width = rawWidth;
            height = rawHeight;
        }

        // Minimum size, same as ComputeResize — and since neither dimension is ever negative
        // above, this is also what rules out a negative/zero size here.
        width = Math.Max(MinElementWidth, width);
        height = Math.Max(MinElementHeight, height);

        // The new center that keeps anchorWorld fixed: the anchor sits at local offset
        // cornerSign * (width/2, height/2) from the center (e.g. BottomRight is always at
        // +halfWidth,+halfHeight from center), so the center is that same offset, in canvas
        // space via xAxis/yAxis, back from the anchor.
        var newCenter = anchorWorld
            + (cornerSign.X * (width / 2f) * xAxis)
            + (cornerSign.Y * (height / 2f) * yAxis);

        var newSize = new Vector2(width, height);
        var newPosition = newCenter - (newSize / 2f);

        // Same rotated-bounds canvas clamp ComputeResize's caller already applies for a rotated
        // element; reused as-is so canvas clamping behavior doesn't regress.
        newPosition = ClampPositionForRotation(canvasSize, newPosition, newSize, rotationDegrees);

        return (newPosition, newSize);
    }
}
