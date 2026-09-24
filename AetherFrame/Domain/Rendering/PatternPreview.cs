using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Rendering;

/// <summary>
/// Builds the temporary background used to preview one candidate pattern in the Design category's
/// Pattern browser: a clone of the profile's own <em>current</em> background with only
/// <see cref="ProfileBackground.Mode"/> and <see cref="ProfileBackground.Texture"/> overridden.
///
/// This is a deliberate truthfulness choice: every card uses the real Base color, Pattern color,
/// Intensity, Scale, and Rotation, so the only thing that differs from card to card is the candidate
/// pattern itself — exactly what applying it would actually look like. If the current Base and
/// Pattern colors happen to be close, the preview truthfully shows a subtle result rather than
/// substituting an artificial high-contrast palette; the browser solves legibility of the selector
/// itself (not the rendered colors) through its card border, selection outline, and name label.
///
/// Pure and Dalamud-free, and takes no dependency on <see cref="ProfileDocument"/> beyond the
/// background it's handed — never mutates it, always returns a new object — so it's usable from both
/// the Windows/ card renderer and from tests. It reuses the exact same
/// <see cref="ProfileBackground"/>/<see cref="ProfileBackgroundTexture"/> definitions the real
/// rendering path (<c>ProfileBackgroundRenderer</c>, <see cref="ProceduralPatternMath"/>) does —
/// never a second pattern catalog or a duplicate visual implementation.
/// </summary>
public static class PatternPreview
{
    /// <summary>
    /// A background identical to <paramref name="current"/> except it shows <paramref name="candidate"/>
    /// as a Textured Fill — Base color, Pattern color, Intensity, Scale, and Rotation are all copied
    /// from <paramref name="current"/> unchanged. Read fresh every call, so a caller that re-renders
    /// every frame (as ImGui does) picks up any live edit to those values immediately, with no
    /// separate preview state to keep in sync or persist.
    /// </summary>
    public static ProfileBackground Create(ProfileBackground current, ProfileBackgroundTexture candidate)
    {
        var preview = current.Clone();
        preview.Mode = ProfileBackgroundMode.TexturedFill;
        preview.Texture = candidate;
        return preview;
    }
}
