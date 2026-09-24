using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Fonts;
using AetherFrame.UI.Editor;

namespace AetherFrame.UI.Rendering;

/// <summary>Measures text with the same fonts and rules <see cref="ProfileTextRenderer"/> draws with.</summary>
internal sealed class ProfileTextMeasurer : IIdentityTextMeasurer
{
    private readonly ProfileFontService fonts;

    internal ProfileTextMeasurer(ProfileFontService fonts)
    {
        this.fonts = fonts;
    }

    public bool TryMeasureNaturalWidth(TextProfileElement element, out float width) =>
        ProfileTextRenderer.TryMeasureNaturalWidth(element, fonts, out width);
}
