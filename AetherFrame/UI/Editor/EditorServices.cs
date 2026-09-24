using System;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

// What the editor sessions need from the game and the renderer, as narrow interfaces: the plugin
// implements them over Dalamud/ImGui (ImageTextureCache, CharacterIdentityService,
// GameTitleCatalog, ProfileTextMeasurer), and tests over plain fakes — so every editing rule can be
// verified without the game.

/// <summary>Image facts the editor session uses (the plugin's texture cache).</summary>
internal interface IEditorImageInfo
{
    /// <summary>An imported image's pixel size, or null if unknown.</summary>
    (int Width, int Height)? GetNativeSize(Guid assetId);

    /// <summary>Forgets cached images (a different Plate was opened).</summary>
    void Clear();
}

/// <summary>The logged-in character, as far as Basic mode needs to know.</summary>
internal interface ICharacterInfoSource
{
    /// <summary>The character's current details, or null when no character is loaded.</summary>
    BasicCharacterInfo? CurrentInfo { get; }
}

/// <summary>Measures text for the Identity Header's one-line layouts.</summary>
internal interface IIdentityTextMeasurer
{
    /// <summary>The element's natural (unwrapped) text width in canvas units; false while its font isn't ready.</summary>
    bool TryMeasureNaturalWidth(TextProfileElement element, out float width);

    /// <summary>The lines the element's text word-wraps to at <paramref name="fontSize"/> within <paramref name="maxWidth"/>; false while its font isn't ready.</summary>
    bool TryCountLines(TextProfileElement element, float fontSize, float maxWidth, out int lines);
}

/// <summary>FFXIV titles, for the Identity Header's title source.</summary>
internal interface IGameTitleSource
{
    GameTitle? Find(uint titleId);

    /// <summary>Whether gendered titles use their feminine form for the logged-in character.</summary>
    bool FeminineForms { get; }
}
