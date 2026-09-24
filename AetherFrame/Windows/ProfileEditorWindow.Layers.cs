using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Layers panel: every element, topmost first, each row with visibility and lock toggles, a type
/// icon, and its (inline-renamable) name. Rows drag to reorder; selection is shared with the
/// canvas (it's the same <see cref="EditorSession.SelectedElementId"/>), so the two can never
/// disagree. A locked element is selectable here and on the canvas; locking only blocks transforms.
/// </summary>
internal sealed partial class ProfileEditorWindow
{
    private const string LayerDragPayloadType = "AF_LAYER";

    private static readonly string EyeIcon = FontAwesomeIcon.Eye.ToIconString();
    private static readonly string EyeSlashIcon = FontAwesomeIcon.EyeSlash.ToIconString();
    private static readonly string LockIcon = FontAwesomeIcon.Lock.ToIconString();
    private static readonly string LockOpenIcon = FontAwesomeIcon.LockOpen.ToIconString();
    private static readonly string TextTypeIcon = FontAwesomeIcon.Font.ToIconString();
    private static readonly string ImageTypeIcon = FontAwesomeIcon.Image.ToIconString();

    private static readonly byte[] LayerDragPayload = [1];

    // Stable per-element ImGui id strings, so rows don't allocate an id string every frame.
    private readonly Dictionary<Guid, string> layerIds = new();

    // The rows being drawn this frame (a snapshot, since row actions mutate the profile).
    private readonly List<ProfileElement> layerRows = new(ProfileDocument.MaxElementCount);

    private Guid? renamingElementId;
    private string renameBuffer = string.Empty;
    private bool renameFocusPending;
    private Guid? layerDragSourceId;
    private Guid? lastScrolledToSelection;

    private void DrawLayersPanel(ProfileDocument profile, Vector2 size)
    {
        using var panel = ImRaii.Child("##AetherFrameLayersPanel", size, true);
        if (!panel.Success)
        {
            return;
        }

        ImGui.TextDisabled("LAYERS");
        ImGui.SameLine();
        var countText = $"{profile.Elements.Count}";
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(countText).X);
        ImGui.TextDisabled(countText);
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y + 4f;
        var listHeight = Math.Max(60f, ImGui.GetContentRegionAvail().Y - footerHeight);

        using (var list = ImRaii.Child("##AetherFrameLayersList", new Vector2(-1, listHeight), false))
        {
            if (list.Success)
            {
                DrawLayerRows(profile);
            }
        }

        ImGui.Separator();
        DrawLayerFooter(profile);
    }

    private void DrawLayerRows(ProfileDocument profile)
    {
        if (profile.Elements.Count == 0)
        {
            EditorWidgets.Hint("No elements yet. Use + Text or + Image in the toolbar.");
            return;
        }

        // Snapshot in paint order (hidden included), shown topmost first. Iterating the snapshot
        // keeps this safe even though row actions (visibility, reorder, delete) mutate the profile
        // mid-loop.
        ProfilePaintOrder.Fill(profile, layerRows, includeHidden: true);

        for (var i = layerRows.Count - 1; i >= 0; i--)
        {
            DrawLayerRow(layerRows[i]);
        }

        layerRows.Clear();

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            layerDragSourceId = null;
        }

        // Clicking the empty area below the rows clears the selection, like the canvas does.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            editorSession.Select(null);
        }
    }

    private void DrawLayerRow(ProfileElement element)
    {
        if (!layerIds.TryGetValue(element.Id, out var id))
        {
            id = element.Id.ToString("N");
            layerIds[element.Id] = id;
        }

        using var pushId = ImRaii.PushId(id);

        var isSelected = editorSession.SelectedElementId == element.Id;
        var isRenaming = renamingElementId == element.Id;
        var rowHeight = ImGui.GetFrameHeight();
        var rowStart = ImGui.GetCursorPos();

        // Full-width row surface: selection highlight, click/double-click/right-click, and the
        // drag handle for reordering. The toggles and name are overlaid on top of it.
        ImGui.Selectable("##row", isSelected, ImGuiSelectableFlags.AllowItemOverlap | ImGuiSelectableFlags.AllowDoubleClick, new Vector2(0f, rowHeight));
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        var rowHovered = ImGui.IsItemHovered();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            SelectFromLayers(element.Id);
        }

        if (rowHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            BeginRename(element);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            RequestElementContextMenu(element.Id);
        }

        if (isSelected && lastScrolledToSelection != element.Id)
        {
            // Selected from the canvas: bring its row into view once.
            ImGui.SetScrollHereY();
            lastScrolledToSelection = element.Id;
        }

        if (!isRenaming && ImGui.BeginDragDropSource())
        {
            layerDragSourceId = element.Id;
            ImGui.SetDragDropPayload(LayerDragPayloadType, LayerDragPayload, ImGuiCond.None);
            ImGui.TextUnformatted(ProfileElementNames.GetDisplayName(element));
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            // Upper half: drop above this row (in front of it); lower half: below (behind it).
            var placeAbove = ImGui.GetMousePos().Y < (rowMin.Y + rowMax.Y) / 2f;
            var lineY = placeAbove ? rowMin.Y : rowMax.Y;
            ImGui.GetWindowDrawList().AddLine(new Vector2(rowMin.X, lineY), new Vector2(rowMax.X, lineY), ImGui.GetColorU32(EditorWidgets.AccentColor), 2f);

            var payload = ImGui.AcceptDragDropPayload(LayerDragPayloadType, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (!payload.IsNull && layerDragSourceId is { } sourceId && sourceId != element.Id)
            {
                editorSession.MoveLayer(sourceId, element.Id, placeAbove);
                layerDragSourceId = null;
            }

            ImGui.EndDragDropTarget();
        }

        // Overlay: [eye] [lock] [type] name
        ImGui.SetCursorPos(rowStart);
        var buttonSize = new Vector2(rowHeight, rowHeight);

        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
        {
            using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, element.Visible ? new Vector4(1f, 1f, 1f, 0.85f) : EditorWidgets.DimTextColor))
                {
                    if (ImGui.Button($"{(element.Visible ? EyeIcon : EyeSlashIcon)}##visible", buttonSize))
                    {
                        editorSession.SetElementVisible(element.Id, !element.Visible);
                    }
                }
            }

            EditorWidgets.Tooltip(element.Visible ? "Hide" : "Show");
            ImGui.SameLine(0f, 0f);

            using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, element.Locked ? EditorWidgets.WarningColor : new Vector4(1f, 1f, 1f, 0.28f)))
                {
                    if (ImGui.Button($"{(element.Locked ? LockIcon : LockOpenIcon)}##locked", buttonSize))
                    {
                        editorSession.SetElementLocked(element.Id, !element.Locked);
                    }
                }
            }

            EditorWidgets.Tooltip(element.Locked ? "Unlock" : "Lock (prevents moving on the canvas)");
        }

        ImGui.SameLine(0f, 4f);
        ImGui.AlignTextToFramePadding();
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.DimTextColor))
        {
            ImGui.TextUnformatted(element is ImageProfileElement ? ImageTypeIcon : TextTypeIcon);
        }

        ImGui.SameLine(0f, 6f);

        if (isRenaming)
        {
            DrawRenameField(element);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            var nameColor = element.Visible ? Vector4.One : EditorWidgets.DimTextColor;
            using (ImRaii.PushColor(ImGuiCol.Text, nameColor))
            {
                ImGui.TextUnformatted(ProfileElementNames.GetDisplayName(element));
            }

            if (rowHovered && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                ShowLayerTooltip(element);
            }
        }

        // Keep the next row starting exactly one row below, whatever the overlay did.
        ImGui.SetCursorPos(rowStart + new Vector2(0f, rowHeight + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void ShowLayerTooltip(ProfileElement element)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted(ProfileElementNames.GetDisplayName(element));

        // Only elements Basic mode still owns (a legacy tagline is ordinary Advanced content now).
        if (ProfileElementNames.GetRoleLabel(element.Role) is { } role && Domain.Basic.BasicSections.SectionOf(element.Role) is not null)
        {
            ImGui.TextDisabled($"Basic: {role}");
        }

        if (element is TextProfileElement text)
        {
            var preview = string.IsNullOrEmpty(text.Text) ? "(no text)" : text.Text.Length > 80 ? text.Text[..80] + "..." : text.Text;
            ImGui.TextDisabled(preview.Replace('\n', ' '));
        }

        ImGui.TextDisabled("Double-click to rename. Drag to reorder.");
    }

    private void SelectFromLayers(Guid elementId)
    {
        editorSession.Select(elementId);
        lastScrolledToSelection = elementId;
        selectElementTabPending = true;
    }

    private void BeginRename(ProfileElement element)
    {
        editorSession.Select(element.Id);
        lastScrolledToSelection = element.Id;
        renamingElementId = element.Id;
        renameBuffer = ProfileElementNames.GetDisplayName(element);
        renameFocusPending = true;
    }

    private void DrawRenameField(ProfileElement element)
    {
        ImGui.SetNextItemWidth(-1);
        if (renameFocusPending)
        {
            ImGui.SetKeyboardFocusHere();
            renameFocusPending = false;
        }

        ImGui.InputText("##rename", ref renameBuffer, ProfileElement.MaxNameLength, ImGuiInputTextFlags.AutoSelectAll);

        // Enter or clicking away deactivates the field and commits; Escape reverts the field
        // (ImGui) before deactivating, so it commits nothing.
        if (ImGui.IsItemDeactivated())
        {
            var newName = renameBuffer;

            // Unchanged automatic name: keep it automatic (empty) instead of freezing the fallback in.
            if (newName == ProfileElementNames.GetAutomaticName(element) && string.IsNullOrEmpty(element.Name))
            {
                newName = string.Empty;
            }

            editorSession.RenameElement(element.Id, newName);
            renamingElementId = null;
        }
        else if (!ImGui.IsItemActive() && !renameFocusPending && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsItemHovered())
        {
            // The field never took focus (e.g. focus went elsewhere first): a click anywhere
            // else simply abandons the rename instead of leaving the field stuck open.
            renamingElementId = null;
        }
    }

    private void DrawLayerFooter(ProfileDocument profile)
    {
        var selected = GetSelectedElement(profile);
        var size = ImGui.GetFrameHeight();

        using (ImRaii.Disabled(selected is null))
        {
            var id = selected?.Id ?? Guid.Empty;

            if (EditorWidgets.IconButton("LayerFront", FontAwesomeIcon.AngleDoubleUp, "Move to Front", size))
            {
                editorSession.BringToFront(id);
            }

            ImGui.SameLine(0f, 2f);
            if (EditorWidgets.IconButton("LayerForward", FontAwesomeIcon.AngleUp, "Move Forward", size))
            {
                editorSession.BringForward(id);
            }

            ImGui.SameLine(0f, 2f);
            if (EditorWidgets.IconButton("LayerBackward", FontAwesomeIcon.AngleDown, "Move Backward", size))
            {
                editorSession.SendBackward(id);
            }

            ImGui.SameLine(0f, 2f);
            if (EditorWidgets.IconButton("LayerBack", FontAwesomeIcon.AngleDoubleDown, "Move to Back", size))
            {
                editorSession.SendToBack(id);
            }

            var rightX = ImGui.GetWindowContentRegionMax().X - (size * 2f) - 2f;
            ImGui.SameLine(rightX);
            if (EditorWidgets.IconButton("LayerDuplicate", FontAwesomeIcon.Clone, "Duplicate (Ctrl+D)", size))
            {
                editorSession.DuplicateElement(id);
            }

            ImGui.SameLine(0f, 2f);
            using (ImRaii.PushColor(ImGuiCol.Text, EditorWidgets.ErrorColor))
            {
                if (EditorWidgets.IconButton("LayerDelete", FontAwesomeIcon.TrashAlt, "Delete (Del)", size))
                {
                    editorSession.RemoveElement(id);
                }
            }
        }
    }

    // ---------------------------------------------------------------- context menu

    /// <summary>
    /// Selects <paramref name="elementId"/> (if it isn't already selected) and requests that its
    /// context menu open. Shared by the canvas and the Layers panel, the two right-click entry
    /// points; the actual <c>ImGui.OpenPopup</c> call happens once, later, in <see cref="Draw"/>
    /// — see the field comment on <see cref="pendingContextMenuOpenElementId"/> for why.
    /// </summary>
    private void RequestElementContextMenu(Guid elementId)
    {
        if (editorSession.SelectedElementId != elementId)
        {
            editorSession.Select(elementId);
        }

        pendingContextMenuOpenElementId = elementId;
    }

    /// <summary>
    /// Opens (if requested this frame) and draws the element context menu popup. Called exactly
    /// once per frame, from <see cref="Draw"/>'s outermost scope, so the id-stack context of the
    /// <c>OpenPopup</c>/<c>BeginPopup</c> pair always matches regardless of which UI element the
    /// right-click actually came from.
    /// </summary>
    private void DrawElementContextMenuPopup(ProfileDocument profile)
    {
        if (pendingContextMenuOpenElementId is { } requestedElementId)
        {
            contextMenuElementId = requestedElementId;
            ImGui.OpenPopup(ElementContextMenuId);
            pendingContextMenuOpenElementId = null;
        }

        using var popup = ImRaii.Popup(ElementContextMenuId);
        if (!popup.Success)
        {
            return;
        }

        var element = profile.Elements.Find(e => e.Id == contextMenuElementId);
        if (element is null)
        {
            // The targeted element vanished (e.g. Delete elsewhere) while the menu was open.
            ImGui.CloseCurrentPopup();
            return;
        }

        DrawElementContextMenuContents(element);
    }

    /// <summary>
    /// Quick-action menu body for one element: common actions for every element type, Image-
    /// specific actions, then Delete visually separated at the bottom to reduce accidental hits.
    /// Every action here routes through the same EditorSession methods as the Inspector/toolbar,
    /// so ownership checks, dirty tracking, and Undo/Redo are identical either way.
    /// </summary>
    private void DrawElementContextMenuContents(ProfileElement element)
    {
        ImGui.TextDisabled(ProfileElementNames.GetDisplayName(element));
        ImGui.Separator();

        if (ImGui.MenuItem("Rename"))
        {
            BeginRename(element);
        }

        var visible = element.Visible;
        if (ImGui.MenuItem("Visible", string.Empty, ref visible))
        {
            editorSession.SetElementVisible(element.Id, visible);
        }

        var locked = element.Locked;
        if (ImGui.MenuItem("Locked", string.Empty, ref locked))
        {
            editorSession.SetElementLocked(element.Id, locked);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
        {
            editorSession.DuplicateElement(element.Id);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Move Forward"))
        {
            editorSession.BringForward(element.Id);
        }

        if (ImGui.MenuItem("Move Backward"))
        {
            editorSession.SendBackward(element.Id);
        }

        if (ImGui.MenuItem("Move to Front"))
        {
            editorSession.BringToFront(element.Id);
        }

        if (ImGui.MenuItem("Move to Back"))
        {
            editorSession.SendToBack(element.Id);
        }

        using (var alignMenu = ImRaii.Menu("Align to Canvas"))
        {
            if (alignMenu.Success)
            {
                using (ImRaii.Disabled(element.Locked))
                {
                    DrawAlignMenuItem("Left", CanvasAlignment.Left);
                    DrawAlignMenuItem("Horizontal Center", CanvasAlignment.HorizontalCenter);
                    DrawAlignMenuItem("Right", CanvasAlignment.Right);
                    ImGui.Separator();
                    DrawAlignMenuItem("Top", CanvasAlignment.Top);
                    DrawAlignMenuItem("Vertical Center", CanvasAlignment.VerticalCenter);
                    DrawAlignMenuItem("Bottom", CanvasAlignment.Bottom);
                }
            }
        }

        if (element is ImageProfileElement imageElement)
        {
            ImGui.Separator();

            if (ImGui.MenuItem("Replace Image..."))
            {
                var elementId = imageElement.Id;
                OpenImageFileDialog("Replace Image", path => editorSession.ReplaceImage(elementId, path));
            }

            // Transforms stay off-limits while locked.
            var unlocked = !imageElement.Locked;

            if (ImGui.MenuItem("Rotate Left 90", string.Empty, false, unlocked))
            {
                RotateImageBy(imageElement.Id, -90f);
            }

            if (ImGui.MenuItem("Rotate Right 90", string.Empty, false, unlocked))
            {
                RotateImageBy(imageElement.Id, 90f);
            }

            if (ImGui.MenuItem("Reset Rotation", string.Empty, false, unlocked && imageElement.RotationDegrees != 0f))
            {
                ApplyImmediateImageEdit(imageElement.Id, image => image.RotationDegrees = 0f);
            }

            if (ImGui.MenuItem("Flip Horizontal", string.Empty, imageElement.FlipX))
            {
                ApplyImmediateImageEdit(imageElement.Id, image => image.FlipX = !image.FlipX);
            }

            if (ImGui.MenuItem("Flip Vertical", string.Empty, imageElement.FlipY))
            {
                ApplyImmediateImageEdit(imageElement.Id, image => image.FlipY = !image.FlipY);
            }

            if (ImGui.MenuItem("Reset Size to Native Aspect Ratio", string.Empty, false, unlocked))
            {
                editorSession.ResetImageToNativeAspect(imageElement.Id);
            }

            var preserveAspectRatio = imageElement.PreserveAspectRatio;
            if (ImGui.MenuItem("Preserve Aspect Ratio", string.Empty, ref preserveAspectRatio))
            {
                ApplyImmediateImageEdit(imageElement.Id, image => image.PreserveAspectRatio = preserveAspectRatio);
            }
        }

        ImGui.Separator();

        // Visually separated (own section, past a divider) and tinted, so it can't be hit by
        // the same casual click that would land on Duplicate or a toggle above it.
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.45f, 0.45f, 1f)))
        {
            if (ImGui.MenuItem("Delete", "Del"))
            {
                editorSession.RemoveElement(element.Id);
            }
        }

        void DrawAlignMenuItem(string label, CanvasAlignment alignment)
        {
            if (ImGui.MenuItem(label))
            {
                editorSession.AlignSelected(alignment);
            }
        }
    }

    /// <summary>90-degree quick rotation (context menu/Inspector): one immediate, normalized history entry.</summary>
    private void RotateImageBy(Guid elementId, float deltaDegrees)
    {
        ApplyImmediateImageEdit(elementId, image => image.RotationDegrees = RotationGeometry.NormalizeDegrees(image.RotationDegrees + deltaDegrees));
    }
}
