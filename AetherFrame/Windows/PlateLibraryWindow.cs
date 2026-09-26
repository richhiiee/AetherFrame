using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates: the visual collection of every saved Plate, and the way into the editors and the
/// Plate Viewer. Cards show a thumbnail (or a fallback built from the Plate's own background),
/// the name, and whether it's the current character's Active Plate; the selected Plate's actions
/// sit below the grid. Split across partial files: this one (lifecycle, header, card grid),
/// <c>.Actions.cs</c> (actions, prompts, and running Library operations), and <c>.Packages.cs</c>
/// (Export and Import of .aetherframe files).
///
/// Never shows technical identifiers (ids, versions, file names, revisions). Works with no
/// character logged in — only Set Active needs one.
/// </summary>
internal sealed partial class PlateLibraryWindow : Window, IDisposable
{
    // Card sizes in unscaled pixels, at Dalamud's global UI scale when drawn, so a card keeps room
    // for its (scaled) name on a high-DPI screen.
    private static float CardWidth => EditorWidgets.Scaled(196f);
    private static float CardPadding => EditorWidgets.Scaled(8f);
    private const float ThumbnailAspect = 16f / 9f;
    private const string CardDragPayloadType = "AF_PLATE";

    // The window's minimum size, and its size the first time it opens: room for four cards across.
    private static readonly Vector2 MinimumWindowSize = new(560f, 440f);
    private static readonly Vector2 FirstUseSize = new(880f, 620f);

    private static readonly byte[] CardDragPayload = [1];
    private static readonly Vector4 CardColor = new(1f, 1f, 1f, 0.04f);
    private static readonly Vector4 CardHoverColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 FallbackBackdropColor = new(0.16f, 0.17f, 0.21f, 1f);
    private static readonly Vector4 ActiveBadgeColor = new(0.95f, 0.78f, 0.30f, 1f);

    private readonly PlateLibraryService library;
    private readonly TemplateLibraryService templates;
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly CharacterIdentityService characterIdentity;
    private readonly PlateThumbnailService thumbnails;
    private readonly PlateThumbnailTextures thumbnailTextures;
    private readonly PlateThumbnailService templateThumbnails;
    private readonly PlateThumbnailTextures templateThumbnailTextures;
    private readonly ProfileRenderResources renderResources;

    // My Plates card previews: the saved documents they draw, by Plate id and version (see
    // PlateCardPreviewCache), and the Plates the grid listed this frame (to drop the rest).
    private readonly PlateCardPreviewCache cardPreviews = new();
    private readonly HashSet<Guid> listedPlateIds = new();
    private readonly Action openBasicEditor;
    private readonly Action openAdvancedEditor;
    private readonly Func<EditorSurfaceKind?> activeEditor;
    private readonly AdvancedEntryGate advancedEntry;
    private readonly Action<Guid> showInViewer;
    private readonly Action<ProfileDocument> showDocumentInViewer;
    private readonly PlatePackageService packages;
    private readonly FileDialogManager fileDialogManager;
    private readonly Action<string> beginImport;

    private readonly Dictionary<Guid, string> cardIds = new();

    private Guid? selectedPlateId;
    private string searchText = string.Empty;
    private Guid? dragSourcePlateId;

    internal PlateLibraryWindow(
        PlateLibraryService library,
        TemplateLibraryService templates,
        ProfileService profileService,
        EditorSession editorSession,
        CharacterIdentityService characterIdentity,
        PlateThumbnailService thumbnails,
        PlateThumbnailTextures thumbnailTextures,
        PlateThumbnailService templateThumbnails,
        PlateThumbnailTextures templateThumbnailTextures,
        ProfileRenderResources renderResources,
        Action openBasicEditor,
        Action openAdvancedEditor,
        Func<EditorSurfaceKind?> activeEditor,
        BasicGuidance basicGuidance,
        Action<Guid> showInViewer,
        Action<ProfileDocument> showDocumentInViewer,
        PlatePackageService packages,
        FileDialogManager fileDialogManager,
        Action<string> beginImport)
        : base("My Plates##AetherFramePlateLibrary")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.library = library;
        this.templates = templates;
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.characterIdentity = characterIdentity;
        this.thumbnails = thumbnails;
        this.thumbnailTextures = thumbnailTextures;
        this.templateThumbnails = templateThumbnails;
        this.templateThumbnailTextures = templateThumbnailTextures;
        this.renderResources = renderResources;
        this.openBasicEditor = openBasicEditor;
        this.openAdvancedEditor = openAdvancedEditor;
        this.activeEditor = activeEditor;
        advancedEntry = new AdvancedEntryGate(basicGuidance);
        this.showInViewer = showInViewer;
        this.showDocumentInViewer = showDocumentInViewer;
        this.packages = packages;
        this.fileDialogManager = fileDialogManager;
        this.beginImport = beginImport;
    }

    public void Dispose()
    {
    }

    public override void OnClose()
    {
        // An Advanced open still waiting on the Basic suggestion is dropped (nothing opens, nothing
        // is handled); the next request asks again.
        advancedEntry.Abandon();
        thumbnailTextures.Clear();
        templateThumbnailTextures.Clear();
        cardPreviews.Clear();
    }

    /// <summary>My Plates is always what this window opens to — Manage Templates is a mode
    /// entered from inside a session, never something that persists across reopens.</summary>
    public override void OnOpen()
    {
        activeView = LibraryView.MyPlates;
    }

    public override void PreDraw() => EditorWidgets.SetFirstUseSize(FirstUseSize, MinimumWindowSize);

    public override void Draw()
    {
        AdvanceOperation();
        AdvanceGuardedOpen();

        if (!library.IsLoaded)
        {
            ImGui.TextDisabled(libraryLoadFailed ? "My Plates couldn't be loaded. See the Dalamud log for details." : "Loading your Plates...");
            return;
        }

        // Templates isn't a permanent top-level tab: My Plates is always what this window opens
        // to. activeView only switches to Templates for as long as the player is inside Manage
        // Templates (entered from the Create Plate chooser), and switches back on its own "Back
        // to My Plates" action or whenever this window is reopened.
        if (activeView == LibraryView.MyPlates)
        {
            DrawMyPlatesView();
        }
        else
        {
            DrawTemplatesView();
        }

        DrawTemplateChooserPopup();
        DrawRenamePopup();
        DrawDeletePopup(characterIdentity.CurrentCharacter);
        DrawUnsavedChangesPopup();
        DrawBasicGuidancePopup();
        DrawOverwritePopup();
        DrawSaveAsTemplatePopup();

        // While the Create Plate chooser is open, its own Rename/Delete requests (from a row's
        // context menu) are drawn from inside the chooser's popup scope instead — see
        // DrawTemplateChooserPopup for why: ImGui.OpenPopup() picks its stack level from how many
        // BeginPopup/EndPopup blocks it's lexically nested inside at the moment it's called, and
        // opening a second popup at the SAME level the chooser occupies closes the chooser first.
        // Calling these two from here too whenever that happens would open Rename/Delete twice in
        // one frame for the same popup id, which is unsafe — so exactly one of the two call sites
        // ever runs on a given frame.
        if (!ImGui.IsPopupOpen(TemplateChooserPopupId))
        {
            DrawTemplateRenamePopup();
            DrawTemplateDeletePopup();
        }

        fileDialogManager.Draw();
    }

    private void DrawMyPlatesView()
    {
        var character = characterIdentity.CurrentCharacter;
        var activePlateId = character is { } who ? library.GetActivePlateId(who.ContentId) : null;
        var plates = library.Search(searchText);
        var allPlates = library.GetOrderedPlates();

        if (selectedPlateId is { } selected && allPlates.All(p => p.PlateId != selected))
        {
            selectedPlateId = null;
        }

        DrawHeader(character, allPlates.Count);
        ImGui.Separator();

        // Two plain text lines now (status/info, then the right-click hint) — no button row.
        var footerHeight = (ImGui.GetTextLineHeightWithSpacing() * 2f) + EditorWidgets.Scaled(4f);
        using (var grid = ImRaii.Child("##PlateGrid", new Vector2(-1, -footerHeight), false))
        {
            if (grid.Success)
            {
                DrawGrid(plates, allPlates.Count, activePlateId, character);
            }
        }

        ImGui.Separator();
        DrawStatusFooter();
    }

    // ---------------------------------------------------------------- header

    private void DrawHeader(CharacterContext? character, int plateCount)
    {
        if (ImGui.Button("Create Plate"))
        {
            pendingTemplateChooserPopup = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Import"))
        {
            OpenImportDialog();
        }

        EditorWidgets.Tooltip("Add a Plate from an .aetherframe file. It's checked and previewed first,\nand always added as a new Plate.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(EditorWidgets.Scaled(220f));
        ImGui.InputTextWithHint("##PlateSearch", "Search Plates...", ref searchText, 64);

        // Never the character's name or World (see MyPlatesCharacterText): only whether one is
        // logged in at all, since Set Active needs one.
        if (MyPlatesCharacterText.HeaderStatus(character) is { } characterStatus)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(characterStatus);
            EditorWidgets.Tooltip("You can still browse, preview, create, and edit Plates.\nLog in to a character to choose its Active Plate.");
        }

        if (plateCount > 0)
        {
            var countText = plateCount == 1 ? "1 Plate" : $"{plateCount} Plates";
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(countText).X));
            ImGui.TextDisabled(countText);
        }
    }

    // ---------------------------------------------------------------- card grid

    private void DrawGrid(IReadOnlyList<PlateSummary> plates, int totalCount, Guid? activePlateId, CharacterContext? character)
    {
        if (totalCount == 0)
        {
            DrawEmptyLibrary();
            return;
        }

        if (plates.Count == 0)
        {
            ImGui.TextDisabled("No Plates match your search.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing;
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((available + spacing.X) / (CardWidth + spacing.X)));
        var canReorder = string.IsNullOrWhiteSpace(searchText);

        listedPlateIds.Clear();
        for (var i = 0; i < plates.Count; i++)
        {
            if (i % columns != 0)
            {
                ImGui.SameLine();
            }

            listedPlateIds.Add(plates[i].PlateId);
            DrawCard(plates[i], plates[i].PlateId == activePlateId, canReorder, character, activePlateId);
        }

        // Card previews are kept only for the Plates this list shows (deleted or filtered-out ones are dropped).
        cardPreviews.Retain(listedPlateIds);

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            dragSourcePlateId = null;
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            selectedPlateId = null;
        }
    }

    private void DrawEmptyLibrary()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("You don't have any Plates yet.");
        ImGui.TextDisabled("A Plate is a complete Adventure Plate style design. Make as many as you like;");
        ImGui.TextDisabled("each character can choose one to be its Active Plate.");
        ImGui.Spacing();
        if (ImGui.Button("Create Your First Plate"))
        {
            pendingTemplateChooserPopup = true;
        }
    }

    private void DrawCard(PlateSummary plate, bool isActive, bool canReorder, CharacterContext? character, Guid? activePlateId)
    {
        if (!cardIds.TryGetValue(plate.PlateId, out var id))
        {
            id = "##PlateCard" + plate.PlateId.ToString("N");
            cardIds[plate.PlateId] = id;
        }

        var thumbnailSize = new Vector2(CardWidth - (CardPadding * 2f), (CardWidth - (CardPadding * 2f)) / ThumbnailAspect);
        var cardSize = new Vector2(CardWidth, thumbnailSize.Y + (CardPadding * 3f) + ImGui.GetTextLineHeight());

        ImGui.InvisibleButton(id, cardSize);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var isSelected = selectedPlateId == plate.PlateId;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            selectedPlateId = plate.PlateId;
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && plate.IsReady)
        {
            RequestEdit(plate.PlateId);
        }

        var contextMenuId = $"##PlateCardMenu{plate.PlateId:N}";
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            selectedPlateId = plate.PlateId;
            ImGui.OpenPopup(contextMenuId);
        }

        using (var menu = ImRaii.Popup(contextMenuId))
        {
            if (menu.Success)
            {
                DrawPlateContextMenuItems(plate, character, activePlateId);
            }
        }

        if (hovered && plate.Problem is { } problem)
        {
            ImGui.SetTooltip(problem);
        }
        else if (hovered && plate.HasUnsupportedElements)
        {
            ImGui.SetTooltip(EditorWidgets.UnsupportedElementsWarning);
        }

        if (canReorder)
        {
            DrawCardDragAndDrop(plate, min, max);
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? CardHoverColor : CardColor), 6f);
        if (isSelected)
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(EditorWidgets.AccentColor), 6f, ImDrawFlags.None, 2f);
        }

        var thumbnailMin = min + new Vector2(CardPadding);
        var thumbnailMax = thumbnailMin + thumbnailSize;
        DrawThumbnail(drawList, plate, thumbnailMin, thumbnailMax);

        if (isActive)
        {
            DrawActiveBadge(drawList, thumbnailMin, thumbnailMax);
        }

        if (plate.HasUnsupportedElements)
        {
            DrawCompatibilityMarker(drawList, thumbnailMin, thumbnailMax);
        }

        // Name, clipped to the card.
        var textPos = new Vector2(thumbnailMin.X, thumbnailMax.Y + CardPadding);
        drawList.PushClipRect(textPos, new Vector2(thumbnailMax.X, max.Y), true);
        var nameColor = plate.IsReady ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(EditorWidgets.DimTextColor);
        drawList.AddText(textPos, nameColor, plate.DisplayName);
        drawList.PopClipRect();
    }

    private void DrawCardDragAndDrop(PlateSummary plate, Vector2 min, Vector2 max)
    {
        if (ImGui.BeginDragDropSource())
        {
            dragSourcePlateId = plate.PlateId;
            ImGui.SetDragDropPayload(CardDragPayloadType, CardDragPayload, ImGuiCond.None);
            ImGui.TextUnformatted(plate.DisplayName);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            // Left half: drop before this card; right half: after it.
            var placeAfter = ImGui.GetMousePos().X > (min.X + max.X) / 2f;
            var lineX = placeAfter ? max.X + 2f : min.X - 2f;
            ImGui.GetWindowDrawList().AddLine(new Vector2(lineX, min.Y), new Vector2(lineX, max.Y), ImGui.GetColorU32(EditorWidgets.AccentColor), 3f);

            var payload = ImGui.AcceptDragDropPayload(CardDragPayloadType, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (!payload.IsNull && dragSourcePlateId is { } sourceId && sourceId != plate.PlateId)
            {
                var targetId = plate.PlateId;
                RunOperation("reorder", () => library.MovePlateAsync(sourceId, targetId, placeAfter));
                dragSourcePlateId = null;
            }

            ImGui.EndDragDropTarget();
        }
    }

    /// <summary>
    /// The card's preview: a thumbnail image when one is Ready; otherwise the real saved Plate in
    /// miniature, drawn by the shared <see cref="ProfileRenderer"/> (background, portrait, identity,
    /// sections, Components and their overflow — tiny text as soft bars, see <see cref="PlateCardPreview"/>),
    /// fitted to the card and clipped to it; the background-only fallback only when the Plate can't
    /// be loaded. Off-screen cards draw nothing.
    /// </summary>
    private void DrawThumbnail(ImDrawListPtr drawList, PlateSummary plate, Vector2 min, Vector2 max)
    {
        if (plate.IsReady)
        {
            var versionKey = PlateThumbnailService.VersionKeyFor(plate.Revision, plate.ModifiedUtc);
            var thumbnail = thumbnails.Get(plate.PlateId, versionKey, () => library.GetSavedDocument(plate.PlateId));
            if (thumbnailTextures.GetWrapOrNull(plate.PlateId, thumbnail) is { } wrap)
            {
                drawList.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFFu);
                return;
            }

            if (!ImGui.IsRectVisible(min, max))
            {
                return; // scrolled out of view: nothing to draw this frame
            }

            if (cardPreviews.Get(plate.PlateId, versionKey, () => library.GetSavedDocument(plate.PlateId)) is { } preview)
            {
                var fit = PlateCardPreview.Fit(max - min, preview.Bounds);
                if (fit.Scale > 0f)
                {
                    drawList.AddRectFilled(min, max, ImGui.GetColorU32(FallbackBackdropColor), 4f);
                    drawList.PushClipRect(min, max, true);
                    ProfileRenderer.Draw(drawList, preview.Document, min + fit.CanvasOffset, fit.Scale, renderResources, PlateCardPreview.Options);
                    drawList.PopClipRect();
                    return;
                }
            }
        }

        DrawFallbackThumbnail(drawList, plate, min, max);
    }

    /// <summary>
    /// A cheap stand-in built only from the saved background's colors (no textures, no text
    /// rendering): enough to tell Plates apart at a glance.
    /// </summary>
    private void DrawFallbackThumbnail(ImDrawListPtr drawList, PlateSummary plate, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(FallbackBackdropColor), 4f);

        var background = plate.IsReady ? library.GetSavedDocument(plate.PlateId)?.Background : null;
        var icon = FontAwesomeIcon.IdCard;

        if (!plate.IsReady)
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

    /// <summary>A small warning glyph in the thumbnail's lower-left corner (the card's tooltip explains it).</summary>
    private static void DrawCompatibilityMarker(ImDrawListPtr drawList, Vector2 thumbnailMin, Vector2 thumbnailMax)
    {
        var icon = EditorWidgets.GetIconString(FontAwesomeIcon.ExclamationTriangle);
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var size = ImGui.CalcTextSize(icon);
            var inset = EditorWidgets.Scaled(5f);
            var margin = new Vector2(EditorWidgets.Scaled(3f));
            var pos = new Vector2(thumbnailMin.X + inset, thumbnailMax.Y - size.Y - inset);
            drawList.AddRectFilled(pos - margin, pos + size + margin, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)), 4f);
            drawList.AddText(pos, ImGui.GetColorU32(EditorWidgets.WarningColor), icon);
        }
    }

    private static void DrawActiveBadge(ImDrawListPtr drawList, Vector2 thumbnailMin, Vector2 thumbnailMax)
    {
        const string label = "Active";
        var textSize = ImGui.CalcTextSize(label);
        var padding = EditorWidgets.Scaled(new Vector2(6f, 2f));
        var inset = EditorWidgets.Scaled(4f);
        var badgeMax = new Vector2(thumbnailMax.X - inset, thumbnailMin.Y + inset + textSize.Y + (padding.Y * 2f));
        var badgeMin = new Vector2(badgeMax.X - textSize.X - (padding.X * 2f), thumbnailMin.Y + inset);

        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(ActiveBadgeColor), 4f);
        drawList.AddText(badgeMin + padding, ImGui.GetColorU32(new Vector4(0.1f, 0.08f, 0.02f, 1f)), label);
    }
}
