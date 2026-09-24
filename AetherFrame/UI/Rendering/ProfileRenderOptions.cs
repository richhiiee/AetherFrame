using System;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// Per-call rendering options. The default value is the finished profile — exactly what Profile
/// View shows — so a caller has to opt in to anything editor-only.
/// </summary>
internal readonly record struct ProfileRenderOptions
{
    /// <summary>What Profile View, Clean Preview, the Basic preview, and any other finished rendering use.</summary>
    internal static readonly ProfileRenderOptions Finished = default;

    /// <summary>Editor-only: the translucent placement box drawn behind each text element.</summary>
    internal bool ShowElementBounds { get; init; }

    /// <summary>
    /// Editor-only: supplies a placeholder string for an element with no content of its own (e.g.
    /// an empty text element, or an empty semantic Basic section), drawn dimmed in place of the
    /// content. Null — always, for finished rendering — draws nothing for empty content, so a
    /// placeholder can never leak into Profile View.
    /// </summary>
    internal Func<ProfileElement, string?>? PlaceholderProvider { get; init; }

    /// <summary>
    /// Editor-only: draws every Basic section heading, even one whose section has nothing to show
    /// (so it stays findable and selectable on the canvas). Finished rendering leaves those out —
    /// see <c>BasicSections.IsDrawnInFinishedRendering</c>.
    /// </summary>
    internal bool ShowEmptySectionHeadings { get; init; }

    /// <summary>
    /// Presentation-only: skips the opaque canvas backdrop <c>ProfileRenderer</c> normally paints
    /// under the Plate's own background, so wherever the authored background is absent or
    /// translucent the Plate shows whatever is behind it (Clean Preview: the game world). The
    /// authored background itself is drawn exactly as always.
    /// </summary>
    internal bool HideCanvasBackdrop { get; init; }

    /// <summary>
    /// Miniatures only (My Plates cards): text that would render smaller than this many screen
    /// pixels is drawn as soft bars in its own color (see <see cref="TextBars"/>) instead of
    /// illegible glyphs. 0 (the default) never simplifies.
    /// </summary>
    internal float TextAsBarsBelowPixelSize { get; init; }
}
