using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// The Basic editor's top-level categories, in navigator order — each answers "what part of my
/// Plate am I editing?": the whole Plate's look (Style), the portrait, the identity (name and
/// title), the details (Home World, Favorite Job and Level, Free Company, Playstyle, Active Hours),
/// the message. Not a wizard — any category can be opened at any time.
/// </summary>
internal enum BasicEditorCategory
{
    Style,
    Portrait,
    Identity,
    Details,
    Message,
}

/// <summary>The Basic editor's panels (a category shows one or more), in the order they appear.</summary>
internal enum BasicEditorPanel
{
    PlateLayout,
    BackgroundTheme,
    Portrait,
    Identity,
    HomeWorld,
    JobAndLevel,
    FreeCompany,
    Playstyle,
    ActiveHours,
    Message,
}

/// <summary>How the Basic editor arranges navigator, inspector, and preview for the space it has.</summary>
internal enum BasicEditorLayoutMode
{
    /// <summary>Navigator | inspector | preview.</summary>
    ThreeColumn,

    /// <summary>Category selector above the inspector | preview.</summary>
    TwoColumn,

    /// <summary>Category selector, then the preview, then the inspector.</summary>
    Stacked,
}

/// <summary>How large the Basic editor's live preview is drawn. Editor-only view state: never saved,
/// and never touches the Plate (its canvas and font sizes stay exactly as they are).</summary>
internal enum PreviewZoom
{
    /// <summary>The whole Plate fits the preview area.</summary>
    Fit,

    /// <summary>1.5x the fitted size; scroll or drag to look around.</summary>
    Large,

    /// <summary>2x the fitted size; scroll or drag to look around.</summary>
    Larger,
}

/// <summary>
/// What a category needs to tell the user, derived from the Plate on demand (nothing stored).
/// Only <see cref="NeedsAttention"/> warrants a warning; the rest are quiet hints.
/// </summary>
/// <param name="Customized">A section here was moved or resized in the Advanced Editor.</param>
/// <param name="Hidden">A section here exists but is hidden.</param>
/// <param name="Collision">A section here overlaps another section on the Plate.</param>
/// <param name="Unsupported">The Plate holds content this version can't display (Design only).</param>
internal readonly record struct BasicCategoryStatus(bool Customized, bool Hidden, bool Collision, bool Unsupported)
{
    internal bool NeedsAttention => Collision || Unsupported;

    internal bool IsQuiet => !Customized && !Hidden && !NeedsAttention;
}

/// <summary>
/// The Basic editor's navigation state: the selected category and the live view's zoom.
/// Lives as long as the editor window; a different Plate opening starts over on Design. Editing
/// never changes it, so the user always stays where they are.
/// </summary>
internal sealed class BasicEditorNavigation
{
    private Guid? plateId;

    internal BasicEditorCategory Selected { get; private set; } = BasicEditorCategory.Style;

    internal PreviewZoom Zoom { get; set; } = PreviewZoom.Fit;

    internal void Select(BasicEditorCategory category) => Selected = category;

    /// <summary>
    /// Call every frame with the open Plate. When it's a different Plate (e.g. one just created),
    /// the editor starts over on Design with the normal layout; the same Plate keeps everything.
    /// </summary>
    internal void TrackPlate(Guid? openPlateId)
    {
        if (openPlateId == plateId)
        {
            return;
        }

        plateId = openPlateId;
        Selected = BasicEditorCategory.Style;
        Zoom = PreviewZoom.Fit;
    }
}

/// <summary>
/// Pure presentation rules for the Basic editor: its categories and panels, which Plate sections
/// each category edits, category status and summaries, the responsive layout, the preview's size,
/// and which category a click on the preview belongs to. No ImGui; nothing here changes the Plate.
/// </summary>
internal static class BasicEditorView
{
    internal static readonly BasicEditorCategory[] Categories = Enum.GetValues<BasicEditorCategory>();

    /// <summary>Each category's panels, in order.</summary>
    private static readonly Dictionary<BasicEditorCategory, BasicEditorPanel[]> Panels = new()
    {
        [BasicEditorCategory.Style] = [BasicEditorPanel.BackgroundTheme, BasicEditorPanel.PlateLayout],
        [BasicEditorCategory.Portrait] = [BasicEditorPanel.Portrait],
        [BasicEditorCategory.Identity] = [BasicEditorPanel.Identity],
        [BasicEditorCategory.Details] =
        [
            BasicEditorPanel.HomeWorld, BasicEditorPanel.JobAndLevel, BasicEditorPanel.FreeCompany,
            BasicEditorPanel.Playstyle, BasicEditorPanel.ActiveHours,
        ],
        [BasicEditorCategory.Message] = [BasicEditorPanel.Message],
    };

    /// <summary>
    /// Every panel in the editing flow, top to bottom: the visual theme, the layout, the portrait,
    /// identity, the details (Home World through Active Hours), the message (the categories'
    /// panels in navigator order).
    /// </summary>
    internal static readonly BasicEditorPanel[] PanelOrder = Categories.SelectMany(c => Panels[c]).ToArray();

    internal static IReadOnlyList<BasicEditorPanel> PanelsOf(BasicEditorCategory category) => Panels[category];

    internal static string Title(BasicEditorCategory category) => category switch
    {
        BasicEditorCategory.Style => "Style",
        BasicEditorCategory.Portrait => "Portrait",
        BasicEditorCategory.Identity => "Identity",
        BasicEditorCategory.Details => "Details",
        _ => "Message",
    };

    /// <summary>The Plate sections a category edits (Style edits the whole Plate, not a section).</summary>
    internal static BasicSection[] SectionsOf(BasicEditorCategory category) => category switch
    {
        BasicEditorCategory.Portrait => [BasicSection.Portrait],
        BasicEditorCategory.Identity => [BasicSection.Identity],
        BasicEditorCategory.Details =>
        [
            BasicSection.World, BasicSection.Job, BasicSection.Level, BasicSection.FreeCompany,
            BasicSection.Playstyle, BasicSection.ActiveHours,
        ],
        BasicEditorCategory.Message => [BasicSection.Message],
        _ => [],
    };

    /// <summary>The category that edits a section.</summary>
    internal static BasicEditorCategory CategoryOf(BasicSection section)
    {
        foreach (var category in Categories)
        {
            if (Array.IndexOf(SectionsOf(category), section) >= 0)
            {
                return category;
            }
        }

        return BasicEditorCategory.Style;
    }

    // ---------------------------------------------------------------- status and summaries

    /// <summary>A category's status, derived from the Plate as it is now.</summary>
    internal static BasicCategoryStatus StatusOf(ProfileDocument profile, BasicEditorCategory category)
    {
        if (category == BasicEditorCategory.Style)
        {
            return new BasicCategoryStatus(false, false, false, profile.HasUnsupportedElements);
        }

        var sections = SectionsOf(category);
        var customized = sections.Any(s => BasicPlateEditor.IsSectionCustomized(profile, s));
        var hidden = sections.Any(s => BasicSections.Exists(profile, s) && !BasicSections.IsVisible(profile, s))
            || (category == BasicEditorCategory.Identity && BasicSections.Find(profile, ProfileElementRole.BasicName) is { Visible: false });
        var collision = BasicPlateEditor.FindOverlaps(profile).Any(o => Involves(o.First) || Involves(o.Second));
        return new BasicCategoryStatus(customized, hidden, collision, false);

        bool Involves(BasicSection group) => BasicSections.LayoutGroupOf(group).Any(s => Array.IndexOf(sections, s) >= 0);
    }

    /// <summary>A short, human description of each non-quiet state (for a tooltip), or empty.</summary>
    internal static IReadOnlyList<string> Describe(BasicCategoryStatus status)
    {
        var lines = new List<string>();
        if (status.Collision)
        {
            lines.Add("Overlaps another section on the Plate.");
        }

        if (status.Unsupported)
        {
            lines.Add("This Plate has content this version can't display (it's kept).");
        }

        if (status.Customized)
        {
            lines.Add("Customized in the Advanced Editor.");
        }

        if (status.Hidden)
        {
            lines.Add("Has a hidden section.");
        }

        return lines;
    }

    /// <summary>
    /// A few words about what the category currently holds, for the top of its inspector. Derived
    /// from the Plate on demand; editor-only (never drawn on the Plate).
    /// </summary>
    internal static IReadOnlyList<string> SummaryOf(ProfileDocument profile, BasicEditorCategory category)
    {
        string Text(ProfileElementRole role) => BasicSections.FindText(profile, role)?.Text.Trim() ?? string.Empty;
        string Visible(ProfileElementRole role) => BasicSections.Find(profile, role) is { Visible: true } ? Text(role) : string.Empty;

        switch (category)
        {
            case BasicEditorCategory.Style:
            {
                var orientation = BasicPlateEditor.GetOrientation(profile) == AdventurePlateOrientation.Mirrored ? "Mirrored" : "Normal";
                var theme = ProfileThemePresets.Find(profile.BasicPlate?.ThemeId) is { } preset ? $"{preset.Name} theme" : "No theme chosen";
                return [$"{orientation} layout  ·  {theme}"];
            }

            case BasicEditorCategory.Portrait:
                return BasicSections.Find(profile, ProfileElementRole.BasicPortrait) switch
                {
                    null => ["No portrait yet"],
                    { Visible: false } => ["Imported image (hidden)"],
                    _ => ["Imported image"],
                };

            case BasicEditorCategory.Identity:
            {
                var lines = new List<string> { Visible(ProfileElementRole.BasicName) is { Length: > 0 } shown ? shown : "No name shown" };
                if (Visible(ProfileElementRole.BasicTitle) is { Length: > 0 } title)
                {
                    lines.Add(title);
                }

                return lines;
            }

            case BasicEditorCategory.Details:
            {
                var job = string.Join(" ", new[] { Text(ProfileElementRole.BasicLevel), Text(ProfileElementRole.BasicJob) }.Where(t => t.Length > 0));
                var count = profile.BasicPlate?.Playstyles.Count ?? 0;
                var hours = Text(ProfileElementRole.BasicActiveHours);
                return
                [
                    Text(ProfileElementRole.BasicWorld) is { Length: > 0 } world ? world : "No Home World",
                    job.Length > 0 ? job : "No Favorite Job",
                    Text(ProfileElementRole.BasicFreeCompany) is { Length: > 0 } fc ? $"Free Company: {fc}" : "No Free Company",
                    $"{count switch { 0 => "No playstyles yet", 1 => "1 playstyle", _ => $"{count} playstyles" }}  ·  {(hours.Length > 0 ? hours : "No active hours")}",
                ];
            }

            default:
            {
                var message = Text(ProfileElementRole.BasicMessage).Replace('\n', ' ');
                return [message.Length == 0 ? "No message yet" : message.Length <= 60 ? message : message[..57].TrimEnd() + "..."];
            }
        }
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Content width (unscaled pixels) from which navigator, inspector, and preview sit side by side.</summary>
    internal const float ThreeColumnMinWidth = 1000f;

    /// <summary>Content width (unscaled pixels) from which inspector and preview sit side by side.</summary>
    internal const float TwoColumnMinWidth = 760f;

    /// <summary>The arrangement for a content width, at the UI's global scale (so it holds at any DPI).</summary>
    internal static BasicEditorLayoutMode ChooseLayout(float contentWidth, float globalScale)
    {
        var scale = Math.Max(0.1f, globalScale);
        return contentWidth >= ThreeColumnMinWidth * scale ? BasicEditorLayoutMode.ThreeColumn
            : contentWidth >= TwoColumnMinWidth * scale ? BasicEditorLayoutMode.TwoColumn
            : BasicEditorLayoutMode.Stacked;
    }

    // ---------------------------------------------------------------- preview

    internal static float ZoomFactor(PreviewZoom zoom) => zoom switch
    {
        PreviewZoom.Large => 1.5f,
        PreviewZoom.Larger => 2f,
        _ => 1f,
    };

    /// <summary>
    /// The preview's scale (screen pixels per canvas unit) and drawn size: the canvas fitted into
    /// <paramref name="available"/>, times the zoom. Zero scale when there's nothing to draw.
    /// </summary>
    internal static (float Scale, Vector2 Size) ComputePreview(Vector2 available, float canvasWidth, float canvasHeight, PreviewZoom zoom)
    {
        var fit = ComputePreview(available, new CanvasBounds(Vector2.Zero, new Vector2(canvasWidth, canvasHeight)), zoom);
        return (fit.Scale, fit.Size);
    }

    /// <summary>
    /// The preview fit of a Plate's visual bounds (see <see cref="ProfileVisualBounds"/>): fitted into
    /// <paramref name="available"/> times the zoom, centered while they fit, at the scroll origin once
    /// zoomed past it. <see cref="PlateViewFit.CanvasOffset"/> places the canvas inside them.
    /// </summary>
    internal static PlateViewFit ComputePreview(Vector2 available, CanvasBounds visualBounds, PreviewZoom zoom) =>
        PlateViewFit.Fit(available, visualBounds, ZoomFactor(zoom));

    /// <summary>Where the drawn Plate starts within the preview area: centered while it fits, else at the scrolled origin.</summary>
    internal static Vector2 PreviewOffset(Vector2 available, Vector2 size) =>
        new(Math.Max(0f, (available.X - size.X) / 2f), Math.Max(0f, (available.Y - size.Y) / 2f));

    /// <summary>
    /// The category of the Basic section drawn topmost at a point on the Plate (logical canvas
    /// coordinates), or null. Uses the same paint order and hit test as the Advanced canvas, over
    /// what the finished Plate actually shows: hidden elements and suppressed empty headings don't
    /// count, and elements that aren't Basic sections are looked through.
    /// </summary>
    internal static BasicEditorCategory? CategoryAt(ProfileDocument profile, Vector2 logicalPoint, List<ProfileElement> paintOrderBuffer)
    {
        ProfilePaintOrder.Fill(profile, paintOrderBuffer, includeHidden: false);
        var hit = ProfilePaintOrder.HitTest(
            paintOrderBuffer,
            logicalPoint,
            element => BasicSections.SectionOf(element.Role) is null || !BasicSections.IsDrawnInFinishedRendering(profile, element));
        paintOrderBuffer.Clear();

        return hit is not null && BasicSections.SectionOf(hit.Role) is { } section ? CategoryOf(section) : null;
    }
}
