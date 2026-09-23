using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// Read-only presentation view of the current character's profile: no inspector, no selection,
/// no editing of any kind, no editor chrome of any kind — the finished profile is the entire
/// point of this window. Renders the same in-memory <see cref="ProfileDocument"/> that
/// <see cref="ProfileEditorWindow"/> edits, via the shared <see cref="ProfileRenderer"/> (with
/// element-bounds chrome always off), so it always reflects the live (possibly unsaved) editor
/// state without needing a reload, and never shows an editing box regardless of the editor's own
/// Guides setting.
/// </summary>
internal sealed class ProfileViewWindow : Window, IDisposable
{
    // How much of the game's viewport the window should occupy by default: large enough to read
    // comfortably without dominating the screen.
    private const float DefaultSizeViewportFraction = 0.52f;

    // Floor for both the default-open size and manual resizing, so ordinary profile text is
    // never unreadable.
    private static readonly Vector2 MinimumWindowSize = new(480f, 320f);

    private const float CloseButtonSize = 20f;
    private const float CloseButtonMargin = 6f;

    private readonly ProfileService profileService;
    private readonly ImageTextureCache imageTextureCache;
    private readonly ProfileFontService fontService;

    internal ProfileViewWindow(ProfileService profileService, ImageTextureCache imageTextureCache, ProfileFontService fontService)
        : base("AetherFrame Profile View##ProfileViewWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.profileService = profileService;
        this.imageTextureCache = imageTextureCache;
        this.fontService = fontService;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Called whenever this window (re)opens. Computes a fresh default size/position — roughly
    /// <see cref="DefaultSizeViewportFraction"/> of the game viewport, preserving the current
    /// profile's aspect ratio, centered on screen — applied via <see cref="ImGuiCond.Appearing"/>
    /// (see <see cref="PreDraw"/>) so it takes effect on every reopen without ever overriding a
    /// size/position the user manually resizes/moves to while the window stays open.
    /// </summary>
    public override void OnOpen()
    {
        var size = ComputeDefaultOpenSize();
        Size = size;

        var viewport = ImGui.GetMainViewport();
        Position = viewport.WorkPos + ((viewport.WorkSize - size) / 2f);
    }

    public override void PreDraw()
    {
        // True only on the transition frame this window becomes visible — every later frame this
        // is simply a no-op, so a manual resize/move is never fought.
        SizeCondition = ImGuiCond.Appearing;
        PositionCondition = ImGuiCond.Appearing;

        // Always borderless: no title bar, no native close/collapse affordances. Resizing by
        // dragging an edge still works (NoResize is deliberately not set).
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void Draw()
    {
        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            ImGui.TextUnformatted("No profile is currently loaded.");
            DrawCloseButton();
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
        var scale = Math.Min(available.X / profile.CanvasWidth, available.Y / profile.CanvasHeight);
        if (scale <= 0f)
        {
            DrawCloseButton();
            return;
        }

        var canvasScreenSize = new Vector2(profile.CanvasWidth, profile.CanvasHeight) * scale;
        var canvasOrigin = ImGui.GetCursorScreenPos() + (available - canvasScreenSize) / 2f;

        // Reserve the layout space so the window's scrollable content region matches what was
        // measured above, then paint over it with the draw list directly.
        ImGui.Dummy(available);

        var drawList = ImGui.GetWindowDrawList();
        ProfileRenderer.Draw(drawList, profile, canvasOrigin, scale, imageTextureCache, fontService, showElementBounds: false);

        DrawCloseButton();
    }

    /// <summary>
    /// The only chrome this window draws: a small, low-contrast "x" in the upper right, drawn
    /// last (on top of the rendered profile) so the finished profile itself stays the visual
    /// focus. No title bar exists to provide a native close button without this.
    /// </summary>
    private void DrawCloseButton()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowWidth = ImGui.GetWindowSize().X;
        var pos = new Vector2(windowPos.X + windowWidth - CloseButtonSize - CloseButtonMargin, windowPos.Y + CloseButtonMargin);

        ImGui.SetCursorScreenPos(pos);

        using var buttonColor = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        using var hoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.3f, 0.3f, 0.45f));
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.2f, 0.2f, 0.65f));
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 0.5f));

        if (ImGui.Button("x##CloseProfileView", new Vector2(CloseButtonSize, CloseButtonSize)))
        {
            IsOpen = false;
        }
    }

    /// <summary>
    /// Roughly <see cref="DefaultSizeViewportFraction"/> of the game viewport, preserving the
    /// current profile's own canvas aspect ratio (or the legacy 1920x1080 aspect, absent a
    /// loaded profile), floored at <see cref="MinimumWindowSize"/> so ordinary profile text is
    /// never unreadable by default.
    /// </summary>
    private Vector2 ComputeDefaultOpenSize()
    {
        var profile = profileService.CurrentProfile;
        var canvasWidth = profile is { CanvasWidth: > 0f } ? profile.CanvasWidth : ProfileDocument.LegacyCanvasWidth;
        var canvasHeight = profile is { CanvasHeight: > 0f } ? profile.CanvasHeight : ProfileDocument.LegacyCanvasHeight;

        var viewportSize = ImGui.GetMainViewport().WorkSize;
        var targetBox = viewportSize * DefaultSizeViewportFraction;
        if (targetBox.X < 1f || targetBox.Y < 1f)
        {
            return MinimumWindowSize;
        }

        var canvasAspect = canvasWidth / canvasHeight;
        var boxAspect = targetBox.X / targetBox.Y;

        var fitSize = canvasAspect > boxAspect
            ? new Vector2(targetBox.X, targetBox.X / canvasAspect)
            : new Vector2(targetBox.Y * canvasAspect, targetBox.Y);

        return Vector2.Max(fitSize, MinimumWindowSize);
    }
}
