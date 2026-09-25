using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Templates: the Create Plate chooser (the primary Template entry point — Start with a built-in,
/// or from My Templates), the quieter Manage Templates mode it opens into (the Templates card grid
/// and its action bar: Preview / Rename / Duplicate / Delete / Use Template), and Save as Template
/// for a Plate. Templates is deliberately not a permanent top-level tab beside My Plates — Manage
/// Templates is entered only from the chooser and returns to My Plates explicitly. Follows the
/// exact visual and interaction patterns <c>PlateLibraryWindow.cs</c>/<c>.Actions.cs</c> already
/// established for My Plates — same card grid math, same async-operation and popup-deferral
/// plumbing, same disabled-with-tooltip convention for actions a built-in Template can't do.
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    /// <summary>Not a permanent tab — see the class doc. My Plates is always the entry point;
    /// Templates is a mode entered from the Create Plate chooser's Manage Templates action.</summary>
    private enum LibraryView
    {
        MyPlates,
        Templates,
    }

    private const string TemplateChooserPopupId = "Create Plate##AetherFrameTemplateChooser";
    private const string SaveAsTemplatePopupId = "Save as Template##AetherFrameSaveAsTemplate";
    private const string TemplateRenamePopupId = "Rename Template##AetherFrameTemplateRename";
    private const string TemplateDeletePopupId = "Delete Template##AetherFrameTemplateDelete";

    private const float ChooserWidth = 720f;
    private const float ChooserHeight = 450f;
    private const float ChooserMinWidth = 620f;
    private const float ChooserMinHeight = 380f;
    private const float ChooserLeftPaneWidth = 260f;

    private static readonly Vector4 BuiltInBadgeColor = new(0.55f, 0.62f, 0.95f, 1f);
    private static readonly Vector4 UserTemplateBadgeColor = new(0.55f, 0.85f, 0.65f, 1f);

    private readonly Dictionary<Guid, string> templateCardIds = new();

    private LibraryView activeView = LibraryView.MyPlates;
    private Guid? selectedTemplateId;
    private string templateSearchText = string.Empty;

    private bool pendingTemplateChooserPopup;
    private Guid chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;

    // The right pane's preview document is regenerated only when the selection actually changes
    // (never every frame): a built-in's document is freshly generated each time it's resolved,
    // and re-running that — plus the renderer's per-instance font prewarming — every single frame
    // the popup is open would be wasteful and would leak a prewarm cache entry per frame.
    private Guid? chooserPreviewedTemplateId;
    private ProfileDocument? chooserPreviewDocument;

    private bool pendingSaveAsTemplatePopup;
    private Guid saveAsTemplateSourcePlateId;
    private string saveAsTemplateBuffer = string.Empty;
    private string? saveAsTemplateError;

    private bool pendingTemplateRenamePopup;
    private Guid templateRenameTargetId;
    private string templateRenameBuffer = string.Empty;
    private string? templateRenameError;

    private bool pendingTemplateDeletePopup;
    private Guid templateDeleteTargetId;

    // ---------------------------------------------------------------- Manage Templates (a mode, not a tab)

    private void DrawTemplatesView()
    {
        var allTemplates = templates.GetOrderedTemplates();
        if (selectedTemplateId is { } selected && allTemplates.All(t => t.TemplateId != selected))
        {
            selectedTemplateId = null;
        }

        DrawTemplateHeader(allTemplates.Count);
        ImGui.Separator();

        var shown = string.IsNullOrWhiteSpace(templateSearchText) ? allTemplates : templates.Search(templateSearchText);
        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y + 4f;
        using (var grid = ImRaii.Child("##TemplateGrid", new Vector2(-1, -footerHeight), false))
        {
            if (grid.Success)
            {
                DrawTemplateGrid(shown, allTemplates.Count);
            }
        }

        ImGui.Separator();
        DrawTemplateActionBar();
    }

    private void DrawTemplateHeader(int templateCount)
    {
        if (ImGui.ArrowButton("##BackToMyPlates", ImGuiDir.Left))
        {
            activeView = LibraryView.MyPlates;
            selectedTemplateId = null;
        }

        EditorWidgets.Tooltip("Back to My Plates");

        ImGui.SameLine();
        ImGui.TextUnformatted("Manage Templates");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##TemplateSearch", "Search Templates...", ref templateSearchText, 64);

        var countText = templateCount == 1 ? "1 Template" : $"{templateCount} Templates";
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(countText).X));
        ImGui.TextDisabled(countText);
    }

    private void DrawTemplateGrid(IReadOnlyList<TemplateSummary> shown, int totalCount)
    {
        if (totalCount == 0)
        {
            ImGui.TextDisabled("No Templates yet.");
            return;
        }

        if (shown.Count == 0)
        {
            ImGui.TextDisabled("No Templates match your search.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing;
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((available + spacing.X) / (CardWidth + spacing.X)));

        for (var i = 0; i < shown.Count; i++)
        {
            if (i % columns != 0)
            {
                ImGui.SameLine();
            }

            DrawTemplateCard(shown[i]);
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            selectedTemplateId = null;
        }
    }

    private void DrawTemplateCard(TemplateSummary template)
    {
        if (!templateCardIds.TryGetValue(template.TemplateId, out var id))
        {
            id = "##TemplateCard" + template.TemplateId.ToString("N");
            templateCardIds[template.TemplateId] = id;
        }

        var thumbnailSize = new Vector2(CardWidth - (CardPadding * 2f), (CardWidth - (CardPadding * 2f)) / ThumbnailAspect);
        var cardSize = new Vector2(CardWidth, thumbnailSize.Y + (CardPadding * 3f) + ImGui.GetTextLineHeight());

        ImGui.InvisibleButton(id, cardSize);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var isSelected = selectedTemplateId == template.TemplateId;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            selectedTemplateId = template.TemplateId;
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && template.IsReady)
        {
            UseTemplate(template.TemplateId);
        }

        if (hovered && template.Problem is { } problem)
        {
            ImGui.SetTooltip(problem);
        }
        else if (hovered && template.HasUnsupportedElements)
        {
            ImGui.SetTooltip(EditorWidgets.UnsupportedElementsWarning);
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? CardHoverColor : CardColor), 6f);
        if (isSelected)
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(EditorWidgets.AccentColor), 6f, ImDrawFlags.None, 2f);
        }

        var thumbnailMin = min + new Vector2(CardPadding);
        var thumbnailMax = thumbnailMin + thumbnailSize;
        DrawTemplateThumbnail(drawList, template, thumbnailMin, thumbnailMax);
        DrawTemplateKindBadge(drawList, template, thumbnailMin, thumbnailMax);

        if (template.HasUnsupportedElements)
        {
            DrawCompatibilityMarker(drawList, thumbnailMin, thumbnailMax);
        }

        var textPos = new Vector2(thumbnailMin.X, thumbnailMax.Y + CardPadding);
        drawList.PushClipRect(textPos, new Vector2(thumbnailMax.X, max.Y), true);
        var nameColor = template.IsReady ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(EditorWidgets.DimTextColor);
        drawList.AddText(textPos, nameColor, template.DisplayName);
        drawList.PopClipRect();
    }

    /// <summary>The thumbnail when one is Ready and loads; otherwise a fallback from the Template's
    /// own background. Hits the exact same (currently non-functional) thumbnail pipeline as Plates
    /// — a separate cache directory only, never a second mechanism.</summary>
    private void DrawTemplateThumbnail(ImDrawListPtr drawList, TemplateSummary template, Vector2 min, Vector2 max)
    {
        if (template.IsReady)
        {
            var versionKey = PlateThumbnailService.VersionKeyFor(0, template.ModifiedUtc);
            var thumbnail = templateThumbnails.Get(template.TemplateId, versionKey, () => templates.GetSavedDocument(template.TemplateId));

            if (templateThumbnailTextures.GetWrapOrNull(template.TemplateId, thumbnail) is { } wrap)
            {
                drawList.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFFu);
                return;
            }
        }

        DrawTemplateFallbackThumbnail(drawList, template, min, max);
    }

    private void DrawTemplateFallbackThumbnail(ImDrawListPtr drawList, TemplateSummary template, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(FallbackBackdropColor), 4f);

        var background = template.IsReady ? templates.GetSavedDocument(template.TemplateId)?.Background : null;
        var icon = FontAwesomeIcon.Copy;

        if (!template.IsReady)
        {
            icon = FontAwesomeIcon.ExclamationTriangle;
        }
        else if (background is { } style)
        {
            var opacity = Math.Clamp(style.Opacity, 0f, 1f);
            var primary = style.PrimaryColor with { W = style.PrimaryColor.W * opacity };
            var secondary = style.SecondaryColor with { W = style.SecondaryColor.W * opacity };

            switch (style.Mode)
            {
                case ProfileBackgroundMode.SolidColor:
                case ProfileBackgroundMode.TexturedFill:
                    drawList.AddRectFilled(min, max, ImGui.GetColorU32(primary), 4f);
                    break;

                case ProfileBackgroundMode.LinearGradient:
                    var mixed = Vector4.Lerp(primary, secondary, 0.5f);
                    drawList.AddRectFilledMultiColor(min, max, ImGui.GetColorU32(primary), ImGui.GetColorU32(mixed), ImGui.GetColorU32(secondary), ImGui.GetColorU32(mixed));
                    break;

                case ProfileBackgroundMode.Image:
                    icon = FontAwesomeIcon.Image;
                    break;
            }
        }

        var iconText = EditorWidgets.GetIconString(icon);
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            drawList.AddText((min + max - iconSize) / 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)), iconText);
        }
    }

    private static void DrawTemplateKindBadge(ImDrawListPtr drawList, TemplateSummary template, Vector2 thumbnailMin, Vector2 thumbnailMax)
    {
        var label = template.IsBuiltIn ? "Built In" : "My Template";
        var color = template.IsBuiltIn ? BuiltInBadgeColor : UserTemplateBadgeColor;
        var textSize = ImGui.CalcTextSize(label);
        var padding = new Vector2(6f, 2f);
        var badgeMax = new Vector2(thumbnailMax.X - 4f, thumbnailMin.Y + 4f + textSize.Y + (padding.Y * 2f));
        var badgeMin = new Vector2(badgeMax.X - textSize.X - (padding.X * 2f), thumbnailMin.Y + 4f);

        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(color), 4f);
        drawList.AddText(badgeMin + padding, ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.07f, 1f)), label);
    }

    private void DrawTemplateActionBar()
    {
        var selected = selectedTemplateId is { } id ? templates.FindTemplate(id) : null;
        var ready = selected is { IsReady: true };

        using (ImRaii.Disabled(!ready || selected is { SupportsPreview: false }))
        {
            if (ImGui.Button("Preview"))
            {
                var document = templates.GetSavedDocument(selected!.TemplateId, new PlateStarterContent(characterIdentity.CurrentInfo));
                if (document is not null)
                {
                    showDocumentInViewer(document);
                }
            }
        }

        EditorWidgets.Tooltip(selected is { SupportsPreview: false }
            ? "This Template has nothing to preview."
            : "Show this Template in the Plate Viewer. Nothing about it changes.");

        ImGui.SameLine();
        using (ImRaii.Disabled(!ready))
        {
            if (ImGui.Button("Use Template"))
            {
                UseTemplate(selected!.TemplateId);
            }

            EditorWidgets.Tooltip("Create a new, independent Plate from this Template.");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(selected is null || selected.IsBuiltIn || IsBusy))
        {
            if (ImGui.Button("Rename"))
            {
                templateRenameTargetId = selected!.TemplateId;
                templateRenameBuffer = selected.DisplayName;
                templateRenameError = null;
                pendingTemplateRenamePopup = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Duplicate"))
            {
                var sourceId = selected!.TemplateId;
                RunOperation<Guid>("duplicate the Template", () => templates.DuplicateTemplateAsync(sourceId), newId => selectedTemplateId = newId);
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                templateDeleteTargetId = selected!.TemplateId;
                pendingTemplateDeletePopup = true;
            }
        }

        if (selected is { IsBuiltIn: true })
        {
            EditorWidgets.Tooltip("Built-in Templates can't be renamed, duplicated, or deleted.");
        }

        // Second row: what's happening, or what went wrong.
        if (IsBusy)
        {
            ImGui.TextDisabled("Working...");
        }
        else if (errorMessage is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
        else if (statusMessage is { } status)
        {
            ImGui.TextColored(EditorWidgets.SuccessColor with { W = 0.85f }, status);
        }
        else if (selected is { } template)
        {
            ImGui.TextDisabled(template.IsReady
                ? (template.IsBuiltIn ? "Ships with AetherFrame." : $"Saved {template.ModifiedUtc.ToLocalTime():g}")
                : template.Problem ?? "This Template can't be opened.");
        }
        else
        {
            ImGui.TextDisabled("Select a Template.");
        }
    }

    /// <summary>Use Template: creates the new Plate, then opens it in Basic or Advanced depending
    /// on whether its content actually has Basic structure (see <see cref="EditorSurfaceChooser"/>,
    /// the same rule as Edit) — never a fixed choice per Template kind, so an arbitrary user
    /// Template opens sensibly.</summary>
    private void UseTemplate(Guid templateId)
    {
        var character = characterIdentity.CurrentCharacter;
        var starter = new PlateStarterContent(characterIdentity.CurrentInfo);
        RunOperation<PlateCreationResult>("use the Template", () => templates.InstantiateAsync(templateId, character, starter), result =>
        {
            activeView = LibraryView.MyPlates;
            selectedPlateId = result.PlateId;
            searchText = string.Empty;
            if (result.BecameActive && character is { } owner)
            {
                statusMessage = $"Created your first Plate. It's now {DescribeCharacter(owner)}'s Active Plate.";
            }

            var basic = EditorSurfaceChooser.ForDocument(library.GetSavedDocument(result.PlateId)) == EditorSurfaceKind.Basic;
            RequestOpen(result.PlateId, basic);
        });
    }

    // ---------------------------------------------------------------- Template chooser (Create Plate)

    /// <summary>
    /// A two-pane dialog: a full-width, scrollable Template browser on the left (Start: the two
    /// built-ins; My Templates: everything saved), and the selected Template's own details —
    /// including a live preview through the exact same <see cref="ProfileRenderer"/> every other
    /// preview surface in AetherFrame uses — on the right. Nothing is created until "Use Template"
    /// (or a double-click on a row) is confirmed.
    /// </summary>
    private void DrawTemplateChooserPopup()
    {
        if (pendingTemplateChooserPopup)
        {
            ImGui.OpenPopup(TemplateChooserPopupId);
            pendingTemplateChooserPopup = false;
            chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;
            chooserPreviewedTemplateId = null;
        }

        ImGui.SetNextWindowSize(new Vector2(ChooserWidth, ChooserHeight) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(ChooserMinWidth, ChooserMinHeight) * ImGuiHelpers.GlobalScale, new Vector2(float.MaxValue, float.MaxValue));

        if (!ImGui.BeginPopupModal(TemplateChooserPopupId, ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        // The selection can go stale without the chooser closing — e.g. Delete Template from a
        // row's own context menu. Fall back to the default rather than showing an empty selection.
        if (BuiltInTemplateCatalog.Find(chosenTemplateId) is null && templates.FindTemplate(chosenTemplateId) is null)
        {
            chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;
            chooserPreviewedTemplateId = null;
        }

        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y + (4f * ImGuiHelpers.GlobalScale);
        var bodyHeight = -footerHeight;
        var leftWidth = ChooserLeftPaneWidth * ImGuiHelpers.GlobalScale;

        using (var left = ImRaii.Child("##TemplateChooserLeft", new Vector2(leftWidth, bodyHeight), true))
        {
            if (left.Success)
            {
                DrawTemplateChooserLeftPane();
            }
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##TemplateChooserRight", new Vector2(-1f, bodyHeight), false))
        {
            if (right.Success)
            {
                DrawTemplateChooserRightPane();
            }
        }

        ImGui.Separator();
        DrawTemplateChooserFooter();

        // Drawn here (nested inside the chooser's own popup scope), not from the top-level Draw(),
        // whenever a row's context menu requested one — see the call site in PlateLibraryWindow.cs
        // for the full explanation. This is what makes ImGui.OpenPopup() register Rename/Delete one
        // level above the chooser instead of at the same level, so opening either one stacks on top
        // of the chooser instead of silently closing it.
        DrawTemplateRenamePopup();
        DrawTemplateDeletePopup();

        ImGui.EndPopup();
    }

    private void DrawTemplateChooserLeftPane()
    {
        ImGui.TextDisabled("START");
        ImGui.Spacing();

        DrawTemplateChooserRow(BuiltInTemplateCatalog.AdventurePlateClassicId, "Adventure Plate Classic", "Basic Editor", isBuiltIn: true);
        DrawTemplateChooserRow(BuiltInTemplateCatalog.BlankCanvasId, "Blank Canvas", "Advanced Editor", isBuiltIn: true);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("MY TEMPLATES");
        ImGui.Spacing();

        var userTemplates = templates.GetOrderedTemplates().Where(t => t.Kind == TemplateKind.UserSaved).ToList();
        if (userTemplates.Count == 0)
        {
            ImGui.TextDisabled("You haven't saved any Templates yet.");
        }
        else
        {
            foreach (var template in userTemplates)
            {
                DrawTemplateChooserRow(template.TemplateId, template.DisplayName, null, isBuiltIn: false);
            }
        }
    }

    /// <summary>
    /// One full-width, fully-clickable row. Uses <see cref="ImGui.Selectable"/> (not a radio
    /// button) purely for its built-in, AetherFrame-accent-tinted highlight — the label itself is
    /// drawn over it, the same "invisible interactive widget plus manual text" technique the
    /// Template/Plate card grids already use. Double-clicking a row uses that Template immediately,
    /// mirroring the same established convention the My Plates and Manage Templates card grids
    /// already use for their own primary action. Right-clicking opens a context menu (see
    /// <see cref="DrawUserTemplateContextMenuItems"/>/<see cref="DrawBuiltInTemplateContextMenuItems"/>).
    /// </summary>
    private void DrawTemplateChooserRow(Guid templateId, string label, string? secondaryText, bool isBuiltIn)
    {
        var selected = chosenTemplateId == templateId;
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var rowHeight = (secondaryText is null ? lineHeight : lineHeight * 2f) + (4f * ImGuiHelpers.GlobalScale);
        var rowId = $"##ChooserRow{templateId:N}";

        using (ImRaii.PushColor(ImGuiCol.Header, EditorWidgets.AccentColor with { W = 0.35f }))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, EditorWidgets.AccentColor with { W = 0.18f }))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, EditorWidgets.AccentColor with { W = 0.45f }))
        {
            if (ImGui.Selectable(rowId, selected, ImGuiSelectableFlags.None, new Vector2(0f, rowHeight)))
            {
                chosenTemplateId = templateId;
            }
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            chosenTemplateId = templateId;
            UseTemplate(templateId);
            ImGui.CloseCurrentPopup();
        }

        var contextMenuId = $"##ChooserRowMenu{templateId:N}";
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            chosenTemplateId = templateId;
            ImGui.OpenPopup(contextMenuId);
        }

        if (ImGui.BeginPopup(contextMenuId))
        {
            if (isBuiltIn)
            {
                DrawBuiltInTemplateContextMenuItems(templateId);
            }
            else
            {
                DrawUserTemplateContextMenuItems(templateId, label);
            }

            ImGui.EndPopup();
        }

        var min = ImGui.GetItemRectMin();
        var padding = new Vector2(6f, 3f) * ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(min + padding, ImGui.GetColorU32(ImGuiCol.Text), label);
        if (secondaryText is not null)
        {
            drawList.AddText(min + padding + new Vector2(0f, lineHeight), ImGui.GetColorU32(EditorWidgets.DimTextColor), secondaryText);
        }
    }

    /// <summary>
    /// The user Template context menu: Use Template, Rename, Duplicate, then Delete Template — the
    /// one reusable set of actions any surface listing user Templates can share (currently the
    /// Create Plate chooser's rows; Manage Templates' action bar already exposes the same actions
    /// as buttons). No Preview entry: selecting the row already shows its preview in the chooser's
    /// right pane. Rename/Delete defer to the existing popups (same pending-flag pattern Manage
    /// Templates' own buttons use) so confirmation, name validation, and duplicate-name policy are
    /// never reimplemented. Must be called between a matching BeginPopup/EndPopup.
    /// </summary>
    private void DrawUserTemplateContextMenuItems(Guid templateId, string displayName)
    {
        if (ImGui.MenuItem("Use Template"))
        {
            UseTemplate(templateId);
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.MenuItem("Rename"))
        {
            templateRenameTargetId = templateId;
            templateRenameBuffer = displayName;
            templateRenameError = null;
            pendingTemplateRenamePopup = true;
        }

        if (ImGui.MenuItem("Duplicate"))
        {
            RunOperation<Guid>("duplicate the Template", () => templates.DuplicateTemplateAsync(templateId), newId =>
            {
                chosenTemplateId = newId;
                selectedTemplateId = newId;
            });
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Delete Template"))
        {
            templateDeleteTargetId = templateId;
            pendingTemplateDeletePopup = true;
        }
    }

    /// <summary>Built-ins are immutable: their context menu (right-click is optional for them —
    /// left-click plus Use Template already covers the common case) offers only Use Template,
    /// never Rename/Duplicate/Delete.</summary>
    private void DrawBuiltInTemplateContextMenuItems(Guid templateId)
    {
        if (ImGui.MenuItem("Use Template"))
        {
            UseTemplate(templateId);
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawTemplateChooserRightPane()
    {
        var selection = ResolveChooserSelection();
        if (selection is null)
        {
            ImGui.TextDisabled("Select a Template.");
            return;
        }

        EnsureChooserPreviewFresh(selection.Value);

        ImGui.TextUnformatted(selection.Value.Name);
        ImGui.TextColored(EditorWidgets.DimTextColor, selection.Value.Destination);
        ImGui.Spacing();
        ImGui.TextWrapped(selection.Value.Description);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!selection.Value.SupportsPreview)
        {
            // Deliberately no rendered preview here — Blank Canvas has nothing to show; a fake
            // stand-in would just be noise. Its destination and description above are enough.
            ImGui.TextDisabled("No preview — Blank Canvas starts empty.");
            return;
        }

        if (chooserPreviewDocument is not { } document)
        {
            ImGui.TextDisabled("Preview unavailable.");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f || document.CanvasWidth <= 0f || document.CanvasHeight <= 0f)
        {
            return;
        }

        // Fits the Plate's visual bounds: its canvas plus any intentional Component overflow.
        var fit = PlateViewFit.Fit(available, ProfileVisualBounds.Compute(document));
        if (fit.Scale <= 0f)
        {
            return;
        }

        var canvasOrigin = ImGui.GetCursorScreenPos() + fit.CanvasOffset;
        ImGui.Dummy(available);

        var drawList = ImGui.GetWindowDrawList();
        ProfileRenderer.Draw(drawList, document, canvasOrigin, fit.Scale, renderResources, ProfileRenderOptions.Finished);
    }

    private void DrawTemplateChooserFooter()
    {
        var character = characterIdentity.CurrentCharacter;
        ImGui.TextColored(EditorWidgets.DimTextColor, character is { } who
            ? $"New Plate will belong to {DescribeCharacter(who)}. It becomes Active only if it's the character's first Plate."
            : "No character is logged in, so the new Plate won't belong to a character yet.");

        ImGui.Spacing();

        // Left side: the quiet door into Manage Templates — not the common path through this
        // popup (see DrawTemplatesView, a mode entered from here, never a permanent tab).
        using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.DimTextColor))
        {
            if (ImGui.Selectable("Manage Templates...", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize("Manage Templates...").X, 0f)))
            {
                activeView = LibraryView.Templates;
                selectedTemplateId = null;
                ImGui.CloseCurrentPopup();
            }
        }

        EditorWidgets.Tooltip("Preview, rename, duplicate, or delete your saved Templates.");

        // Right side: Cancel, then Use Template as the primary (accent-colored) action.
        var buttonSize = new Vector2(130f, 0f) * ImGuiHelpers.GlobalScale;
        var rightWidth = (buttonSize.X * 2f) + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - rightWidth));

        if (ImGui.Button("Cancel", buttonSize))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.AccentColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, EditorWidgets.AccentColor with { W = 0.85f }))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, EditorWidgets.AccentColor with { W = 0.7f }))
        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Use Template", buttonSize))
            {
                UseTemplate(chosenTemplateId);
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private readonly record struct ChooserSelection(string Name, string Description, string Destination, bool SupportsPreview);

    private ChooserSelection? ResolveChooserSelection()
    {
        if (BuiltInTemplateCatalog.Find(chosenTemplateId) is { } builtIn)
        {
            var destination = builtIn.TemplateId == BuiltInTemplateCatalog.AdventurePlateClassicId ? "Basic Editor" : "Advanced Editor";
            return new ChooserSelection(builtIn.Name, builtIn.Description, destination, builtIn.SupportsPreview);
        }

        var summary = templates.FindTemplate(chosenTemplateId);
        if (summary is null)
        {
            return null;
        }

        if (!summary.IsReady)
        {
            return new ChooserSelection(summary.DisplayName, summary.Problem ?? "This Template can't be opened.", string.Empty, false);
        }

        var document = templates.GetSavedDocument(chosenTemplateId);
        var destinationText = EditorSurfaceChooser.ForDocument(document) == EditorSurfaceKind.Basic ? "Basic Editor" : "Advanced Editor";
        return new ChooserSelection(summary.DisplayName, $"Saved {summary.ModifiedUtc.ToLocalTime():g}.", destinationText, summary.SupportsPreview);
    }

    /// <summary>Regenerates the right pane's cached preview document only when the selected
    /// Template id actually changed since the last draw — see the field doc comment.</summary>
    private void EnsureChooserPreviewFresh(ChooserSelection selection)
    {
        if (chooserPreviewedTemplateId == chosenTemplateId)
        {
            return;
        }

        chooserPreviewedTemplateId = chosenTemplateId;
        chooserPreviewDocument = selection.SupportsPreview
            ? templates.GetSavedDocument(chosenTemplateId, new PlateStarterContent(characterIdentity.CurrentInfo))
            : null;
    }

    // ---------------------------------------------------------------- Save as Template

    private void DrawSaveAsTemplatePopup()
    {
        if (pendingSaveAsTemplatePopup)
        {
            ImGui.OpenPopup(SaveAsTemplatePopupId);
            pendingSaveAsTemplatePopup = false;
        }

        if (!ImGui.BeginPopupModal(SaveAsTemplatePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        EditorWidgets.Hint("Saves this Plate's last saved state. Changes you haven't saved yet won't be included.");
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(300f);
        var submitted = ImGui.InputText("##TemplateName", ref saveAsTemplateBuffer, TemplateNaming.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);

        if (saveAsTemplateError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Save as Template", new Vector2(160f, 0f)) || submitted)
            {
                if (!TemplateNaming.TryNormalizeName(saveAsTemplateBuffer, out var name, out var validationError))
                {
                    saveAsTemplateError = validationError;
                }
                else
                {
                    var plateId = saveAsTemplateSourcePlateId;
                    RunOperation<Guid>("save the Template", () => templates.SaveAsTemplateAsync(plateId, name),
                        _ => statusMessage = $"Saved \"{name}\" as a Template.");
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- Template rename

    private void DrawTemplateRenamePopup()
    {
        if (pendingTemplateRenamePopup)
        {
            ImGui.OpenPopup(TemplateRenamePopupId);
            pendingTemplateRenamePopup = false;
        }

        if (!ImGui.BeginPopupModal(TemplateRenamePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(300f);
        var submitted = ImGui.InputText("##RenameTemplateName", ref templateRenameBuffer, TemplateNaming.MaxNameLength, ImGuiInputTextFlags.EnterReturnsTrue);

        if (templateRenameError is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Rename", new Vector2(110f, 0f)) || submitted)
            {
                if (!TemplateNaming.TryNormalizeName(templateRenameBuffer, out var name, out var validationError))
                {
                    templateRenameError = validationError;
                }
                else
                {
                    var templateId = templateRenameTargetId;
                    RunOperation("rename the Template", () => templates.RenameTemplateAsync(templateId, name));
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- Template delete

    private void DrawTemplateDeletePopup()
    {
        if (pendingTemplateDeletePopup)
        {
            ImGui.OpenPopup(TemplateDeletePopupId);
            pendingTemplateDeletePopup = false;
        }

        if (!ImGui.BeginPopupModal(TemplateDeletePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var template = templates.FindTemplate(templateDeleteTargetId);
        if (template is null || template.IsBuiltIn)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Delete \"{template.DisplayName}\"?");
        EditorWidgets.Hint("It's moved to AetherFrame's Template trash folder, not destroyed. Its images are kept.");
        ImGui.Spacing();

        using (ImRaii.Disabled(IsBusy))
        {
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.18f, 0.18f, 1f)))
            {
                if (ImGui.Button("Delete", new Vector2(110f, 0f)))
                {
                    var templateId = template.TemplateId;
                    var name = template.DisplayName;
                    RunOperation("delete the Template", () => templates.DeleteTemplateAsync(templateId), () =>
                    {
                        if (selectedTemplateId == templateId)
                        {
                            selectedTemplateId = null;
                        }

                        statusMessage = $"Deleted \"{name}\".";
                    });
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
