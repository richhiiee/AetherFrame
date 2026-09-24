using System;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Basic;

/// <summary>
/// The one rule for the size the Basic character name renders at. The Identity Header's layout
/// sizes the name's box with it, and the renderer draws the name with it instead of the generic
/// text auto fit — so there is a single decision, made from the name's own text and box, and a
/// stale or narrow box can never crush the name into tiny text.
///
/// <para>The name shows at its font size whenever its box holds it. Only when the text alone is
/// wider than the box is it reduced — continuously, by exactly the amount needed — and never below
/// <see cref="MinimumRatio"/> of its size. A name still too wide at that size word-wraps at that
/// size (within the box) rather than shrinking further: it stays readable, and no text is cut.</para>
///
/// <para>Applies to the Basic name with auto fit on and wrap off (the Basic default); a name the
/// player set to wrap, or without auto fit, keeps the generic text behavior.</para>
/// </summary>
internal static class BasicNameFit
{
    /// <summary>The smallest the name is ever drawn, as a fraction of its font size.</summary>
    public const float MinimumRatio = 0.9f;

    /// <summary>True when <paramref name="element"/> is sized by this rule rather than the generic auto fit.</summary>
    public static bool Applies(TextProfileElement element) =>
        element.Role == ProfileElementRole.BasicName && element.EffectiveAutoFit && !element.EffectiveWrap;

    /// <summary>
    /// The name's rendered size for text <paramref name="naturalWidth"/> wide at
    /// <paramref name="fontSize"/> in a line <paramref name="availableWidth"/> wide (the box minus
    /// its text padding), and whether it word-wraps at that size.
    /// </summary>
    public static (float Size, bool Wrap) Resolve(float fontSize, float naturalWidth, float availableWidth)
    {
        if (!(fontSize > 0f) || !(naturalWidth > availableWidth))
        {
            return (fontSize, false);
        }

        var minimum = fontSize * MinimumRatio;
        var fitted = availableWidth > 0f ? fontSize * (availableWidth / naturalWidth) : 0f;
        return fitted >= minimum ? (fitted, false) : (minimum, true);
    }

    /// <summary>The rendered size and wrap for <paramref name="element"/> in its own box (what the renderer draws).</summary>
    public static (float Size, bool Wrap) ForBox(TextProfileElement element, float naturalWidth) =>
        Resolve(element.FontSize, naturalWidth, Math.Max(0f, element.Size.X - (2f * TextProfileElement.LayoutPadding)));
}
