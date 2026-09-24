using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The Plate Viewer: a read-only presentation of one Plate — no inspector, no selection, no
/// editing of any kind, no editor chrome of any kind; the finished Plate is the entire point of
/// this window. For the Plate open in the editors it renders that same live in-memory
/// <see cref="ProfileDocument"/> (so it reflects unsaved edits without a reload); for any other
/// Plate, the Library's saved copy; for a Template preview, the Template's document. Either way via
/// the shared <see cref="ProfileRenderer"/> with element-bounds chrome always off. Viewing never
/// changes a Plate, its dirty state, its undo history, or which Plate is Active.
///
/// <para><b>Presentation.</b> Like Clean Preview, the Plate floats directly over the game: the
/// window is exactly the Plate's composition (its fitted visual bounds — canvas plus any
/// intentional Component overflow) and draws nothing of its own (<see cref="CleanPreviewPresentation"/>).
/// The Plate's own background is drawn as authored. The only chrome is an always-visible Close
/// control; only the composition and that control take mouse input.</para>
///
/// <para><b>Interaction.</b> Left-drag anywhere on the composition moves the viewer (Close takes
/// priority). Ctrl + mouse wheel over it resizes it in steps, around the Plate's center (a plain
/// wheel doesn't). Right-click opens a small menu: size presets, Reset Size, Center on Screen. A
/// short hint explains this once per session. Position and size (<see cref="PlateViewerPlacement"/>)
/// are session UI state — kept when the viewer reopens or shows another Plate — never Plate data.</para>
/// </summary>
internal sealed class ProfileViewWindow : Window, IDisposable
{
    /// <summary>The transparent presentation's window flags: no title bar, background, native
    /// resize/move or scrolling — placed and sized by <see cref="PlateViewerPlacement"/> every frame.</summary>
    internal const ImGuiWindowFlags PresentationFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse;

    private const ImGuiWindowFlags MessageFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize;

    private const string ContextMenuId = "##PlateViewerMenu";

    private readonly ProfileService profileService;
    private readonly PlateLibraryService library;
    private readonly ProfileRenderResources renderResources;

    // Where and how large the viewer shows its Plate: session UI state, kept across reopening and Plates.
    private readonly PlateViewerPlacement placement = new();
    private readonly PlateViewerHint hint = new();

    // The Plate being viewed; null means "whichever Plate is open in the editors".
    private Guid? viewedPlateId;

    // A Template (or any other document that isn't a saved Plate) being previewed. Mutually
    // exclusive with viewedPlateId: whichever was set most recently wins.
    private ProfileDocument? viewedExternalDocument;

    // This frame's presentation (computed in PreDraw, used by Draw, style pushes popped in PostDraw),
    // and the style's own window padding from before the presentation zeroed it, for the context menu.
    private bool presenting;
    private ProfileDocument? presentedDocument;
    private CanvasBounds presentedBounds;
    private PlateViewerLayout? layout;
    private Vector2 styleWindowPadding;

    internal ProfileViewWindow(ProfileService profileService, PlateLibraryService library, ProfileRenderResources renderResources)
        : base("AetherFrame Plate Viewer##ProfileViewWindow")
    {
        this.profileService = profileService;
        this.library = library;
        this.renderResources = renderResources;
    }

    /// <summary>Shows a specific Plate (its live copy if it's the one open in the editors).</summary>
    internal void ShowPlate(Guid plateId)
    {
        viewedExternalDocument = null;
        viewedPlateId = plateId;
        IsOpen = true;
    }

    /// <summary>
    /// Shows a document that isn't a saved Plate — a Template's saved or freshly generated
    /// content. Read-only, exactly like viewing a Plate: never mutates <paramref name="document"/>,
    /// never changes which Plate is Active, and never touches the editors' own state.
    /// </summary>
    internal void ShowDocument(ProfileDocument document)
    {
        viewedPlateId = null;
        viewedExternalDocument = document;
        IsOpen = true;
    }

    /// <summary>Toggles the viewer on the Plate open in the editors.</summary>
    internal void ToggleOpenPlate()
    {
        var showingOpenPlate = viewedExternalDocument is null && (viewedPlateId is null || viewedPlateId == profileService.OpenPlateId);
        if (IsOpen && showingOpenPlate)
        {
            IsOpen = false;
            return;
        }

        viewedExternalDocument = null;
        viewedPlateId = null;
        IsOpen = true;
    }

    /// <summary>What to draw: an external document if one is being previewed, else the live open
    /// document when it's the viewed Plate, else the saved one.</summary>
    private ProfileDocument? ResolveDocument()
    {
        if (viewedExternalDocument is not null)
        {
            return viewedExternalDocument;
        }

        var live = profileService.CurrentProfile;
        if (viewedPlateId is not { } plateId || live?.ProfileId == plateId)
        {
            return live;
        }

        return library.GetSavedDocument(plateId);
    }

    public void Dispose()
    {
    }

    public override void OnClose() => placement.EndDrag();

    public override void PreDraw()
    {
        presenting = false;
        layout = null;
        presentedDocument = ResolveDocument();

        var viewport = ImGui.GetMainViewport();
        if (presentedDocument is { } document)
        {
            var bounds = ProfileVisualBounds.Compute(document);
            if (placement.Update(bounds, CanvasSize(document), viewport.WorkPos, viewport.WorkSize, ControlSize) is { } computed)
            {
                layout = computed;
                presentedBounds = bounds;
                hint.ShowOnce(ImGui.GetTime());
                styleWindowPadding = ImGui.GetStyle().WindowPadding;
                ImGui.SetNextWindowPos(computed.WindowPos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(computed.WindowSize, ImGuiCond.Always);
                Flags = PresentationFlags;
                AllowBackgroundBlur = CleanPreviewPresentation.AllowBackgroundBlur;

                // Popped in PostDraw (Dalamud calls it after End on every frame PreDraw ran).
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, CleanPreviewPresentation.WindowPadding);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, CleanPreviewPresentation.WindowBorderSize);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, CleanPreviewPresentation.BackgroundColor);
                ImGui.PushStyleColor(ImGuiCol.ChildBg, CleanPreviewPresentation.BackgroundColor);
                presenting = true;
                return;
            }
        }

        // Nothing to present: a small ordinary message window, centered, with its own Close.
        placement.EndDrag();
        ImGui.SetNextWindowPos(viewport.WorkPos + (viewport.WorkSize / 2f), ImGuiCond.Always, new Vector2(0.5f));
        Flags = MessageFlags;
        AllowBackgroundBlur = true;
    }

    public override void PostDraw()
    {
        if (presenting)
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(2);
            presenting = false;
        }
    }

    public override void Draw()
    {
        if (presenting && layout is { } current && presentedDocument is { } profile)
        {
            DrawPresentation(profile, current);
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(presentedDocument is not null ? "This Plate can't be shown."
            : viewedPlateId is null && viewedExternalDocument is null ? "No Plate is open." : "This Plate isn't available.");
        ImGui.SameLine();
        var size = ImGui.GetFrameHeight();
        if (PresentationControls.Close("##CloseProfileView", ImGui.GetCursorScreenPos(), size, "Close"))
        {
            IsOpen = false;
        }
    }

    private static float ControlSize => PlateViewerLayout.DefaultControlSize * ImGuiHelpers.GlobalScale;

    private static Vector2 CanvasSize(ProfileDocument document) => new(Math.Max(0f, document.CanvasWidth), Math.Max(0f, document.CanvasHeight));

    private void DrawPresentation(ProfileDocument profile, PlateViewerLayout current)
    {
        var windowPos = ImGui.GetWindowPos();
        var mouse = ImGui.GetMousePos();
        var io = ImGui.GetIO();
        var viewport = ImGui.GetMainViewport();
        var hovered = ImGui.IsWindowHovered();
        var region = hovered ? current.HitTest(mouse) : PlateViewerRegion.None;

        // Move: a left press on the composition (never on Close) moves the whole viewer until release.
        if (!placement.IsDragging && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && placement.TryBeginDrag(region, mouse))
        {
            hint.Dismiss();
        }

        if (placement.IsDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                placement.DragTo(mouse);
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            }
            else
            {
                placement.EndDrag();
            }
        }

        // Resize: Ctrl + wheel only (a plain wheel does nothing here).
        if (hovered && io.KeyCtrl && io.MouseWheel != 0f && placement.ZoomByWheel(io.MouseWheel, presentedBounds, CanvasSize(profile), viewport.WorkSize, current.ControlSize))
        {
            hint.Dismiss();
        }

        // Options: right-click anywhere on the viewer (never starts a move: moves are left-button only).
        if (region != PlateViewerRegion.None && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            hint.Dismiss();
            ImGui.OpenPopup(ContextMenuId);
        }

        ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, windowPos + current.CanvasOffset, current.Scale, renderResources, CleanPreviewPresentation.RenderOptions);

        if (PresentationControls.Close("##ViewerClose", windowPos + current.CloseOffset, current.ControlSize, "Close (Esc)"))
        {
            placement.EndDrag();
            IsOpen = false;
        }

        DrawContextMenu(profile, current);
        DrawHint(windowPos, current);
    }

    private void DrawContextMenu(ProfileDocument profile, PlateViewerLayout current)
    {
        // The presentation zeroed WindowPadding for the viewer itself; the menu gets the normal padding.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, styleWindowPadding);
        try
        {
            if (!ImGui.BeginPopup(ContextMenuId))
            {
                return;
            }

            var viewport = ImGui.GetMainViewport();
            var canvasSize = CanvasSize(profile);
            ImGui.TextDisabled($"Size: {placement.Percent}%");
            ImGui.Separator();
            foreach (var percent in PlateViewerPlacement.PresetPercents)
            {
                if (ImGui.MenuItem($"{percent}%", string.Empty, placement.Percent == percent))
                {
                    placement.SetPercent(percent, presentedBounds, canvasSize, viewport.WorkSize, current.ControlSize);
                }
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Reset Size"))
            {
                placement.ResetSize(presentedBounds, canvasSize, viewport.WorkSize, current.ControlSize);
            }

            if (ImGui.MenuItem("Center on Screen"))
            {
                placement.CenterOnScreen(presentedBounds, viewport.WorkPos, viewport.WorkSize);
            }

            ImGui.EndPopup();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>The once-per-session usage hint: a small pill at the composition's bottom center, on
    /// the foreground layer (so it neither takes input nor gets clipped), fading out after a few seconds.</summary>
    private void DrawHint(Vector2 windowPos, PlateViewerLayout current)
    {
        var opacity = hint.Opacity(ImGui.GetTime());
        if (opacity <= 0f)
        {
            return;
        }

        var textSize = ImGui.CalcTextSize(PlateViewerHint.Text);
        var padding = new Vector2(10f, 5f) * ImGuiHelpers.GlobalScale;
        var size = textSize + (padding * 2f);
        var bottomCenter = windowPos + new Vector2(current.WindowSize.X / 2f, current.WindowSize.Y);
        var min = bottomCenter - new Vector2(size.X / 2f, size.Y + (12f * ImGuiHelpers.GlobalScale));

        var drawList = ImGui.GetForegroundDrawList();
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(CleanPreviewPresentation.HintBacking with { W = CleanPreviewPresentation.HintBacking.W * opacity }), size.Y / 2f);
        drawList.AddText(min + padding, ImGui.GetColorU32(CleanPreviewPresentation.HintText with { W = CleanPreviewPresentation.HintText.W * opacity }), PlateViewerHint.Text);
    }
}
