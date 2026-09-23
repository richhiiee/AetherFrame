using System;
using System.Collections.Generic;
using Dalamud.Game.Player;
using Lumina.Excel.Sheets;

namespace AetherFrame.Services;

/// <summary>
/// FFXIV character titles, read from the live game data (the Lumina <c>Title</c> sheet, in the
/// client's language) rather than a hardcoded list, plus the logged-in character's unlock state.
///
/// <para><b>Unlocked state</b> comes from Dalamud's <c>IUnlockState</c> (managed, no unsafe code).
/// The game only has the character's title list after it has been received from the server this
/// session — typically once the Titles list in the Character window has been opened. Until then
/// <see cref="IsUnlockStateKnown"/> is false and every title reports "unknown" (never guessed as
/// unlocked or locked).</para>
///
/// Loaded lazily on first use and kept for the plugin's lifetime (a few thousand short strings).
/// Must be used from the main (framework/draw) thread, as <c>IUnlockState</c> reads game memory.
/// </summary>
internal sealed class GameTitleCatalog
{
    private IReadOnlyList<GameTitle>? titles;
    private Dictionary<uint, GameTitle>? titlesById;

    /// <summary>Every non-empty title, in the game's own title list order.</summary>
    internal IReadOnlyList<GameTitle> Titles
    {
        get
        {
            EnsureLoaded();
            return titles!;
        }
    }

    /// <summary>True when the game has the logged-in character's title list, so unlock checks are real.</summary>
    internal bool IsUnlockStateKnown
    {
        get
        {
            try
            {
                return DalamudServices.PlayerState.IsLoaded && DalamudServices.UnlockState.IsTitleListLoaded;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Whether the logged-in character shows the feminine form of gendered titles.</summary>
    internal static bool UseFeminineForms =>
        DalamudServices.PlayerState.IsLoaded && DalamudServices.PlayerState.Sex == Sex.Female;

    internal GameTitle? Find(uint titleId)
    {
        EnsureLoaded();
        return titlesById!.TryGetValue(titleId, out var title) ? title : null;
    }

    /// <summary>True/false once the title list is loaded (see <see cref="IsUnlockStateKnown"/>); null while unknown.</summary>
    internal bool? IsUnlocked(GameTitle title)
    {
        if (!IsUnlockStateKnown)
        {
            return null;
        }

        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Title>();
            return sheet.TryGetRow(title.Id, out var row) && DalamudServices.UnlockState.IsTitleUnlocked(row);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void EnsureLoaded()
    {
        if (titles is not null)
        {
            return;
        }

        var list = new List<GameTitle>();
        try
        {
            foreach (var row in DalamudServices.DataManager.GetExcelSheet<Title>())
            {
                var masculine = row.Masculine.ExtractText().Trim();
                var feminine = row.Feminine.ExtractText().Trim();
                if (masculine.Length == 0 && feminine.Length == 0)
                {
                    continue;
                }

                list.Add(new GameTitle(
                    row.RowId,
                    masculine.Length > 0 ? masculine : feminine,
                    feminine.Length > 0 ? feminine : masculine,
                    row.IsPrefix,
                    row.Order));
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AetherFrame could not read the FFXIV title list from game data.");
        }

        list.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id));
        titles = list;

        titlesById = new Dictionary<uint, GameTitle>(list.Count);
        foreach (var title in list)
        {
            titlesById[title.Id] = title;
        }
    }
}

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
