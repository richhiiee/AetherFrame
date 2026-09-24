using AetherFrame.Services;
using AetherFrame.Services.Fonts;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// The long-lived, shared GPU/font resources <see cref="ProfileRenderer"/> draws with. One
/// instance is created by the plugin and handed to every window that renders a profile (Advanced
/// editor, Basic editor preview, Profile View), so they all share the same caches and can't drift
/// apart visually. Owns nothing itself — each cache is disposed by the plugin.
/// </summary>
internal sealed class ProfileRenderResources
{
    internal ProfileRenderResources(ImageTextureCache images, ProfileFontService fonts, ProceduralTextureCache textures)
    {
        Images = images;
        Fonts = fonts;
        Textures = textures;
    }

    internal ImageTextureCache Images { get; }

    internal ProfileFontService Fonts { get; }

    internal ProceduralTextureCache Textures { get; }
}
