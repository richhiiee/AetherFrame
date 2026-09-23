using System;
using AetherFrame.Domain.Profiles;
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

/// <summary>
/// Per-call rendering options. The default value is the finished profile — exactly what Profile
/// View shows — so a caller has to opt in to anything editor-only.
/// </summary>
internal readonly record struct ProfileRenderOptions
{
    /// <summary>What Profile View, Clean Preview, and any other finished rendering use.</summary>
    internal static readonly ProfileRenderOptions Finished = default;

    /// <summary>Editor-only: the translucent placement box drawn behind each text element.</summary>
    internal bool ShowElementBounds { get; init; }

    /// <summary>
    /// Editor-only: supplies a placeholder string for an element with no content of its own (e.g.
    /// an empty text element, or later a semantic Basic slot), drawn dimmed in place of the
    /// content. Null — always, for finished rendering — draws nothing for empty content, so a
    /// placeholder can never leak into Profile View.
    /// </summary>
    internal Func<ProfileElement, string?>? PlaceholderProvider { get; init; }
}
