using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using AetherFrame.Windows.Tutorial;
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
/// this file (lifecycle, toolbar, status bar, shortcuts, save protection),
/// <c>.Layers.cs</c>, <c>.Canvas.cs</c> (viewport, input, editor chrome), <c>.Inspector.cs</c>
/// (element properties), and <c>.CanvasSettings.cs</c> (canvas size and background).
///
/// Everything drawn on the canvas that isn't the profile itself (bounds, selection, handles, snap
/// guides, placeholders) is editor chrome layered on top of <see cref="ProfileRenderer"/>'s
/// output; the Plate Viewer (Preview, View) uses the renderer alone, so it can never show it.
/// </summary>
internal sealed partial class ProfileEditorWindow : Window, IDisposable, IEditorSurface
{
    private const string ElementContextMenuId = "##AetherFrameElementContextMenu";
    private const string CanvasResizePopupId = "##AetherFrameCanvasResizePopup";
    private const string ZoomMenuPopupId = "##AetherFrameZoomMenu";

    private static readonly float[] ZoomPresets = [0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f];

    private const ImGuiWindowFlags EditorFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ProfileRenderResources renderResources;

    // The artwork the canvas's Plate is missing: downloaded as the Plate is opened (art on demand).
    private readonly ArtNeeds canvasArt = new();
    private readonly FileDialogManager fileDialogManager;
    private readonly EditorSurfaceCoordinator surfaces;
    private readonly BackgroundStylePanel backgroundPanel;
    private readonly EditorActionBar actionBar;
    private readonly Action openLibrary;

    // Reused per frame (render thread only) for paint-order walks, so none of them allocate.
    // The canvas's paint sequence, for hit testing what it just drew, and a Component's outlines.
    private readonly List<Domain.Components.PaintStep> canvasPlanBuffer = new(ProfileDocument.MaxElementCount + 64);
    private readonly List<Vector2[]> componentOutlineBuffer = new(8);

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

    // Unsaved-changes protection when the window closes (shared with the Basic editor's own).
    private readonly EditorCloseGuard closeGuard;

    // AetherFrame's style around this window's frame, and the tutorial's window policy.
    private readonly AetherWindowChrome chrome = new();

    // Escape on a menu, list, color picker or prompt closes only that, never the editor.
    private readonly PopupEscapeGuard escape = new();

    // Inspector tab and focus requests, raised by canvas/layers interactions.
    private bool selectElementTabPending;

    // The Component selection the Inspector last brought forward, and its pending tab switch.
    private Guid? lastInspectedComponentId;
    private bool selectCanvasTabPending;
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
        EditorDocumentCommands commands,
        EditorPlateMenu plateMenu)
        : base("AetherFrame Advanced Editor##ProfileEditorWindow", EditorFlags)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AdvancedEditorLayout.MinimumWindowSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = true;

        this.profileService = profileService;
        this.editorSession = editorSession;
        this.keyboardShortcutService = keyboardShortcutService;
        this.renderResources = renderResources;
        this.fileDialogManager = fileDialogManager;
        this.openLibrary = openLibrary;
        this.surfaces = surfaces;
        backgroundPanel = new BackgroundStylePanel(editorSession, renderResources, OpenImageFileDialog);
        actionBar = new EditorActionBar(commands, EditorSurfaceKind.Advanced, openLibrary, openBasicEditor, () => Help, plateMenu);
        closeGuard = new EditorCloseGuard(editorSession, commands);

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
    public override void PreOpenCheck() => IsOpen = closeGuard.PreOpenCheck(IsOpen);

    /// <summary>The Help menu (tutorial, shortcuts, commands), set by the plugin once the tutorial exists.</summary>
    internal HelpMenu? Help { get; set; }

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
        closeGuard.ConfirmClose();
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

        if (closeGuard.ShouldReopenOnClose())
        {
            IsOpen = true;
            return;
        }

        // Draw won't run again until the window reopens, so this is the only reliable place to
        // tell the keyboard service to stop intercepting immediately.
        keyboardShortcutService.SetEditorFocusState(editorFocused: false, textInputActive: false);
        fileDialogManager.Reset();
    }

    /// <summary>The first-open size.</summary>
    public override void PreDraw()
    {
        chrome.PushStyle();
        EditorWidgets.SetFirstUseSize(AdvancedEditorLayout.FirstUseSize, AdvancedEditorLayout.MinimumWindowSize);
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void Draw()
    {
        using var popupEscape = escape.Update(this);

        // Drawn unconditionally so an in-progress file pick isn't stranded if the profile
        // becomes unavailable (e.g. character logs out) while the dialog is open.
        fileDialogManager.Draw();

        // Before the open Plate is read: a Plate action that opens another Plate (Save as New
        // Plate, Open another Plate, New Plate) takes effect before anything is drawn.
        actionBar.PlateMenu.DrawFrame();

        // It opened in the Basic Editor, which draws it from now on.
        if (!IsOpen)
        {
            return;
        }

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
        if (closeGuard.Advance())
        {
            IsOpen = false;
        }

        DrawEditor(profile);

        // Outside every child region: the one consistent id-stack scope every popup is
        // opened/drawn from — see pendingContextMenuOpenElementId.
        DrawElementContextMenuPopup(profile);
        DrawCanvasResizePromptPopup();
        DrawZoomMenuPopup();
        actionBar.DrawPopups();
        EditorClosePrompt.Draw(closeGuard, () => IsOpen = false);

        // Commits an edit whose widget never reported "deactivated after edit" (see method).
        editorSession.CommitPendingEditsIfIdle(ImGui.IsAnyItemActive());
    }

    private void DrawEditor(ProfileDocument profile)
    {
        DrawToolbar(profile);
        EditorWidgets.UnsupportedElementsNotice(profile);
        ImGui.Separator();

        var spacing = ImGui.GetStyle().ItemSpacing;
        var statusBarHeight = ImGui.GetFrameHeightWithSpacing() + spacing.Y;
        var layout = AdvancedEditorLayout.Compute(ImGui.GetContentRegionAvail(), statusBarHeight, spacing, ImGuiHelpers.GlobalScale);

        DrawLayersPanel(profile, new Vector2(layout.LayersWidth, layout.BodyHeight));
        ImGui.SameLine();
        DrawCanvasPanel(profile, new Vector2(layout.CanvasWidth, layout.BodyHeight));
        ImGui.SameLine();
        DrawInspectorPanel(profile, new Vector2(layout.InspectorWidth, layout.BodyHeight));

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
        actionBar.Draw(profile, () => EditorPreview.Show(editorSession, profile.ProfileId, actionBar.PlateMenu.View), EditorPreview.Tooltip, editorSession.ErrorMessage);

        var toolbarMin = ImGui.GetCursorScreenPos();
        var atCapacity = profile.Elements.Count >= ProfileDocument.MaxElementCount;
        using (ImRaii.Disabled(atCapacity))
        {
            if (ImGui.Button("+ Text"))
            {
                AddTextFromToolbar();
            }

            TutorialAnchorMarks.Mark(TutorialTarget.AdvancedAddText);
            EditorWidgets.Tooltip(atCapacity ? $"At the maximum of {ProfileDocument.MaxElementCount} elements." : "Add a text element");

            ImGui.SameLine();
            if (ImGui.Button("+ Image"))
            {
                OpenImageFileDialog("Add Image", path => editorSession.AddImageElement(path));
            }

            TutorialAnchorMarks.Mark(TutorialTarget.AdvancedAddImage);
            EditorWidgets.Tooltip(atCapacity ? $"At the maximum of {ProfileDocument.MaxElementCount} elements." : "Import an image");
        }

        ToolbarGap();

        if (EditorWidgets.TextToggle("Guides", editorSession.ShowGuides, tooltip: "Show element bounds, selection, and handles on the canvas"))
        {
            editorSession.ShowGuides = !editorSession.ShowGuides;
        }

        TutorialAnchorMarks.Mark(TutorialTarget.AdvancedGuides);

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Snap", editorSession.SnapEnabled, tooltip: "Snap to the canvas and other elements while moving or resizing.\nHold Alt to bypass temporarily."))
        {
            editorSession.SnapEnabled = !editorSession.SnapEnabled;
        }

        TutorialAnchorMarks.Mark(TutorialTarget.AdvancedSnap);
        TutorialAnchorMarks.MarkRect(TutorialTarget.AdvancedToolbar, toolbarMin, ImGui.GetItemRectMax());
    }

    private static void ToolbarGap()
    {
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(EditorWidgets.Scaled(6f), 0f));
        ImGui.SameLine();
    }

    /// <summary>
    /// Bottom status bar: zoom controls plus compact, non-technical canvas/selection info. Never
    /// exposes ZIndex, Revision, or other implementation details.
    /// </summary>
    private void DrawStatusBar(ProfileDocument profile)
    {
        var zoomMin = ImGui.GetCursorScreenPos();
        if (EditorWidgets.IconButton("ZoomOut", FontAwesomeIcon.Minus, "Zoom out"))
        {
            editorSession.SetZoom(editorSession.Zoom / 1.25f);
        }

        ImGui.SameLine(0f, EditorWidgets.Scaled(2f));
        if (ImGui.Button($"{editorSession.Zoom * 100f:0}%##ZoomLevel", new Vector2(EditorWidgets.Scaled(58f), 0f)))
        {
            pendingZoomMenu = true;
        }

        EditorWidgets.Tooltip("Zoom (mouse wheel over the canvas)");

        ImGui.SameLine(0f, EditorWidgets.Scaled(2f));
        if (EditorWidgets.IconButton("ZoomIn", FontAwesomeIcon.Plus, "Zoom in"))
        {
            editorSession.SetZoom(editorSession.Zoom * 1.25f);
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Fit", editorSession.AutoFit, tooltip: "Fit the canvas to the panel (F)"))
        {
            FitCanvas();
        }

        TutorialAnchorMarks.MarkRect(TutorialTarget.AdvancedZoom, zoomMin, ImGui.GetItemRectMax());
        ArtDownloadStatus.DrawInline(canvasArt, renderResources.ArtStore);

        var selected = GetSelectedElement(profile);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.DimTextColor))
        {
            ImGui.TextUnformatted($"   Canvas {profile.CanvasWidth:0} x {profile.CanvasHeight:0}   |   Elements {profile.Elements.Count}/{ProfileDocument.MaxElementCount}   |   {(selected is null ? "Nothing selected" : ProfileElementNames.GetDisplayName(selected))}");
            TutorialAnchorMarks.MarkRect(TutorialTarget.AdvancedStatusBar, zoomMin, ImGui.GetItemRectMax());

            ImGui.SameLine();
            const string hints = "Wheel: zoom   Middle-drag: pan   F: fit   Alt: no snap";
            var hintWidth = ImGui.CalcTextSize(hints).X;
            var hintX = ImGui.GetWindowContentRegionMax().X - hintWidth;
            if (hintX > ImGui.GetCursorPosX() + EditorWidgets.Scaled(16f))
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

    // ---------------------------------------------------------------- keyboard

    /// <summary>
    /// Publishes this frame's focus/text-input/interaction state to
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
