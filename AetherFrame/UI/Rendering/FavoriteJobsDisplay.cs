using System;
using System.Collections.Generic;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Rendering;

/// <summary>
/// The Favorite Jobs value's display, derived every time a Plate is drawn — in every surface: the
/// Basic live view, the Advanced canvas, Preview, the Plate Viewer and My Plates' cards — through
/// <see cref="BasicFavoriteJobs.DisplayText"/>, measured with the renderer's own fonts and the game's
/// job data. The decision is cached per element and recomputed only when one of its inputs changes
/// (the stored text, the chosen jobs or their order, font family, size, bold, italic, letter
/// spacing, symbols, or the box's width), so an unchanged Plate costs one comparison per frame and
/// the derived string stays the same instance (which the text layout cache relies on). Render
/// thread only. Never changes the Plate.
/// </summary>
internal static class FavoriteJobsDisplay
{
    private static readonly Dictionary<Guid, Entry> Entries = new();

    /// <summary>The text to draw instead of the stored one, or null to draw the stored text.</summary>
    internal static string? Resolve(ProfileDocument profile, TextProfileElement element, ProfileRenderResources resources)
    {
        if (element.Role != ProfileElementRole.BasicJob)
        {
            return null;
        }

        if (!Entries.TryGetValue(element.Id, out var entry))
        {
            entry = new Entry();
            Entries[element.Id] = entry;
        }

        if (entry.Matches(profile, element))
        {
            return entry.Display;
        }

        var measured = true;
        var display = BasicFavoriteJobs.DisplayText(
            profile,
            element,
            id => resources.Jobs.Find(id),
            text =>
            {
                if (ProfileTextRenderer.TryMeasureNaturalWidth(element, text, resources.Fonts, out var width))
                {
                    return width;
                }

                measured = false;
                return null;
            });

        // A font that isn't built yet is retried next frame instead of caching "can't tell".
        if (measured)
        {
            entry.Capture(profile, element, display);
        }

        return display;
    }

    private sealed class Entry
    {
        private readonly List<uint> ids = new();
        private bool valid;
        private string text = string.Empty;
        private string prefix = string.Empty;
        private string suffix = string.Empty;
        private string fontFamily = string.Empty;
        private float fontSize;
        private bool bold;
        private bool italic;
        private float letterSpacing;
        private float width;

        internal string? Display { get; private set; }

        internal bool Matches(ProfileDocument profile, TextProfileElement element)
        {
            if (!valid
                || !ReferenceEquals(text, element.Text) || prefix != element.Prefix || suffix != element.Suffix
                || fontFamily != element.FontFamily || !fontSize.Equals(element.FontSize) || bold != element.Bold || italic != element.Italic
                || !letterSpacing.Equals(element.LetterSpacing) || !width.Equals(element.Size.X))
            {
                return false;
            }

            var current = profile.BasicPlate;
            if (current is null)
            {
                return ids.Count == 0;
            }

            if (current.FavoriteJobIds.Count == 0)
            {
                return current.FavoriteJobId == 0 ? ids.Count == 0 : ids.Count == 1 && ids[0] == current.FavoriteJobId;
            }

            if (ids.Count != current.FavoriteJobIds.Count)
            {
                return false;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] != current.FavoriteJobIds[i])
                {
                    return false;
                }
            }

            return true;
        }

        internal void Capture(ProfileDocument profile, TextProfileElement element, string? display)
        {
            valid = true;
            text = element.Text;
            prefix = element.Prefix;
            suffix = element.Suffix;
            fontFamily = element.FontFamily;
            fontSize = element.FontSize;
            bold = element.Bold;
            italic = element.Italic;
            letterSpacing = element.LetterSpacing;
            width = element.Size.X;
            ids.Clear();
            ids.AddRange(BasicFavoriteJobs.IdsOf(profile));
            Display = display;
        }
    }
}
