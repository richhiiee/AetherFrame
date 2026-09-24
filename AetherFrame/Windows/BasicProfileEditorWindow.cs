using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
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
/// The Basic editor: choose what to edit, edit it, always see the result. A category navigator
/// (Design, Portrait, Identity, Details, Playstyle, Message) picks what the inspector shows — one
/// category at a time, its title and summary pinned above its controls — beside an always-visible
/// live preview (which a click on a section also navigates from). On narrower windows the
/// navigator becomes a wrapping category strip and the preview moves above the inspector.
/// The categories live in partial files (.Design, .Identity, .Sections), text styling in .Styling.
///
/// Edits the same <see cref="ProfileDocument"/> as <see cref="ProfileEditorWindow"/>, through the
/// same shared <see cref="EditorSession"/>, via <see cref="BasicEditorSession"/>'s role-based
/// lookups — so undo/redo and dirty state always agree between the two windows, and switching
/// modes never resets, reloads, or duplicates Plate data. Opening it changes nothing; neither does
/// navigating.
/// </summary>
internal sealed partial class BasicProfileEditorWindow : Window, IDisposable, IEditorSurface
{
    private const float NavigatorWidth = 150f;
    private const float InspectorMinWidth = 340f;
    private const float InspectorMaxWidth = 500f;

    // A press that moves further than this is a pan, not a click on a section.
    private const float PreviewClickTolerance = 4f;

    private const string ResetLayoutPopupId = "Reset Basic Layout##AetherFrameResetBasicLayout";

    private static readonly Vector4 CustomizedColor = new(0.6f, 0.8f, 1f, 0.9f);
    private static readonly Vector4 SubheadingColor = new(0.75f, 0.82f, 1f, 0.95f);

    private static readonly string[] PreviewZoomLabels = ["Fit", "150%", "200%"];
    private static readonly string[] PreviewZoomTooltips =
    [
        "The whole Plate.",
        "The Plate at 1.5x. Scroll, or drag with the mouse, to look around.",
        "The Plate at 2x. Scroll, or drag with the mouse, to look around.",
    ];

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly BasicEditorSession basicEditorSession;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileRenderResources renderResources;
    private readonly FileDialogManager fileDialogManager;
    private readonly GameTitleCatalog titleCatalog;
    private readonly JobCatalog jobCatalog;
    private readonly Action openAdvancedEditor;
    private readonly Action openLibrary;
    private readonly EditorSurfaceCoordinator surfaces;
    private readonly BackgroundStylePanel backgroundPanel;

    // Which category is shown, Focus Preview, and zoom: view state only, never part of the Plate.
    private readonly BasicEditorNavigation navigation = new();

    // Reused by the preview's click-to-navigate hit test (render thread only).
    private readonly List<ProfileElement> previewHitBuffer = new(ProfileDocument.MaxElementCount);
    private bool previewDragged;

    // Requested from inside a child window; opened and drawn at window level, where the popup's ID
    // stack is always the same.
    private bool pendingResetLayoutConfirm;

    internal BasicProfileEditorWindow(
        ProfileService profileService,
        EditorSession editorSession,
        BasicEditorSession basicEditorSession,
        ImageTextureCache imageTextureCache,
        ProfileRenderResources renderResources,
        FileDialogManager fileDialogManager,
        GameTitleCatalog titleCatalog,
        JobCatalog jobCatalog,
        Action openAdvancedEditor,
        Action openLibrary,
        EditorSurfaceCoordinator surfaces)
        : base("AetherFrame Basic Editor##BasicProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(1180, 760);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.profileService = profileService;
        this.editorSession = editorSession;
        this.basicEditorSession = basicEditorSession;
        this.imageTextureCache = imageTextureCache;
        this.renderResources = renderResources;
        this.fileDialogManager = fileDialogManager;
        this.titleCatalog = titleCatalog;
        this.jobCatalog = jobCatalog;
        this.openAdvancedEditor = openAdvancedEditor;
        this.openLibrary = openLibrary;
        this.surfaces = surfaces;
        backgroundPanel = new BackgroundStylePanel(editorSession, renderResources, OpenImageFileDialog);
    }

    public void Dispose()
    {
    }

    /// <summary>One editing surface at a time: the Advanced editor hands over if it's open.</summary>
    public override void OnOpen() => surfaces.NotifyOpened(EditorSurfaceKind.Basic);

    /// <inheritdoc/>
    public void Show()
    {
        IsOpen = true;
        BringToFront();
    }

    /// <summary>Handing editing to the Advanced editor, which continues the same session.</summary>
    public void CloseForHandoff() => IsOpen = false;

    public override void OnClose()
    {
        fileDialogManager.Reset();
    }

    public override void Draw()
    {
        // Drawn unconditionally so an in-progress file pick isn't stranded if the Plate
        // becomes unavailable (e.g. it's deleted from My Plates) while the dialog is open.
        fileDialogManager.Draw();

        // Before the null check, so closing or deleting the open Plate also resets the session.
        editorSession.SyncWithCurrentProfile();

        var profile = profileService.CurrentProfile;
        navigation.TrackPlate(profile?.ProfileId);
        if (profile is null)
        {
            ImGui.TextUnformatted("No Plate is open.");
            ImGui.TextDisabled("Choose a Plate to edit in My Plates.");
            if (ImGui.Button("Open My Plates"))
            {
                openLibrary();
            }

            return;
        }

        // Opening Basic mode never creates or changes anything: section elements (and the
        // Basic settings) are only created by the first explicit edit that needs them.
        basicEditorSession.Identity.RefineLayout();

        DrawToolbar(profile);
        EditorWidgets.UnsupportedElementsNotice(profile);
        ImGui.Separator();

        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var footerHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y + style.WindowPadding.Y
            + (basicEditorSession.ErrorMessage is null ? 0f : ImGui.GetTextLineHeightWithSpacing());
        var body = ImGui.GetContentRegionAvail() - new Vector2(0f, footerHeight);
        body.Y = Math.Max(body.Y, 160f * scale);

        if (navigation.FocusPreview)
        {
            // Focus Preview: the Plate gets the whole editor. "Back to Editing" returns to the
            // same category with every control exactly as it was.
            DrawPreview(profile, new Vector2(-1f, body.Y));
        }
        else
        {
            switch (BasicEditorView.ChooseLayout(body.X, scale))
            {
                case BasicEditorLayoutMode.ThreeColumn:
                {
                    var inspectorWidth = Math.Clamp(body.X * 0.36f, InspectorMinWidth * scale, InspectorMaxWidth * scale);
                    DrawNavigator(profile, new Vector2(NavigatorWidth * scale, body.Y));
                    ImGui.SameLine();
                    DrawInspector(profile, new Vector2(inspectorWidth, body.Y), withCategoryStrip: false);
                    ImGui.SameLine();
                    DrawPreview(profile, new Vector2(0f, body.Y));
                    break;
                }

                case BasicEditorLayoutMode.TwoColumn:
                {
                    var inspectorWidth = Math.Clamp(body.X * 0.45f, InspectorMinWidth * scale, InspectorMaxWidth * scale);
                    DrawInspector(profile, new Vector2(inspectorWidth, body.Y), withCategoryStrip: true);
                    ImGui.SameLine();
                    DrawPreview(profile, new Vector2(0f, body.Y));
                    break;
                }

                default:
                {
                    // Narrow: categories first (wrapping, never cut off), the preview at the
                    // canvas' own aspect, then the inspector.
                    DrawCategoryStrip(profile);
                    var remaining = ImGui.GetContentRegionAvail().Y - footerHeight;
                    var previewHeight = Math.Clamp(
                        (body.X * profile.CanvasHeight / Math.Max(1f, profile.CanvasWidth)) + ImGui.GetFrameHeightWithSpacing(),
                        140f * scale,
                        remaining * 0.45f);
                    DrawPreview(profile, new Vector2(-1f, previewHeight));
                    DrawInspector(profile, new Vector2(-1f, Math.Max(120f * scale, remaining - previewHeight - style.ItemSpacing.Y)), withCategoryStrip: false);
                    break;
                }
            }
        }

        DrawFooter();
        DrawResetLayoutPopup();

        // Commits a color/slider edit whose widget never reported "deactivated after edit".
        editorSession.CommitPendingEditsIfIdle(ImGui.IsAnyItemActive());
    }

    private void DrawToolbar(ProfileDocument profile)
    {
        if (EditorWidgets.IconButton("MyPlates", FontAwesomeIcon.ThLarge, "My Plates"))
        {
            openLibrary();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(profile.Name);

        const string advancedLabel = "Advanced Editor";
        var buttonWidth = ImGui.CalcTextSize(advancedLabel).X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttonWidth));
        if (ImGui.Button(advancedLabel))
        {
            openAdvancedEditor();
        }

        EditorWidgets.Tooltip("Freeform editing of this same Plate. Your unsaved changes and undo history come along.");
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>The wide layout's vertical category list; the selected one is highlighted.</summary>
    private void DrawNavigator(ProfileDocument profile, Vector2 size)
    {
        using var child = ImRaii.Child("##BasicNavigator", size, true);
        if (!child.Success)
        {
            return;
        }

        var rowHeight = ImGui.GetFrameHeight() * 1.3f;
        foreach (var category in BasicEditorView.Categories)
        {
            var status = BasicEditorView.StatusOf(profile, category);
            var selected = navigation.Selected == category;
            using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.AccentColor, selected))
            {
                if (ImGui.Selectable($"{BasicEditorView.Title(category).ToUpperInvariant()}##Nav{category}", selected, ImGuiSelectableFlags.None, new Vector2(0f, rowHeight)))
                {
                    navigation.Select(category);
                }
            }

            StatusTooltip(status);
            DrawStatusMarker(status);
        }
    }

    /// <summary>The narrower layouts' category selector: buttons that wrap onto more rows rather than run off screen.</summary>
    private void DrawCategoryStrip(ProfileDocument profile)
    {
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail().X;
        var rowUsed = 0f;

        foreach (var category in BasicEditorView.Categories)
        {
            var label = BasicEditorView.Title(category);
            var width = ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f) + (8f * ImGuiHelpers.GlobalScale);
            if (rowUsed > 0f)
            {
                if (rowUsed + style.ItemSpacing.X + width <= available)
                {
                    ImGui.SameLine();
                    rowUsed += style.ItemSpacing.X;
                }
                else
                {
                    rowUsed = 0f;
                }
            }

            var status = BasicEditorView.StatusOf(profile, category);
            if (EditorWidgets.TextToggle($"{label}##Strip{category}", navigation.Selected == category, new Vector2(width, 0f)))
            {
                navigation.Select(category);
            }

            StatusTooltip(status);
            DrawStatusDot(status);
            rowUsed += width;
        }

        ImGui.Spacing();
    }

    private static void StatusTooltip(BasicCategoryStatus status)
    {
        if (!status.IsQuiet)
        {
            EditorWidgets.Tooltip(string.Join("\n", BasicEditorView.Describe(status)));
        }
    }

    /// <summary>
    /// One restrained marker at the end of a navigator row: a warning for something that needs
    /// attention (an overlap, unsupported content), otherwise a quiet hint for Advanced
    /// customization or a hidden section. Nothing for a quiet category.
    /// </summary>
    private static void DrawStatusMarker(BasicCategoryStatus status)
    {
        if (status.IsQuiet)
        {
            return;
        }

        var (icon, color) = status.NeedsAttention ? (FontAwesomeIcon.ExclamationTriangle, EditorWidgets.WarningColor)
            : status.Customized ? (FontAwesomeIcon.PencilAlt, CustomizedColor with { W = 0.7f })
            : (FontAwesomeIcon.EyeSlash, EditorWidgets.DimTextColor);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        string glyph;
        Vector2 glyphSize;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            glyph = EditorWidgets.GetIconString(icon);
            glyphSize = ImGui.CalcTextSize(glyph);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(max.X - glyphSize.X - ImGui.GetStyle().FramePadding.X, min.Y + ((max.Y - min.Y - glyphSize.Y) / 2f)),
                ImGui.GetColorU32(color),
                glyph);
        }
    }

    /// <summary>The strip's marker: a small dot in the button's corner (warning or quiet hint).</summary>
    private static void DrawStatusDot(BasicCategoryStatus status)
    {
        if (status.IsQuiet)
        {
            return;
        }

        var color = status.NeedsAttention ? EditorWidgets.WarningColor
            : status.Customized ? CustomizedColor
            : EditorWidgets.DimTextColor;
        var radius = 3f * ImGuiHelpers.GlobalScale;
        var max = ImGui.GetItemRectMax();
        var min = ImGui.GetItemRectMin();
        ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(max.X - (radius * 2f), min.Y + (radius * 2f)), radius, ImGui.GetColorU32(color));
    }

    // ---------------------------------------------------------------- inspector

    /// <summary>
    /// The selected category: its title and summary stay pinned at the top (so it's always clear
    /// what's being edited) while its controls scroll beneath.
    /// </summary>
    private void DrawInspector(ProfileDocument profile, Vector2 size, bool withCategoryStrip)
    {
        using var frame = ImRaii.Child("##BasicInspector", size, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!frame.Success)
        {
            return;
        }

        if (withCategoryStrip)
        {
            DrawCategoryStrip(profile);
        }

        var category = navigation.Selected;
        ImGui.TextColored(EditorWidgets.AccentColor, BasicEditorView.Title(category).ToUpperInvariant());
        foreach (var line in BasicEditorView.SummaryOf(profile, category))
        {
            ImGui.TextDisabled(line);
        }

        ImGui.Separator();

        using var body = ImRaii.Child("##BasicInspectorBody", new Vector2(-1f, -1f), false);
        if (!body.Success)
        {
            return;
        }

        using var id = ImRaii.PushId((int)category);
        switch (category)
        {
            case BasicEditorCategory.Design:
                DrawDesignCategory(profile);
                break;
            case BasicEditorCategory.Portrait:
                DrawPortraitCategory(profile);
                break;
            case BasicEditorCategory.Identity:
                DrawIdentityCategory(profile);
                break;
            case BasicEditorCategory.Details:
                DrawDetailsCategory(profile);
                break;
            case BasicEditorCategory.Playstyle:
                DrawPlaystyleCategory(profile);
                break;
            case BasicEditorCategory.Message:
                DrawMessageCategory(profile);
                break;
        }

        ImGui.Spacing();
    }

    /// <summary>A small heading inside a category (not collapsible).</summary>
    private static void Subheading(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(SubheadingColor, text);
    }

    /// <summary>A field's name with its Show toggle at the right edge of the same row.</summary>
    private void FieldHeader(ProfileDocument profile, string title, BasicSection section, string showLabel = "Show", bool showEnabled = true)
    {
        Subheading(title);
        var labelWidth = ImGui.CalcTextSize(showLabel).X;
        var checkboxWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + labelWidth;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - checkboxWidth));
        DrawShowToggle(profile, section, showLabel, showEnabled);
    }

    /// <summary>The section's Show checkbox. Hiding keeps every value; showing a missing section creates it.</summary>
    private void DrawShowToggle(ProfileDocument profile, BasicSection section, string label = "Show", bool enabled = true)
    {
        var visible = BasicSections.IsVisible(profile, section);
        using (ImRaii.Disabled(!enabled))
        {
            if (ImGui.Checkbox($"{label}##Show{section}", ref visible))
            {
                basicEditorSession.SetSectionVisible(section, visible);
            }
        }

        ToolTip("Hidden sections keep their content; show them again any time.");
    }

    /// <summary>One layout row of a category's Layout block: a section (or sections placed as one unit).</summary>
    /// <param name="Title">Its name.</param>
    /// <param name="Sections">The sections it covers (reclaimed together).</param>
    /// <param name="ResetLabel">Its reset button ("Reset Portrait"...).</param>
    private readonly record struct LayoutRow(string Title, BasicSection[] Sections, string ResetLabel);

    /// <summary>
    /// A category's Layout block: for each row, whether it follows the Adventure Plate layout or
    /// was customized in the Advanced Editor (then with Apply Layout), and its Reset; plus any
    /// overlap involving these sections, with the explicit fix. Every action is one undo step.
    /// </summary>
    private void DrawLayoutBlock(ProfileDocument profile, params LayoutRow[] rows)
    {
        Subheading("Layout");
        foreach (var row in rows)
        {
            if (!row.Sections.Any(s => s == BasicSection.Identity || BasicSections.Exists(profile, s)))
            {
                continue;
            }

            using var id = ImRaii.PushId((int)row.Sections[0]);
            var customized = row.Sections.Any(s => BasicEditorSession.IsSectionCustomized(profile, s));
            ImGui.AlignTextToFramePadding();
            if (rows.Length > 1)
            {
                ImGui.TextUnformatted(row.Title);
                ImGui.SameLine();
            }

            if (customized)
            {
                ImGui.TextColored(CustomizedColor, "Customized in Advanced Editor");
                ToolTip("Moved or resized outside Basic mode (or placed before Basic layouts), so Basic keeps it\nexactly where it is, even when the orientation changes. Text and style edits still apply.");
            }
            else
            {
                ImGui.TextDisabled("Follows the layout");
            }

            if (customized)
            {
                if (ImGui.SmallButton("Apply Layout"))
                {
                    basicEditorSession.ApplySectionLayout(row.Sections);
                }

                ToolTip("Moves it back into the Adventure Plate Classic layout.\nIts content and style are kept. Undoable.");
                ImGui.SameLine();
            }

            if (ImGui.SmallButton(row.ResetLabel))
            {
                basicEditorSession.ResetSection(row.Sections);
            }

            ToolTip("Restores its default Adventure Plate Classic placement and style.\nIts content is kept, and nothing else changes. Undoable.");
        }

        var sections = rows.SelectMany(r => r.Sections).ToArray();
        DrawOverlapWarnings(profile, section => BasicSections.LayoutGroupOf(section).Any(s => Array.IndexOf(sections, s) >= 0));
    }

    /// <summary>
    /// Overlapping sections (a customized one left in place while others followed an orientation
    /// change): a warning, and for each customized side the explicit fix. Nothing moves on its own.
    /// </summary>
    private void DrawOverlapWarnings(ProfileDocument profile, Func<BasicSection, bool> relevant)
    {
        foreach (var (first, second) in BasicEditorSession.FindOverlaps(profile))
        {
            if (!relevant(first) && !relevant(second))
            {
                continue;
            }

            ImGui.TextColored(EditorWidgets.WarningColor, $"{GroupTitle(first)} overlaps {GroupTitle(second)}.");
            foreach (var section in (ReadOnlySpan<BasicSection>)[first, second])
            {
                if (!BasicEditorSession.IsSectionCustomized(profile, section))
                {
                    continue;
                }

                if (ImGui.SmallButton($"Move {GroupTitle(section)} into the layout##Overlap{first}{second}{section}"))
                {
                    basicEditorSession.ApplySectionLayout(BasicSections.LayoutGroupOf(section));
                }

                ToolTip($"{GroupTitle(section)} was customized in the Advanced Editor, so it stayed where you placed it.\nThis puts it back into the Adventure Plate Classic layout. Its content and style are kept. Undoable.");
            }
        }
    }

    /// <summary>A layout group's name as the editor shows it (Favorite Job and Level are one group).</summary>
    private static string GroupTitle(BasicSection section) =>
        section is BasicSection.Job or BasicSection.Level ? "Favorite Job & Level" : BasicSections.Get(section).Title;

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// The live preview: the shared renderer's finished rendering — exactly what the Plate Viewer
    /// shows, with no placeholders, guides, or other editor-only overlays. Its toolbar only changes
    /// how large the preview is drawn (Fit, 150%, 200%, Focus); the Plate itself is never touched.
    /// Clicking a section opens its category; dragging while zoomed pans.
    /// </summary>
    private void DrawPreview(ProfileDocument profile, Vector2 size)
    {
        using var outer = ImRaii.Child("##BasicPreviewArea", size, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!outer.Success)
        {
            return;
        }

        DrawPreviewToolbar();

        var zoomed = navigation.Zoom != PreviewZoom.Fit;
        var flags = zoomed ? ImGuiWindowFlags.HorizontalScrollbar : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        using var child = ImRaii.Child("##BasicPreview", new Vector2(-1f, -1f), true, flags);
        if (!child.Success)
        {
            return;
        }

        // Fit is measured against the visible area without scrollbars, so zoom levels are exact
        // multiples of the fitted size.
        var available = ImGui.GetContentRegionAvail();
        if (zoomed)
        {
            available = ImGui.GetWindowSize() - (ImGui.GetStyle().WindowPadding * 2f);
        }

        // Fits the Plate's visual bounds: its canvas plus any intentional Component overflow.
        var fit = BasicEditorView.ComputePreview(available, ProfileVisualBounds.Compute(profile), navigation.Zoom);
        var scale = fit.Scale;
        if (scale <= 0f)
        {
            return;
        }

        var cursor = ImGui.GetCursorScreenPos();
        var canvasOrigin = cursor + fit.CanvasOffset;
        ImGui.InvisibleButton("##PreviewCanvas", Vector2.Max(available, fit.Size));
        HandlePreviewInput(profile, canvasOrigin, scale, zoomed);

        ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, canvasOrigin, scale, renderResources, ProfileRenderOptions.Finished);
    }

    /// <summary>Pan (drag while zoomed) and click-to-navigate on the preview. Never edits the Plate.</summary>
    private void HandlePreviewInput(ProfileDocument profile, Vector2 canvasOrigin, float scale, bool zoomed)
    {
        if (ImGui.IsItemActivated())
        {
            previewDragged = false;
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, PreviewClickTolerance * ImGuiHelpers.GlobalScale))
        {
            previewDragged = true;
            if (zoomed)
            {
                var delta = ImGui.GetIO().MouseDelta;
                ImGui.SetScrollX(ImGui.GetScrollX() - delta.X);
                ImGui.SetScrollY(ImGui.GetScrollY() - delta.Y);
            }
        }

        var logicalMouse = (ImGui.GetMousePos() - canvasOrigin) / scale;
        if (ImGui.IsItemDeactivated() && !previewDragged
            && BasicEditorView.CategoryAt(profile, logicalMouse, previewHitBuffer) is { } clicked)
        {
            navigation.Select(clicked);
        }

        if (ImGui.IsItemHovered() && !ImGui.IsItemActive()
            && BasicEditorView.CategoryAt(profile, logicalMouse, previewHitBuffer) is { } hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip($"Edit {BasicEditorView.Title(hovered)}");
        }
    }

    private void DrawPreviewToolbar()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Preview");
        ImGui.SameLine();

        for (var i = 0; i < PreviewZoomLabels.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, 2f);
            }

            var zoom = (PreviewZoom)i;
            if (EditorWidgets.TextToggle($"{PreviewZoomLabels[i]}##PreviewZoom", navigation.Zoom == zoom, tooltip: PreviewZoomTooltips[i]))
            {
                navigation.Zoom = zoom;
            }
        }

        ImGui.SameLine();
        var focus = navigation.FocusPreview;
        if (EditorWidgets.TextToggle(focus ? "Back to Editing##FocusPreview" : "Focus##FocusPreview", focus,
                tooltip: focus ? "Show the editing controls again." : "Give the whole editor to the preview for a close look.\nNothing changes on your Plate."))
        {
            navigation.ToggleFocusPreview();
        }
    }

    // ---------------------------------------------------------------- footer

    private void DrawFooter()
    {
        ImGui.Separator();

        // The same shared history as the Advanced editor: undoing here or there is identical.
        using (ImRaii.Disabled(!editorSession.CanUndo))
        {
            if (ImGui.Button("Undo"))
            {
                editorSession.Undo();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!editorSession.CanRedo))
        {
            if (ImGui.Button("Redo"))
            {
                editorSession.Redo();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(profileService.IsBusy))
        {
            if (ImGui.Button("Save Plate"))
            {
                editorSession.SaveProfile();
            }
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (profileService.IsBusy)
        {
            ImGui.TextUnformatted("Saving...");
        }
        else if (editorSession.IsDirty)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, "Unsaved changes");
        }
        else
        {
            ImGui.TextColored(EditorWidgets.SuccessColor, "Saved");
        }

        if (basicEditorSession.ErrorMessage is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
    }

    private void DrawResetLayoutPopup()
    {
        if (pendingResetLayoutConfirm)
        {
            pendingResetLayoutConfirm = false;
            ImGui.OpenPopup(ResetLayoutPopupId);
        }

        var open = true;
        if (!ImGui.BeginPopupModal(ResetLayoutPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TextUnformatted("Reset the Basic layout?");
        ImGui.Spacing();
        ImGui.TextUnformatted("Resets:");
        BulletText("the orientation, back to Normal");
        BulletText("the position and size of every Basic section: portrait, identity header,\nhome world, favorite job, level, free company, playstyle, active hours, message");
        BulletText("sections customized in the Advanced Editor, too");
        ImGui.Spacing();
        ImGui.TextUnformatted("Keeps:");
        BulletText("all text, your portrait and other images, styles and colors, hidden sections");
        BulletText("everything you added in the Advanced Editor, exactly as it is");
        ImGui.Spacing();
        Hint("You can undo this.");
        ImGui.Separator();

        var buttonSize = new Vector2(120f * ImGuiHelpers.GlobalScale, 0f);
        if (ImGui.Button("Reset Layout", buttonSize))
        {
            basicEditorSession.ResetBasicLayout();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        static void BulletText(string text)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(text);
        }
    }

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

    private static void Hint(string text) => EditorWidgets.Hint(text);

    private static void ToolTip(string text) => EditorWidgets.Tooltip(text);
}
