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

    // Small visual margin around the fitted canvas so it doesn't touch the panel's edges.
    private const float FitPaddingPixels = 16f;

    internal const float MinElementWidth = 20f;
    internal const float MinElementHeight = 20f;

    // Caps how far back undo can go; history is runtime-only, so this just bounds memory.
    private const int MaxHistoryEntries = 100;

    private readonly ProfileService profileService;
    private readonly AssetStorageService assetStorage;
    private readonly ImageTextureCache imageTextureCache;

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

    // A background slider/combo edit in progress; same "commit on deactivate" pattern as
    // pendingEditBefore above, just for ProfileDocument-level background fields.
    private ProfileService.BackgroundState? pendingBackgroundBefore;

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
    private ProfileService.BackgroundState? savedBackgroundBaseline;
    private Vector2? savedCanvasSizeBaseline;

    internal EditorSession(ProfileService profileService, AssetStorageService assetStorage, ImageTextureCache imageTextureCache)
    {
        this.profileService = profileService;
        this.assetStorage = assetStorage;
        this.imageTextureCache = imageTextureCache;
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
            var currentProfile = profileService.CurrentProfile;
            return !ProfileStatesEqual(savedBaseline, currentProfile?.Elements)
                || !BackgroundStatesEqual(savedBackgroundBaseline, currentProfile)
                || !CanvasSizeStatesEqual(savedCanvasSizeBaseline, currentProfile)
                || ActiveInteraction != ElementInteractionKind.None
                || pendingEditBefore is not null
                || pendingBackgroundBefore is not null;
        }
    }

    internal float Zoom { get; set; } = 1f;

    /// <summary>
    /// Runtime-only viewport preference — never persisted with the profile. While true, the
    /// editor canvas recalculates <see cref="Zoom"/> to fit its available panel size; setting
    /// <see cref="Zoom"/> manually (the Zoom slider) is expected to also set this false.
    /// </summary>
    internal bool AutoFit { get; set; } = true;

    /// <summary>
    /// Runtime-only editor chrome preference — never persisted with the profile (see
    /// <c>ProfileDocument</c>, which has no field for it). While true (the default), the canvas
    /// draws element bounds, the selection outline, and resize handles; while false, the canvas
    /// draws nothing but the finished profile itself, matching <c>ProfileViewWindow</c>.
    /// Selection state itself is unaffected either way — an element can still be selected via the
    /// Elements panel, and the Inspector still edits it — only the on-canvas chrome is hidden.
    /// </summary>
    internal bool ShowGuides { get; set; } = true;

    internal Guid? SelectedElementId { get; private set; }

    internal ElementInteractionKind ActiveInteraction { get; private set; } = ElementInteractionKind.None;

    internal ResizeHandle ActiveResizeHandle { get; private set; } = ResizeHandle.None;

    internal bool CanUndo => undoStack.Count > 0;

    internal bool CanRedo => redoStack.Count > 0;

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
    /// small padding margin, clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>]. Does not
    /// touch <see cref="AutoFit"/> — callers decide whether this was an auto or manual fit.
    /// </summary>
    internal void ApplyFitZoom(Vector2 availablePanelSize)
    {
        Zoom = ComputeFitZoom(CurrentCanvasSize, availablePanelSize);
    }

    private static float ComputeFitZoom(Vector2 canvasSize, Vector2 availablePanelSize)
    {
        var usableWidth = Math.Max(1f, availablePanelSize.X - (FitPaddingPixels * 2f));
        var usableHeight = Math.Max(1f, availablePanelSize.Y - (FitPaddingPixels * 2f));

        var fitZoom = Math.Min(usableWidth / canvasSize.X, usableHeight / canvasSize.Y);
        return Math.Clamp(fitZoom, MinZoom, MaxZoom);
    }

    /// <summary>
    /// Resizes the current profile's canvas, optionally scaling every element's Position/Size
    /// proportionally to the new dimensions (rotation values are never touched either way — see
    /// <c>ProfileService.ResizeCanvas</c>), and records one undoable history entry. Surfaces
    /// failures (no profile loaded, etc.) via <see cref="ErrorMessage"/>.
    /// </summary>
    internal void ApplyCanvasResize(float newWidth, float newHeight, bool scaleContentsProportionally)
    {
        ErrorMessage = null;

        ProfileService.CanvasLayoutState before;
        try
        {
            before = profileService.CaptureCanvasLayoutState();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        try
        {
            profileService.ResizeCanvas(newWidth, newHeight, scaleContentsProportionally);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var after = profileService.CaptureCanvasLayoutState();
        RecordHistory(
            undo: () => profileService.RestoreCanvasLayoutState(before),
            redo: () => profileService.RestoreCanvasLayoutState(after));
    }

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

    /// <summary>
    /// Imports an image file into managed asset storage and adds it as a new element, sized to
    /// the source image's own aspect ratio (see <see cref="ComputeDefaultImportSize"/>),
    /// selecting it and recording one undoable history entry. Errors (unreadable file,
    /// unsupported format, profile at capacity, etc.) are surfaced via <see cref="ErrorMessage"/>.
    /// </summary>
    internal void AddImageElement(string sourceFilePath)
    {
        ErrorMessage = null;

        Guid assetId;
        try
        {
            assetId = assetStorage.ImportImage(sourceFilePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var size = ComputeDefaultImportSize(sourceFilePath);
        var position = ClampPosition(CurrentCanvasSize, new Vector2(ProfileElement.DefaultPositionX, ProfileElement.DefaultPositionY), size);

        var newId = AddElement(new ImageProfileElement
        {
            AssetId = assetId,
            Position = position,
            Size = size,
        });

        if (newId != Guid.Empty)
        {
            Select(newId);
        }
    }

    /// <summary>
    /// Adds a fully-formed element (e.g. a Basic-mode role-tagged element with its own default
    /// position and styling) and records one undoable history entry. Mirrors
    /// <see cref="AddTextElement"/>/<see cref="AddImageElement(string)"/> for callers that need
    /// more control than those Advanced-editor-oriented defaults provide.
    /// </summary>
    internal Guid AddElement(ProfileElement element)
    {
        ErrorMessage = null;

        try
        {
            var newId = profileService.AddElement(element);
            var snapshot = profileService.CloneElement(newId);
            RecordHistory(
                undo: () => profileService.RemoveElement(snapshot.Id),
                redo: () => profileService.InsertElement(snapshot.Clone()));
            return newId;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Imports a new image file and points an existing image element at it. The previous asset
    /// remains on disk (undo may still need it) and its cache entry is left alone.
    /// </summary>
    internal void ReplaceImage(Guid elementId, string sourceFilePath)
    {
        ErrorMessage = null;

        Guid assetId;
        try
        {
            assetId = assetStorage.ImportImage(sourceFilePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        ApplyImmediateEdit(elementId, element =>
        {
            if (element is ImageProfileElement image)
            {
                image.AssetId = assetId;
            }
        });
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

    /// <summary>Imports an image and sets it as the profile's background.</summary>
    internal void SetBackground(string sourceFilePath)
    {
        ErrorMessage = null;

        Guid assetId;
        try
        {
            assetId = assetStorage.ImportImage(sourceFilePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        ApplyBackgroundEdit(doc => doc.BackgroundAssetId = assetId);
    }

    /// <summary>Clears the profile's background. The asset itself is left on disk (see undo).</summary>
    internal void RemoveBackground() => ApplyBackgroundEdit(doc => doc.BackgroundAssetId = null);

    /// <summary>
    /// Applies a discrete background edit (combo, button) and immediately records one history
    /// entry, mirroring <see cref="ApplyImmediateEdit"/> for elements.
    /// </summary>
    internal void ApplyBackgroundEdit(Action<ProfileDocument> update)
    {
        ErrorMessage = null;

        ProfileService.BackgroundState before;
        try
        {
            before = profileService.CaptureBackgroundState();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        try
        {
            profileService.UpdateBackground(update);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var after = profileService.CaptureBackgroundState();
        RecordHistory(
            undo: () => profileService.RestoreBackgroundState(before),
            redo: () => profileService.RestoreBackgroundState(after));
    }

    /// <summary>
    /// Applies a live, in-progress background edit (e.g. the opacity slider being dragged)
    /// without recording history yet. Mirrors <see cref="BeginOrContinueEdit"/> for elements;
    /// call <see cref="CommitPendingBackgroundEdit"/> once the edit completes.
    /// </summary>
    internal void BeginOrContinueBackgroundEdit(Action<ProfileDocument> apply)
    {
        ErrorMessage = null;

        if (pendingBackgroundBefore is null)
        {
            try
            {
                pendingBackgroundBefore = profileService.CaptureBackgroundState();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return;
            }
        }

        try
        {
            profileService.UpdateBackground(apply);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Finalizes a pending background edit started by <see cref="BeginOrContinueBackgroundEdit"/>.</summary>
    internal void CommitPendingBackgroundEdit()
    {
        if (pendingBackgroundBefore is not { } before)
        {
            return;
        }

        pendingBackgroundBefore = null;

        ProfileService.BackgroundState after;
        try
        {
            after = profileService.CaptureBackgroundState();
        }
        catch
        {
            // Profile no longer editable (e.g. character switch mid-edit); nothing to record.
            return;
        }

        RecordHistory(
            undo: () => profileService.RestoreBackgroundState(before),
            redo: () => profileService.RestoreBackgroundState(after));
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

        var elementId = interactingElementId;
        var rotationDegrees = interactionBeforeSnapshot is null ? 0f : RotationGeometry.GetRotationDegrees(interactionBeforeSnapshot);
        var canvasSize = CurrentCanvasSize;

        try
        {
            if (ActiveInteraction == ElementInteractionKind.Dragging)
            {
                // Translation is invariant under rotation: moving a rotated element just moves
                // its (still unrotated) Position/Size by the same screen-space delta.
                var delta = mouseCanvasPosition - dragStartMousePosition;
                var newPosition = ClampPositionForRotation(canvasSize, dragOriginalPosition + delta, dragOriginalSize, rotationDegrees);
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
                    (newPosition, newSize) = ComputeResize(canvasSize, dragOriginalPosition, dragOriginalSize, ActiveResizeHandle, delta, lockedAspectRatio);
                }
                else
                {
                    // Resizing a rotated element keeps the VISIBLE corner opposite the one being
                    // dragged fixed in canvas space — see ComputeRotatedResize for why the naive
                    // "hold the opposite LOCAL corner's coordinates fixed" approach (used above
                    // for rotation 0) doesn't generalize to a rotated element.
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
            // A genuine profile switch (not just a post-save re-baseline of the same profile):
            // cached GPU textures for the old profile's images are no longer relevant.
            imageTextureCache.Clear();
            CaptureBaseline(profile);
        }
    }

    private void CaptureBaseline(ProfileDocument? profile)
    {
        baselineSourceProfile = profile;
        savedBaseline = profile?.Elements.Select(e => e.Clone()).ToList();
        savedBackgroundBaseline = profile is null
            ? null
            : new ProfileService.BackgroundState(profile.BackgroundAssetId, profile.BackgroundFitMode, profile.BackgroundOpacity);
        savedCanvasSizeBaseline = profile is null
            ? null
            : new Vector2(profile.CanvasWidth, profile.CanvasHeight);
    }

    private static bool BackgroundStatesEqual(ProfileService.BackgroundState? baseline, ProfileDocument? current)
    {
        if (baseline is null || current is null)
        {
            return baseline is null && current is null;
        }

        var b = baseline.Value;
        return b.AssetId == current.BackgroundAssetId
            && b.FitMode == current.BackgroundFitMode
            && b.Opacity.Equals(current.BackgroundOpacity);
    }

    private static bool CanvasSizeStatesEqual(Vector2? baseline, ProfileDocument? current)
    {
        if (baseline is null || current is null)
        {
            return baseline is null && current is null;
        }

        return baseline.Value == new Vector2(current.CanvasWidth, current.CanvasHeight);
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
            || a.Position != b.Position || a.Size != b.Size || a.ZIndex != b.ZIndex || a.Role != b.Role)
        {
            return false;
        }

        if (a is TextProfileElement textA && b is TextProfileElement textB)
        {
            return textA.Text == textB.Text
                && textA.FontSize.Equals(textB.FontSize)
                && textA.Color == textB.Color
                && textA.Alignment == textB.Alignment
                && textA.Wrap == textB.Wrap
                && textA.FontFamily == textB.FontFamily
                && textA.Bold == textB.Bold
                && textA.Italic == textB.Italic
                && textA.Underline == textB.Underline
                && textA.Strikethrough == textB.Strikethrough;
        }

        if (a is ImageProfileElement imageA && b is ImageProfileElement imageB)
        {
            return imageA.AssetId == imageB.AssetId
                && imageA.Opacity.Equals(imageB.Opacity)
                && imageA.PreserveAspectRatio == imageB.PreserveAspectRatio
                && imageA.RotationDegrees.Equals(imageB.RotationDegrees);
        }

        return true;
    }

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

    private sealed record HistoryEntry(Action Undo, Action Redo);
}
