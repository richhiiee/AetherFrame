namespace AetherFrame.Domain.Profiles;

/// <summary>
/// Stable, persisted ids for the font families a <see cref="TextProfileElement"/> can select.
/// Deliberately just string ids here — no Dalamud dependency — so Domain.Profiles stays usable
/// without the Dalamud font runtime (e.g. for a profile received over the network). The actual
/// fonts these ids resolve to (and their bold/italic capabilities) live in
/// AetherFrame.Services.Fonts.ProfileFontCatalog.
/// </summary>
public static class ProfileFontFamilies
{
    /// <summary>
    /// Dalamud's own default UI font. Never supports Bold/Italic as real faces. This is
    /// <see cref="TextProfileElement.FontFamily"/>'s property-initializer default — i.e. what
    /// every legacy element (saved before FontFamily existed, or before AetherFrame's own
    /// curated fonts existed) deserializes to — so it must never change: doing so would silently
    /// alter every legacy profile's font on next load.
    /// </summary>
    public const string DalamudDefault = "dalamud-default";

    /// <summary>The AetherFrame default for newly created text: a portable, fully-styled
    /// (Regular/Bold/Italic/Bold Italic) sans-serif. Set explicitly by
    /// <c>ProfileService.AddTextElement</c> — never via a changed property-initializer default,
    /// which would also affect legacy elements (see <see cref="DalamudDefault"/>).</summary>
    public const string AetherFrameSans = "aetherframe-sans";

    public const string AetherFrameSerif = "aetherframe-serif";

    public const string AetherFrameMono = "aetherframe-mono";
}
