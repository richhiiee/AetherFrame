using System;
using System.IO;
using System.Text.RegularExpressions;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;

namespace AetherFrame.Services.Diagnostics;

/// <summary>
/// What the player is shown when something fails. AetherFrame's own refusals are written for the
/// player and shown as they are; anything else — a file system or runtime exception, whose text
/// names local paths (and so the Windows account name) and is technical anyway — becomes a plain
/// description instead. The full exception still belongs in the log.
/// </summary>
internal static partial class UserFacingError
{
    /// <param name="exception">What failed.</param>
    /// <param name="fallback">What to say when the exception's own text isn't meant for the player
    /// (e.g. "The Plate couldn't be saved.").</param>
    internal static string Describe(Exception exception, string fallback)
    {
        var message = exception switch
        {
            // Thrown by AetherFrame itself with player-facing text. Exactly InvalidOperationException:
            // the runtime's own subclasses (ObjectDisposedException, …) are technical.
            PlateLibraryException or TemplateLibraryException => exception.Message,
            _ when exception.GetType() == typeof(InvalidOperationException) => exception.Message,

            FileNotFoundException or DirectoryNotFoundException => $"{fallback} The file couldn't be found.",
            UnauthorizedAccessException => $"{fallback} AetherFrame wasn't allowed to access the file.",
            PathTooLongException => $"{fallback} The file's location is too long.",
            IOException => $"{fallback} The file may be in use by another program.",
            _ => fallback,
        };

        // A safety net for anything above that still carries a path.
        return string.IsNullOrWhiteSpace(message) || ContainsPath(message) ? fallback : message;
    }

    /// <summary>Whether <paramref name="text"/> contains something that looks like a local path.</summary>
    internal static bool ContainsPath(string text) => PathPattern().IsMatch(text);

    // A drive path (C:\… or C:/…), a UNC path (\\server\…), or an absolute Unix path under a
    // user or system root.
    [GeneratedRegex(@"[A-Za-z]:[\\/]|\\\\[^\\\s]+\\|(?:^|[\s'""(])/(?:home|Users|mnt|media|tmp|var|root|opt)/", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
