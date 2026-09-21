using System;
using System.Collections.Generic;
using System.Linq;
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
/// interaction state (selection, drag/resize in progress, undo/redo history) that is
/// never persisted.
/// </summary>
internal sealed class EditorSession
{
    internal const float MinZoom = 0.25f;
    internal const float MaxZoom = 4f;

    internal const float MinElementWidth = 20f;
    internal const float MinElementHeight = 20f;

    // Caps how far back undo can go; history is runtime-only, so this just bounds memory.
    private const int MaxHistoryEntries = 100;

    private readonly ProfileService profileService;

    // Active drag/resize interaction. Runtime only; never persisted.
    private Guid interactingElementId;
    private Vector2 dragStartMousePosition;
    private Vector2 dragOriginalPosition;
    private Vector2 dragOriginalSize;
    private ProfileElement? interactionBeforeSnapshot;

    // A slider/color edit in progress: the element's state before the first change this
    // "session" of edits, committed to history as a single entry once the widget deactivates.
    private Guid pendingEditElementId;
    private ProfileElement? pendingEditBefore;

    private readonly List<HistoryEntry> undoStack = new();
    private readonly List<HistoryEntry> redoStack = new();

    // A structural snapshot of the last successfully loaded-or-saved element set. IsDirty is
    // computed by comparing the live profile's elements against this baseline every time it's
    // queried, rather than by tracking history-position bookkeeping that has to stay in sync —
    // so it can't drift out of sync with reality regardless of what path got us here.
    // baselineSourceProfile is a reference marker only (never dereferenced) used to notice a
    // fresh load: every load produces a brand-new ProfileDocument instance, even when reloading
    // the same profile, so a reference change reliably means "establish a new clean baseline".
    private ProfileDocument? baselineSourceProfile;
    private List<ProfileElement>? savedBaseline;

    internal EditorSession(ProfileService profileService)
    {
        this.profileService = profileService;
    }

    internal string NewElementText { get; set; } = string.Empty;

    internal string? ErrorMessage { get; private set; }

    /// <summary>
    /// True when the live profile's elements differ from the last successfully loaded/saved
    /// baseline, or while an edit is actively in progress (a drag/resize, or a slider/color
    /// edit that hasn't been committed to history yet).
    /// </summary>
    internal bool IsDirty
    {
        get
        {
            EnsureBaselineCurrent();
            return !ProfileStatesEqual(savedBaseline, profileService.CurrentProfile?.Elements)
                || ActiveInteraction != ElementInteractionKind.None
                || pendingEditBefore is not null;
        }
    }

    internal float Zoom { get; set; } = 1f;

    internal Guid? SelectedElementId { get; private set; }

    internal ElementInteractionKind ActiveInteraction { get; private set; } = ElementInteractionKind.None;

    internal ResizeHandle ActiveResizeHandle { get; private set; } = ResizeHandle.None;

    internal bool CanUndo => undoStack.Count > 0;

    internal bool CanRedo => redoStack.Count > 0;

    internal void AddTextElement()
    {
        ErrorMessage = null;

        try
        {
            var newId = profileService.AddTextElement(NewElementText);
            NewElementText = string.Empty;

            var snapshot = profileService.CloneElement(newId);
            RecordHistory(
                undo: () => profileService.RemoveElement(snapshot.Id),
                redo: () => profileService.InsertElement(snapshot.Clone()));
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
            var snapshot = profileService.CloneElement(elementId);
            profileService.RemoveElement(elementId);

            if (SelectedElementId == elementId)
            {
                SelectedElementId = null;
            }

            RecordHistory(
                undo: () =>
                {
                    profileService.InsertElement(snapshot.Clone());
                    Select(snapshot.Id);
                },
                redo: () =>
                {
                    profileService.RemoveElement(snapshot.Id);
                    if (SelectedElementId == snapshot.Id)
                    {
                        SelectedElementId = null;
                    }
                });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    internal void DuplicateElement(Guid elementId)
    {
        ErrorMessage = null;

        try
        {
            var newId = profileService.DuplicateElement(elementId);
            var snapshot = profileService.CloneElement(newId);
            Select(newId);

            RecordHistory(
                undo: () =>
                {
                    profileService.RemoveElement(snapshot.Id);
                    if (SelectedElementId == snapshot.Id)
                    {
                        SelectedElementId = null;
                    }
                },
                redo: () =>
                {
                    profileService.InsertElement(snapshot.Clone());
                    Select(snapshot.Id);
                });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    internal void BringForward(Guid elementId) => ApplyZOrder(elementId, profileService.BringForward);

    internal void SendBackward(Guid elementId) => ApplyZOrder(elementId, profileService.SendBackward);

    internal void BringToFront(Guid elementId) => ApplyZOrder(elementId, profileService.BringToFront);

    internal void SendToBack(Guid elementId) => ApplyZOrder(elementId, profileService.SendToBack);

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

        ProfileElement before;
        try
        {
            before = profileService.CloneElement(elementId);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        if (before.Locked)
        {
            return;
        }

        var newPosition = ClampPosition(before.Position + delta, before.Size);
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
            ErrorMessage = ex.Message;
            return;
        }

        var oldPosition = before.Position;
        RecordHistory(
            undo: () => profileService.UpdateElement(elementId, element => element.Position = oldPosition),
            redo: () => profileService.UpdateElement(elementId, element => element.Position = newPosition));
    }

    /// <summary>
    /// Applies a discrete, single-step edit (checkbox, combo, button) to an existing element
    /// and immediately records one history entry. Marks the session dirty on success.
    /// </summary>
    internal void ApplyImmediateEdit(Guid elementId, Action<ProfileElement> update)
    {
        ErrorMessage = null;

        ProfileElement before;
        try
        {
            before = profileService.CloneElement(elementId);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        try
        {
            profileService.UpdateElement(elementId, update);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var after = profileService.CloneElement(elementId);
        RecordHistory(
            undo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(before)),
            redo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(after)));
    }

    /// <summary>
    /// Applies a live, in-progress edit (e.g. a slider being dragged) without recording
    /// history yet. Call <see cref="CommitPendingEdit"/> once the edit completes (e.g. on
    /// ImGui's "deactivated after edit") to record a single history entry for the whole
    /// sequence of calls since the first one for this element.
    /// </summary>
    internal void BeginOrContinueEdit(Guid elementId, Action<ProfileElement> apply)
    {
        ErrorMessage = null;

        if (pendingEditBefore is null || pendingEditElementId != elementId)
        {
            try
            {
                pendingEditBefore = profileService.CloneElement(elementId);
                pendingEditElementId = elementId;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return;
            }
        }

        try
        {
            profileService.UpdateElement(elementId, apply);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Finalizes a pending edit started by <see cref="BeginOrContinueEdit"/>.</summary>
    internal void CommitPendingEdit()
    {
        if (pendingEditBefore is null)
        {
            return;
        }

        var elementId = pendingEditElementId;
        var before = pendingEditBefore;
        pendingEditBefore = null;

        ProfileElement after;
        try
        {
            after = profileService.CloneElement(elementId);
        }
        catch
        {
            // Element no longer exists (e.g. removed mid-edit); nothing to record.
            return;
        }

        RecordHistory(
            undo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(before)),
            redo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(after)));
    }

    internal async void SaveProfile()
    {
        ErrorMessage = null;

        try
        {
            await profileService.SaveCurrentProfileAsync().ConfigureAwait(false);
            CaptureBaseline(profileService.CurrentProfile);
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
        interactionBeforeSnapshot = element.Clone();
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
        interactionBeforeSnapshot = element.Clone();
    }

    /// <summary>
    /// Advances the active drag or resize interaction using the mouse's current logical
    /// canvas position. Safe to call every frame; a no-op when nothing is active. Does not
    /// record history — see <see cref="EndInteraction"/>.
    /// </summary>
    internal void UpdateInteraction(Vector2 mouseCanvasPosition)
    {
        if (ActiveInteraction == ElementInteractionKind.None)
        {
            return;
        }

        var delta = mouseCanvasPosition - dragStartMousePosition;
        var elementId = interactingElementId;

        try
        {
            if (ActiveInteraction == ElementInteractionKind.Dragging)
            {
                var newPosition = ClampPosition(dragOriginalPosition + delta, dragOriginalSize);
                profileService.UpdateElement(elementId, element => element.Position = newPosition);
            }
            else
            {
                var (newPosition, newSize) = ComputeResize(dragOriginalPosition, dragOriginalSize, ActiveResizeHandle, delta);
                profileService.UpdateElement(elementId, element =>
                {
                    element.Position = newPosition;
                    element.Size = newSize;
                });
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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

        interactionBeforeSnapshot = null;
        ActiveInteraction = ElementInteractionKind.None;
        ActiveResizeHandle = ResizeHandle.None;
    }

    /// <summary>Reverts the most recent recorded action, if any.</summary>
    internal void Undo()
    {
        if (undoStack.Count == 0)
        {
            return;
        }

        var entry = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);

        ErrorMessage = null;
        try
        {
            entry.Undo();
            redoStack.Add(entry);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Re-applies the most recently undone action, if any.</summary>
    internal void Redo()
    {
        if (redoStack.Count == 0)
        {
            return;
        }

        var entry = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);

        ErrorMessage = null;
        try
        {
            entry.Redo();
            undoStack.Add(entry);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void ApplyZOrder(Guid elementId, Action<Guid> operation)
    {
        ErrorMessage = null;

        Dictionary<Guid, int> before;
        try
        {
            before = profileService.SnapshotZOrder();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        try
        {
            operation(elementId);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var after = profileService.SnapshotZOrder();
        RecordHistory(
            undo: () => profileService.RestoreZOrder(before),
            redo: () => profileService.RestoreZOrder(after));
    }

    /// <summary>Records a new undoable action. Always clears the redo stack.</summary>
    private void RecordHistory(Action undo, Action redo)
    {
        undoStack.Add(new HistoryEntry(undo, redo));
        redoStack.Clear();

        if (undoStack.Count > MaxHistoryEntries)
        {
            undoStack.RemoveAt(0);
        }
    }

    /// <summary>Re-baselines against the live profile if it's not the one we last baselined.</summary>
    private void EnsureBaselineCurrent()
    {
        var profile = profileService.CurrentProfile;
        if (!ReferenceEquals(profile, baselineSourceProfile))
        {
            CaptureBaseline(profile);
        }
    }

    private void CaptureBaseline(ProfileDocument? profile)
    {
        baselineSourceProfile = profile;
        savedBaseline = profile?.Elements.Select(e => e.Clone()).ToList();
    }

    private static bool ProfileStatesEqual(List<ProfileElement>? baseline, List<ProfileElement>? current)
    {
        if (baseline is null || current is null)
        {
            return baseline is null && current is null;
        }

        if (baseline.Count != current.Count)
        {
            return false;
        }

        var baselineById = new Dictionary<Guid, ProfileElement>(baseline.Count);
        foreach (var element in baseline)
        {
            baselineById[element.Id] = element;
        }

        foreach (var element in current)
        {
            if (!baselineById.TryGetValue(element.Id, out var baselineElement) || !ElementsEqual(baselineElement, element))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Value-equality over the persistent, editable fields of an element — the same set that's
    /// meaningful to save. Deliberately excludes anything that isn't actually element data.
    /// </summary>
    private static bool ElementsEqual(ProfileElement a, ProfileElement b)
    {
        if (a.GetType() != b.GetType())
        {
            return false;
        }

        if (a.Id != b.Id || a.Visible != b.Visible || a.Locked != b.Locked
            || a.Position != b.Position || a.Size != b.Size || a.ZIndex != b.ZIndex)
        {
            return false;
        }

        if (a is TextProfileElement textA && b is TextProfileElement textB)
        {
            return textA.Text == textB.Text
                && textA.FontSize.Equals(textB.FontSize)
                && textA.Color == textB.Color
                && textA.Alignment == textB.Alignment
                && textA.Wrap == textB.Wrap;
        }

        return true;
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

    private sealed record HistoryEntry(Action Undo, Action Redo);
}
