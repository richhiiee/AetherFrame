using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Editor-only placeholder text for elements with no content yet, drawn dimmed by
/// <see cref="ProfileTextRenderer"/> in place of the (empty) content. Lives in the editor layer
/// and only reaches the renderer through <see cref="ProfileRenderOptions.PlaceholderProvider"/>,
/// which finished rendering (Profile View, Clean Preview) never sets — so a placeholder can't
/// appear in a finished profile, and nothing like "(empty)" is hardcoded into rendering.
///
/// Semantic Basic slots show their role ("Character Name", "Message"), which is what a future
/// Basic layout's placeholders build on; an ordinary empty text element shows a generic hint.
/// </summary>
internal static class EditorPlaceholders
{
    /// <summary>Advanced editor canvas: placement boxes plus placeholders.</summary>
    internal static readonly ProfileRenderOptions CanvasOptions = new()
    {
        ShowElementBounds = true,
        PlaceholderProvider = GetPlaceholder,
    };

    /// <summary>Advanced editor canvas with Guides off: no placement boxes, still placeholders
    /// (an empty element would otherwise be invisible and impossible to find on the canvas).</summary>
    internal static readonly ProfileRenderOptions CanvasOptionsWithoutGuides = new()
    {
        ShowElementBounds = false,
        PlaceholderProvider = GetPlaceholder,
    };

    /// <summary>An editor's embedded preview (e.g. the Basic editor): finished look plus placeholders.</summary>
    internal static readonly ProfileRenderOptions PreviewOptions = CanvasOptionsWithoutGuides;

    internal static string? GetPlaceholder(ProfileElement element) => element switch
    {
        TextProfileElement => element.Role switch
        {
            ProfileElementRole.BasicName => "Character Name",
            ProfileElementRole.BasicTitle => "Choose a title",
            ProfileElementRole.BasicTagline => "Add a tagline",
            _ => ProfileElementNames.GetRoleLabel(element.Role) ?? "Empty text",
        },
        _ => null,
    };
}
