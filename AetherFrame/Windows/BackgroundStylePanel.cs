using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Controls for the shared <see cref="ProfileBackground"/> — mode, theme presets, colors,
/// gradient, texture, image, and opacity — used by both the Advanced editor's Canvas tab and the
/// Basic editor, so there is one background editor, not two. Every mode's settings are kept while
/// switching modes; every edit goes through <see cref="EditorSession"/> (sliders and colors are
/// one undo step per drag).
/// </summary>
internal sealed class BackgroundStylePanel
{
    private static readonly string[] BackgroundModeLabels = ["None", "Solid Color", "Linear Gradient", "Textured Fill", "Image"];
    private static readonly string[] TextureLabels =
    [
        "None", "Fine Noise", "Dots", "Grid", "Diagonal Lines", "Crosshatch", "Subtle Paper",
        "Checkerboard", "Stripes", "Waves", "Herringbone", "Honeycomb", "Scales", "Speckle",
        "Diamonds", "Chevron", "Sparkle", "Linen", "Ripples", "Quatrefoil", "Brick",
    ];
    private static readonly string[] ImageFitLabels = ["Fit", "Fill", "Stretch"];
    private static readonly ProfileImageFit[] ImageFitOrder = [ProfileImageFit.Fit, ProfileImageFit.Fill, ProfileImageFit.Stretch];

    private readonly EditorSession editorSession;
    private readonly ProfileRenderResources renderResources;
    private readonly Action<string, Action<string>> openImageFileDialog;

    /// <param name="editorSession">The shared editing session.</param>
    /// <param name="renderResources">For the background image's native size.</param>
    /// <param name="openImageFileDialog">Opens the owning window's image picker (title, on-selected).</param>
    internal BackgroundStylePanel(EditorSession editorSession, ProfileRenderResources renderResources, Action<string, Action<string>> openImageFileDialog)
    {
        this.editorSession = editorSession;
        this.renderResources = renderResources;
        this.openImageFileDialog = openImageFileDialog;
    }

    /// <summary>
    /// Draws the controls. <paramref name="applyTheme"/> is what a preset card does (the Advanced
    /// editor recolors the background only, so its row is labelled Presets, not Theme); the row is
    /// left out in Image mode (where a background preset would replace the image), or entirely when
    /// null — the Basic editor draws its own Theme browser (<see cref="DrawThemeBrowser"/>) first.
    /// </summary>
    internal void Draw(ProfileDocument profile, Action<ProfileThemePreset>? applyTheme)
    {
        if (profile.Background is not { } background)
        {
            EditorWidgets.Hint("Background unavailable.");
            return;
        }

        var modeIndex = (int)background.Mode;
        EditorWidgets.PropertyLabel("Mode");
        if (ImGui.Combo("##BackgroundMode", ref modeIndex, BackgroundModeLabels, BackgroundModeLabels.Length))
        {
            var newMode = (ProfileBackgroundMode)modeIndex;
            editorSession.ApplyBackgroundEdit(style => style.Mode = newMode);
        }

        if (applyTheme is not null && background.Mode != ProfileBackgroundMode.Image)
        {
            DrawBackgroundPresets(profile, applyTheme);
        }

        switch (background.Mode)
        {
            case ProfileBackgroundMode.None:
                EditorWidgets.Hint("No background. Choose a mode or a theme.");
                return;

            // TexturedFill's base has always been identical to SolidColor's (a flat fill) — kept
            // selectable only for Plates that already saved it; a Pattern no longer requires it.
            case ProfileBackgroundMode.SolidColor:
            case ProfileBackgroundMode.TexturedFill:
                DrawBackgroundColor("Color", "##BgPrimary", background.PrimaryColor, primary: true);
                DrawSolidSwatches();
                break;

            case ProfileBackgroundMode.LinearGradient:
                DrawGradientControls(background);
                break;

            case ProfileBackgroundMode.Image:
                DrawBackgroundImageControls(background);
                break;
        }

        // The Pattern's own tuning — independent of Mode, shown whenever a Pattern is selected,
        // exactly like the renderer composes it independent of Mode (see ProfileBackgroundRenderer).
        if (background.Texture != ProfileBackgroundTexture.None)
        {
            ImGui.Spacing();
            DrawPatternTuningControls(background);
        }

        // Global Background Opacity isn't exposed in Basic (applyTheme is null there — see the
        // two call sites in BasicProfileEditorWindow.Design.cs / ProfileEditorWindow.CanvasSettings.cs):
        // a new or Theme-driven Basic background should always read as fully visible, with Pattern
        // strength controlled separately by Pattern Intensity above. The field itself (and Advanced's
        // existing control here) is untouched — no migration, no schema change, nothing deleted.
        if (applyTheme is not null)
        {
            ImGui.Spacing();
            var opacity = background.Opacity * 100f;
            EditorWidgets.PropertyLabel("Opacity");
            if (ImGui.SliderFloat("##BgOpacity", ref opacity, 0f, 100f, "%.0f%%"))
            {
                var value = opacity / 100f;
                editorSession.BeginOrContinueBackgroundEdit(style => style.Opacity = value);
            }

            CommitBackgroundOnRelease();
        }
    }

    private const float ThemeCardWidth = 118f;
    private const float ThemeCardPadding = 6f;
    private const float PatternCardSize = 68f;

    // How many rows of theme cards the Basic Theme browser shows before its grid scrolls.
    private const float ThemeBrowserVisibleRows = 2.5f;

    // The Basic Theme browser's search and filter (editor-only view state).
    private readonly ThemeBrowserState themeBrowser = new();

    private static readonly Vector4 CardColor = new(1f, 1f, 1f, 0.04f);
    private static readonly Vector4 CardHoverColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 CardBorderColor = new(1f, 1f, 1f, 0.15f);

    /// <summary>
    /// The Advanced editor's background Presets: the theme presets as truthful preview cards grouped
    /// into <see cref="ThemeFamily"/> sections. It recolors only the background (not the text
    /// colors, and not the Plate's Basic theme), so it's worded as background Presets and marks no
    /// card as the current theme — which it never changes.
    /// </summary>
    private void DrawBackgroundPresets(ProfileDocument profile, Action<ProfileThemePreset> applyTheme)
    {
        EditorWidgets.PropertyLabel("Presets", 0f);
        ImGui.TextDisabled("Background colors only");
        EditorWidgets.Tooltip("Sets the background's colors. Text colors stay as they are.\nThe Basic Editor's Theme sets the background and every Basic text color together.");

        foreach (var family in ProfileThemePresets.FamilyOrder)
        {
            var members = ProfileThemePresets.All.Where(p => p.Family == family).ToArray();
            if (members.Length == 0)
            {
                continue;
            }

            using var id = ImRaii.PushId($"ThemeFamily{family}");
            if (ImGui.CollapsingHeader($"{family} ({members.Length})"))
            {
                DrawThemeCardGrid(profile, members, null, applyTheme);
                ImGui.Spacing();
            }
        }
    }

    /// <summary>
    /// The Basic editor's Theme browser: every theme in one collection — no tab or section per
    /// family, so it keeps working however large the catalog grows. From the top: the Plate's
    /// current theme (always named, whatever is filtered or scrolled away), a search field, family
    /// filters (All by default; one per family the catalog actually has), then one responsive grid
    /// of truthful preview cards that scrolls on its own once it's taller than a few rows. The
    /// current theme's card is marked and scrolled into view when a Plate opens. Clicking a card
    /// applies that theme exactly as before (<paramref name="applyTheme"/>, by its stable id).
    /// Filtering is <see cref="ThemeBrowser"/>'s; the search and filter are view state only.
    /// </summary>
    internal void DrawThemeBrowser(ProfileDocument profile, Action<ProfileThemePreset> applyTheme)
    {
        var current = ThemeBrowser.Current(profile);
        if (themeBrowser.PlateId != profile.ProfileId)
        {
            themeBrowser.PlateId = profile.ProfileId;
            themeBrowser.ScrollToCurrent = true;
        }

        // The current theme, at a glance.
        EditorWidgets.PropertyLabel("Current", 0f);
        if (current is { } chosen)
        {
            var swatch = ImGui.GetTextLineHeight();
            var min = ImGui.GetCursorScreenPos() + new Vector2(0f, (ImGui.GetFrameHeight() - swatch) / 2f);
            ImGui.GetWindowDrawList().AddRectFilledMultiColor(
                min, min + new Vector2(swatch * 1.6f, swatch),
                ImGui.GetColorU32(chosen.PrimaryColor), ImGui.GetColorU32(chosen.SecondaryColor),
                ImGui.GetColorU32(chosen.SecondaryColor), ImGui.GetColorU32(chosen.PrimaryColor));
            ImGui.Dummy(new Vector2(swatch * 1.6f, ImGui.GetFrameHeight()));
            ImGui.SameLine();
            ImGui.TextUnformatted(chosen.Name);
            ImGui.SameLine();
            ImGui.TextDisabled(chosen.Family.ToString());
        }
        else
        {
            ImGui.TextDisabled("None chosen yet");
        }

        // Search, with a clear button while it holds anything.
        var search = themeBrowser.Search;
        var clearWidth = search.Length > 0 ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X : 0f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - clearWidth);
        if (ImGui.InputTextWithHint("##ThemeSearch", "Search themes...", ref search, ThemeBrowserState.MaxSearchLength))
        {
            themeBrowser.Search = search;
        }

        if (themeBrowser.Search.Length > 0)
        {
            ImGui.SameLine();
            if (EditorWidgets.IconButton("ClearThemeSearch", FontAwesomeIcon.Times, "Clear search"))
            {
                themeBrowser.Search = string.Empty;
            }
        }

        DrawThemeFilters();

        var themes = ThemeBrowser.Filter(ProfileThemePresets.All, themeBrowser.Search, themeBrowser.Family);
        if (themes.Count == 0)
        {
            EditorWidgets.Hint("No themes match.");
            if (ImGui.SmallButton("Show all themes"))
            {
                themeBrowser.Clear();
            }

            return;
        }

        // One grid, sized to its rows up to a few, then scrolling on its own.
        var style = ImGui.GetStyle();
        var cardHeight = ThemeCardHeight(profile);
        var columns = ThemeBrowser.Columns(ImGui.GetContentRegionAvail().X - style.ScrollbarSize, ThemeCardWidth, style.ItemSpacing.X);
        var rows = (themes.Count + columns - 1) / columns;
        var contentHeight = (rows * cardHeight) + ((rows - 1) * style.ItemSpacing.Y);
        var maxHeight = (ThemeBrowserVisibleRows * cardHeight) + ((ThemeBrowserVisibleRows - 0.5f) * style.ItemSpacing.Y);
        using (var grid = ImRaii.Child("##ThemeGrid", new Vector2(-1f, MathF.Min(contentHeight, maxHeight)), false))
        {
            if (grid.Success)
            {
                DrawThemeCardGrid(profile, themes, current, applyTheme);
            }
        }
    }

    /// <summary>All, then one filter per family the catalog has (with its count); they wrap rather than run off.</summary>
    private void DrawThemeFilters()
    {
        using var id = ImRaii.PushId("ThemeFilters");
        var families = ThemeBrowser.Families(ProfileThemePresets.All);
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail().X;
        var rowUsed = 0f;

        Filter("All", null, ProfileThemePresets.All.Length);
        foreach (var (family, count) in families)
        {
            Filter(family.ToString(), family, count);
        }

        void Filter(string name, ThemeFamily? family, int count)
        {
            var label = $"{name} ({count})";
            var width = ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f);
            if (rowUsed > 0f)
            {
                if (rowUsed + style.ItemSpacing.X + width <= available)
                {
                    ImGui.SameLine();
                    rowUsed += style.ItemSpacing.X;
                }
                else
                {
                    rowUsed = 0f;
                }
            }

            if (EditorWidgets.TextToggle($"{label}##{name}", themeBrowser.Family == family))
            {
                themeBrowser.Family = family;
            }

            rowUsed += width;
        }
    }

    private float ThemeCardHeight(ProfileDocument profile) =>
        (ThemeCardWidth * profile.CanvasHeight / MathF.Max(1f, profile.CanvasWidth)) + (ThemeCardPadding * 3f) + ImGui.GetTextLineHeight();

    private void DrawThemeCardGrid(ProfileDocument profile, IReadOnlyList<ProfileThemePreset> members, ProfileThemePreset? current, Action<ProfileThemePreset> applyTheme)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var columns = ThemeBrowser.Columns(ImGui.GetContentRegionAvail().X, ThemeCardWidth, spacing);
        var rowStartX = ImGui.GetCursorPosX();

        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                if (i % columns == 0)
                {
                    ImGui.SetCursorPosX(rowStartX);
                }
                else
                {
                    ImGui.SameLine();
                }
            }

            var selected = current?.Id == members[i].Id;
            if (DrawThemeCard(profile, members[i], selected))
            {
                applyTheme(members[i]);
            }

            if (selected && themeBrowser.ScrollToCurrent)
            {
                // Once per Plate: the current theme's card in view, however long the list.
                themeBrowser.ScrollToCurrent = false;
                ImGui.SetScrollHereY(0.5f);
            }
        }
    }

    /// <summary>One theme's card: the profile's background as it would look with the theme applied,
    /// sample text in its Name/Title colors, and its name below. An accent border and a check mark
    /// mark the theme currently applied to this profile. A card scrolled out of view draws nothing.</summary>
    private bool DrawThemeCard(ProfileDocument profile, ProfileThemePreset preset, bool selected)
    {
        var previewHeight = ThemeCardWidth * profile.CanvasHeight / MathF.Max(1f, profile.CanvasWidth);
        var textHeight = ImGui.GetTextLineHeight();
        var cardSize = new Vector2(ThemeCardWidth, previewHeight + (ThemeCardPadding * 3f) + textHeight);

        ImGui.InvisibleButton($"##Theme{preset.Id}", cardSize);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        EditorWidgets.Tooltip($"{preset.Name}\n{preset.Description}");
        if (!ImGui.IsRectVisible(min, max))
        {
            return clicked;
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? CardHoverColor : CardColor), 6f);

        var previewMin = min + new Vector2(ThemeCardPadding);
        var previewSize = new Vector2(ThemeCardWidth - (ThemeCardPadding * 2f), previewHeight);
        var previewMax = previewMin + previewSize;

        var preview = profile.Background?.Clone() ?? new ProfileBackground();
        var keepImage = preview.HasImage;
        preset.ApplyTo(preview);
        if (keepImage)
        {
            preview.Mode = ProfileBackgroundMode.Image;
        }

        drawList.PushClipRect(previewMin, previewMax, true);
        var scale = previewSize.X / MathF.Max(1f, profile.CanvasWidth);
        ProfileBackgroundRenderer.Draw(drawList, preview, previewMin, previewSize, scale, renderResources);

        var font = ImGui.GetFont();
        var sampleSize = MathF.Max(8f, ImGui.GetFontSize() * 0.55f);
        var textX = previewMin.X + 4f;
        drawList.AddText(font, sampleSize, new Vector2(textX, previewMax.Y - (sampleSize * 2.1f)), ImGui.GetColorU32(preset.PreferredNameColor with { W = 1f }), "Name");
        drawList.AddText(font, sampleSize * 0.85f, new Vector2(textX, previewMax.Y - sampleSize), ImGui.GetColorU32(preset.AccentTextColor with { W = 1f }), "Title");
        drawList.PopClipRect();

        var borderColor = selected ? EditorWidgets.AccentColor : hovered ? EditorWidgets.AccentColor : CardBorderColor;
        drawList.AddRect(previewMin, previewMax, ImGui.GetColorU32(borderColor), 3f, ImDrawFlags.None, selected || hovered ? 2f : 1f);

        if (selected)
        {
            DrawSelectedMark(drawList, new Vector2(previewMax.X - 4f, previewMin.Y + 4f));
        }

        var textPos = new Vector2(previewMin.X, previewMax.Y + ThemeCardPadding);
        drawList.PushClipRect(textPos, new Vector2(previewMax.X, max.Y), true);
        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), preset.Name);
        drawList.PopClipRect();

        return clicked;
    }

    private void DrawSolidSwatches()
    {
        EditorWidgets.PropertyLabel("Swatches", 0f);

        const int perRow = 8;
        var spacing = 3f;
        var size = MathF.Floor((ImGui.GetContentRegionAvail().X - (spacing * (perRow - 1))) / perRow);
        var rowStartX = ImGui.GetCursorPosX();

        for (var i = 0; i < ProfileThemePresets.SolidSwatches.Length; i++)
        {
            if (i > 0)
            {
                if (i % perRow == 0)
                {
                    ImGui.SetCursorPosX(rowStartX);
                }
                else
                {
                    ImGui.SameLine(0f, spacing);
                }
            }

            var swatch = ProfileThemePresets.SolidSwatches[i];
            if (EditorWidgets.Swatch($"##Swatch{i}", swatch, size))
            {
                editorSession.ApplyBackgroundEdit(style => style.PrimaryColor = swatch);
            }
        }
    }

    private void DrawGradientControls(ProfileBackground background)
    {
        DrawBackgroundColor("From", "##BgPrimary", background.PrimaryColor, primary: true);
        DrawBackgroundColor("To", "##BgSecondary", background.SecondaryColor, primary: false);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        if (ImGui.Button("Swap Colors", new Vector2(-1, 0f)))
        {
            editorSession.ApplyBackgroundEdit(style => (style.PrimaryColor, style.SecondaryColor) = (style.SecondaryColor, style.PrimaryColor));
        }

        var angle = background.GradientAngle;
        var buttonSize = ImGui.GetFrameHeight();
        EditorWidgets.PropertyLabel("Angle", ImGui.GetContentRegionAvail().X - EditorWidgets.LabelColumnWidth - ((buttonSize + 2f) * 2f) - 2f);
        if (ImGui.SliderFloat("##GradientAngle", ref angle, 0f, 360f, "%.0f deg"))
        {
            var value = angle;
            editorSession.BeginOrContinueBackgroundEdit(style => style.GradientAngle = value);
        }

        CommitBackgroundOnRelease();

        ImGui.SameLine(0f, 4f);
        if (EditorWidgets.IconButton("AngleMinus", FontAwesomeIcon.UndoAlt, "Rotate -45", buttonSize))
        {
            editorSession.ApplyBackgroundEdit(style => style.GradientAngle = WrapAngle(style.GradientAngle - 45f));
        }

        ImGui.SameLine(0f, 2f);
        if (EditorWidgets.IconButton("AnglePlus", FontAwesomeIcon.RedoAlt, "Rotate +45", buttonSize))
        {
            editorSession.ApplyBackgroundEdit(style => style.GradientAngle = WrapAngle(style.GradientAngle + 45f));
        }
    }

    /// <summary>
    /// Pattern as its own first-class browser (not hidden behind discovering Textured Fill first):
    /// truthful preview cards, each rendering the exact pattern definition
    /// (<see cref="PatternPreview"/>) at a standardized, always-legible size and contrast — never the
    /// profile's own colors, intensity, or scale, which could make a candidate invisible (nearly
    /// matching Base/Pattern colors, a theme's low intensity, a canvas-relative scale too small to
    /// clear the shared renderer's tiling threshold). Picking a card sets that Pattern — exactly the
    /// one thing choosing a pattern ever does — as one undo step: it is an overlay independent of
    /// the background's Mode, so a gradient (or solid, or image) base is always preserved exactly as
    /// it was, never replaced or flattened. The only time Mode is touched is when there was no
    /// background at all (None) — switching to Solid Color so the pick is actually visible, the same
    /// visibility rule an applied Theme already follows. Picking None removes the overlay and
    /// reveals the base underneath, unchanged. Detailed tuning stays under Customize Background
    /// (<see cref="DrawPatternTuningControls"/>), reachable once a pattern is chosen.
    /// </summary>
    internal void DrawPatternPresets(ProfileDocument profile)
    {
        if (profile.Background is not { } background)
        {
            EditorWidgets.Hint("Background unavailable.");
            return;
        }

        EditorWidgets.PropertyLabel("Pattern", 0f);
        ImGui.TextDisabled($"Current: {TextureLabels[(int)background.Texture]}");

        DrawPatternCardGrid(background, texture =>
        {
            editorSession.ApplyBackgroundEdit(style =>
            {
                style.Texture = texture;

                // Only an actual pattern needs a visible Mode to show up in; picking None on a
                // document with no background at all is a true no-op, not a reason to conjure one.
                if (texture != ProfileBackgroundTexture.None && style.Mode == ProfileBackgroundMode.None)
                {
                    style.Mode = ProfileBackgroundMode.SolidColor;
                }
            });
        });

        EditorWidgets.Hint("Colors, intensity, scale, and rotation are under Customize Background.");
    }

    private void DrawPatternCardGrid(ProfileBackground background, Action<ProfileBackgroundTexture> onPick)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((available + spacing) / (PatternCardSize + spacing)));
        var rowStartX = ImGui.GetCursorPosX();

        for (var i = 0; i < TextureLabels.Length; i++)
        {
            if (i > 0)
            {
                if (i % columns == 0)
                {
                    ImGui.SetCursorPosX(rowStartX);
                }
                else
                {
                    ImGui.SameLine();
                }
            }

            var texture = (ProfileBackgroundTexture)i;
            if (DrawPatternCard(background, texture))
            {
                onPick(texture);
            }
        }
    }

    // Fixed regardless of card size or the profile's canvas dimensions: with the shared renderer's
    // tile size formula (TextureScale * PeriodsPerTile * scale), a scale of 1 keeps every valid
    // TextureScale (4-128) comfortably above DrawTexture's own "too small to actually tile" fallback
    // threshold (which is what made every card render as a flat, near-invisible average tint before
    // this was fixed) — worst case, the minimum Scale of 4 still yields an on-screen tile of 32px,
    // well clear of that threshold. This only changes how a small card renders the real Scale value;
    // it never changes what Scale means or what's saved.
    private const float PatternCardRenderScale = 1f;

    /// <summary>
    /// One pattern's card. Render order: card fill/pattern first, the legibility scrim and label,
    /// then the selection/hover border last — nothing is drawn after the border that could cover
    /// anything beneath it. Every card — including None — is built from the profile's own current
    /// Base color, Pattern color, Intensity, Scale, and Rotation via <see cref="PatternPreview"/>, so
    /// the only thing that differs between cards is the candidate pattern: a deliberately truthful
    /// side-by-side comparison, not a standardized swatch. If the current colors are close, a card is
    /// meant to look subtle — legibility of the selector itself comes from the border, selection
    /// outline, and name label below, never from altering the rendered pattern colors.
    /// </summary>
    private bool DrawPatternCard(ProfileBackground background, ProfileBackgroundTexture texture)
    {
        var size = new Vector2(PatternCardSize);
        ImGui.InvisibleButton($"##Pattern{texture}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var selected = background.Texture == texture;
        var label = TextureLabels[(int)texture];
        EditorWidgets.Tooltip(label);

        var drawList = ImGui.GetWindowDrawList();

        if (texture == ProfileBackgroundTexture.None)
        {
            // The direct comparison point: the current Base color with no pattern overlay at all.
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(background.PrimaryColor with { W = 1f }), 4f);
        }
        else
        {
            var preview = PatternPreview.Create(background, texture);

            drawList.PushClipRect(min, max, true);
            ProfileBackgroundRenderer.Draw(drawList, preview, min, size, PatternCardRenderScale, renderResources);
            drawList.PopClipRect();
        }

        // A legibility scrim + label, independent of whatever the current colors render as — this is
        // how the card stays identifiable when a subtle color pairing makes the pattern itself hard
        // to make out at a glance, without touching the rendered colors themselves.
        var labelHeight = MathF.Min(16f, size.Y * 0.32f);
        var scrimMin = new Vector2(min.X, max.Y - labelHeight);
        drawList.AddRectFilled(scrimMin, max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)));
        drawList.PushClipRect(scrimMin, max, true);
        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = new Vector2(min.X + MathF.Max(2f, (size.X - labelSize.X) / 2f), scrimMin.Y + ((labelHeight - labelSize.Y) / 2f));
        drawList.AddText(labelPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.92f)), label);
        drawList.PopClipRect();

        var borderColor = selected ? EditorWidgets.AccentColor : hovered ? new Vector4(1f, 1f, 1f, 0.4f) : CardBorderColor;
        drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 4f, ImDrawFlags.None, selected ? 2f : 1f);

        return clicked;
    }

    /// <summary>
    /// Detailed tuning for the Pattern chosen in <see cref="DrawPatternPresets"/>: its tint color,
    /// intensity, scale, and (when meaningful) rotation — shown whenever a Pattern is selected,
    /// independent of the background's own Mode (see <see cref="ProfileBackgroundRenderer"/>, which
    /// composes it the same way). The base fill's own color lives under that Mode's own controls
    /// (e.g. Solid Color's "Color", Linear Gradient's "From"/"To") — never duplicated here. The
    /// pattern picker itself lives in the Design category's own first-class Pattern section, not here.
    /// </summary>
    private void DrawPatternTuningControls(ProfileBackground background)
    {
        DrawBackgroundColor("Pattern", "##BgSecondary", background.SecondaryColor, primary: false);

        var intensity = background.TextureIntensity * 100f;
        EditorWidgets.PropertyLabel("Intensity");
        if (ImGui.SliderFloat("##TextureIntensity", ref intensity, 0f, 100f, "%.0f%%"))
        {
            var value = intensity / 100f;
            editorSession.BeginOrContinueBackgroundEdit(style => style.TextureIntensity = value);
        }

        CommitBackgroundOnRelease();

        var scale = background.TextureScale;
        EditorWidgets.PropertyLabel("Scale");
        if (ImGui.SliderFloat("##TextureScale", ref scale, ProfileBackground.MinTextureScale, ProfileBackground.MaxTextureScale, "%.0f px", ImGuiSliderFlags.AlwaysClamp))
        {
            var value = scale;
            editorSession.BeginOrContinueBackgroundEdit(style => style.TextureScale = value);
        }

        CommitBackgroundOnRelease();

        if (ProfileBackground.SupportsRotation(background.Texture))
        {
            var rotation = background.TextureRotation;
            EditorWidgets.PropertyLabel("Rotation");
            if (ImGui.SliderFloat("##TextureRotation", ref rotation, 0f, 360f, "%.0f deg"))
            {
                var value = rotation;
                editorSession.BeginOrContinueBackgroundEdit(style => style.TextureRotation = value);
            }

            CommitBackgroundOnRelease();
        }
    }

    private void DrawBackgroundImageControls(ProfileBackground background)
    {
        var halfButton = new Vector2((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f, 0f);

        if (ImGui.Button(background.ImageAssetId is null ? "Choose Image..." : "Replace Image...", halfButton))
        {
            openImageFileDialog("Background Image", path => editorSession.SetBackground(path));
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(background.ImageAssetId is null))
        {
            if (ImGui.Button("Remove Image", halfButton))
            {
                editorSession.RemoveBackground();
            }
        }

        if (background.ImageAssetId is not { } assetId)
        {
            EditorWidgets.Hint("No image chosen yet.");
            return;
        }

        if (renderResources.Images.GetNativeSize(assetId) is { } native)
        {
            EditorWidgets.PropertyLabel("Native", 0f);
            ImGui.TextUnformatted($"{native.Width} x {native.Height} px");
        }

        EditorWidgets.PropertyLabel("Fit", 0f);
        var fitClicked = EditorWidgets.Segmented("BgFit", ImageFitLabels, Array.IndexOf(ImageFitOrder, background.ImageFit));
        if (fitClicked >= 0)
        {
            var newFit = ImageFitOrder[fitClicked];
            editorSession.ApplyBackgroundEdit(style => style.ImageFit = newFit);
        }

        EditorWidgets.PropertyLabel("Flip", 0f);
        if (EditorWidgets.TextToggle("Flip X##Bg", background.ImageFlipX, tooltip: "Mirror horizontally"))
        {
            editorSession.ApplyBackgroundEdit(style => style.ImageFlipX = !style.ImageFlipX);
        }

        ImGui.SameLine();
        if (EditorWidgets.TextToggle("Flip Y##Bg", background.ImageFlipY, tooltip: "Mirror vertically"))
        {
            editorSession.ApplyBackgroundEdit(style => style.ImageFlipY = !style.ImageFlipY);
        }
    }

    /// <summary>A background color picker (RGB; the background's own Opacity controls transparency).</summary>
    private void DrawBackgroundColor(string label, string id, Vector4 current, bool primary)
    {
        var color = current;
        EditorWidgets.PropertyLabel(label);
        if (ImGui.ColorEdit4(id, ref color, ImGuiColorEditFlags.NoAlpha))
        {
            var value = color with { W = 1f };
            editorSession.BeginOrContinueBackgroundEdit(style =>
            {
                if (primary)
                {
                    style.PrimaryColor = value;
                }
                else
                {
                    style.SecondaryColor = value;
                }
            });
        }

        CommitBackgroundOnRelease();
    }

    private void CommitBackgroundOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editorSession.CommitPendingBackgroundEdit();
        }
    }

    private static float WrapAngle(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }

    /// <summary>A small accent disc with a check mark, its top-right corner at <paramref name="topRight"/>: "this is your theme".</summary>
    private static void DrawSelectedMark(ImDrawListPtr drawList, Vector2 topRight)
    {
        var glyph = EditorWidgets.GetIconString(FontAwesomeIcon.Check);
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var size = ImGui.CalcTextSize(glyph);
            var radius = (MathF.Max(size.X, size.Y) / 2f) + 3f;
            var center = topRight + new Vector2(-radius, radius);
            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(EditorWidgets.AccentColor));
            drawList.AddText(center - (size / 2f), ImGui.GetColorU32(Vector4.One), glyph);
        }
    }
}
