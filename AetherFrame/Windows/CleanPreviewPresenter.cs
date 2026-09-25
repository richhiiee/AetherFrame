using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// Clean Preview — what both editors' Preview shows: the finished Plate exactly as a viewer sees
/// it, over the game, and nothing else. One implementation for the Basic and Advanced editors (the
/// Advanced editor's original, moved here unchanged), driven by the shared
/// <see cref="EditorSession.PreviewActive"/> (entered through <see cref="EditorPreview"/>).
///
/// <para>The editor's own window becomes the preview: it shrinks to exactly the Plate's fitted
/// visual bounds (plus its close control) within the rectangle the editor occupied, and draws
/// nothing of its own — no background, border, title bar, padding or blur
/// (<see cref="CleanPreviewPresentation"/>); the Plate is drawn without the renderer's workspace
/// backdrop. Only the Plate, its artwork and the close control show over the game, and the window
/// takes mouse input only there (see <see cref="CleanPreviewLayout"/> for why it can't be
/// click-through). Leaving it (close control, Escape) puts the editor back exactly where and as
/// large as it was.</para>
///
/// <para>The window calls <see cref="PreDraw"/>, <see cref="PostDraw"/>, <see cref="CaptureEditorRect"/>
/// (first thing in Draw) and, while <see cref="EditorSession.PreviewActive"/>, <see cref="Draw"/>
/// instead of its editor content.</para>
/// </summary>
internal sealed class CleanPreviewPresenter
{
    /// <summary>Clean Preview's presentation: no background, border (see PreDraw), title bar or
    /// chrome; placed and sized by <see cref="CleanPreviewLayout"/>, so not movable or resizable.</summary>
    internal const ImGuiWindowFlags PreviewFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse;

    private readonly Window window;
    private readonly EditorSession editorSession;
    private readonly ProfileService profileService;
    private readonly ProfileRenderResources renderResources;
    private readonly ImGuiWindowFlags editorFlags;

    // The editor's own rectangle, captured every editing frame so the preview fits inside it and
    // the editor returns to it; whether the transparent presentation is in effect (and its style
    // pushes need popping in PostDraw); and the size constraints set aside meanwhile (the editor's
    // minimum size would otherwise inflate the preview).
    private Vector2 editorWindowPos;
    private Vector2 editorWindowSize;
    private bool presentingPreview;
    private bool previewPresentationApplied;
    private CleanPreviewLayout? previewLayout;
    private WindowSizeConstraints? editorSizeConstraints;
    private bool editorAllowsBackgroundBlur = true;

    /// <param name="window">The editor window that becomes the preview.</param>
    /// <param name="editorSession">The shared session (its PreviewActive says whether Preview is showing).</param>
    /// <param name="profileService">The open Plate.</param>
    /// <param name="renderResources">What the renderer draws with.</param>
    /// <param name="editorFlags">The window's own flags while it's the editor.</param>
    internal CleanPreviewPresenter(
        Window window, EditorSession editorSession, ProfileService profileService, ProfileRenderResources renderResources, ImGuiWindowFlags editorFlags)
    {
        this.window = window;
        this.editorSession = editorSession;
        this.profileService = profileService;
        this.renderResources = renderResources;
        this.editorFlags = editorFlags;
    }

    /// <summary>The window's PreDraw: applies (or undoes) the transparent presentation.</summary>
    internal void PreDraw()
    {
        presentingPreview = false;
        if (editorSession.PreviewActive && profileService.CurrentProfile is { } profile && editorWindowSize is { X: > 0f, Y: > 0f }
            && CleanPreviewLayout.Compute(editorWindowPos, editorWindowSize, ProfileVisualBounds.Compute(profile), CleanPreviewLayout.DefaultCloseButtonSize * ImGuiHelpers.GlobalScale) is { } layout)
        {
            if (!previewPresentationApplied)
            {
                editorSizeConstraints = window.SizeConstraints;
                editorAllowsBackgroundBlur = window.AllowBackgroundBlur;
                previewPresentationApplied = true;
            }

            window.AllowBackgroundBlur = CleanPreviewPresentation.AllowBackgroundBlur;
            previewLayout = layout;
            window.SizeConstraints = null;
            ImGui.SetNextWindowPos(layout.WindowPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(layout.WindowSize, ImGuiCond.Always);
            window.Flags = PreviewFlags;
            window.RespectCloseHotkey = false; // Escape belongs to Clean Preview

            // Popped in PostDraw (Dalamud calls it after End on every frame PreDraw ran). No ImRaii
            // scope can span PreDraw and PostDraw, so these stay a manual push/pop pair.
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
            window.SizeConstraints = editorSizeConstraints;
            window.AllowBackgroundBlur = editorAllowsBackgroundBlur;
            ImGui.SetNextWindowPos(editorWindowPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(editorWindowSize, ImGuiCond.Always);
        }

        // Escape belongs to Clean Preview while it's up, so the window-close hotkey stands down then.
        window.Flags = editorFlags;
        window.RespectCloseHotkey = !editorSession.PreviewActive;
    }

    /// <summary>The window's PostDraw: pops what <see cref="PreDraw"/> pushed.</summary>
    internal void PostDraw()
    {
        if (presentingPreview)
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(2);
            presentingPreview = false;
        }
    }

    /// <summary>First thing in the window's Draw: remembers where the editor is while it's the editor.</summary>
    internal void CaptureEditorRect()
    {
        if (!presentingPreview)
        {
            editorWindowPos = ImGui.GetWindowPos();
            editorWindowSize = ImGui.GetWindowSize();
        }
    }

    /// <summary>
    /// The finished profile alone, drawn through exactly the same renderer call as Profile View: no
    /// panels, toolbar, guides, selection, placeholders, or any other editor chrome. An
    /// always-visible close control sits at the top-right of the visual bounds (Escape also exits).
    /// </summary>
    internal void Draw(ProfileDocument profile)
    {
        var available = ImGui.GetContentRegionAvail();
        var windowPos = ImGui.GetWindowPos();
        if (presentingPreview && previewLayout is { } layout)
        {
            ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, windowPos + layout.CanvasOffset, layout.Scale, renderResources, CleanPreviewPresentation.RenderOptions);
            DrawCloseButton(windowPos + layout.CloseButtonOffset, layout.CloseButtonSize);
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
        DrawCloseButton(new Vector2(contentMax.X - buttonSize, contentMin.Y), buttonSize);
    }

    /// <summary>Clean Preview's close control: AetherFrame's shared Close look (<see cref="PresentationControls"/>). Leaves the preview.</summary>
    private void DrawCloseButton(Vector2 min, float size)
    {
        if (PresentationControls.Close("##CleanPreviewClose", min, size, "Exit Preview (Esc)"))
        {
            EditorPreview.Exit(editorSession);
        }
    }
}
