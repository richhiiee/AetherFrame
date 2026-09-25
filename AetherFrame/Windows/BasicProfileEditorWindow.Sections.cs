using System;
using System.Numerics;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The Portrait, Details, and Message categories. Each is structured input over its
/// role-tagged elements through <see cref="BasicEditorSession"/> — no coordinates, no freeform
/// placement — with content first, then Appearance and Advanced Styling (collapsed), then the
/// Layout block (follows the layout / customized, Apply Layout, Reset).
/// </summary>
internal sealed partial class BasicProfileEditorWindow
{
    private const int MaxWorldLength = 64;
    private const int MaxFreeCompanyLength = 64;

    private static readonly string[] PortraitFitLabels = ["Fill", "Fit", "Stretch"];
    private static readonly ProfileImageFit[] PortraitFitOrder = [ProfileImageFit.Fill, ProfileImageFit.Fit, ProfileImageFit.Stretch];
    private static readonly string[] DayLabels = ["M", "T", "W", "T", "F", "S", "S"];
    private static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    // Time sliders move in half-hour steps.
    private const int MinutesPerStep = 30;
    private const int StepsPerDay = BasicActiveHours.MinutesPerDay / MinutesPerStep;

    private string customPlaystyle = string.Empty;
    private string jobSearch = string.Empty;

    /// <summary>A single-line text input bound to a section value; typing is one undo step per run.</summary>
    private void DrawValueInput(ProfileDocument profile, ProfileElementRole role, string hint, int maxLength)
    {
        var buffer = BasicSections.FindText(profile, role)?.Text ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint($"##Value{role}", hint, ref buffer, maxLength))
        {
            basicEditorSession.SetText(role, buffer.Replace('\n', ' '));
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }
    }

    // ---------------------------------------------------------------- portrait

    private void DrawPortraitCategory(ProfileDocument profile)
    {
        var portrait = basicEditorSession.Portrait;

        FieldHeader(profile, "Portrait", BasicSection.Portrait, showEnabled: portrait is not null);

        if (portrait is null)
        {
            Hint("Choose a portrait to show beside your details.");
            if (ImGui.Button("Choose a Portrait...", new Vector2(-1, 0f)))
            {
                OpenImageFileDialog("Choose a Portrait", basicEditorSession.SetPortrait);
            }
        }
        else
        {
            var thumbnailSize = 72f * ImGuiHelpers.GlobalScale;
            if (imageTextureCache.GetWrapOrNull(portrait.AssetId) is { } wrap)
            {
                ImGui.Image(wrap.Handle, new Vector2(thumbnailSize, thumbnailSize));
            }
            else
            {
                ImGui.Dummy(new Vector2(thumbnailSize, thumbnailSize));
                ToolTip("The image couldn't be loaded.");
            }

            ImGui.SameLine();
            using (ImRaii.Group())
            {
                var buttonSize = new Vector2(Math.Max(100f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X), 0f);
                if (ImGui.Button("Replace Portrait...", buttonSize))
                {
                    OpenImageFileDialog("Replace Portrait", basicEditorSession.SetPortrait);
                }

                ToolTip("Keeps the portrait's placement and fit.");
                if (ImGui.Button("Remove Portrait", buttonSize))
                {
                    basicEditorSession.RemovePortrait();
                }

                ToolTip("Removes the portrait from this Plate (undoable). The image stays in your library.");
            }

            Subheading("Image Fit");
            var fitClicked = EditorWidgets.Segmented("PortraitFit", PortraitFitLabels, Array.IndexOf(PortraitFitOrder, portrait.DisplayMode));
            if (fitClicked >= 0)
            {
                var fit = PortraitFitOrder[fitClicked];
                editorSession.ApplyImmediateEdit(portrait.Id, element => ((ImageProfileElement)element).DisplayMode = fit);
            }

            Hint("Fill covers the frame (cropping edges), Fit shows the whole image, Stretch fills it exactly.");
        }

        // Only imported images exist today; the source model is ready for more, which aren't
        // offered until they genuinely work.
        Hint("Source: an image you import. Using your in-game portrait isn't available yet.");

        ImGui.Spacing();
        Subheading("Frame & Overlay");
        DrawComponentSlot(profile, Domain.Components.PlateComponentKind.PortraitFrame);
        DrawComponentSlot(profile, Domain.Components.PlateComponentKind.PortraitOverlay);

        DrawLayoutBlock(profile, new LayoutRow("Portrait", [BasicSection.Portrait], "Reset Portrait"));
    }

    // ---------------------------------------------------------------- details

    /// <summary>
    /// Everything about the character besides name and title, as one area: the logged-in character
    /// once at the top, then Home World, Favorite Jobs, and Free Company (each with its
    /// Show toggle and its "use current" shortcut — nothing is filled in without a click), then
    /// Playstyle and Active Hours; one Appearance and Advanced Styling group for all of them; one
    /// Layout block.
    /// </summary>
    private void DrawDetailsCategory(ProfileDocument profile)
    {
        var info = basicEditorSession.CharacterInfo.CurrentInfo;
        Hint(info is null
            ? "No character loaded. You can still type any value."
            : $"Logged in as {info.Name}. Use the buttons to fill in current details; nothing changes on its own.");

        // Home World
        FieldHeader(profile, "Home World", BasicSection.World);
        DrawValueInput(profile, ProfileElementRole.BasicWorld, "Home World [Data Center]", MaxWorldLength);
        if (info is not null
            && BasicPlateText.World(info.HomeWorld, info.DataCenter) is { Length: > 0 } world
            && BasicSections.FindText(profile, ProfileElementRole.BasicWorld)?.Text != world
            && ImGui.SmallButton($"Use {world}"))
        {
            basicEditorSession.UseCurrentWorld();
        }

        DrawFavoriteJobs(profile, info);

        // Free Company
        FieldHeader(profile, "Free Company", BasicSection.FreeCompany);
        var notInOne = info is { FreeCompanyTag: "" };
        DrawValueInput(profile, ProfileElementRole.BasicFreeCompany, notInOne ? "Not in a Free Company" : "Free Company name", MaxFreeCompanyLength);
        if (notInOne)
        {
            Hint("Not in a Free Company. You can hide this section, or type any name.");
        }
        else if (info?.FreeCompanyTag is { Length: > 0 } tag)
        {
            var tagText = BasicPlateText.FreeCompanyTag(tag);
            if (BasicSections.FindText(profile, ProfileElementRole.BasicFreeCompany)?.Text != tagText && ImGui.SmallButton($"Use {tagText}"))
            {
                basicEditorSession.UseCurrentFreeCompany();
            }

            Hint("The game provides the Free Company's tag; type its full name if you prefer.");
        }

        DrawPlaystyleEntries(profile);
        DrawActiveHours(profile);

        ImGui.Spacing();
        var targets = new[]
        {
            SectionStyle(profile, "Home World", ProfileElementRole.BasicWorld),
            SectionStyle(profile, "Favorite Jobs", ProfileElementRole.BasicJob),
            SectionStyle(profile, "Free Company", ProfileElementRole.BasicFreeCompany),
            SectionStyle(profile, "Playstyle", ProfileElementRole.BasicPlaystyle),
            SectionStyle(profile, "Active Hours", ProfileElementRole.BasicActiveHours),
        };
        DrawAppearance(targets);
        DrawAdvancedStyling(targets);

        DrawLayoutBlock(
            profile,
            new LayoutRow("Home World", [BasicSection.World], "Reset Home World"),
            new LayoutRow("Favorite Jobs", [BasicSection.Job], "Reset Favorite Jobs"),
            new LayoutRow("Free Company", [BasicSection.FreeCompany], "Reset Free Company"),
            new LayoutRow("Playstyle", [BasicSection.Playstyle], "Reset Playstyle"),
            new LayoutRow("Active Hours", [BasicSection.ActiveHours], "Reset Active Hours"));
    }

    /// <summary>
    /// Favorite Jobs: the chosen jobs in order (the first is the primary favorite) with Up, Down and
    /// Remove on each, then a searchable list to add more and "Add current". No level anywhere: the
    /// level earlier versions showed is retired (kept in the Plate, never shown or edited in Basic).
    /// </summary>
    private void DrawFavoriteJobs(ProfileDocument profile, BasicCharacterInfo? info)
    {
        FieldHeader(profile, "Favorite Jobs", BasicSection.Job);

        var ids = BasicEditorSession.FavoriteJobIds(profile);
        ImGui.TextDisabled($"{ids.Count} of {BasicFavoriteJobs.MaxJobs}");
        if (ids.Count == 0)
        {
            Hint("None yet. Add your favorites; the first one leads.");
        }

        var buttonSize = ImGui.GetFrameHeight();
        for (var i = 0; i < ids.Count; i++)
        {
            using var id = ImRaii.PushId($"FavoriteJob{i}");
            var job = basicEditorSession.DescribeJob(ids[i]);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(job.Name);
            if (i == 0 && ids.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(primary)");
            }

            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ((buttonSize + 2f) * 3f)));
            using (ImRaii.Disabled(i == 0))
            {
                if (EditorWidgets.IconButton("Up", FontAwesomeIcon.ArrowUp, "Move up", buttonSize))
                {
                    basicEditorSession.MoveFavoriteJob(i, -1);
                }
            }

            ImGui.SameLine(0f, 2f);
            using (ImRaii.Disabled(i == ids.Count - 1))
            {
                if (EditorWidgets.IconButton("Down", FontAwesomeIcon.ArrowDown, "Move down", buttonSize))
                {
                    basicEditorSession.MoveFavoriteJob(i, 1);
                }
            }

            ImGui.SameLine(0f, 2f);
            if (EditorWidgets.IconButton("Remove", FontAwesomeIcon.Times, "Remove", buttonSize))
            {
                basicEditorSession.RemoveFavoriteJobAt(i);
            }
        }

        // Add: a searchable list of the jobs not chosen yet.
        using (ImRaii.Disabled(ids.Count >= BasicFavoriteJobs.MaxJobs))
        {
            ImGui.SetNextItemWidth(-1);
            using var combo = ImRaii.Combo("##AddFavoriteJob", ids.Count >= BasicFavoriteJobs.MaxJobs ? "The list is full" : "Add a job...");
            if (combo.Success)
            {
                if (ImGui.IsWindowAppearing())
                {
                    jobSearch = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##JobSearch", "Search jobs...", ref jobSearch, 32);
                var shown = 0;
                foreach (var job in jobCatalog.Jobs)
                {
                    if (ids.Contains(job.Id)
                        || (jobSearch.Trim().Length > 0
                            && !job.Name.Contains(jobSearch.Trim(), StringComparison.OrdinalIgnoreCase)
                            && !job.Abbreviation.Contains(jobSearch.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    shown++;
                    if (ImGui.Selectable($"{job.Name}##Job{job.Id}"))
                    {
                        basicEditorSession.AddFavoriteJob(job.Id);
                    }

                    if (job.Abbreviation.Length > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(job.Abbreviation);
                    }
                }

                if (jobCatalog.Jobs.Count == 0)
                {
                    ImGui.TextDisabled("No job data available.");
                }
                else if (shown == 0)
                {
                    ImGui.TextDisabled("No other jobs match.");
                }
            }
        }

        // From the logged-in character, only on request.
        if (info is { JobId: > 0, JobName.Length: > 0 } current && BasicEditorSession.CanAddFavoriteJob(profile, current.JobId)
            && ImGui.SmallButton($"Add current: {current.JobName}"))
        {
            basicEditorSession.UseCurrentJob();
        }

        Hint("Shown as full names when they fit, otherwise as job abbreviations (AST, WHM...).");
    }

    // ---------------------------------------------------------------- playstyle and active hours (in Details)

    private void DrawPlaystyleEntries(ProfileDocument profile)
    {
        FieldHeader(profile, "Playstyle", BasicSection.Playstyle);

        var entries = profile.BasicPlate?.Playstyles ?? [];
        ImGui.TextDisabled($"{entries.Count} of {BasicPlateSettings.MaxPlaystyles}");
        if (entries.Count == 0)
        {
            Hint("None yet. Pick from the list or type your own.");
        }

        var buttonSize = ImGui.GetFrameHeight();
        for (var i = 0; i < entries.Count; i++)
        {
            using var row = ImRaii.PushId(i);
            using (ImRaii.Disabled(i == 0))
            {
                if (EditorWidgets.IconButton("Up", FontAwesomeIcon.ArrowUp, "Move earlier", buttonSize))
                {
                    basicEditorSession.MovePlaystyle(i, -1);
                }
            }

            ImGui.SameLine(0f, 2f);
            using (ImRaii.Disabled(i == entries.Count - 1))
            {
                if (EditorWidgets.IconButton("Down", FontAwesomeIcon.ArrowDown, "Move later", buttonSize))
                {
                    basicEditorSession.MovePlaystyle(i, 1);
                }
            }

            ImGui.SameLine(0f, 2f);
            if (EditorWidgets.IconButton("Remove", FontAwesomeIcon.Times, "Remove", buttonSize))
            {
                basicEditorSession.RemovePlaystyleAt(i);
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entries[i]);
        }

        var full = entries.Count >= BasicPlateSettings.MaxPlaystyles;
        using (ImRaii.Disabled(full))
        {
            ImGui.SetNextItemWidth(-1);
            using (var combo = ImRaii.Combo("##AddPlaystyle", full ? "All six entries used" : "Add a playstyle..."))
            {
                if (combo.Success)
                {
                    foreach (var suggestion in BasicPlateText.SuggestedPlaystyles)
                    {
                        if (entries.Exists(e => string.Equals(e, suggestion, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        if (ImGui.Selectable(suggestion))
                        {
                            basicEditorSession.AddPlaystyle(suggestion);
                        }
                    }
                }
            }

            var addWidth = ImGui.CalcTextSize("Add").X + (ImGui.GetStyle().FramePadding.X * 2f);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - addWidth - ImGui.GetStyle().ItemSpacing.X);
            var submitted = ImGui.InputTextWithHint("##CustomPlaystyle", "Or type your own", ref customPlaystyle, BasicPlateSettings.MaxPlaystyleLength, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            using (ImRaii.Disabled(BasicPlateText.NormalizePlaystyle(customPlaystyle).Length == 0))
            {
                if ((ImGui.Button("Add") || submitted) && BasicPlateText.NormalizePlaystyle(customPlaystyle).Length > 0)
                {
                    basicEditorSession.AddPlaystyle(customPlaystyle);
                    customPlaystyle = string.Empty;
                }
            }
        }
    }

    private void DrawActiveHours(ProfileDocument profile)
    {
        FieldHeader(profile, "Active Hours", BasicSection.ActiveHours);

        if (profile.BasicPlate?.ActiveHours is not { } stored)
        {
            Hint("When you're usually around. Only shown on your Plate; never connected to anything online.");
            if (ImGui.Button("Set Active Hours", new Vector2(-1, 0f)))
            {
                basicEditorSession.SetActiveHours(new BasicActiveHours { Days = BasicWeekdays.Everyday });
            }

            return;
        }

        var hours = stored.Clone();

        // Days: one toggle per day, plus quick sets.
        EditorWidgets.PropertyLabel("Days", 0f);
        var spacing = 2f;
        var dayWidth = (ImGui.GetContentRegionAvail().X - (spacing * 6f)) / 7f;
        for (var i = 0; i < 7; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            var day = (BasicWeekdays)(1 << i);
            if (EditorWidgets.TextToggle($"{DayLabels[i]}##Day{i}", hours.Days.HasFlag(day), new Vector2(dayWidth, 0f), DayNames[i]))
            {
                hours.Days ^= day;
                basicEditorSession.SetActiveHours(hours);
            }
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + EditorWidgets.LabelColumnWidth);
        var quickWidth = (ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().ItemSpacing.X * 2f)) / 3f;
        QuickDays("Weekdays", BasicWeekdays.Weekdays);
        ImGui.SameLine();
        QuickDays("Weekends", BasicWeekdays.Weekends);
        ImGui.SameLine();
        QuickDays("Every day", BasicWeekdays.Everyday);

        DrawTimeSlider("From", "##From", hours, start: true);
        DrawTimeSlider("To", "##To", hours, start: false);

        EditorWidgets.PropertyLabel("Clock", 0f);
        var use24 = hours.Use24HourClock;
        if (ImGui.Checkbox("24-hour##Clock24", ref use24))
        {
            hours.Use24HourClock = use24;
            basicEditorSession.SetActiveHours(hours);
        }

        var zone = hours.TimeZone;
        EditorWidgets.PropertyLabel("Time zone");
        if (ImGui.InputTextWithHint("##TimeZone", "e.g. EST or Server Time", ref zone, BasicActiveHours.MaxTimeZoneLength))
        {
            hours.TimeZone = zone;
            basicEditorSession.SetActiveHoursContinuous(hours);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }

        var shown = BasicPlateText.ActiveHours(stored);
        Hint(shown.Length == 0 ? "Choose at least one day or a time." : $"Shows: {shown}");
        if (ImGui.SmallButton("Clear Active Hours"))
        {
            basicEditorSession.SetActiveHours(null);
        }

        void QuickDays(string label, BasicWeekdays days)
        {
            if (EditorWidgets.TextToggle(label, hours.Days == days, new Vector2(quickWidth, 0f)))
            {
                hours.Days = days;
                basicEditorSession.SetActiveHours(hours);
            }
        }
    }

    private void DrawTimeSlider(string label, string id, BasicActiveHours hours, bool start)
    {
        var minutes = start ? hours.StartMinutes : hours.EndMinutes;
        var step = minutes / MinutesPerStep;
        EditorWidgets.PropertyLabel(label);

        // The slider's format string is the time itself (it contains no '%').
        if (ImGui.SliderInt(id, ref step, 0, StepsPerDay - 1, BasicPlateText.Time(step * MinutesPerStep, hours.Use24HourClock), ImGuiSliderFlags.AlwaysClamp))
        {
            if (start)
            {
                hours.StartMinutes = step * MinutesPerStep;
            }
            else
            {
                hours.EndMinutes = step * MinutesPerStep;
            }

            basicEditorSession.SetActiveHoursContinuous(hours);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }
    }

    // ---------------------------------------------------------------- message

    private void DrawMessageCategory(ProfileDocument profile)
    {
        FieldHeader(profile, "Message", BasicSection.Message);

        // Shown even before the message exists: the first keystroke creates it (one undo step).
        var buffer = BasicSections.FindText(profile, ProfileElementRole.BasicMessage)?.Text ?? string.Empty;
        if (buffer.Length == 0)
        {
            Hint("Add a message: a greeting, what you're looking for, anything.");
        }

        var height = Math.Max(110f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y * 0.4f);
        if (ImGui.InputTextMultiline("##BasicMessage", ref buffer, TextProfileElement.MaxTextLength, new Vector2(-1, height)))
        {
            basicEditorSession.SetText(ProfileElementRole.BasicMessage, buffer);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            basicEditorSession.CommitTextEdit();
        }

        ImGui.Spacing();
        var targets = new[] { SectionStyle(profile, "Message", ProfileElementRole.BasicMessage) };
        DrawAppearance(targets);
        DrawAdvancedStyling(targets);

        DrawLayoutBlock(profile, new LayoutRow("Message", [BasicSection.Message], "Reset Message"));
    }
}
