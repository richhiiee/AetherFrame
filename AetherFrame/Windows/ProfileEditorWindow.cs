using System;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

internal sealed class ProfileEditorWindow : Window, IDisposable
{
    private const float HandleScreenSize = 8f;

    private const float LeftPanelWidth = 210f;
    private const float RightPanelWidth = 320f;
    private const float MinCanvasWidth = 360f;
    private const float NewElementTextBoxHeight = 48f;

    private const string ElementContextMenuId = "##AetherFrameElementContextMenu";
    private const string CanvasResizePopupId = "##AetherFrameCanvasResizePopup";
    private const float MinCanvasDimension = 100f;

    private static readonly string[] AlignmentLabels = ["Left", "Center", "Right"];
    private static readonly string[] FitModeLabels = ["Cover", "Contain", "Stretch"];
    private static readonly string[] FontFamilyLabels = ProfileFontCatalog.All.Select(f => f.DisplayName).ToArray();

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileFontService fontService;
    private readonly FileDialogManager fileDialogManager;
    private readonly Action openProfileView;
    private readonly Action openBasicEditor;

    // Which element the currently-open context menu targets (read back when drawing the popup's
    // body). Right-click can be detected from two different places with two different ImGui ID
    // stacks (the canvas child vs. an Elements-panel row) — ImGui.OpenPopup/BeginPopup only
    // match when called from the SAME id-stack scope, so both sites just record the request
    // here, and Draw() is the single place that actually calls OpenPopup/BeginPopup, always
    // from the same (outermost) scope.
    private Guid contextMenuElementId;
    private Guid? pendingContextMenuOpenElementId;

    // Same deferred-open pattern as the element context menu above (see
    // pendingContextMenuOpenElementId): the resize-choice popup is only ever requested from
    // DrawCanvasControls, but is opened/drawn once from Draw()'s outer scope so it keeps
    // rendering across frames even if the Inspector's active tab changes while it's up.
    private (float Width, float Height) canvasResizePromptTarget;
    private (float Width, float Height)? pendingCanvasResizeOpenRequest;

    // Runtime-only scratch buffers for the Canvas tab's custom width/height fields — resynced
    // from the profile's actual canvas size (see lastSyncedCustomCanvasSize) only when it
    // changes from outside the fields themselves (preset click, undo/redo, profile switch), so
    // in-progress typing is never clobbered by the per-frame redraw.
    private float customCanvasWidthInput = ProfileDocument.DefaultCanvasWidth;
    private float customCanvasHeightInput = ProfileDocument.DefaultCanvasHeight;
    private Vector2 lastSyncedCustomCanvasSize = new(-1f, -1f);

    // Fit-to-window viewport state — all runtime only, never persisted with the profile.
    // lastCanvasPanelSize starts at a sentinel that can never match a real panel size, so Auto
    // Fit's size-change check always fires once on the first Draw after (re)opening.
    private Vector2 lastCanvasPanelSize = new(-1f, -1f);
    private bool resetCanvasScrollPending;

    internal ProfileEditorWindow(
        ProfileService profileService,
        EditorSession editorSession,
        KeyboardShortcutService keyboardShortcutService,
        ImageTextureCache imageTextureCache,
        ProfileFontService fontService,
        FileDialogManager fileDialogManager,
        Action openProfileView,
        Action openBasicEditor)
        : base("AetherFrame Profile Editor##ProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.editorSession = editorSession;
        this.keyboardShortcutService = keyboardShortcutService;
        this.imageTextureCache = imageTextureCache;
        this.fontService = fontService;
        this.fileDialogManager = fileDialogManager;
        this.openProfileView = openProfileView;
        this.openBasicEditor = openBasicEditor;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Called by the window system whenever this window (re)opens. Auto Fit always turns back
    /// on for a freshly-opened editor, regardless of whatever manual zoom was left over from a
    /// previous session, and the sentinel forces the very next Draw to (re)compute a fresh fit
    /// against whatever the panel size actually is now.
    /// </summary>
    public override void OnOpen()
    {
        editorSession.AutoFit = true;
        lastCanvasPanelSize = new Vector2(-1f, -1f);
    }

    /// <summary>
    /// Called by the window system exactly once when this window closes. Draw won't run again
    /// until it reopens, so this is the only reliable place to tell the keyboard service to
    /// stop intercepting immediately rather than leaving it stuck on stale "focused" state.
    /// </summary>
    public override void OnClose()
    {
        keyboardShortcutService.SetEditorFocusState(editorFocused: false, textInputActive: false);
        fileDialogManager.Reset();
    }

    public override void Draw()
    {
        // Drawn unconditionally so an in-progress file pick isn't stranded if the profile
        // becomes unavailable (e.g. character logs out) while the dialog is open.
        fileDialogManager.Draw();

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile is currently loaded.");
            return;
        }

        PublishKeyboardFocusState();
        ApplyPendingShortcutActions();

        DrawToolbar(profile);
        ImGui.Separator();

        var contentAvail = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing;
        var statusBarHeight = ImGui.GetFrameHeightWithSpacing() + spacing.Y;
        var bodyHeight = Math.Max(160f, contentAvail.Y - statusBarHeight - spacing.Y);
        var canvasWidth = Math.Max(MinCanvasWidth, contentAvail.X - LeftPanelWidth - RightPanelWidth - (spacing.X * 2f));

        DrawElementsPanel(profile, new Vector2(LeftPanelWidth, bodyHeight));
        ImGui.SameLine();
        DrawCanvasPanel(profile, new Vector2(canvasWidth, bodyHeight));
        ImGui.SameLine();
        DrawInspectorPanel(profile, new Vector2(RightPanelWidth, bodyHeight));

        ImGui.Separator();
        DrawStatusBar(profile);

        // Outside every child region, so this is the one consistent id-stack scope both the
        // canvas and the Elements panel's right-click can safely target — see
        // DrawElementContextMenuPopup.
        DrawElementContextMenuPopup(profile);
        DrawCanvasResizePromptPopup();
    }

    /// <summary>
    /// Fixed row of editor-wide actions and the dirty/saved indicator. Drawn before any of the
    /// scrolling panels below, so it (and Save Profile in particular) is always reachable
    /// without scrolling, regardless of how long the element list or inspector gets.
    /// </summary>
    private void DrawToolbar(ProfileDocument profile)
    {
        ImGui.TextUnformatted($"Editing: {profile.Name}");

        ImGui.SameLine();
        if (ImGui.Button("Basic Editor"))
        {
            openBasicEditor();
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12f, 0f));
        ImGui.SameLine();

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
        ImGui.Dummy(new Vector2(12f, 0f));
        ImGui.SameLine();

        var canSave = !profileService.IsBusy && editorSession.IsDirty;
        using (ImRaii.Disabled(!canSave))
        {
            if (ImGui.Button("Save Profile"))
            {
                editorSession.SaveProfile();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("View Profile"))
        {
            openProfileView();
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12f, 0f));
        ImGui.SameLine();

        // Purely a canvas display preference — see EditorSession.ShowGuides for why it's never
        // persisted and never affects selection itself, only the chrome drawn for it.
        var showGuides = editorSession.ShowGuides;
        if (ImGui.Checkbox("Guides", ref showGuides))
        {
            editorSession.ShowGuides = showGuides;
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12f, 0f));
        ImGui.SameLine();
        DrawSaveStateIndicator();

        if (editorSession.ErrorMessage is { } error)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), error);
        }
    }

    /// <summary>Compact, non-technical save state: never exposes Revision or busy internals.</summary>
    private void DrawSaveStateIndicator()
    {
        if (profileService.IsBusy)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.4f, 1f), "Saving...");
        }
        else if (editorSession.IsDirty)
        {
            ImGui.TextColored(new Vector4(1f, 0.65f, 0.3f, 1f), "Unsaved");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "Saved");
        }
    }

    /// <summary>
    /// Left panel: the element list (own scroll region), selection-driven Duplicate/Delete/Z
    /// order actions, and the Add Text/Add Image controls. The list scrolls independently so
    /// the Add controls stay reachable regardless of how many elements exist.
    /// </summary>
    private void DrawElementsPanel(ProfileDocument profile, Vector2 size)
    {
        using var panel = ImRaii.Child("##AetherFrameElementsPanel", size, true);
        if (!panel.Success)
        {
            return;
        }

        ImGui.TextDisabled("ELEMENTS");
        ImGui.Separator();

        var hasSelection = editorSession.SelectedElementId is not null;
        var frameHeight = ImGui.GetFrameHeightWithSpacing();

        // Rough estimate of the footer's height (selection actions + add controls) so the list
        // above gets an explicit size and scrolls on its own. Being slightly off just means the
        // outer panel itself picks up the slack with its own scrollbar — never a hard failure.
        var footerHeight = (frameHeight * 2f)
            + (hasSelection ? frameHeight * 2f : 0f)
            + NewElementTextBoxHeight
            + (frameHeight * 2f)
            + 32f;

        var listHeight = Math.Max(60f, ImGui.GetContentRegionAvail().Y - footerHeight);
        using (var list = ImRaii.Child("##AetherFrameElementsList", new Vector2(-1, listHeight), false))
        {
            if (list.Success)
            {
                // Snapshot before iterating: Select/Duplicate/Remove below can mutate the live
                // list, and ImGui widgets can fire mid-loop, which would otherwise invalidate
                // this enumeration.
                foreach (var element in profile.Elements.ToArray())
                {
                    DrawElementListRow(element);
                }
            }
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!hasSelection))
        {
            var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;

            if (ImGui.Button("Duplicate", new Vector2(halfWidth, 0f)) && editorSession.SelectedElementId is { } duplicateId)
            {
                editorSession.DuplicateElement(duplicateId);
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete", new Vector2(halfWidth, 0f)) && editorSession.SelectedElementId is { } deleteId)
            {
                editorSession.RemoveElement(deleteId);
            }

            if (editorSession.SelectedElementId is { } zOrderId)
            {
                DrawZOrderRow(zOrderId);
            }
        }

        ImGui.Separator();

        var atCapacity = profile.Elements.Count >= ProfileDocument.MaxElementCount;
        if (atCapacity)
        {
            ImGui.TextWrapped($"At the maximum of {ProfileDocument.MaxElementCount} elements.");
        }

        var buffer = editorSession.NewElementText;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline("##NewElementText", ref buffer, TextProfileElement.MaxTextLength, new Vector2(-1, NewElementTextBoxHeight)))
        {
            editorSession.NewElementText = buffer;
        }

        using (ImRaii.Disabled(atCapacity))
        {
            if (ImGui.Button("Add Text", new Vector2(-1, 0f)))
            {
                editorSession.AddTextElement();
            }

            if (ImGui.Button("Add Image", new Vector2(-1, 0f)))
            {
                OpenImageFileDialog("Add Image", path => editorSession.AddImageElement(path));
            }
        }
    }

    private void DrawElementListRow(ProfileElement element)
    {
        var isSelected = editorSession.SelectedElementId == element.Id;

        ImGui.PushID(element.Id.ToString());
        if (ImGui.Selectable(GetElementListLabel(element), isSelected))
        {
            editorSession.Select(element.Id);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            RequestElementContextMenu(element.Id);
        }

        ImGui.PopID();
    }

    private static string GetElementListLabel(ProfileElement element)
    {
        var baseLabel = element switch
        {
            TextProfileElement text => string.IsNullOrWhiteSpace(text.Text) ? "Text (empty)" : Truncate(text.Text, 22),
            ImageProfileElement => "Image",
            _ => "Element",
        };

        var suffix = string.Empty;
        if (!element.Visible)
        {
            suffix += " (hidden)";
        }

        if (element.Locked)
        {
            suffix += " (locked)";
        }

        return baseLabel + suffix;
    }

    private static string Truncate(string value, int maxLength)
    {
        var singleLine = value.Replace('\n', ' ');
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "...";
    }

    /// <summary>
    /// Bring Forward / Send Backward / Bring to Front / Send to Back for the given element.
    /// These operate on ZIndex under the hood, but that number itself is never shown.
    /// </summary>
    private void DrawZOrderRow(Guid elementId)
    {
        var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;

        if (ImGui.Button("Bring Forward", new Vector2(halfWidth, 0f)))
        {
            editorSession.BringForward(elementId);
        }

        ImGui.SameLine();
        if (ImGui.Button("Send Backward", new Vector2(halfWidth, 0f)))
        {
            editorSession.SendBackward(elementId);
        }

        if (ImGui.Button("Bring to Front", new Vector2(halfWidth, 0f)))
        {
            editorSession.BringToFront(elementId);
        }

        ImGui.SameLine();
        if (ImGui.Button("Send to Back", new Vector2(halfWidth, 0f)))
        {
            editorSession.SendToBack(elementId);
        }
    }

    /// <summary>
    /// Selects <paramref name="elementId"/> (if it isn't already selected) and requests that its
    /// context menu open. Shared by the canvas and the Elements panel, the two right-click entry
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
        var visible = element.Visible;
        if (ImGui.MenuItem("Visible", string.Empty, ref visible))
        {
            editorSession.ApplyImmediateEdit(element.Id, e => e.Visible = visible);
        }

        var locked = element.Locked;
        if (ImGui.MenuItem("Locked", string.Empty, ref locked))
        {
            editorSession.ApplyImmediateEdit(element.Id, e => e.Locked = locked);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Duplicate"))
        {
            editorSession.DuplicateElement(element.Id);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Bring Forward"))
        {
            editorSession.BringForward(element.Id);
        }

        if (ImGui.MenuItem("Send Backward"))
        {
            editorSession.SendBackward(element.Id);
        }

        if (ImGui.MenuItem("Bring to Front"))
        {
            editorSession.BringToFront(element.Id);
        }

        if (ImGui.MenuItem("Send to Back"))
        {
            editorSession.SendToBack(element.Id);
        }

        if (element is ImageProfileElement imageElement)
        {
            ImGui.Separator();

            if (ImGui.MenuItem("Replace Image"))
            {
                var elementId = imageElement.Id;
                OpenImageFileDialog("Replace Image", path => editorSession.ReplaceImage(elementId, path));
            }

            if (ImGui.MenuItem("Rotate Left 90"))
            {
                RotateImageBy(imageElement.Id, -90f);
            }

            if (ImGui.MenuItem("Rotate Right 90"))
            {
                RotateImageBy(imageElement.Id, 90f);
            }

            if (ImGui.MenuItem("Reset Rotation", string.Empty, false, imageElement.RotationDegrees != 0f))
            {
                ApplyImmediateImageEdit(imageElement.Id, image => image.RotationDegrees = 0f);
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
            if (ImGui.MenuItem("Delete"))
            {
                editorSession.RemoveElement(element.Id);
            }
        }
    }

    /// <summary>90-degree quick rotation (context menu): one immediate, normalized history entry.</summary>
    private void RotateImageBy(Guid elementId, float deltaDegrees)
    {
        ApplyImmediateImageEdit(elementId, image => image.RotationDegrees = RotationGeometry.NormalizeDegrees(image.RotationDegrees + deltaDegrees));
    }

    /// <summary>
    /// Right panel: properties for the currently selected element only, plus a separate
    /// Background tab so background configuration never mixes with element editing.
    /// </summary>
    private void DrawInspectorPanel(ProfileDocument profile, Vector2 size)
    {
        using var panel = ImRaii.Child("##AetherFrameInspectorPanel", size, true);
        if (!panel.Success)
        {
            return;
        }

        ImGui.TextDisabled("INSPECTOR");
        ImGui.Separator();

        using var tabBar = ImRaii.TabBar("##AetherFrameInspectorTabs");
        if (!tabBar.Success)
        {
            return;
        }

        using (var elementTab = ImRaii.TabItem("Element"))
        {
            if (elementTab.Success)
            {
                DrawSelectedElementInspector(profile);
            }
        }

        using (var backgroundTab = ImRaii.TabItem("Background"))
        {
            if (backgroundTab.Success)
            {
                ImGui.Spacing();
                DrawBackgroundControls(profile);
            }
        }

        using (var canvasTab = ImRaii.TabItem("Canvas"))
        {
            if (canvasTab.Success)
            {
                ImGui.Spacing();
                DrawCanvasControls(profile);
            }
        }
    }

    private void DrawSelectedElementInspector(ProfileDocument profile)
    {
        ImGui.Spacing();

        var selected = editorSession.SelectedElementId is { } selectedId
            ? profile.Elements.Find(e => e.Id == selectedId)
            : null;

        if (selected is null)
        {
            ImGui.TextWrapped("Select an element on the canvas or in the Elements panel to edit its properties.");
            return;
        }

        ImGui.PushID(selected.Id.ToString());

        switch (selected)
        {
            case TextProfileElement textElement:
                DrawTextElementInspector(textElement);
                break;
            case ImageProfileElement imageElement:
                DrawImageElementInspector(imageElement);
                break;
        }

        ImGui.PopID();
    }

    private void DrawTextElementInspector(TextProfileElement textElement)
    {
        // Locked stays interactive even while locked, so the user can unlock the element.
        var locked = textElement.Locked;
        if (ImGui.Checkbox("Locked", ref locked))
        {
            editorSession.ApplyImmediateEdit(textElement.Id, element => element.Locked = locked);
        }

        using (ImRaii.Disabled(textElement.Locked))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("CONTENT");

            // Deferred edit, same pattern as Font Size/Color below: every keystroke updates the
            // live profile immediately (so the canvas and Presentation Mode reflect it as you
            // type), but only one history entry is recorded for the whole typing session, once
            // the widget deactivates. While this widget holds keyboard focus, ImGui reports
            // WantTextInput, which already makes KeyboardShortcutService stand down — so Ctrl+Z/
            // Ctrl+Y here stay ordinary text-field undo/redo and never reach AetherFrame's
            // global Undo/Redo.
            var text = textElement.Text;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextMultiline("##TextContent", ref text, TextProfileElement.MaxTextLength, new Vector2(-1, 80f)))
            {
                ContinueTextEdit(textElement.Id, element => element.Text = text);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            ImGui.Spacing();
            ImGui.TextDisabled("FONT");

            var familyIndex = 0;
            for (var i = 0; i < ProfileFontCatalog.All.Count; i++)
            {
                if (ProfileFontCatalog.All[i].Id == textElement.FontFamily)
                {
                    familyIndex = i;
                    break;
                }
            }

            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Family", ref familyIndex, FontFamilyLabels, FontFamilyLabels.Length))
            {
                var newFamily = ProfileFontCatalog.All[familyIndex].Id;
                ApplyImmediateTextEdit(textElement.Id, element => element.FontFamily = newFamily);
            }

            var fontSize = textElement.FontSize;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("Size", ref fontSize, TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize))
            {
                ContinueTextEdit(textElement.Id, element => element.FontSize = fontSize);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            ImGui.Spacing();
            ImGui.TextDisabled("STYLE");

            // Bold/Italic are only ever shown for a family with the real face to back them —
            // see ProfileFontCatalog. Underline/Strikethrough are plain line-draws, unrelated to
            // the font face, so they're always available regardless of family.
            var fontDescriptor = ProfileFontCatalog.Resolve(textElement.FontFamily);

            if (fontDescriptor.SupportsBold)
            {
                var bold = textElement.Bold;
                if (ImGui.Checkbox("Bold", ref bold))
                {
                    ApplyImmediateTextEdit(textElement.Id, element => element.Bold = bold);
                }

                if (fontDescriptor.SupportsItalic)
                {
                    ImGui.SameLine();
                }
            }

            if (fontDescriptor.SupportsItalic)
            {
                var italic = textElement.Italic;
                if (ImGui.Checkbox("Italic", ref italic))
                {
                    ApplyImmediateTextEdit(textElement.Id, element => element.Italic = italic);
                }
            }

            var underline = textElement.Underline;
            if (ImGui.Checkbox("Underline", ref underline))
            {
                ApplyImmediateTextEdit(textElement.Id, element => element.Underline = underline);
            }

            ImGui.SameLine();
            var strikethrough = textElement.Strikethrough;
            if (ImGui.Checkbox("Strikethrough", ref strikethrough))
            {
                ApplyImmediateTextEdit(textElement.Id, element => element.Strikethrough = strikethrough);
            }

            ImGui.Spacing();
            ImGui.TextDisabled("LAYOUT");

            var alignmentIndex = (int)textElement.Alignment;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Alignment", ref alignmentIndex, AlignmentLabels, AlignmentLabels.Length))
            {
                var newAlignment = (TextAlignment)alignmentIndex;
                ApplyImmediateTextEdit(textElement.Id, element => element.Alignment = newAlignment);
            }

            var wrap = textElement.Wrap;
            if (ImGui.Checkbox("Wrap", ref wrap))
            {
                ApplyImmediateTextEdit(textElement.Id, element => element.Wrap = wrap);
            }

            ImGui.Spacing();
            ImGui.TextDisabled("COLOR");

            var color = textElement.Color;
            if (ImGui.ColorEdit4("##TextColor", ref color))
            {
                ContinueTextEdit(textElement.Id, element => element.Color = color);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            ImGui.Spacing();
            ImGui.TextDisabled("VISIBILITY");

            var visible = textElement.Visible;
            if (ImGui.Checkbox("Visible", ref visible))
            {
                editorSession.ApplyImmediateEdit(textElement.Id, element => element.Visible = visible);
            }
        }
    }

    /// <summary>Routes a discrete (checkbox/combo) <see cref="TextProfileElement"/> edit.</summary>
    private void ApplyImmediateTextEdit(Guid elementId, Action<TextProfileElement> update)
    {
        editorSession.ApplyImmediateEdit(elementId, element =>
        {
            if (element is TextProfileElement textElement)
            {
                update(textElement);
            }
        });
    }

    /// <summary>Routes a live, in-progress (slider/color) <see cref="TextProfileElement"/> edit.</summary>
    private void ContinueTextEdit(Guid elementId, Action<TextProfileElement> update)
    {
        editorSession.BeginOrContinueEdit(elementId, element =>
        {
            if (element is TextProfileElement textElement)
            {
                update(textElement);
            }
        });
    }

    private void DrawImageElementInspector(ImageProfileElement imageElement)
    {
        ImGui.TextDisabled("IMAGE ELEMENT");
        ImGui.Spacing();
        ImGui.Separator();

        // Locked stays interactive even while locked, so the user can unlock the element.
        var locked = imageElement.Locked;
        if (ImGui.Checkbox("Locked", ref locked))
        {
            editorSession.ApplyImmediateEdit(imageElement.Id, element => element.Locked = locked);
        }

        using (ImRaii.Disabled(imageElement.Locked))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("APPEARANCE");

            var opacity = imageElement.Opacity;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("Opacity", ref opacity, 0f, 1f))
            {
                ContinueImageEdit(imageElement.Id, element => element.Opacity = opacity);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            var preserveAspectRatio = imageElement.PreserveAspectRatio;
            if (ImGui.Checkbox("Preserve Aspect Ratio", ref preserveAspectRatio))
            {
                ApplyImmediateImageEdit(imageElement.Id, element => element.PreserveAspectRatio = preserveAspectRatio);
            }

            ImGui.Spacing();
            ImGui.TextDisabled("ROTATION");

            // Precise, arbitrary rotation; the context menu's 90-degree steps are the quick
            // version of the same underlying property. Same deferred-edit pattern as Opacity: a
            // full slider drag is one history entry, not one per intermediate value. ImGui
            // sliders already support Ctrl+Click to type an exact degree value.
            var rotation = imageElement.RotationDegrees;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("Rotation", ref rotation, 0f, 359.9f, "%.1f deg"))
            {
                var normalized = RotationGeometry.NormalizeDegrees(rotation);
                ContinueImageEdit(imageElement.Id, element => element.RotationDegrees = normalized);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                editorSession.CommitPendingEdit();
            }

            ImGui.Spacing();
            ImGui.TextDisabled("VISIBILITY");

            var visible = imageElement.Visible;
            if (ImGui.Checkbox("Visible", ref visible))
            {
                editorSession.ApplyImmediateEdit(imageElement.Id, element => element.Visible = visible);
            }

            ImGui.Spacing();
            ImGui.TextDisabled("ACTIONS");

            if (ImGui.Button("Replace Image", new Vector2(-1, 0f)))
            {
                var elementId = imageElement.Id;
                OpenImageFileDialog("Replace Image", path => editorSession.ReplaceImage(elementId, path));
            }
        }
    }

    /// <summary>Routes a discrete (checkbox) <see cref="ImageProfileElement"/> edit.</summary>
    private void ApplyImmediateImageEdit(Guid elementId, Action<ImageProfileElement> update)
    {
        editorSession.ApplyImmediateEdit(elementId, element =>
        {
            if (element is ImageProfileElement imageElement)
            {
                update(imageElement);
            }
        });
    }

    /// <summary>Routes a live, in-progress (slider) <see cref="ImageProfileElement"/> edit.</summary>
    private void ContinueImageEdit(Guid elementId, Action<ImageProfileElement> update)
    {
        editorSession.BeginOrContinueEdit(elementId, element =>
        {
            if (element is ImageProfileElement imageElement)
            {
                update(imageElement);
            }
        });
    }

    /// <summary>
    /// Background configuration, kept out of the per-element inspector entirely (its own
    /// Inspector tab) since it isn't a selectable canvas element.
    /// </summary>
    private void DrawBackgroundControls(ProfileDocument profile)
    {
        ImGui.TextUnformatted("Background: " + (profile.BackgroundAssetId is null ? "None set" : "Set"));
        ImGui.Spacing();

        if (ImGui.Button("Set Background", new Vector2(-1, 0f)))
        {
            OpenImageFileDialog("Set Background", path => editorSession.SetBackground(path));
        }

        if (profile.BackgroundAssetId is null)
        {
            return;
        }

        if (ImGui.Button("Remove Background", new Vector2(-1, 0f)))
        {
            editorSession.RemoveBackground();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("FIT & OPACITY");

        var fitModeIndex = (int)profile.BackgroundFitMode;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("Fit", ref fitModeIndex, FitModeLabels, FitModeLabels.Length))
        {
            var newFitMode = (BackgroundFitMode)fitModeIndex;
            editorSession.ApplyBackgroundEdit(document => document.BackgroundFitMode = newFitMode);
        }

        var opacity = profile.BackgroundOpacity;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("Opacity", ref opacity, 0f, 1f))
        {
            editorSession.BeginOrContinueBackgroundEdit(document => document.BackgroundOpacity = opacity);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingBackgroundEdit();
        }
    }

    /// <summary>
    /// Canvas tab: preset/custom canvas size controls. Never resizes on its own — every action
    /// here just requests the resize-choice popup (see <see cref="DrawCanvasResizePromptPopup"/>),
    /// which is what actually calls <see cref="EditorSession.ApplyCanvasResize"/> once the user
    /// picks Resize Canvas Only or Scale Contents Proportionally.
    /// </summary>
    private void DrawCanvasControls(ProfileDocument profile)
    {
        var currentSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight);
        if (currentSize != lastSyncedCustomCanvasSize)
        {
            customCanvasWidthInput = profile.CanvasWidth;
            customCanvasHeightInput = profile.CanvasHeight;
            lastSyncedCustomCanvasSize = currentSize;
        }

        var matchedPreset = ProfileCanvasPreset.Match(profile.CanvasWidth, profile.CanvasHeight);
        ImGui.TextUnformatted($"Current: {profile.CanvasWidth:0} x {profile.CanvasHeight:0}");
        ImGui.TextDisabled(matchedPreset is null ? "Custom size" : matchedPreset.Name);

        ImGui.Spacing();
        ImGui.TextDisabled("PRESET");

        foreach (var preset in ProfileCanvasPreset.All)
        {
            using (ImRaii.Disabled(preset == matchedPreset))
            {
                if (ImGui.Button($"{preset.Name} ({preset.Width:0}x{preset.Height:0})", new Vector2(-1, 0f)))
                {
                    pendingCanvasResizeOpenRequest = (preset.Width, preset.Height);
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("CUSTOM");

        var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        ImGui.SetNextItemWidth(halfWidth);
        ImGui.InputFloat("##CustomCanvasWidth", ref customCanvasWidthInput, 0f, 0f, "%.0f W");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(halfWidth);
        ImGui.InputFloat("##CustomCanvasHeight", ref customCanvasHeightInput, 0f, 0f, "%.0f H");

        var validCustomSize = customCanvasWidthInput >= MinCanvasDimension && customCanvasHeightInput >= MinCanvasDimension;
        using (ImRaii.Disabled(!validCustomSize))
        {
            if (ImGui.Button("Apply Custom Size", new Vector2(-1, 0f)))
            {
                pendingCanvasResizeOpenRequest = (customCanvasWidthInput, customCanvasHeightInput);
            }
        }

        if (!validCustomSize)
        {
            ImGui.TextWrapped($"Width and height must each be at least {MinCanvasDimension:0}.");
        }
    }

    /// <summary>
    /// Opens (if requested this frame) and draws the "how should the canvas resize?" popup,
    /// mirroring <see cref="DrawElementContextMenuPopup"/>'s deferred-open pattern so it keeps
    /// rendering across frames regardless of which Inspector tab is active.
    /// </summary>
    private void DrawCanvasResizePromptPopup()
    {
        if (pendingCanvasResizeOpenRequest is { } requested)
        {
            canvasResizePromptTarget = requested;
            ImGui.OpenPopup(CanvasResizePopupId);
            pendingCanvasResizeOpenRequest = null;
        }

        using var popup = ImRaii.Popup(CanvasResizePopupId);
        if (!popup.Success)
        {
            return;
        }

        var target = canvasResizePromptTarget;
        const float popupContentWidth = 280f;

        ImGui.TextUnformatted($"Resize canvas to {target.Width:0} x {target.Height:0}?");
        ImGui.Spacing();

        if (ImGui.Button("Resize Canvas Only", new Vector2(popupContentWidth, 0f)))
        {
            editorSession.ApplyCanvasResize(target.Width, target.Height, scaleContentsProportionally: false);
            ImGui.CloseCurrentPopup();
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + popupContentWidth);
        ImGui.TextDisabled("Keeps every element's position and size exactly as-is; only the canvas bounds change.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        if (ImGui.Button("Scale Contents Proportionally", new Vector2(popupContentWidth, 0f)))
        {
            editorSession.ApplyCanvasResize(target.Width, target.Height, scaleContentsProportionally: true);
            ImGui.CloseCurrentPopup();
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + popupContentWidth);
        ImGui.TextDisabled("Scales every element's position and size to match the new canvas proportions.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Cancel", new Vector2(popupContentWidth, 0f)))
        {
            ImGui.CloseCurrentPopup();
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

    /// <summary>
    /// Publishes this frame's focus/text-input state to <see cref="KeyboardShortcutService"/>
    /// so it knows whether to intercept shortcuts on the NEXT <c>Framework.Update</c> tick.
    /// Detection/suppression happen there now, not here — see that class for why.
    /// </summary>
    private void PublishKeyboardFocusState()
    {
        var io = ImGui.GetIO();
        var editorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var textInputActive = io.WantTextInput;

        keyboardShortcutService.SetEditorFocusState(editorFocused, textInputActive);

        if (editorFocused && !textInputActive)
        {
            // Claim the keyboard at the ImGui/WndProc level too, so clicks/typing route to us.
            // The actual suppression that stops FFXIV from also reacting happens earlier, in
            // KeyboardShortcutService during Framework.Update — this is a secondary measure.
            ImGui.SetNextFrameWantCaptureKeyboard(true);
        }
    }

    /// <summary>
    /// Applies shortcut actions queued by <see cref="KeyboardShortcutService"/> during
    /// <c>Framework.Update</c> (key detection/suppression already happened there; this just
    /// performs the resulting edit through the normal, render-thread-only EditorSession API).
    /// </summary>
    private void ApplyPendingShortcutActions()
    {
        foreach (var action in keyboardShortcutService.DequeuePendingActions())
        {
            switch (action.Kind)
            {
                case EditorShortcutActionKind.Undo:
                    editorSession.Undo();
                    break;
                case EditorShortcutActionKind.Redo:
                    editorSession.Redo();
                    break;
                case EditorShortcutActionKind.Delete:
                    if (editorSession.SelectedElementId is { } selectedId)
                    {
                        editorSession.RemoveElement(selectedId);
                    }

                    break;
                case EditorShortcutActionKind.Nudge:
                    editorSession.NudgeSelected(action.NudgeDelta);
                    break;
            }
        }
    }

    /// <summary>
    /// Center panel: the pannable/zoomable canvas in its own scroll region so it never shifts
    /// due to inspector or element list content.
    /// </summary>
    private void DrawCanvasPanel(ProfileDocument profile, Vector2 size)
    {
        using var child = ImRaii.Child("##AetherFrameCanvasScroll", size, true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child.Success)
        {
            return;
        }

        // The interior content region (post-border/padding), not the outer `size` passed in —
        // this is what the canvas actually has to fit inside.
        var availablePanelSize = ImGui.GetContentRegionAvail();

        if (editorSession.AutoFit && Vector2.DistanceSquared(availablePanelSize, lastCanvasPanelSize) > 0.25f)
        {
            // Covers both the initial Fit-to-Window on open (lastCanvasPanelSize starts at an
            // impossible sentinel) and continuous re-fitting while the panel is being resized.
            editorSession.ApplyFitZoom(availablePanelSize);
            resetCanvasScrollPending = true;
        }

        lastCanvasPanelSize = availablePanelSize;

        if (resetCanvasScrollPending)
        {
            ImGui.SetScrollX(0f);
            ImGui.SetScrollY(0f);
            resetCanvasScrollPending = false;
        }

        var zoom = editorSession.Zoom;
        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * zoom;

        // Centered when the canvas is smaller than the panel (the fitted/typical case); flush at
        // the scroll origin — exactly the prior behavior — once zoomed in past the panel size,
        // where the user pans via the ordinary scrollbars instead.
        var centeringOffset = Vector2.Max(Vector2.Zero, (availablePanelSize - canvasScreenSize) / 2f);
        var canvasOrigin = ImGui.GetCursorScreenPos() + centeringOffset;
        ImGui.SetCursorScreenPos(canvasOrigin);

        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton("##AetherFrameCanvasArea", canvasScreenSize);
        var canvasHovered = ImGui.IsItemHovered();

        var showGuides = editorSession.ShowGuides;

        // The background isn't a ProfileElement: it's always painted first (beneath every
        // element) and is deliberately excluded from hit testing below, so it can never be
        // selected, dragged, resized, or reordered like an ordinary element.
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, zoom, imageTextureCache, fontService, showGuides);

        if (showGuides)
        {
            // Editor-only chrome: outlines the logical canvas bounds. Not part of the finished
            // profile's visual content, so ProfileRenderer (shared with presentation mode) doesn't
            // draw it.
            drawList.AddRect(canvasOrigin, canvasOrigin + canvasScreenSize, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));
        }

        // Hit testing below walks this array in reverse, so the visually topmost element (paint
        // order: ascending ZIndex, ties broken by list order) is always tested/selected first.
        var visibleElements = profile.Elements.Where(e => e.Visible).OrderBy(e => e.ZIndex).ToArray();

        var selectedElement = editorSession.SelectedElementId is { } selectedId
            ? profile.Elements.Find(e => e.Id == selectedId)
            : null;

        Vector2[]? selectedScreenCorners = null;

        if (selectedElement is not null)
        {
            // Rotated corners (identity for rotation 0), not the old axis-aligned rect, so the
            // outline and handles always match what's actually drawn/hit-tested.
            var rotationDegrees = RotationGeometry.GetRotationDegrees(selectedElement);
            var logicalCorners = RotationGeometry.GetRotatedCorners(selectedElement.Position, selectedElement.Size, rotationDegrees);
            selectedScreenCorners =
            [
                canvasOrigin + logicalCorners[0] * zoom,
                canvasOrigin + logicalCorners[1] * zoom,
                canvasOrigin + logicalCorners[2] * zoom,
                canvasOrigin + logicalCorners[3] * zoom,
            ];

            if (showGuides)
            {
                var outlineColor = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f));
                drawList.AddQuad(selectedScreenCorners[0], selectedScreenCorners[1], selectedScreenCorners[2], selectedScreenCorners[3], outlineColor);

                if (!selectedElement.Locked)
                {
                    DrawResizeHandles(drawList, selectedScreenCorners);
                }
            }
        }

        // Guides off also disables resize-handle interaction (nothing is drawn to grab) by
        // simply not handing HandleCanvasInput any corners to hit-test against; plain click-to-
        // select and drag-to-move on the canvas stay fully functional either way.
        HandleCanvasInput(profile, visibleElements, selectedElement, showGuides ? selectedScreenCorners : null, canvasOrigin, canvasHovered, zoom);
    }

    /// <summary>
    /// Bottom status bar: zoom control plus compact, non-technical canvas/selection info. Never
    /// exposes ZIndex, Revision, or other implementation details.
    /// </summary>
    private void DrawStatusBar(ProfileDocument profile)
    {
        var zoom = editorSession.Zoom;
        ImGui.SetNextItemWidth(140);
        if (ImGui.SliderFloat("Zoom", ref zoom, EditorSession.MinZoom, EditorSession.MaxZoom))
        {
            // A manual zoom change is an explicit override: keep it, and stop auto-recalculating
            // on panel resize until the user asks for Fit again.
            editorSession.Zoom = zoom;
            editorSession.AutoFit = false;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Fit"))
        {
            // lastCanvasPanelSize was captured moments ago by this same frame's canvas draw, so
            // this takes effect immediately (the canvas itself catches up next frame, same as
            // any other immediate-mode zoom change).
            editorSession.AutoFit = true;
            editorSession.ApplyFitZoom(lastCanvasPanelSize);
            resetCanvasScrollPending = true;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"Canvas: {profile.CanvasWidth:0}x{profile.CanvasHeight:0}");

        var selectedElement = editorSession.SelectedElementId is { } selectedId
            ? profile.Elements.Find(e => e.Id == selectedId)
            : null;
        var selectedLabel = selectedElement switch
        {
            TextProfileElement => "Text",
            ImageProfileElement => "Image",
            _ => "None",
        };

        ImGui.SameLine();
        ImGui.TextUnformatted($"Selected: {selectedLabel}");

        ImGui.SameLine();
        ImGui.TextUnformatted($"Elements: {profile.Elements.Count}/{ProfileDocument.MaxElementCount}");
    }

    private void HandleCanvasInput(
        ProfileDocument profile,
        ProfileElement[] visibleElementsInPaintOrder,
        ProfileElement? selectedElement,
        Vector2[]? selectedScreenCorners,
        Vector2 canvasOrigin,
        bool canvasHovered,
        float zoom)
    {
        var mouseScreen = ImGui.GetMousePos();
        var logicalMouse = (mouseScreen - canvasOrigin) / zoom;
        var leftClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var leftReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        var rightClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        if (editorSession.ActiveInteraction != ElementInteractionKind.None)
        {
            if (leftReleased)
            {
                editorSession.EndInteraction();
            }
            else
            {
                var mouseDelta = ImGui.GetIO().MouseDelta;
                if (mouseDelta.X != 0f || mouseDelta.Y != 0f)
                {
                    editorSession.UpdateInteraction(logicalMouse);
                }
            }

            return;
        }

        if (!canvasHovered)
        {
            return;
        }

        if (selectedElement is not null && !selectedElement.Locked && selectedScreenCorners is not null
            && TryGetHoveredHandle(mouseScreen, selectedScreenCorners, out var hoveredHandle))
        {
            ImGui.SetMouseCursor(GetResizeCursor(selectedScreenCorners, hoveredHandle));

            if (leftClicked)
            {
                editorSession.BeginResize(selectedElement, hoveredHandle, logicalMouse);
                return;
            }
        }

        if (rightClicked)
        {
            var rightHitElement = HitTestElement(visibleElementsInPaintOrder, logicalMouse);
            if (rightHitElement is not null)
            {
                RequestElementContextMenu(rightHitElement.Id);
            }

            return;
        }

        if (!leftClicked)
        {
            return;
        }

        var hitElement = HitTestElement(visibleElementsInPaintOrder, logicalMouse);
        editorSession.Select(hitElement?.Id);

        if (hitElement is not null && !hitElement.Locked)
        {
            editorSession.BeginDrag(hitElement, logicalMouse);
        }
    }

    private static void DrawResizeHandles(ImDrawListPtr drawList, Vector2[] screenCorners)
    {
        var color = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f));
        var half = HandleScreenSize / 2f;

        foreach (var corner in screenCorners)
        {
            drawList.AddRectFilled(corner - new Vector2(half, half), corner + new Vector2(half, half), color);
        }
    }

    /// <summary>
    /// <paramref name="screenCorners"/> is in the same perimeter order as
    /// <see cref="RotationGeometry.GetRotatedCorners"/> (TopLeft, TopRight, BottomRight,
    /// BottomLeft) — <paramref name="handle"/> identifies which LOCAL corner of the element was
    /// hit, not which corner it currently appears at on screen (rotation can move that visually).
    /// </summary>
    private static bool TryGetHoveredHandle(Vector2 mouseScreen, Vector2[] screenCorners, out ResizeHandle handle)
    {
        var half = HandleScreenSize;

        ResizeHandle[] handleForCornerIndex = [ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomRight, ResizeHandle.BottomLeft];

        for (var i = 0; i < screenCorners.Length; i++)
        {
            var center = screenCorners[i];
            var min = center - new Vector2(half, half);
            var max = center + new Vector2(half, half);

            if (mouseScreen.X >= min.X && mouseScreen.X <= max.X && mouseScreen.Y >= min.Y && mouseScreen.Y <= max.Y)
            {
                handle = handleForCornerIndex[i];
                return true;
            }
        }

        handle = ResizeHandle.None;
        return false;
    }

    /// <summary>
    /// Picks the diagonal resize cursor (NW-SE vs NE-SW) that matches the CURRENT visual angle
    /// between the hovered corner and its opposite — not <paramref name="handle"/>'s local
    /// identity, which points at a different visual diagonal once the element is rotated. At 90
    /// degrees, for example, the local TopLeft/BottomRight pair (the NW-SE diagonal at rotation
    /// 0) visually sits on the NE-SW diagonal instead.
    /// </summary>
    private static ImGuiMouseCursor GetResizeCursor(Vector2[] screenCorners, ResizeHandle handle)
    {
        var index = handle switch
        {
            ResizeHandle.TopLeft => 0,
            ResizeHandle.TopRight => 1,
            ResizeHandle.BottomRight => 2,
            _ => 3, // BottomLeft
        };
        var oppositeIndex = (index + 2) % 4;

        // Same-signed X/Y offset to the opposite corner means it's visually down-right (or
        // up-left) of the hovered one, i.e. the NW-SE diagonal; opposite signs mean NE-SW.
        var diff = screenCorners[oppositeIndex] - screenCorners[index];
        return diff.X * diff.Y >= 0f ? ImGuiMouseCursor.ResizeNwse : ImGuiMouseCursor.ResizeNesw;
    }

    /// <summary>
    /// Topmost-first hit test: walks the paint-ordered array backwards. For a rotated element,
    /// the mouse point is inverse-rotated around the element's center into its local (unrotated)
    /// coordinate space before testing against its plain local rectangle.
    /// </summary>
    private static ProfileElement? HitTestElement(ProfileElement[] paintOrderElements, Vector2 logicalPoint)
    {
        for (var i = paintOrderElements.Length - 1; i >= 0; i--)
        {
            var element = paintOrderElements[i];
            var rotationDegrees = RotationGeometry.GetRotationDegrees(element);
            var testPoint = rotationDegrees == 0f
                ? logicalPoint
                : RotationGeometry.RotatePoint(logicalPoint, RotationGeometry.GetCenter(element.Position, element.Size), -rotationDegrees);

            var min = element.Position;
            var max = element.Position + element.Size;

            if (testPoint.X >= min.X && testPoint.X <= max.X && testPoint.Y >= min.Y && testPoint.Y <= max.Y)
            {
                return element;
            }
        }

        return null;
    }
}
