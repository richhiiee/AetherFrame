using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The persistent top action bar both editors share, so Basic and Advanced read as two modes of
/// one editor: the same actions, with the same words, in the same places.
///
/// <code>
/// [My Plates] [Basic|Advanced]  Plate name      [Undo][Redo]      Unsaved changes [Preview] [Revert] [Save]
/// </code>
///
/// It is drawn at the top of the window, outside every scrolling region, so it stays in view while
/// the editor's content scrolls. Availability comes from <see cref="EditorDocumentCommands"/>: Save
/// and Revert only with unsaved changes, Undo and Redo only when there's something to undo or redo.
/// Revert always asks first (and the revert itself can be undone). Everything an editor mode adds
/// of its own (Advanced's + Text, Guides...) goes on its own row below this one.
/// </summary>
internal sealed class EditorActionBar
{
    private const string RevertPopupId = "Revert to Saved##AetherFrameRevert";
    private const string PreviewLabel = "Preview";
    private const string RevertLabel = "Revert";
    private const string SaveLabel = "Save";

    private static readonly Vector4 SavingColor = new(0.85f, 0.85f, 0.4f, 1f);

    private readonly EditorDocumentCommands commands;
    private readonly EditorSurfaceKind mode;
    private readonly Action openMyPlates;
    private readonly Action switchMode;

    // Requested from the bar, opened at window level (one id-stack scope, see ProfileEditorWindow).
    private bool pendingRevertPrompt;

    /// <param name="commands">The shared document actions.</param>
    /// <param name="mode">Which editor this bar belongs to (its half of the Basic / Advanced switch is highlighted).</param>
    /// <param name="openMyPlates">Opens My Plates, or brings it forward when it's already open.</param>
    /// <param name="switchMode">Hands the open Plate to the other editor mode.</param>
    internal EditorActionBar(EditorDocumentCommands commands, EditorSurfaceKind mode, Action openMyPlates, Action switchMode)
    {
        this.commands = commands;
        this.mode = mode;
        this.openMyPlates = openMyPlates;
        this.switchMode = switchMode;
    }

    internal EditorDocumentCommands Commands => commands;

    /// <summary>Asks to revert to the last saved version (confirmed by <see cref="DrawPopups"/>); ignored when there's nothing to revert.</summary>
    internal void RequestRevert()
    {
        if (commands.CanRevert)
        {
            pendingRevertPrompt = true;
        }
    }

    /// <summary>
    /// Draws the bar row, then <paramref name="errorMessage"/> (if any) on its own line.
    /// </summary>
    /// <param name="profile">The open Plate.</param>
    /// <param name="previewActive">Whether this editor's Preview is showing (the Preview button is highlighted).</param>
    /// <param name="togglePreview">What Preview does in this editor.</param>
    /// <param name="previewTooltip">What Preview shows, in this editor's words.</param>
    /// <param name="errorMessage">The editor's current error, if any.</param>
    internal void Draw(ProfileDocument profile, bool previewActive, Action togglePreview, string previewTooltip, string? errorMessage)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var gap = 12f * scale;

        // ---- left: My Plates and the mode switch
        if (EditorWidgets.IconButton("MyPlates", FontAwesomeIcon.ThLarge, "My Plates"))
        {
            openMyPlates();
        }

        ImGui.SameLine();
        DrawModeSwitch();
        var leftEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;

        // ---- measure the other two groups
        var frame = ImGui.GetFrameHeight();
        const float historyGap = 2f;
        var centerWidth = (frame * 2f) + historyGap;

        var (stateText, stateColor) = SaveState();
        var rightWidth = ImGui.CalcTextSize(stateText).X
            + ButtonWidth(PreviewLabel) + ButtonWidth(RevertLabel) + ButtonWidth(SaveLabel)
            + (style.ItemSpacing.X * 3f);

        var (centerX, rightX, nameWidth) = EditorActionBarLayout.Arrange(
            ImGui.GetWindowContentRegionMin().X, ImGui.GetWindowContentRegionMax().X, leftEnd, centerWidth, rightWidth, gap);

        // ---- the Plate's name, in whatever room is left
        if (nameWidth >= 24f * scale)
        {
            ImGui.SameLine(leftEnd + gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(FitText(profile.Name, nameWidth));
            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(profile.Name).X > nameWidth)
            {
                ImGui.SetTooltip(profile.Name);
            }
        }

        // ---- center: history
        ImGui.SameLine(centerX);
        using (ImRaii.Disabled(!commands.CanUndo))
        {
            if (EditorWidgets.IconButton("Undo", FontAwesomeIcon.Undo, "Undo (Ctrl+Z)"))
            {
                commands.Undo();
            }
        }

        ImGui.SameLine(0f, historyGap);
        using (ImRaii.Disabled(!commands.CanRedo))
        {
            if (EditorWidgets.IconButton("Redo", FontAwesomeIcon.Redo, "Redo (Ctrl+Y)"))
            {
                commands.Redo();
            }
        }

        // ---- right: save state, Preview, Revert, Save
        ImGui.SameLine(rightX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(stateColor, stateText);

        ImGui.SameLine();
        if (EditorWidgets.TextToggle(PreviewLabel, previewActive, tooltip: previewTooltip))
        {
            togglePreview();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!commands.CanRevert))
        {
            if (ImGui.Button(RevertLabel))
            {
                RequestRevert();
            }
        }

        EditorWidgets.Tooltip("Discard unsaved changes and go back to the last saved version. Asks first.");

        ImGui.SameLine();
        var canSave = commands.CanSave;
        using (ImRaii.Disabled(!canSave))
        using (ImRaii.PushColor(ImGuiCol.Button, EditorWidgets.ActiveToggleColor, canSave))
        {
            if (ImGui.Button(SaveLabel))
            {
                commands.Save();
            }
        }

        EditorWidgets.Tooltip("Save (Ctrl+S)");

        if (errorMessage is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
    }

    /// <summary>The bar's popups (Revert's confirmation). Call once per frame from the window's outermost scope.</summary>
    internal void DrawPopups()
    {
        if (pendingRevertPrompt)
        {
            pendingRevertPrompt = false;
            ImGui.OpenPopup(RevertPopupId);
        }

        using var popup = ImRaii.PopupModal(RevertPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
        {
            return;
        }

        ImGui.TextUnformatted("Revert this Plate to its last saved version?");
        EditorWidgets.Hint("All unsaved changes will be discarded. You can still undo the revert.");
        ImGui.Spacing();

        var buttonSize = new Vector2(120f * ImGuiHelpers.GlobalScale, 0f);
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.62f, 0.22f, 0.22f, 1f)))
        {
            if (ImGui.Button("Revert", buttonSize))
            {
                commands.Revert();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary>Basic | Advanced: the current mode highlighted; the other hands this same Plate over.</summary>
    private void DrawModeSwitch()
    {
        using var id = ImRaii.PushId("EditorMode");
        ModeButton(EditorSurfaceKind.Basic, "Basic", "Basic Editor: guided editing with familiar Adventure Plate sections.");
        ImGui.SameLine(0f, 0f);
        ModeButton(EditorSurfaceKind.Advanced, "Advanced", "Advanced Editor: freeform positioning, layering and every property.");

        void ModeButton(EditorSurfaceKind kind, string label, string description)
        {
            var current = kind == mode;
            if (EditorWidgets.TextToggle(label, current, tooltip: current
                    ? $"{description}\nYou're editing here now."
                    : $"{description}\nSwitch to it: your unsaved changes and undo history come along.")
                && !current)
            {
                switchMode();
            }
        }
    }

    private (string Text, Vector4 Color) SaveState() =>
        commands.IsSaving ? ("Saving...", SavingColor)
        : commands.IsDirty ? ("Unsaved changes", EditorWidgets.WarningColor)
        : ("Saved", EditorWidgets.SuccessColor with { W = 0.75f });

    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);

    /// <summary>The text, shortened with an ellipsis when it's wider than <paramref name="width"/>.</summary>
    private static string FitText(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        const string ellipsis = "...";
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(string.Concat(text.AsSpan(0, length), ellipsis)).X > width)
        {
            length--;
        }

        return length == 0 ? string.Empty : string.Concat(text.AsSpan(0, length), ellipsis);
    }
}
