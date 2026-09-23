using System;
using System.Numerics;
using System.Threading.Tasks;
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

/// <summary>
/// Primary, simplified editing experience modeled on the FFXIV Adventure Plate: a portrait,
/// name, title, message, background, and an accent color — nothing about the freeform canvas
/// (coordinates, Z order, element ids, rotation, layer ordering, asset management) is exposed
/// here. Edits the same <see cref="ProfileDocument"/> as <see cref="ProfileEditorWindow"/>,
/// through the same shared <see cref="EditorSession"/>, via <see cref="BasicEditorSession"/>'s
/// role-based lookups — so undo/redo and dirty state always agree between the two windows, and
/// switching modes never resets or duplicates profile data.
/// </summary>
internal sealed class BasicProfileEditorWindow : Window, IDisposable
{
    private const float PreviewHeight = 420f;

    private static readonly string[] FitModeLabels = ["Cover", "Contain", "Stretch"];

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly BasicEditorSession basicEditorSession;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileFontService fontService;
    private readonly FileDialogManager fileDialogManager;
    private readonly Action openAdvancedEditor;

    internal BasicProfileEditorWindow(
        ProfileService profileService,
        EditorSession editorSession,
        BasicEditorSession basicEditorSession,
        ImageTextureCache imageTextureCache,
        ProfileFontService fontService,
        FileDialogManager fileDialogManager,
        Action openAdvancedEditor)
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
        this.fontService = fontService;
        this.fileDialogManager = fileDialogManager;
        this.openAdvancedEditor = openAdvancedEditor;
    }

    public void Dispose()
    {
    }

    public override void OnClose()
    {
        fileDialogManager.Reset();
    }

    public override void Draw()
    {
        // Drawn unconditionally so an in-progress file pick isn't stranded if the profile
        // becomes unavailable (e.g. character logs out) while the dialog is open.
        fileDialogManager.Draw();

        if (!DalamudServices.PlayerState.IsLoaded)
        {
            ImGui.TextUnformatted("No character is currently logged in.");
            return;
        }

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile loaded.");

            using (ImRaii.Disabled(profileService.IsBusy))
            {
                if (ImGui.Button("Load Profile"))
                {
                    _ = LoadSafelyAsync();
                }
            }

            return;
        }

        // Idempotent: only creates whichever of Name/Title/Message don't already exist, and
        // never touches one that does — see BasicEditorSession for why this is safe every frame.
        basicEditorSession.EnsureBasicContentInitialized();

        ImGui.TextUnformatted($"Adventure Plate — {profile.Name}");
        ImGui.SameLine();
        if (ImGui.Button("Advanced Editor"))
        {
            openAdvancedEditor();
        }

        ImGui.Separator();

        DrawPreview(profile);
        ImGui.Separator();

        DrawPortraitControls(profile);
        ImGui.Separator();

        DrawTextField("Character Name", ProfileElementRole.BasicName, multiline: false);
        DrawTextField("Title", ProfileElementRole.BasicTitle, multiline: false);
        DrawTextField("Message", ProfileElementRole.BasicMessage, multiline: true);
        ImGui.Separator();

        DrawAccentControl();
        ImGui.Separator();

        DrawBackgroundControls(profile);
        ImGui.Separator();

        DrawFooter(profile);
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
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, scale, imageTextureCache, fontService, showElementBounds: false);
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
        if (profile is null || BasicEditorSession.FindByRole(profile, role) is not TextProfileElement element)
        {
            return;
        }

        ImGui.TextUnformatted(label);

        var buffer = element.Text;
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

    private void DrawAccentControl()
    {
        if (basicEditorSession.CurrentAccentColor is not { } color)
        {
            return;
        }

        ImGui.TextUnformatted("Accent Color");
        ImGui.SetNextItemWidth(200);
        if (ImGui.ColorEdit4("##BasicAccentColor", ref color))
        {
            basicEditorSession.SetAccentColor(color);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }
    }

    private void DrawBackgroundControls(ProfileDocument profile)
    {
        ImGui.TextUnformatted("Background: " + (profile.BackgroundAssetId is null ? "(none)" : "set"));

        if (ImGui.Button("Choose Background"))
        {
            OpenImageFileDialog("Choose Background", editorSession.SetBackground);
        }

        if (profile.BackgroundAssetId is null)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove Background"))
        {
            editorSession.RemoveBackground();
        }

        var fitModeIndex = (int)profile.BackgroundFitMode;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Fit", ref fitModeIndex, FitModeLabels, FitModeLabels.Length))
        {
            var newFitMode = (BackgroundFitMode)fitModeIndex;
            editorSession.ApplyBackgroundEdit(document => document.BackgroundFitMode = newFitMode);
        }
    }

    private void DrawFooter(ProfileDocument profile)
    {
        if (ImGui.Button("Save Profile"))
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

    private async Task LoadSafelyAsync()
    {
        try
        {
            await profileService.LoadForCurrentCharacterAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "AetherFrame failed to load the current character's profile.");
        }
    }
}
