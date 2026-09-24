using System;

namespace AetherFrame.Domain.Basic;

/// <summary>One FFXIV character title from game data.</summary>
/// <param name="Id">The <c>Title</c> sheet row id.</param>
/// <param name="Masculine">Text shown for masculine characters.</param>
/// <param name="Feminine">Text shown for feminine characters (same as masculine for ungendered titles).</param>
/// <param name="IsPrefix">The game's own placement: true = shown before (above) the name, false = after (below).</param>
/// <param name="Order">The game's title list order.</param>
internal sealed record GameTitle(uint Id, string Masculine, string Feminine, bool IsPrefix, ushort Order)
{
    internal string GetText(bool feminine) => feminine ? Feminine : Masculine;

    internal bool Matches(string search) =>
        Masculine.Contains(search, StringComparison.OrdinalIgnoreCase) || Feminine.Contains(search, StringComparison.OrdinalIgnoreCase);
}
