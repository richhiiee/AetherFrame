using System;
using AetherFrame.Domain.Basic;

namespace AetherFrame.Services;

/// <summary>
/// Keeps the logged-in character's details fresh without re-reading game data every frame: a read
/// is reused for at most <see cref="RefreshMilliseconds"/>, then taken again — so logging in, out,
/// changing job, or leveling up shows up within a moment while the editor stays open, and a
/// "no character" read is never kept past that. Dalamud-free (clock and reader are injected).
/// </summary>
internal sealed class CharacterInfoCache
{
    internal const long RefreshMilliseconds = 500;

    private readonly Func<BasicCharacterInfo?> read;
    private readonly Func<long> clockMilliseconds;
    private BasicCharacterInfo? cached;
    private long readAt;
    private bool hasRead;

    /// <param name="read">Reads the current details (null when no character is loaded).</param>
    /// <param name="clockMilliseconds">A monotonic millisecond clock.</param>
    internal CharacterInfoCache(Func<BasicCharacterInfo?> read, Func<long> clockMilliseconds)
    {
        this.read = read;
        this.clockMilliseconds = clockMilliseconds;
    }

    internal BasicCharacterInfo? Current
    {
        get
        {
            var now = clockMilliseconds();

            // Elapsed time, not a timestamp comparison, so no starting value can overflow into
            // "still fresh" (the bug this class replaces).
            if (!hasRead || now - readAt >= RefreshMilliseconds || now < readAt)
            {
                cached = read();
                readAt = now;
                hasRead = true;
            }

            return cached;
        }
    }

    /// <summary>Forgets the last read, so the next access reads again (e.g. on login or logout).</summary>
    internal void Invalidate() => hasRead = false;
}
