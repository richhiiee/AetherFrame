using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
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
/// The Basic editor: choose what to edit, edit it, always see the result. The shared
/// <see cref="EditorActionBar"/> (the same one the Advanced editor has) sits on top; its Preview opens
/// the Plate Viewer on the open Plate, as the Advanced editor's does (<see cref="EditorPreview"/>). A navigation
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

    // The artwork the live view's Plate is missing: downloaded as the Plate is opened (art on demand).
    private readonly ArtNeeds previewArt = new();
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

    // AetherFrame's style around this window's frame, and the tutorial's window policy.
    private readonly AetherWindowChrome chrome = new();

    // Escape on a menu, list, color picker or prompt closes only that, never the editor.
    private readonly PopupEscapeGuard escape = new();

    // Which category is shown, and the live view's zoom: view state only, never part of the Plate.
    private readonly BasicEditorNavigation navigation = new();

    // The live view's paint sequence, for clicks on sections and Components and the selected
    // Component's outline (issue #115; render thread only).
    private readonly List<Domain.Components.PaintStep> previewPlanBuffer = new(ProfileDocument.MaxElementCount + 64);
    private readonly List<Vector2[]> previewOutlineBuffer = new(8);

    // The Component slot to scroll into view once, after a click selected its Component on the live view.
    private Domain.Components.PlateComponentKind? revealComponentSlot;
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
        KeyboardShortcutService keyboardShortcuts,
        EditorPlateMenu plateMenu)
        : base("AetherFrame Basic Editor##BasicProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = BasicEditorView.MinimumWindowSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = true;

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
        actionBar = new EditorActionBar(commands, EditorSurfaceKind.Basic, openLibrary, openAdvancedEditor, () => Help, plateMenu);
        this.keyboardShortcuts = keyboardShortcuts;
        closeGuard = new EditorCloseGuard(editorSession, commands);
    }

    /// <summary>The Help menu (tutorial, shortcuts, commands), set by the plugin once the tutorial exists.</summary>
    internal HelpMenu? Help { get; set; }

    public void Dispose()
    {
    }

    /// <summary>One editing surface at a time: the Advanced editor hands over if it's open.</summary>
    public override void OnOpen() => surfaces.NotifyOpened(EditorSurfaceKind.Basic);

    /// <summary>The first-open size, kept within the screen as the Advanced editor's and My Plates' are.</summary>
    public override void PreDraw()
    {
        chrome.PushStyle();
        EditorWidgets.SetFirstUseSize(BasicEditorView.FirstUseSize, BasicEditorView.MinimumWindowSize);
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

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
            IsOpen = true;
            return;
        }

        // Draw won't run again until the window reopens: stop claiming shortcuts right away.
        keyboardShortcuts.SetEditorFocusState(editorFocused: false, textInputActive: false);
        fileDialogManager.Reset();
    }

    public override void Draw()
    {
        using var popupEscape = escape.Update(this);

        // Drawn unconditionally so an in-progress file pick isn't stranded if the Plate
        // becomes unavailable (e.g. it's deleted from My Plates) while the dialog is open.
        fileDialogManager.Draw();

        // Before the open Plate is read: a Plate action that opens another Plate (Save as New
        // Plate, Open another Plate, New Plate) takes effect before anything is drawn.
        actionBar.PlateMenu.DrawFrame();

        // It opened in the Advanced Editor, which draws it from now on.
        if (!IsOpen)
        {
            return;
        }

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

        // Ctrl+S / Ctrl+Z / Ctrl+Y, exactly as the action bar's Save, Undo and Redo.
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        keyboardShortcuts.SetDocumentShortcutFocusState(focused, ImGui.GetIO().WantTextInput);

        ApplyShortcuts();
        if (closeGuard.Advance())
        {
            IsOpen = false;
        }

        // Opening Basic mode never creates or changes anything: section elements (and the
        // Basic settings) are only created by the first explicit edit that needs them.
        basicEditorSession.Identity.RefineLayout();

        // The shared action bar (My Plates, Basic | Advanced, Undo/Redo, Preview/Revert/Save),
        // outside every scrolling region so it's always in view.
        actionBar.Draw(profile, () => EditorPreview.Show(editorSession, profile.ProfileId, actionBar.PlateMenu.View), EditorPreview.Tooltip, basicEditorSession.ErrorMessage);
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

        DrawResetLayoutPopup();
        actionBar.DrawPopups();
        EditorClosePrompt.Draw(closeGuard, () => IsOpen = false);

        // Commits a color/slider edit whose widget never reported "deactivated after edit".
        editorSession.CommitPendingEditsIfIdle(ImGui.IsAnyItemActive());
    }

    /// <summary>
    /// The document shortcuts queued by <see cref="KeyboardShortcutService"/> (Basic only gets
    /// those), applied through the same commands as the action bar — so Ctrl+S never saves a clean
    /// Plate, exactly like the Save button.
    /// </summary>
    private void ApplyShortcuts()
    {
        foreach (var action in keyboardShortcuts.DequeuePendingActions())
        {
            switch (action.Kind)
            {
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

    /// <summary>The tutorial target of a category's row in the navigator or the strip.</summary>
    private static TutorialTarget NavigatorTarget(BasicEditorCategory category) => category switch
    {
        BasicEditorCategory.Style => TutorialTarget.BasicNavigatorStyle,
        BasicEditorCategory.Portrait => TutorialTarget.BasicNavigatorPortrait,
        BasicEditorCategory.Identity => TutorialTarget.BasicNavigatorIdentity,
        BasicEditorCategory.Details => TutorialTarget.BasicNavigatorDetails,
        BasicEditorCategory.Message => TutorialTarget.BasicNavigatorMessage,
        _ => TutorialTarget.None,
    };

    /// <summary>
    /// The wide layout's navigation rail: its own subtle background, then one row per category from
    /// the top (the action bar's Basic | Advanced switch already says which editor this is) —
    /// Title Case, one height, evenly spaced, text inset. The selected
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

        TutorialAnchorMarks.MarkWindow(TutorialTarget.BasicNavigator);
        var padding = RailPadding * scale;
        var rowHeight = MathF.Round(ImGui.GetFrameHeight() * 1.45f);
        var rowWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X - (padding * 2f));
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorPosY(padding);

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

            TutorialAnchorMarks.Mark(NavigatorTarget(category));
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
        var stripMin = ImGui.GetCursorScreenPos();
        var stripMax = stripMin;

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

            TutorialAnchorMarks.Mark(NavigatorTarget(category));
            stripMax = Vector2.Max(stripMax, ImGui.GetItemRectMax());
            StatusTooltip(status);
            DrawStatusDot(status);
            rowUsed += width;
        }

        TutorialAnchorMarks.MarkRect(TutorialTarget.BasicNavigator, stripMin, stripMax);
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

        TutorialAnchorMarks.MarkWindow(TutorialTarget.BasicInspector);

        if (withCategoryStrip)
        {
            DrawCategoryStrip(profile);
        }

        var category = navigation.Selected;
        AetherControls.SectionHeader(BasicEditorView.Title(category), topSpacing: 0f);
        foreach (var line in BasicEditorView.SummaryOf(profile, category))
        {
            AetherControls.Secondary(line);
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

        // The page that holds the clicked Component's slot has been drawn once: if the slot wasn't on
        // it (its group is hidden, or the page returned early), the request lapses rather than
        // scrolling the page later, when the slot comes back for some other reason.
        if (revealComponentSlot is { } reveal && BasicEditorView.CategoryOf(reveal) == category)
        {
            revealComponentSlot = null;
        }

        ImGui.Spacing();
    }

    /// <summary>A group's label inside a category: the small label face in the accent, on one row so a Show toggle can follow it.</summary>
    private static void Subheading(string text)
    {
        ImGui.Spacing();
        using (AetherFonts.Label())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(AetherPalette.Aether, text);
        }
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
        section is BasicSection.Job or BasicSection.Level ? "Favorite Jobs" : BasicSections.Get(section).Title;

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// The live preview: the shared renderer's finished rendering — exactly what the Plate Viewer
    /// shows, with no placeholders, guides, or other editor-only overlays. Its toolbar only changes
    /// how large the preview is drawn (Fit, 150%, 200%); the Plate itself is never touched.
    /// Clicking a section opens its category, clicking a Component also selects it (its slot is
    /// brought into view and it is outlined); dragging while zoomed pans.
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

        TutorialAnchorMarks.MarkWindow(TutorialTarget.BasicPreview);

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

        previewArt.Begin(renderResources);
        ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, canvasOrigin, scale, renderResources, ProfileRenderOptions.Finished);
        previewArt.End(renderResources);

        // The one editor overlay on the live view: the selected Component's outline, each place it
        // is drawn (every corner of a Corner Ornament), so it's clear what the slot's controls change.
        if (editorSession.SelectedComponentId is { } selectedId && Domain.Components.PlateComponentEditor.Find(profile, selectedId) is { } selected)
        {
            ProfileRenderer.BuildPaintPlan(profile, renderResources, ProfileRenderOptions.Finished, previewPlanBuffer);
            CanvasHitTest.Outlines(previewPlanBuffer, selected, previewOutlineBuffer);
            var drawList = ImGui.GetWindowDrawList();
            var color = ImGui.GetColorU32(EditorWidgets.AccentColor);
            foreach (var corners in previewOutlineBuffer)
            {
                drawList.AddQuad(
                    canvasOrigin + (corners[0] * scale), canvasOrigin + (corners[1] * scale), canvasOrigin + (corners[2] * scale), canvasOrigin + (corners[3] * scale),
                    color, 2f);
            }

            previewOutlineBuffer.Clear();
            previewPlanBuffer.Clear();
        }
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
        var clicked = ImGui.IsItemDeactivated() && !previewDragged;
        var hovered = ImGui.IsItemHovered() && !ImGui.IsItemActive();
        if (!clicked && !hovered)
        {
            return;
        }

        ProfileRenderer.BuildPaintPlan(profile, renderResources, ProfileRenderOptions.Finished, previewPlanBuffer);
        var (category, component) = BasicEditorView.TargetAt(profile, previewPlanBuffer, logicalMouse);
        previewPlanBuffer.Clear();

        if (clicked)
        {
            if (category is { } target)
            {
                navigation.Select(target);
            }

            // A Component is selected, and its slot brought into view; a section, or nothing, lets go
            // of a selected Component. Selection never changes the Plate.
            if (component is not null)
            {
                editorSession.SelectComponent(component.Id);
                revealComponentSlot = component.Kind;
            }
            else if (editorSession.SelectedComponentId is not null)
            {
                editorSession.SelectComponent(null);
            }
        }

        if (hovered && category is { } hoveredCategory)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(component is not null
                ? $"Edit the {Domain.Components.PlateComponentEditor.KindLabel(component.Kind)} ({BasicEditorView.Title(hoveredCategory)})"
                : $"Edit {BasicEditorView.Title(hoveredCategory)}");
        }
    }

    private void DrawPreviewToolbar()
    {
        // "Live view", not "Preview": Preview means the finished Plate alone (the action bar's).
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Live view");
        ImGui.SameLine();

        var zoomMin = ImGui.GetCursorScreenPos();
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

        TutorialAnchorMarks.MarkRect(TutorialTarget.BasicPreviewZoom, zoomMin, ImGui.GetItemRectMax());
        ArtDownloadStatus.DrawInline(previewArt, renderResources.ArtStore);
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
        BulletText("the position and size of every Basic section: portrait, identity header,\nhome world, favorite jobs, free company, playstyle, active hours, message");
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
        if (ImGui.Button("Cancel", buttonSize) || PopupEscapeGuard.CancelsPrompt())
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
