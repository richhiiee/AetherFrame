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
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

    private static readonly Vector4 BuiltInBadgeColor = new(0.55f, 0.62f, 0.95f, 1f);
    private static readonly Vector4 UserTemplateBadgeColor = new(0.55f, 0.85f, 0.65f, 1f);

    private readonly Dictionary<Guid, string> templateCardIds = new();

    private LibraryView activeView = LibraryView.MyPlates;
    private Guid? selectedTemplateId;
    private string templateSearchText = string.Empty;

    private bool pendingTemplateChooserPopup;
    private Guid chosenTemplateId = BuiltInTemplateCatalog.AdventurePlateClassicId;

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
    /// on whether its content actually has Basic structure (see <see cref="BasicEditorSession.CanResetLayout"/>)
    /// — never a fixed choice per Template kind, so an arbitrary user Template opens sensibly.</summary>
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

            var document = library.GetSavedDocument(result.PlateId);
            var basic = document is not null && BasicEditorSession.CanResetLayout(document);
            RequestOpen(result.PlateId, basic);
        });
    }

    // ---------------------------------------------------------------- Template chooser (Create Plate)

    private void DrawTemplateChooserPopup()
    {
        if (pendingTemplateChooserPopup)
        {
            ImGui.OpenPopup(TemplateChooserPopupId);
            pendingTemplateChooserPopup = false;
        }

        if (!ImGui.BeginPopupModal(TemplateChooserPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TextDisabled("START");
        ImGui.Spacing();

        DrawTemplateChoice(BuiltInTemplateCatalog.AdventurePlateClassicId, "Adventure Plate Classic",
            "A ready-to-fill Adventure Plate with every section in place, filled from\nyour character where the game provides it. Opens in the Basic Editor.");
        DrawTemplateChoice(BuiltInTemplateCatalog.BlankCanvasId, "Blank Canvas",
            "An empty Adventure Plate canvas.\nOpens in the Advanced Editor.");

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
            var childHeight = Math.Min(140f, userTemplates.Count * ImGui.GetFrameHeightWithSpacing());
            using var child = ImRaii.Child("##MyTemplatesChoice", new Vector2(360f, childHeight), true);
            if (child.Success)
            {
                foreach (var template in userTemplates)
                {
                    DrawTemplateChoice(template.TemplateId, template.DisplayName, null);
                }
            }
        }

        ImGui.Spacing();
        var character = characterIdentity.CurrentCharacter;
        EditorWidgets.Hint(character is { } who
            ? $"The new Plate will belong to {DescribeCharacter(who)}. It becomes the Active Plate only if it's the character's first."
            : "No character is logged in, so the new Plate won't belong to a character yet.");
        ImGui.Spacing();

        using (ImRaii.Disabled(IsBusy))
        {
            if (ImGui.Button("Use Template", new Vector2(140f, 0f)))
            {
                UseTemplate(chosenTemplateId);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
        {
            ImGui.CloseCurrentPopup();
        }

        // Quieter than the two buttons above: managing Templates isn't the common path through
        // this popup, just a door into it (see DrawTemplatesView, a mode — not a permanent tab).
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(16f, 0f));
        ImGui.SameLine();
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

        ImGui.EndPopup();
    }

    private void DrawTemplateChoice(Guid templateId, string label, string? description)
    {
        if (ImGui.RadioButton(label, chosenTemplateId == templateId))
        {
            chosenTemplateId = templateId;
        }

        if (description is not null)
        {
            using (ImRaii.PushIndent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X))
            {
                EditorWidgets.Hint(description);
            }
        }

        ImGui.Spacing();
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
