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
/// The Basic editor: choose what to edit, edit it, always see the result. The shared
/// <see cref="EditorActionBar"/> (the same one the Advanced editor has) sits on top; its Preview is
/// the same Clean Preview as the Advanced editor's (<see cref="CleanPreviewPresenter"/>). A navigation
/// rail (Style, Portrait, Identity, Details, Message) picks what the inspector shows — one
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
    // The navigation rail (unscaled pixels): wide enough for every category name with room to spare
    // beside its status marker; rows of one height, a small gap between them, text inset from the
    // selection indicator.
    private const float NavigatorWidth = 176f;
    private const float RailPadding = 8f;
    private const float RailRowGap = 3f;
    private const float RailTextInset = 14f;
    private const float RailIndicatorWidth = 3f;

    private static readonly Vector4 RailBackground = new(1f, 1f, 1f, 0.035f);
    private static readonly Vector4 RailHoverBackground = new(1f, 1f, 1f, 0.05f);
    private static readonly Vector4 RailSelectedBackground = new(0.30f, 0.62f, 1.00f, 0.14f);
    private static readonly Vector4 RailInactiveText = new(1f, 1f, 1f, 0.68f);
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
    private readonly Action openLibrary;
    private readonly EditorSurfaceCoordinator surfaces;
    private readonly BackgroundStylePanel backgroundPanel;
    private readonly EditorActionBar actionBar;
    private readonly KeyboardShortcutService keyboardShortcuts;

    // Unsaved-changes protection when the window closes: the same guard the Advanced editor has.
    private readonly EditorCloseGuard closeGuard;

    // Preview: the same Clean Preview the Advanced editor's Preview shows.
    private readonly CleanPreviewPresenter cleanPreview;

    // Which category is shown, and the live view's zoom: view state only, never part of the Plate.
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
        EditorSurfaceCoordinator surfaces,
        EditorDocumentCommands commands,
        KeyboardShortcutService keyboardShortcuts)
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
        this.openLibrary = openLibrary;
        this.surfaces = surfaces;
        backgroundPanel = new BackgroundStylePanel(editorSession, renderResources, OpenImageFileDialog);
        actionBar = new EditorActionBar(commands, EditorSurfaceKind.Basic, openLibrary, openAdvancedEditor);
        this.keyboardShortcuts = keyboardShortcuts;
        closeGuard = new EditorCloseGuard(editorSession, commands);
        cleanPreview = new CleanPreviewPresenter(this, editorSession, profileService, renderResources, ImGuiWindowFlags.None);
    }

    public void Dispose()
    {
    }

    /// <summary>One editing surface at a time: the Advanced editor hands over if it's open. Opens editing, never Preview.</summary>
    public override void OnOpen()
    {
        surfaces.NotifyOpened(EditorSurfaceKind.Basic);
        EditorPreview.Exit(editorSession);
    }

    /// <summary>Clean Preview's presentation (see <see cref="CleanPreviewPresenter"/>), or the editor's own.</summary>
    public override void PreDraw() => cleanPreview.PreDraw();

    public override void PostDraw() => cleanPreview.PostDraw();

    /// <inheritdoc/>
    public void Show()
    {
        IsOpen = true;
        BringToFront();
    }

    /// <summary>
    /// Runs every frame before Dalamud checks whether the window is open. A close that would lose
    /// unsaved work — the title bar Close, Escape, a toggle from elsewhere — is turned back into
    /// "still open" here and Save / Discard / Cancel asked instead (see <see cref="EditorCloseGuard"/>).
    /// </summary>
    public override void PreOpenCheck() => IsOpen = closeGuard.PreOpenCheck(IsOpen);

    /// <summary>
    /// Handing editing to the Advanced editor, which continues the same session (document, dirty
    /// state, history), so the unsaved-changes question doesn't apply.
    /// </summary>
    public void CloseForHandoff()
    {
        closeGuard.ConfirmClose();
        IsOpen = false;
    }

    /// <summary>
    /// Called once when the window closes. A close that slipped past <see cref="PreOpenCheck"/> with
    /// unsaved work reopens the window and asks instead.
    /// </summary>
    public override void OnClose()
    {
        if (closeGuard.ShouldReopenOnClose())
        {
            // The unsaved-changes question needs the normal editor window, not the preview's.
            EditorPreview.Exit(editorSession);
            IsOpen = true;
            return;
        }

        EditorPreview.Exit(editorSession);

        // Draw won't run again until the window reopens: stop claiming shortcuts right away.
        keyboardShortcuts.SetEditorFocusState(editorFocused: false, textInputActive: false);
        fileDialogManager.Reset();
    }

    public override void Draw()
    {
        cleanPreview.CaptureEditorRect();

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

            keyboardShortcuts.SetEditorFocusState(editorFocused: false, textInputActive: false);
            return;
        }

        // Ctrl+S / Ctrl+Z / Ctrl+Y, exactly as the action bar's Save, Undo and Redo — and in Preview,
        // Escape (leave it) and Ctrl+S, exactly as in the Advanced editor's.
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (editorSession.PreviewActive)
        {
            keyboardShortcuts.SetEditorFocusState(focused, ImGui.GetIO().WantTextInput, previewActive: true, canvasInteractionActive: false);
        }
        else
        {
            keyboardShortcuts.SetDocumentShortcutFocusState(focused, ImGui.GetIO().WantTextInput);
        }

        ApplyShortcuts();
        if (closeGuard.Advance())
        {
            IsOpen = false;
        }

        // Opening Basic mode never creates or changes anything: section elements (and the
        // Basic settings) are only created by the first explicit edit that needs them.
        basicEditorSession.Identity.RefineLayout();

        if (editorSession.PreviewActive)
        {
            // Preview: the same Clean Preview as the Advanced editor's — this window becomes the
            // finished Plate alone, over the game, until its close control or Escape.
            cleanPreview.Draw(profile);
        }
        else
        {
            // The shared action bar (My Plates, Basic | Advanced, Undo/Redo, Preview/Revert/Save),
            // outside every scrolling region so it's always in view.
            actionBar.Draw(profile, editorSession.PreviewActive, () => EditorPreview.Enter(editorSession), EditorPreview.Tooltip, basicEditorSession.ErrorMessage);
            EditorWidgets.UnsupportedElementsNotice(profile);
            ImGui.Separator();

            var scale = ImGuiHelpers.GlobalScale;
            var style = ImGui.GetStyle();
            var body = ImGui.GetContentRegionAvail();
            body.Y = Math.Max(body.Y, 160f * scale);

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
                    var remaining = ImGui.GetContentRegionAvail().Y;
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

        DrawResetLayoutPopup();
        actionBar.DrawPopups();
        EditorClosePrompt.Draw(closeGuard, () => IsOpen = false);

        // Commits a color/slider edit whose widget never reported "deactivated after edit".
        editorSession.CommitPendingEditsIfIdle(ImGui.IsAnyItemActive());
    }

    /// <summary>
    /// The document shortcuts queued by <see cref="KeyboardShortcutService"/> (Basic only gets
    /// those, plus Escape in Preview), applied through the same commands as the action bar — so
    /// Ctrl+S never saves a clean Plate, exactly like the Save button.
    /// </summary>
    private void ApplyShortcuts()
    {
        foreach (var action in keyboardShortcuts.DequeuePendingActions())
        {
            if (editorSession.PreviewActive && action.Kind is not (EditorShortcutActionKind.ExitPreview or EditorShortcutActionKind.Save))
            {
                continue;
            }

            switch (action.Kind)
            {
                case EditorShortcutActionKind.ExitPreview:
                    EditorPreview.Exit(editorSession);
                    break;
                case EditorShortcutActionKind.Save:
                    actionBar.Commands.Save();
                    break;
                case EditorShortcutActionKind.Undo:
                    actionBar.Commands.Undo();
                    break;
                case EditorShortcutActionKind.Redo:
                    actionBar.Commands.Redo();
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>
    /// The wide layout's navigation rail: its own subtle background, a small "Basic Editor" header,
    /// then one row per category — Title Case, one height, evenly spaced, text inset. The selected
    /// row has a soft accent tint, a narrow accent bar at its left edge and full-strength text;
    /// the others muted text, with a faint background on hover. Rows are ordinary ImGui items, so
    /// mouse, keyboard and gamepad navigation work as before; a status marker still sits at the
    /// right of any row that needs one.
    /// </summary>
    private void DrawNavigator(ProfileDocument profile, Vector2 size)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 6f * scale);
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, RailBackground);
        using var child = ImRaii.Child("##BasicNavigator", size, false);
        if (!child.Success)
        {
            return;
        }

        var padding = RailPadding * scale;
        var rowHeight = MathF.Round(ImGui.GetFrameHeight() * 1.45f);
        var rowWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X - (padding * 2f));
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorPos(new Vector2(padding + (RailTextInset * scale) - (RailIndicatorWidth * scale), padding));
        ImGui.TextDisabled("Basic Editor");
        ImGui.Dummy(new Vector2(0f, 2f * scale));

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, RailRowGap * scale));
        foreach (var category in BasicEditorView.Categories)
        {
            var status = BasicEditorView.StatusOf(profile, category);
            var selected = navigation.Selected == category;
            var title = BasicEditorView.Title(category);

            ImGui.SetCursorPosX(padding);
            if (ImGui.InvisibleButton($"##Nav{category}", new Vector2(rowWidth, rowHeight)))
            {
                navigation.Select(category);
            }

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();
            var corner = 4f * scale;
            if (selected)
            {
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(RailSelectedBackground), corner);
                var inset = 6f * scale;
                drawList.AddRectFilled(
                    new Vector2(min.X, min.Y + inset), new Vector2(min.X + (RailIndicatorWidth * scale), max.Y - inset),
                    ImGui.GetColorU32(EditorWidgets.AccentColor), RailIndicatorWidth * scale / 2f);
            }
            else if (hovered)
            {
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(RailHoverBackground), corner);
            }

            var textSize = ImGui.CalcTextSize(title);
            var textColor = selected ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(RailInactiveText);
            drawList.AddText(new Vector2(min.X + (RailTextInset * scale), min.Y + ((max.Y - min.Y - textSize.Y) / 2f)), textColor, title);

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
            case BasicEditorCategory.Style:
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
    /// how large the preview is drawn (Fit, 150%, 200%); the Plate itself is never touched.
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
        // "Live view", not "Preview": Preview means the finished Plate alone (the action bar's).
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Live view");
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
    }

    private void DrawResetLayoutPopup()
    {
        if (pendingResetLayoutConfirm)
        {
            pendingResetLayoutConfirm = false;
            ImGui.OpenPopup(ResetLayoutPopupId);
        }

        var open = true;
        using var popup = ImRaii.PopupModal(ResetLayoutPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
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
