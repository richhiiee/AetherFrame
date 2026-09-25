using AetherFrame.Services.Plates;

namespace AetherFrame.UI.Library;

/// <summary>
/// How My Plates refers to the logged-in character. Deliberately never by name, Home World, or any
/// other identifier: AetherFrame's own window chrome doesn't show automatic player identity unless a
/// feature genuinely needs the player to see it (what a player puts into their own Plate is theirs).
/// The character still decides which Plate is Active — only through its binding, never on screen.
/// </summary>
internal static class MyPlatesCharacterText
{
    /// <summary>The logged-in character, as My Plates' messages and prompts name it.</summary>
    internal const string CurrentCharacter = "your current character";

    /// <summary>The header's character line: nothing while a character is logged in, otherwise why
    /// Set Active is unavailable.</summary>
    internal static string? HeaderStatus(CharacterContext? character) =>
        character is null ? "No character logged in" : null;

    /// <summary>After Set Active.</summary>
    internal static string NowActive(string plateName) => $"\"{plateName}\" is now {CurrentCharacter}'s Active Plate.";

    /// <summary>After a character's first Plate was created (and so became its Active Plate).</summary>
    internal const string FirstPlateCreated = $"Created your first Plate. It's now {CurrentCharacter}'s Active Plate.";

    /// <summary>The Create Plate chooser's note on who a new Plate belongs to.</summary>
    internal const string NewPlateBelongsToCurrent =
        $"New Plate will belong to {CurrentCharacter}. It becomes Active only if it's the character's first Plate.";

    /// <summary>The Delete prompt's warning when the Plate is the logged-in character's Active Plate.</summary>
    internal static readonly string[] DeletingCurrentActive =
    [
        $"This is {CurrentCharacter}'s Active Plate.",
        "Your current character will be left without an Active Plate.",
    ];
}
