using System.Numerics;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The size a window opens at the very first time (before ImGui has remembered one): its preferred
/// size at Dalamud's global UI scale, kept within the screen's work area so a large scale on a
/// small screen never opens it partly off-screen — but never below the window's own minimum size.
/// </summary>
internal static class FirstUseWindowSize
{
    /// <summary>The most of the screen's work area a window may cover when it first opens.</summary>
    internal const float MaxWorkAreaFraction = 0.9f;

    /// <param name="preferred">The preferred size, in unscaled pixels.</param>
    /// <param name="minimum">The window's minimum size, in unscaled pixels.</param>
    /// <param name="workArea">The main viewport's work area (zero when unknown).</param>
    /// <param name="scale">Dalamud's global UI scale.</param>
    /// <returns>The size to open at, in screen pixels.</returns>
    internal static Vector2 Compute(Vector2 preferred, Vector2 minimum, Vector2 workArea, float scale)
    {
        scale = scale > 0f ? scale : 1f;
        var size = preferred * scale;
        if (workArea is { X: > 0f, Y: > 0f })
        {
            size = Vector2.Min(size, workArea * MaxWorkAreaFraction);
        }

        return Vector2.Max(size, minimum * scale);
    }
}
