using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// Read-only presentation view of the current character's profile: no inspector, no selection,
/// no editing of any kind. Renders the same in-memory <see cref="ProfileDocument"/> that
/// <see cref="ProfileEditorWindow"/> edits, via the shared <see cref="ProfileRenderer"/>, so it
/// always reflects the live (possibly unsaved) editor state without needing a reload.
/// </summary>
internal sealed class ProfileViewWindow : Window, IDisposable
{
    private readonly ProfileService profileService;
    private readonly ImageTextureCache imageTextureCache;

    private bool borderless;

    internal ProfileViewWindow(ProfileService profileService, ImageTextureCache imageTextureCache)
        : base("AetherFrame Profile View##ProfileViewWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 180),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.imageTextureCache = imageTextureCache;
    }

    public void Dispose()
    {
    }

    public override void OnClose()
    {
        // Don't strand the window borderless (and thus effectively unreachable) the next time
        // it opens.
        borderless = false;
    }

    public override void PreDraw()
    {
        Flags = borderless
            ? ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void Draw()
    {
        if (borderless && ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            borderless = false;
        }

        if (!borderless)
        {
            if (ImGui.SmallButton("Borderless Preview"))
            {
                borderless = true;
            }

            ImGui.Separator();
        }

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile is currently loaded.");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f)
        {
            return;
        }

        // Uniform scale so the profile's aspect ratio is always preserved: fit the logical
        // canvas inside the available space, centered, rather than stretching it to match a
        // presentation window of a different aspect ratio.
        var scale = Math.Min(available.X / ProfileDocument.CanvasWidth, available.Y / ProfileDocument.CanvasHeight);
        if (scale <= 0f)
        {
            return;
        }

        var canvasScreenSize = new Vector2(ProfileDocument.CanvasWidth, ProfileDocument.CanvasHeight) * scale;
        var canvasOrigin = ImGui.GetCursorScreenPos() + (available - canvasScreenSize) / 2f;

        // Reserve the layout space so the window's scrollable content region matches what was
        // measured above, then paint over it with the draw list directly.
        ImGui.Dummy(available);

        var drawList = ImGui.GetWindowDrawList();
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, scale, imageTextureCache);

        if (borderless)
        {
            var hintPos = ImGui.GetWindowPos() + new Vector2(8f, 8f);
            drawList.AddText(hintPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), "Esc to exit borderless preview");
        }
    }
}
