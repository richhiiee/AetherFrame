using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// What a My Plates card shows: the real saved Plate, drawn in miniature by the shared
/// <c>ProfileRenderer</c> (see <see cref="PlateCardPreviewCache"/> for the document side and
/// <see cref="Options"/> for how text is simplified at card size). Pure logic, no Dalamud.
/// </summary>
internal static class PlateCardPreview
{
    /// <summary>
    /// Text drawn smaller than this many screen pixels at card size becomes a soft bar in its own
    /// color (<see cref="TextBars"/>) — the section's shape and color, not illegible glyph noise.
    /// </summary>
    internal const float TextBarThresholdPixels = 8f;

    /// <summary>Card previews render the finished Plate, with tiny text simplified to bars.</summary>
    internal static readonly ProfileRenderOptions Options = ProfileRenderOptions.Finished with { TextAsBarsBelowPixelSize = TextBarThresholdPixels };

    /// <summary>
    /// The fit of a Plate's visual bounds (canvas plus any Component overflow) into a card's
    /// thumbnail area: uniform scale, centered, never stretched — the same fit every preview uses.
    /// </summary>
    internal static PlateViewFit Fit(Vector2 cardSize, CanvasBounds visualBounds) => PlateViewFit.Fit(cardSize, visualBounds);
}

/// <summary>
/// The documents behind My Plates card previews, keyed by Plate id and the Plate's content version
/// (its revision and modified time — the same key thumbnails use), so an unchanged Plate reuses its
/// entry (document and visual bounds) and a saved change is picked up by the very next frame.
/// Holds only references to the Library's own saved documents (no copies, no GPU resources); entries
/// for Plates no longer shown are dropped with <see cref="Retain"/>.
/// </summary>
internal sealed class PlateCardPreviewCache
{
    private readonly Dictionary<Guid, Entry> entries = new();
    private readonly List<Guid> removeBuffer = new();

    internal int Count => entries.Count;

    /// <summary>
    /// The preview for <paramref name="plateId"/> at <paramref name="versionKey"/>: the cached entry
    /// when the version is unchanged, else freshly loaded through <paramref name="load"/> (null when
    /// the Plate can't be loaded — the card then shows its fallback).
    /// </summary>
    internal Entry? Get(Guid plateId, string versionKey, Func<ProfileDocument?> load)
    {
        if (entries.TryGetValue(plateId, out var cached) && cached.VersionKey == versionKey)
        {
            return cached;
        }

        var document = load();
        if (document is null)
        {
            entries.Remove(plateId);
            return null;
        }

        var entry = new Entry(versionKey, document, ProfileVisualBounds.Compute(document));
        entries[plateId] = entry;
        return entry;
    }

    /// <summary>Forgets a Plate's entry (it's reloaded on its next use).</summary>
    internal void Invalidate(Guid plateId) => entries.Remove(plateId);

    /// <summary>Drops every entry whose Plate isn't in <paramref name="shown"/>.</summary>
    internal void Retain(IReadOnlySet<Guid> shown)
    {
        removeBuffer.Clear();
        foreach (var plateId in entries.Keys)
        {
            if (!shown.Contains(plateId))
            {
                removeBuffer.Add(plateId);
            }
        }

        foreach (var plateId in removeBuffer)
        {
            entries.Remove(plateId);
        }

        removeBuffer.Clear();
    }

    internal void Clear() => entries.Clear();

    /// <summary>One card's preview: the saved document it shows and that document's visual bounds.</summary>
    internal sealed record Entry(string VersionKey, ProfileDocument Document, CanvasBounds Bounds);
}

/// <summary>
/// Text simplified for tiny sizes: one soft bar per line the text would occupy, placed and aligned
/// like the text itself, its width following the content's length. Canvas units throughout.
/// </summary>
internal static class TextBars
{
    // Average glyph advance of the curated fonts, in ems (close enough for a stand-in shape).
    private const float AverageAdvanceEm = 0.5f;
    private const float BarHeightEm = 0.5f;
    private const float LineHeightEm = 1.25f;

    /// <summary>
    /// The bars for <paramref name="content"/> in a text box (<paramref name="position"/>,
    /// <paramref name="size"/>) with <paramref name="padding"/> inside it; empty when there's nothing to show.
    /// </summary>
    internal static void Compute(
        Vector2 position, Vector2 size, string? content, float fontSize, bool wrap,
        TextAlignment alignment, TextVerticalAlignment verticalAlignment, float padding, List<(Vector2 Min, Vector2 Max)> output)
    {
        output.Clear();
        if (string.IsNullOrEmpty(content) || !(fontSize > 0f))
        {
            return;
        }

        var available = new Vector2(Math.Max(0f, size.X - (2f * padding)), Math.Max(0f, size.Y - (2f * padding)));
        if (!(available.X > 0f))
        {
            return;
        }

        var total = content.Length * fontSize * AverageAdvanceEm;
        var lineHeight = fontSize * LineHeightEm;
        var maxLines = Math.Max(1, (int)(available.Y / lineHeight));
        var lines = wrap ? Math.Clamp((int)MathF.Ceiling(total / available.X), 1, maxLines) : 1;
        var barHeight = fontSize * BarHeightEm;
        var block = lines * lineHeight;

        var top = verticalAlignment switch
        {
            TextVerticalAlignment.Middle => position.Y + padding + ((available.Y - block) / 2f),
            TextVerticalAlignment.Bottom => position.Y + padding + available.Y - block,
            _ => position.Y + padding,
        };

        var remaining = total;
        for (var line = 0; line < lines; line++)
        {
            var width = Math.Min(remaining, available.X);
            remaining -= width;
            if (!(width > 0f))
            {
                break;
            }

            var x = alignment switch
            {
                TextAlignment.Center => position.X + padding + ((available.X - width) / 2f),
                TextAlignment.Right => position.X + padding + available.X - width,
                _ => position.X + padding,
            };

            var y = top + (line * lineHeight) + ((lineHeight - barHeight) / 2f);
            output.Add((new Vector2(x, y), new Vector2(x + width, y + barHeight)));
        }
    }
}
