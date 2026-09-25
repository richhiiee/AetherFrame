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
using Dalamud.Interface.Utility;
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
    private const string ZoomMenuPopupId = "##AetherFrameZoomMenu";

    private static readonly float[] ZoomPresets = [0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f];

    private const ImGuiWindowFlags EditorFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>Clean Preview's presentation: no background, border (see PreDraw), title bar or
    /// chrome; placed and sized by <see cref="CleanPreviewLayout"/>, so not movable or resizable.</summary>
    internal const ImGuiWindowFlags CleanPreviewFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse;

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ProfileRenderResources renderResources;
    private readonly FileDialogManager fileDialogManager;
    private readonly EditorSurfaceCoordinator surfaces;
    private readonly BackgroundStylePanel backgroundPanel;
    private readonly EditorActionBar actionBar;
    private readonly Action openLibrary;

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

    private bool pendingZoomMenu;

    // Unsaved-changes protection: the action waiting on the user's Save/Discard/Cancel answer,
    // and (after Save) the save it's waiting on.
    private GuardedAction? guardedAction;
    private bool pendingGuardPrompt;
    private Task<bool>? guardSaveTask;
    private bool closeConfirmed;

    // Clean Preview presentation (see PreDraw): the editor's own rectangle, captured every editing
    // frame so the preview fits inside it and the editor returns to it; whether the transparent
    // presentation is in effect (and its style pushes need popping in PostDraw); and the size
    // constraints set aside meanwhile (the editor's minimum size would otherwise inflate the preview).
    private Vector2 editorWindowPos;
    private Vector2 editorWindowSize;
    private bool presentingPreview;
    private bool previewPresentationApplied;
    private CleanPreviewLayout? previewLayout;
    private WindowSizeConstraints? editorSizeConstraints;
    private bool editorAllowsBackgroundBlur = true;

    // Whether the window was open at the previous PreOpenCheck (so a close is noticed exactly once).
    private bool openLastFrame;

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
        Action openBasicEditor,
        Action openLibrary,
        EditorSurfaceCoordinator surfaces,
        EditorDocumentCommands commands)
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
        this.openLibrary = openLibrary;
        this.surfaces = surfaces;
        backgroundPanel = new BackgroundStylePanel(editorSession, renderResources, OpenImageFileDialog);
        actionBar = new EditorActionBar(commands, EditorSurfaceKind.Advanced, openLibrary, openBasicEditor);

        // Title bar, left to right: Dalamud's Window Options (Settings) | Minimize | Close — all three
        // Dalamud's own, the same as every other AetherFrame window (see TitleBarOrder). Minimize is
        // the native collapse button (EditorFlags allows it), so collapsing and restoring — by the
        // button or a title bar double-click — is ImGui's own state, never forced by this window.
        //
        // Close is the standard title bar close every AetherFrame window has (Dalamud's native one,
        // always far right, the same in the collapsed title bar). It can close with unsaved work, so
        // the close is vetoed in PreOpenCheck — before Dalamud acts on it — and the unsaved-changes
        // question asked instead (see CloseGuard).
    }

    /// <summary>
    /// Runs every frame before Dalamud checks whether the window is open. A close that would lose
    /// unsaved work — native Close button, Escape, a toggle from elsewhere — is turned back into
    /// "still open" here and the unsaved-changes question asked, so Dalamud never starts closing
    /// (no close sound, no fade-out flicker). OnClose remains the fallback for anything else.
    /// </summary>
    public override void PreOpenCheck()
    {
        if (!IsOpen && openLastFrame && !closeConfirmed && profileService.CurrentProfile is not null)
        {
            editorSession.CommitPendingEdits();
            if (CloseGuard.ShouldVeto(wasOpen: true, isOpen: false, closeConfirmed, hasPlate: true, editorSession.IsDirty))
            {
                IsOpen = true;
                RequestGuardedAction(GuardedAction.Close);
            }
        }

        openLastFrame = IsOpen;
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
            // The unsaved-changes question needs the normal editor window, not the preview's.
            editorSession.PreviewActive = false;
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
        presentingPreview = false;
        if (editorSession.PreviewActive && profileService.CurrentProfile is { } profile && editorWindowSize is { X: > 0f, Y: > 0f }
            && CleanPreviewLayout.Compute(editorWindowPos, editorWindowSize, ProfileVisualBounds.Compute(profile), CleanPreviewLayout.DefaultCloseButtonSize * ImGuiHelpers.GlobalScale) is { } layout)
        {
            // Clean Preview: this same window shrinks to exactly the Plate's fitted visual bounds
            // (plus its close control) within the rectangle the editor occupied, and draws nothing
            // of its own — no background, border, title bar, padding or blur (CleanPreviewPresentation);
            // the Plate is drawn without the renderer's workspace backdrop. Only the Plate, its
            // artwork and the close control show over the game, and the window takes mouse input
            // only there (see CleanPreviewLayout for why it can't be click-through).
            if (!previewPresentationApplied)
            {
                editorSizeConstraints = SizeConstraints;
                editorAllowsBackgroundBlur = AllowBackgroundBlur;
                previewPresentationApplied = true;
            }

            AllowBackgroundBlur = CleanPreviewPresentation.AllowBackgroundBlur;
            previewLayout = layout;
            SizeConstraints = null;
            ImGui.SetNextWindowPos(layout.WindowPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(layout.WindowSize, ImGuiCond.Always);
            Flags = CleanPreviewFlags;
            RespectCloseHotkey = false; // Escape belongs to Clean Preview

            // Popped in PostDraw (Dalamud calls it after End on every frame PreDraw ran).
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, CleanPreviewPresentation.WindowPadding);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, CleanPreviewPresentation.WindowBorderSize);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, CleanPreviewPresentation.BackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, CleanPreviewPresentation.BackgroundColor);
            presentingPreview = true;
            return;
        }

        if (previewPresentationApplied)
        {
            // Leaving Clean Preview (by any path): the editor comes back exactly where and as large as it was.
            previewPresentationApplied = false;
            SizeConstraints = editorSizeConstraints;
            AllowBackgroundBlur = editorAllowsBackgroundBlur;
            ImGui.SetNextWindowPos(editorWindowPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(editorWindowSize, ImGuiCond.Always);
        }

        // The layout is sized to fit exactly; the panels scroll themselves. Escape belongs to
        // Clean Preview while it's up, so the window-close hotkey stands down then.
        Flags = EditorFlags;
        RespectCloseHotkey = !editorSession.PreviewActive;
    }

    public override void PostDraw()
    {
        if (presentingPreview)
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(2);
            presentingPreview = false;
        }
    }

    public override void Draw()
    {
        if (!presentingPreview)
        {
            editorWindowPos = ImGui.GetWindowPos();
            editorWindowSize = ImGui.GetWindowSize();
        }

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
        DrawZoomMenuPopup();
        actionBar.DrawPopups();
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
    /// The shared <see cref="EditorActionBar"/> (the same one the Basic editor has: My Plates,
    /// Basic | Advanced, Undo/Redo, Preview/Revert/Save), then this mode's own tools on a row of
    /// their own: add content, and the canvas view toggles. Properties never live here — that's the
    /// Inspector's job. Always reachable without scrolling.
    /// </summary>
    private void DrawToolbar(ProfileDocument profile)
    {
        actionBar.Draw(profile, editorSession.PreviewActive, EnterPreview, "Preview: the finished Plate only, over the game (Esc to exit)", editorSession.ErrorMessage);

        var atCapacity = profile.Elements.Count >= ProfileDocument.MaxElementCount;
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

        if (EditorWidgets.TextToggle("Guides", editorSession.ShowGuides, tooltip: "Show element bounds, selection, and handles on the canvas"))
        {
            editorSession.ShowGuides = !editorSession.ShowGuides;
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Snap", editorSession.SnapEnabled, tooltip: "Snap to the canvas and other elements while moving or resizing.\nHold Alt to bypass temporarily."))
        {
            editorSession.SnapEnabled = !editorSession.SnapEnabled;
        }
    }

    private static void ToolbarGap()
    {
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(6f, 0f));
        ImGui.SameLine();
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

    // ---------------------------------------------------------------- unsaved-changes protection


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
    /// The finished profile alone, drawn through exactly the same renderer call as Profile View: no
    /// panels, toolbar, guides, snapping guides, selection, placeholders, or any other editor chrome.
    /// Normally the window itself is shrunk to the Plate's visual bounds and draws nothing of its
    /// own (see PreDraw and <see cref="CleanPreviewLayout"/>), and the Plate is drawn without the
    /// workspace backdrop, so it sits directly over the game. An always-visible close control sits
    /// at the top-right of the visual bounds (Escape also exits).
    /// </summary>
    private void DrawCleanPreview(ProfileDocument profile)
    {
        var available = ImGui.GetContentRegionAvail();
        var windowPos = ImGui.GetWindowPos();
        if (presentingPreview && previewLayout is { } layout)
        {
            ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, windowPos + layout.CanvasOffset, layout.Scale, renderResources, CleanPreviewPresentation.RenderOptions);
            DrawCleanPreviewCloseButton(windowPos + layout.CloseButtonOffset, layout.CloseButtonSize);
            return;
        }

        // The one frame between entering preview and PreDraw applying its presentation (or no
        // layout at all): the old in-window fit, so the Plate never blinks out.
        var buttonSize = CleanPreviewLayout.DefaultCloseButtonSize * ImGuiHelpers.GlobalScale;
        if (available.X >= 1f && available.Y >= 1f)
        {
            var fit = PlateViewFit.Fit(available, ProfileVisualBounds.Compute(profile));
            if (fit.Scale > 0f)
            {
                ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, ImGui.GetCursorScreenPos() + fit.CanvasOffset, fit.Scale, renderResources, CleanPreviewPresentation.RenderOptions);
            }
        }

        var contentMax = windowPos + ImGui.GetWindowContentRegionMax();
        var contentMin = windowPos + ImGui.GetWindowContentRegionMin();
        DrawCleanPreviewCloseButton(new Vector2(contentMax.X - buttonSize, contentMin.Y), buttonSize);
    }

    /// <summary>Clean Preview's close control: AetherFrame's shared Close look (<see cref="PresentationControls"/>). Leaves the preview.</summary>
    private void DrawCleanPreviewCloseButton(Vector2 min, float size)
    {
        if (PresentationControls.Close("##CleanPreviewClose", min, size, "Exit Preview (Esc)"))
        {
            editorSession.PreviewActive = false;
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
                    actionBar.Commands.Undo();
                    break;
                case EditorShortcutActionKind.Redo:
                    actionBar.Commands.Redo();
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
                    actionBar.Commands.Save();
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
