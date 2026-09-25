using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Services.Fonts;

/// <summary>
/// One selectable entry in the Inspector's Font Family control: a display name plus whether the
/// family has a REAL Bold and/or Italic face (not a synthesized one). The Inspector uses these
/// flags to decide which style checkboxes to even show — see
/// <c>ProfileEditorWindow.DrawTypographySection</c> — rather than exposing a control that
/// would silently do nothing, or worse, fake the style geometrically.
/// </summary>
internal sealed record ProfileFontFamilyDescriptor(string Id, string DisplayName, bool SupportsBold, bool SupportsItalic);

/// <summary>
/// The small, curated, portable set of font families AetherFrame ships with. Every curated
/// family (everything except <see cref="DalamudDefault"/>) is embedded directly in the plugin
/// assembly as real Regular/Bold/Italic/Bold Italic TrueType faces — never depends on an
/// arbitrary locally-installed Windows font, and never fakes Bold/Italic geometrically for a
/// family that lacks the real face. See <see cref="ProfileFontService"/> for how an id here maps
/// to the actual embedded font bytes (or, for <see cref="DalamudDefault"/>, to Dalamud's own
/// built-in default font).
///
/// Deliberately just a static list rather than anything pluggable: adding a family later is a
/// matter of embedding its four TTFs (see the AetherFrame.csproj Fonts glob) and adding one
/// descriptor plus one switch arm in <see cref="ProfileFontService"/> — no architecture changes
/// needed. Real font importing (arbitrary user-supplied files) is intentionally out of scope for
/// now.
/// </summary>
internal static class ProfileFontCatalog
{
    /// <summary>Preserved for legacy compatibility only — every element saved before FontFamily
    /// existed resolves here. No real Bold/Italic face; never offered as the default for new text.</summary>
    internal static readonly ProfileFontFamilyDescriptor DalamudDefault =
        new(ProfileFontFamilies.DalamudDefault, "Dalamud Default", SupportsBold: false, SupportsItalic: false);

    /// <summary>AetherFrame's own default for new text: a complete Regular/Bold/Italic/Bold
    /// Italic family (PT Sans, SIL OFL 1.1).</summary>
    internal static readonly ProfileFontFamilyDescriptor AetherFrameSans =
        new(ProfileFontFamilies.AetherFrameSans, "AetherFrame Sans", SupportsBold: true, SupportsItalic: true);

    /// <summary>A complete serif family (PT Serif, SIL OFL 1.1).</summary>
    internal static readonly ProfileFontFamilyDescriptor AetherFrameSerif =
        new(ProfileFontFamilies.AetherFrameSerif, "AetherFrame Serif", SupportsBold: true, SupportsItalic: true);

    /// <summary>A complete monospace family (Cousine, SIL OFL 1.1).</summary>
    internal static readonly ProfileFontFamilyDescriptor AetherFrameMono =
        new(ProfileFontFamilies.AetherFrameMono, "AetherFrame Mono", SupportsBold: true, SupportsItalic: true);

    /// <summary>Display order for the Inspector's Family combo: AetherFrame's fully-styled
    /// families first (the ones a user should actually pick), legacy compatibility last.</summary>
    internal static readonly IReadOnlyList<ProfileFontFamilyDescriptor> All =
        [AetherFrameSans, AetherFrameSerif, AetherFrameMono, DalamudDefault];

    /// <summary>Resolves a persisted family id to its descriptor, falling back to
    /// <see cref="DalamudDefault"/> for null/unrecognized ids (e.g. a legacy element, or one
    /// saved by a newer build with a family this one doesn't know).</summary>
    internal static ProfileFontFamilyDescriptor Resolve(string? familyId) =>
        All.FirstOrDefault(f => f.Id == familyId) ?? DalamudDefault;
}
