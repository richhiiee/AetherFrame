using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The Plate Viewer: a read-only presentation of one Plate — no inspector, no selection, no
/// editing of any kind, no editor chrome of any kind; the finished Plate is the entire point of
/// this window. For an explicitly requested Plate that's open in the editors it renders that same
/// live in-memory <see cref="ProfileDocument"/> (so it reflects unsaved edits without a reload); for
/// any other Plate, and always for the default Active Plate request, the Library's saved copy; for
/// a Template preview, the Template's document. Either way via
/// the shared <see cref="ProfileRenderer"/> with element-bounds chrome always off. Viewing never
/// changes a Plate, its dirty state, its undo history, or which Plate is Active.
///
/// <para><b>What it shows</b> (<see cref="PlateViewerTarget"/>): an explicitly requested Plate or
/// Template document; otherwise — the default request, e.g. <c>/aetherframe view</c> — the
/// logged-in character's Active Plate, always as last saved (never the editors' unsaved state). With
/// no Active Plate it shows an intentional empty state pointing at My Plates, never some other Plate.</para>
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
    private readonly ActivePlateResolver activePlates;
    private readonly ProfileRenderResources renderResources;
    private readonly Action openMyPlates;

    // Where and how large the viewer shows its Plate: session UI state, kept across reopening and Plates.
    private readonly PlateViewerPlacement placement = new();
    private readonly PlateViewerHint hint = new();

    // What the viewer was last asked to show; starts as the default (Active Plate) request.
    private readonly PlateViewerTarget target = new();

    // This frame's resolved content and presentation (computed in PreDraw, used by Draw, style
    // pushes popped in PostDraw), and the style's own window padding from before the presentation
    // zeroed it, for the context menu.
    private bool presenting;
    private PlateViewerContent content;
    private ProfileDocument? presentedDocument;
    private CanvasBounds presentedBounds;
    private PlateViewerLayout? layout;
    private Vector2 styleWindowPadding;

    /// <param name="openMyPlates">The No Active Plate empty state's Open My Plates action.</param>
    internal ProfileViewWindow(
        ProfileService profileService, PlateLibraryService library, ActivePlateResolver activePlates, ProfileRenderResources renderResources, Action openMyPlates)
        : base("AetherFrame Plate Viewer##ProfileViewWindow")
    {
        this.profileService = profileService;
        this.library = library;
        this.activePlates = activePlates;
        this.renderResources = renderResources;
        this.openMyPlates = openMyPlates;
    }

    /// <summary>
    /// The default viewing request: shows the logged-in character's Active Plate — whichever it is
    /// while the viewer stays open — or the No Active Plate empty state.
    /// </summary>
    internal void ShowActivePlate()
    {
        target.RequestActivePlate();
        IsOpen = true;
        BringToFront();
    }

    /// <summary>Shows a specific Plate (its live copy if it's the one open in the editors).</summary>
    internal void ShowPlate(Guid plateId)
    {
        target.RequestPlate(plateId);
        IsOpen = true;
    }

    /// <summary>
    /// Shows a document that isn't a saved Plate — a Template's saved or freshly generated
    /// content. Read-only, exactly like viewing a Plate: never mutates <paramref name="document"/>,
    /// never changes which Plate is Active, and never touches the editors' own state.
    /// </summary>
    internal void ShowDocument(ProfileDocument document)
    {
        target.RequestDocument(document);
        IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void OnClose() => placement.EndDrag();

    public override void PreDraw()
    {
        presenting = false;
        layout = null;
        content = target.Resolve(activePlates, library.GetSavedDocument, profileService.CurrentProfile);
        presentedDocument = content.Document;

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

        if (content.State == PlateViewerState.NoActivePlate)
        {
            DrawNoActivePlate();
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(content.State switch
        {
            PlateViewerState.Showing => "This Plate can't be shown.",
            PlateViewerState.NoCharacter => "Log in to a character to view its Active Plate.",
            PlateViewerState.LibraryUnavailable => "My Plates isn't available right now.",
            _ => "This Plate isn't available.",
        });
        ImGui.SameLine();
        DrawMessageClose();
    }

    /// <summary>The default request's intentional empty state: this character has no Active Plate.</summary>
    private void DrawNoActivePlate()
    {
        // Heading with Close at the right edge of the fixed-width explanation below it.
        const string heading = "No Active Plate";
        var left = ImGui.GetCursorPosX();
        var width = 300f * ImGuiHelpers.GlobalScale;
        var headingEnd = left + ImGui.CalcTextSize(heading).X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(heading);
        ImGui.SameLine(Math.Max(headingEnd, left + width - ImGui.GetFrameHeight()));
        DrawMessageClose();

        using (ImRaii.TextWrapPos(left + width))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextUnformatted("Choose an Active Plate in My Plates to make it your default AetherFrame Plate.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Open My Plates"))
        {
            // Choosing one there is enough: the viewer never reopens on its own because Active changed.
            IsOpen = false;
            openMyPlates();
        }
    }

    private void DrawMessageClose()
    {
        if (PresentationControls.Close("##CloseProfileView", ImGui.GetCursorScreenPos(), ImGui.GetFrameHeight(), "Close"))
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
