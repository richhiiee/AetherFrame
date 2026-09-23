using System;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// Primary, simplified editing experience modeled on the FFXIV Adventure Plate: an Identity
/// Header (name, title, tagline — see the .Identity.cs part), a portrait, a message, and a
/// background — nothing about the freeform canvas
/// (coordinates, Z order, element ids, rotation, layer ordering, asset management) is exposed
/// here. Edits the same <see cref="ProfileDocument"/> as <see cref="ProfileEditorWindow"/>,
/// through the same shared <see cref="EditorSession"/>, via <see cref="BasicEditorSession"/>'s
/// role-based lookups — so undo/redo and dirty state always agree between the two windows, and
/// switching modes never resets or duplicates profile data.
/// </summary>
internal sealed partial class BasicProfileEditorWindow : Window, IDisposable, IEditorSurface
{
    private const float PreviewHeight = 420f;

    // Indexed by ProfileImageFit (Stretch, Fit, Fill) — the shared background model's fit modes.
    private static readonly string[] FitModeLabels = ["Stretch", "Fit", "Fill"];

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly BasicEditorSession basicEditorSession;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileRenderResources renderResources;
    private readonly FileDialogManager fileDialogManager;
    private readonly Action openAdvancedEditor;
    private readonly Action openLibrary;
    private readonly EditorSurfaceCoordinator surfaces;

    internal BasicProfileEditorWindow(
        ProfileService profileService,
        EditorSession editorSession,
        BasicEditorSession basicEditorSession,
        ImageTextureCache imageTextureCache,
        ProfileRenderResources renderResources,
        FileDialogManager fileDialogManager,
        Action openAdvancedEditor,
        Action openLibrary,
        EditorSurfaceCoordinator surfaces)
        : base("AetherFrame Basic Editor##BasicProfileEditorWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 640),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.editorSession = editorSession;
        this.basicEditorSession = basicEditorSession;
        this.imageTextureCache = imageTextureCache;
        this.renderResources = renderResources;
        this.fileDialogManager = fileDialogManager;
        this.openAdvancedEditor = openAdvancedEditor;
        this.openLibrary = openLibrary;
        this.surfaces = surfaces;
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

        // Opening Basic mode never creates or changes anything: reserved elements (and the
        // Identity settings) are only created by the first explicit edit that needs them.
        basicEditorSession.Identity.RefineLayout();

        if (EditorWidgets.IconButton("MyPlates", FontAwesomeIcon.ThLarge, "My Plates"))
        {
            openLibrary();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(profile.Name);
        ImGui.SameLine();
        if (ImGui.Button("Advanced Editor"))
        {
            openAdvancedEditor();
        }

        EditorWidgets.UnsupportedElementsNotice(profile);
        ImGui.Separator();

        DrawPreview(profile);
        ImGui.Separator();

        DrawIdentitySection(profile);
        ImGui.Separator();

        DrawPortraitControls(profile);
        ImGui.Separator();

        DrawTextField("Message", ProfileElementRole.BasicMessage, multiline: true);
        ImGui.Separator();

        DrawBackgroundControls(profile);
        ImGui.Separator();

        DrawFooter(profile);

        // Commits a color/slider edit whose widget never reported "deactivated after edit".
        editorSession.CommitPendingEditsIfIdle(ImGui.IsAnyItemActive());
    }

    private void DrawPreview(ProfileDocument profile)
    {
        using var child = ImRaii.Child("##BasicPreview", new Vector2(-1, PreviewHeight), true);
        if (!child.Success)
        {
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f)
        {
            return;
        }

        // Uniform scale, auto-fit: the user never controls zoom in Basic mode.
        var scale = Math.Min(available.X / profile.CanvasWidth, available.Y / profile.CanvasHeight);
        if (scale <= 0f)
        {
            return;
        }

        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * scale;
        var canvasOrigin = ImGui.GetCursorScreenPos() + (available - canvasScreenSize) / 2f;

        ImGui.Dummy(available);

        var drawList = ImGui.GetWindowDrawList();
        // Editor preview: the finished rendering plus semantic placeholders for empty Basic
        // fields (never shown by Profile View, which always renders ProfileRenderOptions.Finished).
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, scale, renderResources, EditorPlaceholders.PreviewOptions);
    }

    private void DrawPortraitControls(ProfileDocument profile)
    {
        ImGui.TextUnformatted("Portrait");

        var portrait = basicEditorSession.Portrait;
        if (portrait is not null)
        {
            var wrap = imageTextureCache.GetWrapOrNull(portrait.AssetId);
            if (wrap is not null)
            {
                ImGui.Image(wrap.Handle, new Vector2(56, 56));
                ImGui.SameLine();
            }
        }
        else
        {
            ImGui.TextUnformatted("(no portrait set)");
        }

        if (ImGui.Button(portrait is null ? "Add Portrait" : "Replace Portrait"))
        {
            OpenImageFileDialog(portrait is null ? "Add Portrait" : "Replace Portrait", basicEditorSession.SetPortrait);
        }

        if (portrait is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove Portrait"))
            {
                basicEditorSession.RemovePortrait();
            }
        }
    }

    private void DrawTextField(string label, ProfileElementRole role, bool multiline)
    {
        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        ImGui.TextUnformatted(label);

        // Shown even before the element exists: the first keystroke creates it (one undo step).
        var buffer = (BasicEditorSession.FindByRole(profile, role) as TextProfileElement)?.Text ?? string.Empty;
        ImGui.SetNextItemWidth(-1);

        var changed = multiline
            ? ImGui.InputTextMultiline($"##Basic{role}", ref buffer, TextProfileElement.MaxTextLength, new Vector2(-1, 120))
            : ImGui.InputText($"##Basic{role}", ref buffer, TextProfileElement.MaxTextLength);

        if (changed)
        {
            basicEditorSession.SetText(role, buffer);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }
    }

    private void DrawBackgroundControls(ProfileDocument profile)
    {
        var background = profile.Background;
        var hasImage = background is { HasImage: true };
        ImGui.TextUnformatted("Background: " + (hasImage ? "set" : "(none)"));

        if (ImGui.Button("Choose Background"))
        {
            OpenImageFileDialog("Choose Background", editorSession.SetBackground);
        }

        if (!hasImage)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove Background"))
        {
            editorSession.RemoveBackground();
        }

        var fitModeIndex = (int)background!.ImageFit;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Fit", ref fitModeIndex, FitModeLabels, FitModeLabels.Length))
        {
            var newFitMode = (ProfileImageFit)fitModeIndex;
            editorSession.ApplyBackgroundEdit(style => style.ImageFit = newFitMode);
        }
    }

    private void DrawFooter(ProfileDocument profile)
    {
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
        if (ImGui.Button("Save Plate"))
        {
            editorSession.SaveProfile();
        }

        ImGui.SameLine();
        if (profileService.IsBusy)
        {
            ImGui.TextUnformatted("Saving...");
        }
        else if (editorSession.IsDirty)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Unsaved changes");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "Saved");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!basicEditorSession.CanResetLayout(profile)))
        {
            if (ImGui.Button("Reset Basic Layout"))
            {
                ImGui.OpenPopup("ResetBasicLayoutConfirm");
            }
        }

        DrawResetConfirmationPopup();

        if (basicEditorSession.ErrorMessage is { } error)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), error);
        }
    }

    private void DrawResetConfirmationPopup()
    {
        var open = true;
        if (!ImGui.BeginPopupModal("ResetBasicLayoutConfirm", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextUnformatted("Reset the portrait, name, title, and message back to their");
        ImGui.TextUnformatted("default Basic layout positions and sizes?");
        ImGui.TextUnformatted("Their content (text, portrait image) is kept.");
        ImGui.Separator();

        if (ImGui.Button("Reset"))
        {
            basicEditorSession.ResetBasicLayout();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
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
}
