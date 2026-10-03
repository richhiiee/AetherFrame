using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;

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
/// interaction state (selection, drag/resize in progress, snapping, undo/redo history,
/// viewport and preview state) that is never persisted.
///
/// Shared by the Advanced and Basic editors, so both always agree on undo/redo history and
/// dirty state. Split across partial files: this one (history, dirty state, save/revert,
/// element operations), <c>EditorSession.Canvas.cs</c> (drag/resize/snap/nudge/alignment and
/// their geometry), and <c>EditorSession.Document.cs</c> (background and canvas size).
///
/// <para><b>Undo granularity.</b> Every persistent change is recorded exactly once:
/// discrete edits (checkbox, combo, button, rename, reorder) immediately via
/// <see cref="ApplyImmediateEdit"/>; continuous edits (sliders, drags, color pickers, typing)
/// through the <see cref="BeginOrContinueEdit"/>/<see cref="CommitPendingEdit"/> pair, which
/// coalesces a whole slider drag or typing burst into one entry.</para>
/// </summary>
internal sealed partial class EditorSession
{
    internal const float MinZoom = 0.1f;
    internal const float MaxZoom = 4f;

    internal const float MinElementWidth = 20f;
    internal const float MinElementHeight = 20f;

    // Caps how far back undo can go; history is runtime-only, so this just bounds memory.
    private const int MaxHistoryEntries = 100;

    internal const string DefaultNewText = "New text";

    private readonly ProfileService profileService;
    private readonly AssetStorageService assetStorage;
    private readonly IEditorImageInfo imageTextureCache;
    private readonly IAetherFrameLog log;

    // The UI frame number (ImGui's frame count in the plugin): dirty state is compared at most once per frame.
    private readonly Func<int> frameCounter;

    // A slider/color/text edit in progress: the element's state before the first change of this
    // "session" of edits, committed to history as a single entry once the widget deactivates.
    private Guid pendingEditElementId;
    private ProfileElement? pendingEditBefore;

    private readonly List<HistoryEntry> undoStack = new();
    private readonly List<HistoryEntry> redoStack = new();

    // The last loaded-or-saved state of the profile. IsDirty compares the live profile against
    // this, rather than tracking history-position bookkeeping that has to stay in sync — so it
    // can't drift out of sync with reality regardless of what path got us here.
    // baselineSourceProfile is a reference marker only, used to notice a fresh load: every load
    // produces a brand-new ProfileDocument instance, so a reference change reliably means
    // "new document: new baseline, fresh history".
    private ProfileDocument? baselineSourceProfile;
    private ProfileService.DocumentState? savedBaseline;

    // Published by a save's continuation (which may run off the render thread) and adopted by the
    // render thread on its next SyncWithCurrentProfile — so the baseline itself is only ever
    // touched on the render thread.
    private volatile CompletedSave? completedSave;

    private int dirtyMemoFrame = -1;
    private bool dirtyMemo;

    // Every asset this document has referred to since it was opened (see AssetsInUse).
    private readonly HashSet<Guid> assetsInUse = new();

    internal EditorSession(
        ProfileService profileService, AssetStorageService assetStorage, IEditorImageInfo imageTextureCache, IAetherFrameLog log, Func<int> frameCounter)
    {
        this.profileService = profileService;
        this.assetStorage = assetStorage;
        this.imageTextureCache = imageTextureCache;
        this.log = log;
        this.frameCounter = frameCounter;
    }

    internal string? ErrorMessage { get; private set; }

    // What ErrorMessage says when a failure's own text isn't meant for the player (see UserFacingError).
    internal const string EditFailedMessage = "That change couldn't be made.";
    internal const string ImageImportFailedMessage = "The image couldn't be added.";
    internal const string SaveFailedMessage = "The Plate couldn't be saved.";
    internal const string BaselineFailedMessage = "The Plate's saved state couldn't be read, so Revert to Saved isn't available.";

    internal Guid? SelectedElementId { get; private set; }

    /// <summary>
    /// The Component selected instead of an element (issue #115): from a click on it on either
    /// editor's canvas, or from a list. At most one of this and <see cref="SelectedElementId"/> is
    /// set. Selection is editor state only: it never changes the Plate, its history or its dirty state.
    /// </summary>
    internal Guid? SelectedComponentId { get; private set; }

    internal bool CanUndo => undoStack.Count > 0;

    internal bool CanRedo => redoStack.Count > 0;

    /// <summary>
    /// Every asset the open document has referred to since it was opened: what it referenced when
    /// it was opened, every image imported through the editor since, and every state a recorded
    /// edit left it in — so it covers everything undo or redo can bring back, not only what is on
    /// the canvas now. This is the <c>additionallyInUse</c> feed for
    /// <see cref="Services.Assets.AssetGarbageCollector.Plan"/>: a cleanup that ignores it could
    /// trash an image that is one Ctrl+Z away. Reset when another Plate is opened; only ever grown
    /// by an edit, never scanned per frame.
    /// </summary>
    internal IReadOnlySet<Guid> AssetsInUse => assetsInUse;

    /// <summary>
    /// True when the live profile differs from the last successfully loaded/saved state, or while
    /// an edit is actively in progress (a drag/resize, or a slider/color/text edit that hasn't
    /// been committed to history yet). The structural comparison runs at most once per frame.
    /// </summary>
    internal bool IsDirty
    {
        get
        {
            SyncWithCurrentProfile();

            if (ActiveInteraction != ElementInteractionKind.None || pendingEditBefore is not null || pendingBackgroundBefore is not null || pendingDocumentBefore is not null)
            {
                return true;
            }

            var frame = frameCounter();
            if (frame != dirtyMemoFrame)
            {
                dirtyMemoFrame = frame;
                dirtyMemo = !DocumentMatchesBaseline(profileService.CurrentProfile);
            }

            return dirtyMemo;
        }
    }

    /// <summary>
    /// Re-baselines against the live profile if it's not the one we last baselined (a load or
    /// reload), and adopts a just-completed save's baseline. Call at the start of each editor
    /// frame; also called implicitly by <see cref="IsDirty"/>.
    /// </summary>
    internal void SyncWithCurrentProfile()
    {
        var profile = profileService.CurrentProfile;

        if (!ReferenceEquals(profile, baselineSourceProfile))
        {
            // A genuine document switch (not a post-save re-baseline of the same profile): cached
            // GPU textures for the old profile's images are no longer relevant, and neither is any
            // history, selection, or in-progress edit — those all refer to the old document.
            imageTextureCache.Clear();
            ResetTransientState();
            completedSave = null;
            CaptureBaseline(profile);
            return;
        }

        if (completedSave is { } save)
        {
            completedSave = null;
            if (ReferenceEquals(save.Profile, profile))
            {
                savedBaseline = save.State;
                InvalidateDirtyMemo();
            }
        }
    }

    // ---------------------------------------------------------------- element operations

    /// <summary>
    /// Adds a new text element (AetherFrame's default font, an automatic "Text N" layer name,
    /// placeholder content the user is expected to replace), selects it, and records one history
    /// entry. Returns its id, or null on failure.
    /// </summary>
    internal Guid? AddTextElement(string? initialText = null)
    {
        ErrorMessage = null;
        CommitPendingEdits();

        try
        {
            var newId = profileService.AddTextElement(initialText ?? DefaultNewText);
            RecordAdd(newId);
            Select(newId);
            return newId;
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return null;
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

        if (ImportImage(sourceFilePath) is not { } assetId)
        {
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
        CommitPendingEdits();

        try
        {
            var newId = profileService.AddElement(element);
            RecordAdd(newId);
            return newId;
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Imports a new image file and points an existing image element at it. Every transform
    /// (position, size, rotation, flips, display mode, opacity) is kept — resetting any of them is
    /// always a separate, explicit action. The previous asset remains on disk (undo may still need
    /// it) and its cache entry is left alone.
    /// </summary>
    internal void ReplaceImage(Guid elementId, string sourceFilePath)
    {
        ErrorMessage = null;

        if (ImportImage(sourceFilePath) is not { } assetId)
        {
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
        CommitPendingEdits();

        try
        {
            var snapshot = profileService.CloneElement(elementId);

            // Where it sits in the list: undo puts it back there, among the elements it shares
            // its ZIndex with (ties paint in list order), not on top of them at the end.
            var index = profileService.CurrentProfile?.Elements.FindIndex(e => e.Id == elementId);
            profileService.RemoveElement(elementId);

            if (SelectedElementId == elementId)
            {
                SelectedElementId = null;
            }

            RecordHistory(
                undo: () =>
                {
                    profileService.InsertElement(snapshot.Clone(), index);
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
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
        }
    }

    /// <summary>
    /// Duplicates an element — same styling and visual settings, a "Copy" layer name, offset so
    /// it's visibly distinct — selects the copy, and records one history entry.
    /// </summary>
    internal void DuplicateElement(Guid elementId)
    {
        ErrorMessage = null;
        CommitPendingEdits();

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
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
        }
    }

    /// <summary>Renames an element's layer (one history entry); empty restores the automatic name.</summary>
    internal void RenameElement(Guid elementId, string? name)
    {
        var sanitized = ProfileElementNames.Sanitize(name);
        var current = profileService.CurrentProfile?.Elements.Find(e => e.Id == elementId);
        if (current is null || current.Name == sanitized)
        {
            return;
        }

        ApplyImmediateEdit(elementId, element => element.Name = sanitized);
    }

    internal void SetElementVisible(Guid elementId, bool visible) =>
        ApplyImmediateEdit(elementId, element => element.Visible = visible);

    internal void SetElementLocked(Guid elementId, bool locked) =>
        ApplyImmediateEdit(elementId, element => element.Locked = locked);

    internal void BringForward(Guid elementId) => ApplyZOrder(() => profileService.BringForward(elementId));

    internal void SendBackward(Guid elementId) => ApplyZOrder(() => profileService.SendBackward(elementId));

    internal void BringToFront(Guid elementId) => ApplyZOrder(() => profileService.BringToFront(elementId));

    internal void SendToBack(Guid elementId) => ApplyZOrder(() => profileService.SendToBack(elementId));

    /// <summary>Layers panel drag reorder: places an element directly above/below another (one history entry).</summary>
    internal void MoveLayer(Guid elementId, Guid targetElementId, bool placeAbove)
    {
        if (elementId == targetElementId)
        {
            return;
        }

        ApplyZOrder(() => profileService.MoveNextTo(elementId, targetElementId, placeAbove));
    }

    /// <summary>
    /// Applies a discrete, single-step edit (checkbox, combo, button) to an existing element
    /// and immediately records one history entry — or nothing, if the edit changed nothing.
    /// </summary>
    internal void ApplyImmediateEdit(Guid elementId, Action<ProfileElement> update)
    {
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

        try
        {
            profileService.UpdateElement(elementId, update);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        var after = profileService.CloneElement(elementId);
        if (after.ContentEquals(before))
        {
            return;
        }

        RecordHistory(
            undo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(before)),
            redo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(after)));
    }

    /// <summary>
    /// Applies a live, in-progress edit (e.g. a slider being dragged) without recording
    /// history yet. Call <see cref="CommitPendingEdit"/> once the edit completes (e.g. on
    /// ImGui's "deactivated after edit") to record a single history entry for the whole
    /// sequence of calls since the first one for this element. Starting an edit on a different
    /// element first commits the previous element's pending edit, so it's never lost.
    /// </summary>
    internal void BeginOrContinueEdit(Guid elementId, Action<ProfileElement> apply)
    {
        ErrorMessage = null;

        if (pendingEditBefore is not null && pendingEditElementId != elementId)
        {
            CommitPendingEdit();
        }

        if (pendingEditBefore is null)
        {
            CommitPendingBackgroundEdit();
            CommitPendingDocumentEdit();

            try
            {
                pendingEditBefore = profileService.CloneElement(elementId);
                pendingEditElementId = elementId;
            }
            catch (Exception ex)
            {
                ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
                return;
            }
        }

        try
        {
            profileService.UpdateElement(elementId, apply);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
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

        if (after.ContentEquals(before))
        {
            return;
        }

        RecordHistory(
            undo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(before)),
            redo: () => profileService.UpdateElement(elementId, element => element.CopyFrom(after)));
    }

    /// <summary>Finalizes every pending (element or background) edit. Called before any action
    /// that must not interleave with one (undo, save, structural changes).</summary>
    internal void CommitPendingEdits()
    {
        CommitPendingEdit();
        CommitPendingBackgroundEdit();
        CommitPendingDocumentEdit();
    }

    /// <summary>
    /// Safety net for widgets whose "deactivated after edit" signal is unreliable (e.g. a color
    /// picker edited through its popup): once no widget is active any more, whatever was pending
    /// is committed, so an edit can never stay uncommitted (and the profile stuck "dirty").
    /// </summary>
    internal void CommitPendingEditsIfIdle(bool anyWidgetActive)
    {
        if (!anyWidgetActive)
        {
            CommitPendingEdits();
        }
    }

    // ---------------------------------------------------------------- save / revert

    /// <summary>Fire-and-forget save (see <see cref="SaveProfileAsync"/>).</summary>
    internal void SaveProfile() => _ = SaveProfileAsync();

    /// <summary>
    /// Saves the current profile. On success the saved state becomes the new clean baseline (the
    /// state captured here, on the render thread, is exactly what was written: the profile can't
    /// be edited while the save is in flight). A drag or resize still in progress (Ctrl+S with the
    /// mouse button held) is ended first, so it is recorded as one history entry and what is saved
    /// is exactly what is on screen. Returns false (with <see cref="ErrorMessage"/> set) on failure.
    /// </summary>
    internal async Task<bool> SaveProfileAsync()
    {
        ErrorMessage = null;
        CommitPendingEdits();
        EndInteraction();

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ErrorMessage = "No Plate is open.";
            return false;
        }

        var savedState = ProfileService.DocumentState.Capture(profile);

        try
        {
            await profileService.SaveCurrentProfileAsync().ConfigureAwait(false);
            completedSave = new CompletedSave(profile, savedState);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, SaveFailedMessage);
            log.Error(ex, "AetherFrame failed to save the current profile.");
            return false;
        }
    }

    /// <summary>True if there is a saved baseline to revert to.</summary>
    internal bool CanRevert => savedBaseline is not null && profileService.CurrentProfile is not null;

    /// <summary>
    /// Restores the live profile to its last loaded/saved state. As an explicit toolbar action
    /// (<paramref name="undoable"/> true) the revert itself is one undoable history entry; as the
    /// "Discard" answer to an unsaved-changes prompt, history is cleared instead, since it only
    /// described the work being thrown away. Returns whether the saved state was restored: false,
    /// with the document untouched, when there is no baseline or the Plate can't be changed right
    /// now (a save in flight; <see cref="ErrorMessage"/> says so).
    /// </summary>
    internal bool RevertToSaved(bool undoable)
    {
        ErrorMessage = null;
        CommitPendingEdits();
        CancelInteraction();

        if (savedBaseline is not { } baseline)
        {
            ErrorMessage = profileService.CurrentProfile is null ? "No Plate is open." : BaselineFailedMessage;
            return false;
        }

        ProfileService.DocumentState before;
        try
        {
            before = profileService.CaptureDocumentState();
            profileService.RestoreDocumentState(baseline);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return false;
        }

        DropSelectionIfMissing();
        InvalidateDirtyMemo();

        if (!undoable)
        {
            ClearHistory();
            return true;
        }

        RecordHistory(
            undo: () =>
            {
                profileService.RestoreDocumentState(before);
                DropSelectionIfMissing();
            },
            redo: () =>
            {
                profileService.RestoreDocumentState(baseline);
                DropSelectionIfMissing();
            });
        return true;
    }

    /// <summary>Discards unsaved work (see <see cref="RevertToSaved"/>) without an undo entry. Returns whether it was discarded.</summary>
    internal bool DiscardChanges() => RevertToSaved(undoable: false);

    // ---------------------------------------------------------------- selection / history

    /// <summary>Selects an element, or clears the selection (a Component's too) when null.</summary>
    internal void Select(Guid? elementId)
    {
        if (SelectedElementId != elementId)
        {
            CommitPendingEdits();
        }

        SelectedElementId = elementId;
        SelectedComponentId = null;
    }

    /// <summary>Selects a Component of the open Plate (which deselects any element), or clears the
    /// selection when null. An id the Plate doesn't hold clears it.</summary>
    internal void SelectComponent(Guid? componentId)
    {
        if (componentId is { } id && profileService.CurrentProfile?.Components?.Exists(c => c.Id == id) != true)
        {
            componentId = null;
        }

        if (SelectedElementId is not null || SelectedComponentId != componentId)
        {
            CommitPendingEdits();
        }

        SelectedElementId = null;
        SelectedComponentId = componentId;
    }

    /// <summary>Reverts the most recent recorded action, if any.</summary>
    internal void Undo()
    {
        CommitPendingEdits();
        CancelInteraction();

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
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
        }

        DropSelectionIfMissing();
        InvalidateDirtyMemo();
    }

    /// <summary>Re-applies the most recently undone action, if any.</summary>
    internal void Redo()
    {
        CommitPendingEdits();
        CancelInteraction();

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
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
        }

        DropSelectionIfMissing();
        InvalidateDirtyMemo();
    }

    internal void ClearHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
    }

    private void RecordAdd(Guid newId)
    {
        var snapshot = profileService.CloneElement(newId);
        RecordHistory(
            undo: () =>
            {
                profileService.RemoveElement(snapshot.Id);
                if (SelectedElementId == snapshot.Id)
                {
                    SelectedElementId = null;
                }
            },
            redo: () => profileService.InsertElement(snapshot.Clone()));
    }

    private void ApplyZOrder(Action operation)
    {
        ErrorMessage = null;
        CommitPendingEdits();

        Dictionary<Guid, int> before;
        try
        {
            before = profileService.SnapshotZOrder();
            operation();
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return;
        }

        var after = profileService.SnapshotZOrder();
        if (ZOrdersEqual(before, after))
        {
            return;
        }

        RecordHistory(
            undo: () => profileService.RestoreZOrder(before),
            redo: () => profileService.RestoreZOrder(after));
    }

    private static bool ZOrdersEqual(Dictionary<Guid, int> a, Dictionary<Guid, int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (id, z) in a)
        {
            if (!b.TryGetValue(id, out var other) || other != z)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Records a new undoable action. Always clears the redo stack.</summary>
    private HistoryEntry RecordHistory(Action undo, Action redo)
    {
        var entry = new HistoryEntry(undo, redo);
        undoStack.Add(entry);
        redoStack.Clear();

        if (undoStack.Count > MaxHistoryEntries)
        {
            undoStack.RemoveAt(0);
        }

        // The state this action left is what its redo brings back: whatever it refers to stays in
        // use for the rest of the session (see AssetsInUse), however the image got there.
        NoteDocumentAssets();
        InvalidateDirtyMemo();
        return entry;
    }

    /// <summary>
    /// Imports an image file into managed asset storage for the open document, recording it as in
    /// use for the rest of the session. Returns null, with <see cref="ErrorMessage"/> set, when
    /// the file can't be imported.
    /// </summary>
    private Guid? ImportImage(string sourceFilePath)
    {
        try
        {
            var assetId = assetStorage.ImportImage(sourceFilePath);
            assetsInUse.Add(assetId);
            return assetId;
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, ImageImportFailedMessage);
            return null;
        }
    }

    private void NoteDocumentAssets()
    {
        if (profileService.CurrentProfile is { } profile)
        {
            AssetReferenceScanner.Collect(profile, assetsInUse);
        }
    }

    private void DropSelectionIfMissing()
    {
        if (SelectedElementId is { } id && profileService.CurrentProfile?.Elements.Exists(e => e.Id == id) != true)
        {
            SelectedElementId = null;
        }

        if (SelectedComponentId is { } componentId && profileService.CurrentProfile?.Components?.Exists(c => c.Id == componentId) != true)
        {
            SelectedComponentId = null;
        }
    }

    private void ResetTransientState()
    {
        ClearHistory();
        assetsInUse.Clear();
        pendingEditBefore = null;
        pendingBackgroundBefore = null;
        pendingDocumentBefore = null;
        lastDocumentEdit = null;
        SelectedElementId = null;
        SelectedComponentId = null;
        CancelInteraction();
        ErrorMessage = null;
        InvalidateDirtyMemo();
    }

    private void InvalidateDirtyMemo() => dirtyMemoFrame = -1;

    /// <summary>
    /// Makes <paramref name="profile"/> the baselined document. The capture comes first and the
    /// two fields are assigned together: a capture that fails must leave this document with no
    /// baseline at all (it reads as unsaved, and Revert to Saved is unavailable), never with the
    /// previous Plate's — reverting would then put another Plate's content into this one.
    /// </summary>
    private void CaptureBaseline(ProfileDocument? profile)
    {
        ProfileService.DocumentState? baseline = null;
        if (profile is not null)
        {
            try
            {
                baseline = ProfileService.DocumentState.Capture(profile);
                AssetReferenceScanner.Collect(profile, assetsInUse);
            }
            catch (Exception ex)
            {
                ErrorMessage = BaselineFailedMessage;
                log.Error(ex, "AetherFrame couldn't capture the open Plate's saved state; Revert to Saved is unavailable until it is reopened.");
            }
        }

        baselineSourceProfile = profile;
        savedBaseline = baseline;
        InvalidateDirtyMemo();
    }

    /// <summary>Structural comparison of the live profile against the saved baseline.</summary>
    private bool DocumentMatchesBaseline(ProfileDocument? profile)
    {
        var baseline = savedBaseline;
        if (baseline is null || profile is null)
        {
            return baseline is null && profile is null;
        }

        return baseline.Matches(profile.CanvasWidth, profile.CanvasHeight, profile.Background, profile.BasicIdentity, profile.BasicPlate, profile.Elements, profile.Components);
    }

    /// <summary>
    /// What AetherFrame keeps when it unloads (see <see cref="UnsavedChangesKeeper"/>): a copy of the
    /// open Plate as it is now when it differs from its last loaded or saved state, or when that state
    /// couldn't be captured. An edit still in progress (a slider being dragged, text being typed, a
    /// drag on the canvas) is already in the document, so it is kept too. Null when no Plate is open,
    /// when nothing changed, and for a document no editor frame has seen yet (opened since the last
    /// frame, so exactly as saved). Reads no frame counter and nothing of ImGui, records no history
    /// and changes nothing: it runs while the plugin unloads, on whichever thread that is. The copy is
    /// taken even while a save is being written, so a save in flight still leaves one; the next load
    /// finds it equal to the saved Plate if the save landed, and retires it.
    /// </summary>
    internal ProfileService.OpenDocumentCopy? CaptureUnsavedChanges()
    {
        if (profileService.CopyOpenDocument() is not { } copy || !ReferenceEquals(copy.Source, baselineSourceProfile))
        {
            return null;
        }

        // A save that completed since the last frame is the baseline its frame would adopt.
        var baseline = completedSave is { } save && ReferenceEquals(save.Profile, copy.Source) ? save.State : savedBaseline;
        return baseline is not null && baseline.ContentEquals(ProfileService.DocumentState.Capture(copy.Document)) ? null : copy;
    }

    /// <summary>
    /// Puts unsaved changes AetherFrame kept when it last unloaded back into the open Plate, as one
    /// undoable history entry, the way an undoable Revert to Saved works: the document becomes
    /// <paramref name="state"/> and reads as unsaved, nothing is written, and one Undo returns to
    /// what it held before. Call <see cref="SyncWithCurrentProfile"/> first, once the Plate is open,
    /// so its saved state is the baseline and the restored changes don't become it. Returns whether
    /// they were applied: false, with the document untouched and <see cref="ErrorMessage"/> set, when
    /// the Plate can't be changed right now (no Plate open, a save in flight).
    /// </summary>
    internal bool ApplyRecoveredState(ProfileService.DocumentState state)
    {
        ErrorMessage = null;
        CommitPendingEdits();
        CancelInteraction();

        ProfileService.DocumentState before;
        try
        {
            before = profileService.CaptureDocumentState();
            profileService.RestoreDocumentState(state);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, EditFailedMessage);
            return false;
        }

        DropSelectionIfMissing();
        InvalidateDirtyMemo();

        // Its assets join AssetsInUse here, as every recorded edit's do.
        RecordHistory(
            undo: () =>
            {
                profileService.RestoreDocumentState(before);
                DropSelectionIfMissing();
            },
            redo: () =>
            {
                profileService.RestoreDocumentState(state);
                DropSelectionIfMissing();
            });
        return true;
    }

    /// <summary>
    /// Whether the open document is exactly <paramref name="state"/>, with no edit in progress: how
    /// Save as New Plate knows nothing changed while its copy was being written, before it opens
    /// the copy in place of this document.
    /// </summary>
    internal bool LiveDocumentMatches(ProfileService.DocumentState state)
    {
        if (ActiveInteraction != ElementInteractionKind.None || pendingEditBefore is not null || pendingBackgroundBefore is not null || pendingDocumentBefore is not null)
        {
            return false;
        }

        return profileService.CurrentProfile is { } profile
            && state.Matches(profile.CanvasWidth, profile.CanvasHeight, profile.Background, profile.BasicIdentity, profile.BasicPlate, profile.Elements, profile.Components);
    }

    private static bool StatesEqual(ProfileService.DocumentState a, ProfileService.DocumentState b) => a.ContentEquals(b);

    private sealed record HistoryEntry(Action Undo, Action Redo);

    private sealed record CompletedSave(ProfileDocument Profile, ProfileService.DocumentState State);
}
