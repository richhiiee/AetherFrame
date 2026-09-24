using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Editor-only placeholder text for elements with no content yet, drawn dimmed by
/// <see cref="ProfileTextRenderer"/> in place of the (empty) content. Lives in the editor layer
/// and only reaches the renderer through <see cref="ProfileRenderOptions.PlaceholderProvider"/>,
/// which finished rendering (Profile View, Clean Preview, the Basic preview) never sets — so a
/// placeholder can't appear in a finished profile, and nothing like "(empty)" is hardcoded into
/// rendering.
///
/// Semantic Basic sections show a friendly hint for their role ("Character Name", "Add a
/// message"); an ordinary empty text element shows a generic hint.
/// </summary>
internal static class EditorPlaceholders
{
    /// <summary>Advanced editor canvas: placement boxes plus placeholders.</summary>
    internal static readonly ProfileRenderOptions CanvasOptions = new()
    {
        ShowElementBounds = true,
        PlaceholderProvider = GetPlaceholder,
        ShowEmptySectionHeadings = true,
    };

    /// <summary>Advanced editor canvas with Guides off: no placement boxes, still placeholders
    /// (an empty element would otherwise be invisible and impossible to find on the canvas).</summary>
    internal static readonly ProfileRenderOptions CanvasOptionsWithoutGuides = new()
    {
        ShowElementBounds = false,
        PlaceholderProvider = GetPlaceholder,
        ShowEmptySectionHeadings = true,
    };

    internal static string? GetPlaceholder(ProfileElement element) => element switch
    {
        TextProfileElement => element.Role switch
        {
            ProfileElementRole.BasicName => "Character Name",
            ProfileElementRole.BasicTitle => "Choose a title",
            ProfileElementRole.BasicTagline => "Add a tagline",
            ProfileElementRole.BasicWorld => "Home World",
            ProfileElementRole.BasicJob => "Favorite Job",
            ProfileElementRole.BasicLevel => "Level",
            ProfileElementRole.BasicFreeCompany => "Free Company",
            ProfileElementRole.BasicPlaystyle => "Playstyle",
            ProfileElementRole.BasicActiveHours => "Active Hours",
            ProfileElementRole.BasicMessage => "Add a message",
            _ => ProfileElementNames.GetRoleLabel(element.Role) ?? "Empty text",
        },
        _ => null,
    };
}
