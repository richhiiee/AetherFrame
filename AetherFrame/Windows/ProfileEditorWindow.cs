using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The Advanced (freeform) profile editor: Layers panel, canvas, and Inspector around one shared
/// <see cref="EditorSession"/>. Split across partial files by panel:
/// this file (lifecycle, toolbar, status bar, shortcuts, save protection, Clean Preview),
/// <c>.Layers.cs</c>, <c>.Canvas.cs</c> (viewport, input, editor chrome), <c>.Inspector.cs</c>
/// (element properties), and <c>.CanvasSettings.cs</c> (canvas size and background).
///
/// Everything drawn on the canvas that isn't the profile itself (bounds, selection, handles, snap
/// guides, placeholders) is editor chrome layered on top of <see cref="ProfileRenderer"/>'s
/// output; Clean Preview and Profile View use the renderer alone, so they can never show it.
/// </summary>
internal sealed partial class ProfileEditorWindow : Window, IDisposable, IEditorSurface
{
    private const float LeftPanelWidth = 236f;
    private const float RightPanelWidth = 344f;
    private const float MinCanvasWidth = 360f;

    private const string ElementContextMenuId = "##AetherFrameElementContextMenu";
    private const string CanvasResizePopupId = "##AetherFrameCanvasResizePopup";
    private const string UnsavedChangesPopupId = "Unsaved Changes##AetherFrameUnsavedChanges";
    private const string RevertPopupId = "Revert to Saved##AetherFrameRevert";
    private const string SaveMenuPopupId = "##AetherFrameSaveMenu";
    private const string ZoomMenuPopupId = "##AetherFrameZoomMenu";

    private static readonly float[] ZoomPresets = [0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f];

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ProfileRenderResources renderResources;
    private readonly FileDialogManager fileDialogManager;
    private readonly Action openProfileView;
    private readonly Action openBasicEditor;
    private readonly Action openLibrary;
    private readonly EditorSurfaceCoordinator surfaces;
    private readonly BackgroundStylePanel backgroundPanel;

    // Reused per frame (render thread only) for paint-order walks, so none of them allocate.
    private readonly List<ProfileElement> paintOrderBuffer = new(ProfileDocument.MaxElementCount);

    // Which element the currently-open context menu targets (read back when drawing the popup's
    // body). Right-click can be detected from two different places with two different ImGui ID
    // stacks (the canvas child vs. a Layers row) — ImGui.OpenPopup/BeginPopup only match when
    // called from the SAME id-stack scope, so both sites just record the request here, and Draw()
    // is the single place that actually calls OpenPopup/BeginPopup, always from the same
    // (outermost) scope. Every other popup below follows the same deferred-open pattern.
    private Guid contextMenuElementId;
    private Guid? pendingContextMenuOpenElementId;

    private (float Width, float Height) canvasResizePromptTarget;
    private (float Width, float Height)? pendingCanvasResizeOpenRequest;

    private bool pendingRevertPrompt;
    private bool pendingSaveMenu;
    private bool pendingZoomMenu;

    // Unsaved-changes protection: the action waiting on the user's Save/Discard/Cancel answer,
    // and (after Save) the save it's waiting on.
    private GuardedAction? guardedAction;
    private bool pendingGuardPrompt;
    private Task<bool>? guardSaveTask;
    private bool closeConfirmed;

    // Inspector tab and focus requests, raised by canvas/layers interactions.
    private bool selectElementTabPending;
    private bool focusTextContentPending;
    private Guid? lastInspectedElementId;

    internal ProfileEditorWindow(
        ProfileService profileService,
        EditorSession editorSession,
        KeyboardShortcutService keyboardShortcutService,
        ProfileRenderResources renderResources,
        FileDialogManager fileDialogManager,
        Action openProfileView,
        Action openBasicEditor,
        Action openLibrary,
        EditorSurfaceCoordinator surfaces)
        : base("AetherFrame Advanced Editor##ProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.editorSession = editorSession;
        this.keyboardShortcutService = keyboardShortcutService;
        this.renderResources = renderResources;
        this.fileDialogManager = fileDialogManager;
        this.openProfileView = openProfileView;
        this.openBasicEditor = openBasicEditor;
        this.openLibrary = openLibrary;
        this.surfaces = surfaces;
        backgroundPanel = new BackgroundStylePanel(editorSession, renderResources, OpenImageFileDialog);

        // The native close button can't be intercepted, so it's replaced by one that goes through
        // the unsaved-changes prompt. (Other close paths are caught in OnClose.)
        ShowCloseButton = false;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Times,
            IconOffset = new Vector2(1.5f, 1f),
            Click = _ => RequestClose(),
            ShowTooltip = () => ImGui.SetTooltip("Close"),
            Priority = int.MaxValue,
        });
    }

    private enum GuardedAction
    {
        Close,
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Called by the window system whenever this window (re)opens. Auto Fit always turns back
    /// on for a freshly-opened editor, regardless of whatever manual zoom was left over from a
    /// previous session, and the sentinel forces the very next Draw to (re)compute a fresh fit
    /// against whatever the panel size actually is now.
    /// </summary>
    public override void OnOpen()
    {
        // One editing surface at a time: the Basic editor hands over if it's open.
        surfaces.NotifyOpened(EditorSurfaceKind.Advanced);

        editorSession.AutoFit = true;
        editorSession.PreviewActive = false;
        lastCanvasPanelSize = new Vector2(-1f, -1f);
    }

    /// <inheritdoc/>
    public void Show()
    {
        IsOpen = true;
        BringToFront();
    }

    /// <summary>
    /// Handing editing to the Basic editor: the same document, dirty state, and history continue
    /// there, so the unsaved-changes question doesn't apply (see <see cref="EditorSurfaceCoordinator"/>).
    /// </summary>
    public void CloseForHandoff()
    {
        closeConfirmed = true;
        IsOpen = false;
    }

    /// <summary>
    /// Called by the window system exactly once when this window closes. The guarded close button
    /// only closes after the unsaved-changes question is answered; any other path (Escape, a
    /// toggle from another window or command) arrives here unguarded, so with unsaved work the
    /// window is simply reopened and the question asked instead.
    /// </summary>
    public override void OnClose()
    {
        editorSession.CommitPendingEdits();
        editorSession.EndInteraction();

        if (!closeConfirmed && profileService.CurrentProfile is not null && editorSession.IsDirty)
        {
            IsOpen = true;
            RequestGuardedAction(GuardedAction.Close);
            return;
        }

        closeConfirmed = false;
        editorSession.PreviewActive = false;

        // Draw won't run again until the window reopens, so this is the only reliable place to
        // tell the keyboard service to stop intercepting immediately.
        keyboardShortcutService.SetEditorFocusState(editorFocused: false, textInputActive: false);
        fileDialogManager.Reset();
    }

    public override void PreDraw()
    {
        // The layout is sized to fit exactly; the panels scroll themselves. Escape belongs to
        // Clean Preview while it's up, so the window-close hotkey stands down then.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        RespectCloseHotkey = !editorSession.PreviewActive;
    }

    public override void Draw()
    {
        // Drawn unconditionally so an in-progress file pick isn't stranded if the profile
        // becomes unavailable (e.g. character logs out) while the dialog is open.
        fileDialogManager.Draw();

        editorSession.SyncWithCurrentProfile();

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No Plate is open.");
            ImGui.TextDisabled("Choose a Plate to edit in My Plates.");
            if (ImGui.Button("Open My Plates"))
            {
                openLibrary();
            }

            keyboardShortcutService.SetEditorFocusState(editorFocused: false, textInputActive: false);
            return;
        }

        PublishKeyboardFocusState();
        ApplyPendingShortcutActions();
        AdvanceGuardedSave();

        if (editorSession.PreviewActive)
        {
            DrawCleanPreview(profile);
        }
        else
        {
            DrawEditor(profile);
        }

        // Outside every child region: the one consistent id-stack scope every popup is
        // opened/drawn from — see pendingContextMenuOpenElementId.
        DrawElementContextMenuPopup(profile);
        DrawCanvasResizePromptPopup();
        DrawSaveMenuPopup();
        DrawZoomMenuPopup();
        DrawRevertPopup();
        DrawUnsavedChangesPopup();

        // Commits an edit whose widget never reported "deactivated after edit" (see method).
        editorSession.CommitPendingEditsIfIdle(ImGui.IsAnyItemActive());
    }

    private void DrawEditor(ProfileDocument profile)
    {
        DrawToolbar(profile);
        EditorWidgets.UnsupportedElementsNotice(profile);
        ImGui.Separator();

        var contentAvail = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing;
        var statusBarHeight = ImGui.GetFrameHeightWithSpacing() + spacing.Y;
        var bodyHeight = Math.Max(200f, contentAvail.Y - statusBarHeight - spacing.Y);
        var canvasWidth = Math.Max(MinCanvasWidth, contentAvail.X - LeftPanelWidth - RightPanelWidth - (spacing.X * 2f));

        DrawLayersPanel(profile, new Vector2(LeftPanelWidth, bodyHeight));
        ImGui.SameLine();
        DrawCanvasPanel(profile, new Vector2(canvasWidth, bodyHeight));
        ImGui.SameLine();
        DrawInspectorPanel(profile, new Vector2(RightPanelWidth, bodyHeight));

        ImGui.Separator();
        DrawStatusBar(profile);
    }

    // ---------------------------------------------------------------- toolbar & status bar

    /// <summary>
    /// One compact row of editor-wide actions: add, history, save, view toggles, preview/view.
    /// Properties never live here — that's the Inspector's job. Always reachable without scrolling.
    /// </summary>
    private void DrawToolbar(ProfileDocument profile)
    {
        var atCapacity = profile.Elements.Count >= ProfileDocument.MaxElementCount;

        if (EditorWidgets.IconButton("MyPlates", FontAwesomeIcon.ThLarge, "My Plates"))
        {
            openLibrary();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(profile.Name);

        ToolbarGap();

        using (ImRaii.Disabled(atCapacity))
        {
            if (ImGui.Button("+ Text"))
            {
                AddTextFromToolbar();
            }

            EditorWidgets.Tooltip(atCapacity ? $"At the maximum of {ProfileDocument.MaxElementCount} elements." : "Add a text element");

            ImGui.SameLine();
            if (ImGui.Button("+ Image"))
            {
                OpenImageFileDialog("Add Image", path => editorSession.AddImageElement(path));
            }

            EditorWidgets.Tooltip(atCapacity ? $"At the maximum of {ProfileDocument.MaxElementCount} elements." : "Import an image");
        }

        ToolbarGap();

        using (ImRaii.Disabled(!editorSession.CanUndo))
        {
            if (EditorWidgets.IconButton("Undo", FontAwesomeIcon.Undo, "Undo (Ctrl+Z)"))
            {
                editorSession.Undo();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!editorSession.CanRedo))
        {
            if (EditorWidgets.IconButton("Redo", FontAwesomeIcon.Redo, "Redo (Ctrl+Y)"))
            {
                editorSession.Redo();
            }
        }

        ToolbarGap();

        var dirty = editorSession.IsDirty;
        var canSave = CanSaveNow() && dirty;
        using (ImRaii.Disabled(!canSave))
        using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.ActiveToggleColor, canSave))
        {
            if (ImGui.Button("Save"))
            {
                editorSession.SaveProfile();
            }
        }

        EditorWidgets.Tooltip("Save (Ctrl+S)");

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconButton("SaveMenu", FontAwesomeIcon.CaretDown, "More save options"))
        {
            pendingSaveMenu = true;
        }

        ToolbarGap();

        if (EditorWidgets.TextToggle("Guides", editorSession.ShowGuides, tooltip: "Show element bounds, selection, and handles on the canvas"))
        {
            editorSession.ShowGuides = !editorSession.ShowGuides;
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Snap", editorSession.SnapEnabled, tooltip: "Snap to the canvas and other elements while moving or resizing.\nHold Alt to bypass temporarily."))
        {
            editorSession.SnapEnabled = !editorSession.SnapEnabled;
        }

        ToolbarGap();

        if (ImGui.Button("Preview"))
        {
            EnterPreview();
        }

        EditorWidgets.Tooltip("Clean Preview: the finished Plate only (Esc to exit)");

        ImGui.SameLine();
        if (ImGui.Button("Plate Viewer"))
        {
            openProfileView();
        }

        // Right-aligned: save state, then the Basic editor switch.
        const string basicLabel = "Basic Editor";
        var stateText = profileService.IsBusy ? "Saving..." : dirty ? "Unsaved changes" : "Saved";
        var rightWidth = ImGui.CalcTextSize(stateText).X + ImGui.CalcTextSize(basicLabel).X + (ImGui.GetStyle().FramePadding.X * 2f) + 36f;
        var rightStart = ImGui.GetWindowContentRegionMax().X - rightWidth;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + 8f, rightStart));
        DrawSaveStateIndicator(dirty);

        ImGui.SameLine();
        if (ImGui.Button(basicLabel))
        {
            openBasicEditor();
        }

        if (editorSession.ErrorMessage is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
    }

    private static void ToolbarGap()
    {
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(6f, 0f));
        ImGui.SameLine();
    }

    /// <summary>Compact, non-technical save state: never exposes Revision or busy internals.</summary>
    private void DrawSaveStateIndicator(bool dirty)
    {
        ImGui.AlignTextToFramePadding();
        if (profileService.IsBusy)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.4f, 1f), "Saving...");
        }
        else if (dirty)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, "Unsaved changes");
            EditorWidgets.Tooltip("Ctrl+S to save. Use the arrow next to Save to revert.");
        }
        else
        {
            ImGui.TextColored(EditorWidgets.SuccessColor with { W = 0.75f }, "Saved");
        }
    }

    /// <summary>
    /// Bottom status bar: zoom controls plus compact, non-technical canvas/selection info. Never
    /// exposes ZIndex, Revision, or other implementation details.
    /// </summary>
    private void DrawStatusBar(ProfileDocument profile)
    {
        if (EditorWidgets.IconButton("ZoomOut", FontAwesomeIcon.Minus, "Zoom out"))
        {
            editorSession.SetZoom(editorSession.Zoom / 1.25f);
        }

        ImGui.SameLine(0f, 2f);
        if (ImGui.Button($"{editorSession.Zoom * 100f:0}%##ZoomLevel", new Vector2(58f, 0f)))
        {
            pendingZoomMenu = true;
        }

        EditorWidgets.Tooltip("Zoom (mouse wheel over the canvas)");

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconButton("ZoomIn", FontAwesomeIcon.Plus, "Zoom in"))
        {
            editorSession.SetZoom(editorSession.Zoom * 1.25f);
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Fit", editorSession.AutoFit, tooltip: "Fit the canvas to the panel (F)"))
        {
            FitCanvas();
        }

        var selected = GetSelectedElement(profile);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.DimTextColor))
        {
            ImGui.TextUnformatted($"   Canvas {profile.CanvasWidth:0} x {profile.CanvasHeight:0}   |   Elements {profile.Elements.Count}/{ProfileDocument.MaxElementCount}   |   {(selected is null ? "Nothing selected" : ProfileElementNames.GetDisplayName(selected))}");

            ImGui.SameLine();
            const string hints = "Wheel: zoom   Middle-drag: pan   F: fit   Alt: no snap";
            var hintWidth = ImGui.CalcTextSize(hints).X;
            var hintX = ImGui.GetWindowContentRegionMax().X - hintWidth;
            if (hintX > ImGui.GetCursorPosX() + 16f)
            {
                ImGui.SameLine(hintX);
                ImGui.TextUnformatted(hints);
            }
        }
    }

    private void DrawZoomMenuPopup()
    {
        if (pendingZoomMenu)
        {
            ImGui.OpenPopup(ZoomMenuPopupId);
            pendingZoomMenu = false;
        }

        using var popup = ImRaii.Popup(ZoomMenuPopupId);
        if (!popup.Success)
        {
            return;
        }

        if (ImGui.MenuItem("Fit Canvas", "F"))
        {
            FitCanvas();
        }

        ImGui.Separator();

        foreach (var preset in ZoomPresets)
        {
            if (ImGui.MenuItem($"{preset * 100f:0}%", string.Empty, MathF.Abs(editorSession.Zoom - preset) < 0.001f))
            {
                editorSession.SetZoom(preset);
            }
        }
    }

    private void DrawSaveMenuPopup()
    {
        if (pendingSaveMenu)
        {
            ImGui.OpenPopup(SaveMenuPopupId);
            pendingSaveMenu = false;
        }

        using var popup = ImRaii.Popup(SaveMenuPopupId);
        if (!popup.Success)
        {
            return;
        }

        var dirty = editorSession.IsDirty;
        if (ImGui.MenuItem("Save", "Ctrl+S", false, dirty && CanSaveNow()))
        {
            editorSession.SaveProfile();
        }

        if (ImGui.MenuItem("Revert to Saved...", string.Empty, false, dirty && editorSession.CanRevert))
        {
            pendingRevertPrompt = true;
        }
    }

    private void DrawRevertPopup()
    {
        if (pendingRevertPrompt)
        {
            ImGui.OpenPopup(RevertPopupId);
            pendingRevertPrompt = false;
        }

        if (!ImGui.BeginPopupModal(RevertPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TextUnformatted("Revert this Plate to its last saved version?");
        EditorWidgets.Hint("All unsaved changes will be discarded. You can still undo the revert.");
        ImGui.Spacing();

        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.62f, 0.22f, 0.22f, 1f)))
        {
            if (ImGui.Button("Revert", new Vector2(120f, 0f)))
            {
                editorSession.RevertToSaved(undoable: true);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- unsaved-changes protection

    private void RequestClose() => RequestGuardedAction(GuardedAction.Close);

    /// <summary>
    /// Runs <paramref name="action"/> right away when there's nothing unsaved; otherwise asks
    /// Save / Discard / Cancel first. The single choke point in this window for anything that would
    /// otherwise silently lose unsaved work (closing the editor; switching Plates is guarded by My
    /// Plates, which opens them).
    /// </summary>
    private void RequestGuardedAction(GuardedAction action)
    {
        editorSession.CommitPendingEdits();

        if (!editorSession.IsDirty)
        {
            RunGuardedAction(action);
            return;
        }

        guardedAction = action;
        guardSaveTask = null;
        pendingGuardPrompt = true;
    }

    private void RunGuardedAction(GuardedAction action)
    {
        guardedAction = null;
        guardSaveTask = null;

        switch (action)
        {
            case GuardedAction.Close:
                closeConfirmed = true;
                IsOpen = false;
                break;
        }
    }

    /// <summary>After "Save" in the prompt: once that save finishes, run the waiting action — or,
    /// if it failed, keep everything open with the error showing.</summary>
    private void AdvanceGuardedSave()
    {
        if (guardSaveTask is not { IsCompleted: true } task || guardedAction is not { } action)
        {
            return;
        }

        guardSaveTask = null;
        editorSession.SyncWithCurrentProfile();

        if (task.IsCompletedSuccessfully && task.Result)
        {
            RunGuardedAction(action);
        }
        else
        {
            guardedAction = null;
        }
    }

    private void DrawUnsavedChangesPopup()
    {
        if (pendingGuardPrompt)
        {
            ImGui.OpenPopup(UnsavedChangesPopupId);
            pendingGuardPrompt = false;
        }

        if (!ImGui.BeginPopupModal(UnsavedChangesPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (guardedAction is not { } action)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted("This Plate has unsaved changes. Save them before closing?");

        var canSave = CanSaveNow();
        ImGui.Spacing();

        var buttonSize = new Vector2(110f, 0f);
        using (ImRaii.Disabled(!canSave || guardSaveTask is not null))
        {
            if (ImGui.Button(guardSaveTask is null ? "Save" : "Saving...", buttonSize))
            {
                guardSaveTask = editorSession.SaveProfileAsync();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Discard", buttonSize))
        {
            editorSession.DiscardChanges();
            ImGui.CloseCurrentPopup();
            RunGuardedAction(action);
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
        {
            guardedAction = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private bool CanSaveNow() => !profileService.IsBusy;

    // ---------------------------------------------------------------- Clean Preview

    private void EnterPreview()
    {
        editorSession.CommitPendingEdits();
        editorSession.EndInteraction();
        editorSession.PreviewActive = true;
    }

    /// <summary>
    /// The finished profile alone, centered and fit to the whole window, drawn through exactly the
    /// same renderer call as Profile View: no panels, toolbar, guides, snapping guides, selection,
    /// placeholders, or any other editor chrome. The only affordance is a faint exit button that
    /// appears while the mouse is over the window (Escape also exits).
    /// </summary>
    private void DrawCleanPreview(ProfileDocument profile)
    {
        var available = ImGui.GetContentRegionAvail();
        if (available.X >= 1f && available.Y >= 1f)
        {
            var scale = Math.Min(available.X / profile.CanvasWidth, available.Y / profile.CanvasHeight);
            if (scale > 0f)
            {
                var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * scale;
                var canvasOrigin = ImGui.GetCursorScreenPos() + ((available - canvasScreenSize) / 2f);

                ImGui.Dummy(available);
                ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, canvasOrigin, scale, renderResources, ProfileRenderOptions.Finished);
            }
        }

        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            return;
        }

        const string label = "Exit Preview (Esc)";
        var size = ImGui.CalcTextSize(label) + (ImGui.GetStyle().FramePadding * 2f);
        var windowPos = ImGui.GetWindowPos();
        var contentMax = ImGui.GetWindowContentRegionMax();
        ImGui.SetCursorScreenPos(new Vector2(windowPos.X + contentMax.X - size.X, windowPos.Y + ImGui.GetWindowContentRegionMin().Y));

        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0.45f)))
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 0.75f)))
        {
            if (ImGui.Button(label))
            {
                editorSession.PreviewActive = false;
            }
        }
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>
    /// Publishes this frame's focus/text-input/preview/interaction state to
    /// <see cref="KeyboardShortcutService"/> so it knows what to intercept on the NEXT
    /// <c>Framework.Update</c> tick. Detection/suppression happen there — early enough that FFXIV
    /// never also reacts — not here.
    /// </summary>
    private void PublishKeyboardFocusState()
    {
        var io = ImGui.GetIO();
        var editorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var textInputActive = io.WantTextInput;

        keyboardShortcutService.SetEditorFocusState(
            editorFocused,
            textInputActive,
            editorSession.PreviewActive,
            editorSession.ActiveInteraction != ElementInteractionKind.None);

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
            if (editorSession.PreviewActive && action.Kind is not (EditorShortcutActionKind.ExitPreview or EditorShortcutActionKind.Save))
            {
                continue;
            }

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
                case EditorShortcutActionKind.Save:
                    if (CanSaveNow() && editorSession.IsDirty)
                    {
                        editorSession.SaveProfile();
                    }

                    break;
                case EditorShortcutActionKind.Duplicate:
                    if (editorSession.SelectedElementId is { } duplicateId)
                    {
                        editorSession.DuplicateElement(duplicateId);
                    }

                    break;
                case EditorShortcutActionKind.FitCanvas:
                    FitCanvas();
                    break;
                case EditorShortcutActionKind.ExitPreview:
                    editorSession.PreviewActive = false;
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- shared helpers

    private void AddTextFromToolbar()
    {
        if (editorSession.AddTextElement() is not null)
        {
            // Straight into typing: the Inspector's content box takes focus with its text selected.
            selectElementTabPending = true;
            focusTextContentPending = true;
        }
    }

    private ProfileElement? GetSelectedElement(ProfileDocument profile) =>
        editorSession.SelectedElementId is { } selectedId ? profile.Elements.Find(e => e.Id == selectedId) : null;

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
}
