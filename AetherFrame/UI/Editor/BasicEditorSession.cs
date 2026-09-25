using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Basic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Translates the Basic (Adventure Plate Classic) editor's semantic actions — set the portrait,
/// write the message, show or hide a section, apply or reset the layout — into edits of the same
/// <see cref="ProfileDocument"/> the Advanced editor uses, through the same <see cref="EditorSession"/>
/// (one undo history, one dirty-state calculation, no second persistence system). Sections are
/// found by <see cref="ProfileElementRole"/>, never by list position or a hardcoded id. The rules
/// themselves live in <see cref="BasicPlateEditor"/>; the Identity Header (name, title, tagline) in
/// <see cref="BasicIdentitySession"/>, exposed here as <see cref="Identity"/>.
///
/// Opening the Basic editor changes nothing: a section element is only created by the first
/// explicit edit that needs it, and every action is exactly one undo step. Content and style edits
/// never move anything; placement only changes through Apply Layout, Reset Section, Reset Basic
/// Layout, or an orientation change (which moves only elements still following the layout).
/// </summary>
internal sealed class BasicEditorSession
{
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly AssetStorageService assetStorage;

    // Set only by operations this class performs itself (currently just portrait asset import,
    // which happens before any EditorSession call exists to own the failure). Anything routed
    // through EditorSession surfaces its own ErrorMessage instead; see the ErrorMessage getter.
    private string? localErrorMessage;

    internal BasicEditorSession(
        ProfileService profileService,
        EditorSession editorSession,
        AssetStorageService assetStorage,
        BasicIdentitySession identity,
        ICharacterInfoSource characterInfo)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.assetStorage = assetStorage;
        Identity = identity;
        CharacterInfo = characterInfo;
    }

    /// <summary>The Identity Header (character name, title, tagline).</summary>
    internal BasicIdentitySession Identity { get; }

    /// <summary>The logged-in character, for "use current" actions (never applied on its own).</summary>
    internal ICharacterInfoSource CharacterInfo { get; }

    internal string? ErrorMessage => localErrorMessage ?? editorSession.ErrorMessage;

    internal static ProfileElement? FindByRole(ProfileDocument profile, ProfileElementRole role) => BasicSections.Find(profile, role);

    /// <summary>A <see cref="BasicPlateEditor"/> over the open document, adding and removing elements
    /// through <see cref="ProfileService"/> (Z order, capacity).</summary>
    internal static BasicPlateEditor CreatePlateEditor(ProfileService profileService, ProfileDocument profile) =>
        new(profile, element => profileService.AddElement(element), element => profileService.RemoveElement(element.Id));

    // ---------------------------------------------------------------- content

    /// <summary>
    /// Applies a live, in-progress edit to a section value's text (e.g. every keystroke in the
    /// Message field), creating that section on the first keystroke if it doesn't exist yet. Call
    /// <see cref="CommitTextEdit"/> once the edit completes: the whole typing run (including any
    /// creation) is one undo step.
    /// </summary>
    internal void SetText(ProfileElementRole role, string text) => EditContinuous(editor => editor.SetText(role, text));

    /// <summary>Finalizes a pending text edit started by <see cref="SetText"/>.</summary>
    internal void CommitTextEdit() => editorSession.CommitPendingDocumentEdit();

    /// <summary>Shows or hides a whole section (never deleting its content).</summary>
    internal void SetSectionVisible(BasicSection section, bool visible) => Edit(editor => editor.SetSectionVisible(section, visible));

    internal void SetFavoriteJob(uint jobId, string jobName) => Edit(editor => editor.SetFavoriteJob(jobId, jobName));

    /// <summary>Live level typing / stepping; commit with <see cref="CommitTextEdit"/>.</summary>
    internal void SetLevel(int level) => EditContinuous(editor => editor.SetLevel(level));

    /// <summary>The level the Favorite Job has on the logged-in character, explicitly applied.</summary>
    internal void UseLevel(int level) => Edit(editor => editor.SetLevel(level));

    internal void AddPlaystyle(string entry) => Edit(editor => editor.AddPlaystyle(entry));

    internal void RemovePlaystyleAt(int index) => Edit(editor => editor.RemovePlaystyleAt(index));

    internal void MovePlaystyle(int index, int offset) => Edit(editor => editor.MovePlaystyle(index, offset));

    /// <summary>Discrete Active Hours change (day toggles, clock format).</summary>
    internal void SetActiveHours(BasicActiveHours? hours) => Edit(editor => editor.SetActiveHours(hours));

    /// <summary>Continuous Active Hours change (time sliders, time zone typing); commit with <see cref="CommitTextEdit"/>.</summary>
    internal void SetActiveHoursContinuous(BasicActiveHours hours) => EditContinuous(editor => editor.SetActiveHours(hours));

    /// <summary>
    /// Edits one section text element's style (font, size, color...) through the shared text
    /// properties; never its placement. <paramref name="continuous"/> coalesces a slider or color
    /// drag into one undo step (commit with <see cref="CommitTextEdit"/>). A no-op if it doesn't exist.
    /// </summary>
    internal void EditSectionStyle(ProfileElementRole role, Action<TextProfileElement> apply, bool continuous)
    {
        void Change(BasicPlateEditor editor)
        {
            if (BasicSections.FindText(editor.Profile, role) is { } element)
            {
                apply(element);
            }
        }

        if (continuous)
        {
            EditContinuous(Change);
        }
        else
        {
            Edit(Change);
        }
    }

    /// <summary>
    /// Section heading size: every standard section heading resized together (see
    /// <see cref="BasicPlateEditor.SetHeadingSize"/>). <paramref name="continuous"/> coalesces a
    /// slider drag into one undo step (commit with <see cref="CommitTextEdit"/>).
    /// </summary>
    internal void SetHeadingSize(float size, bool continuous)
    {
        if (continuous)
        {
            EditContinuous(editor => editor.SetHeadingSize(size));
        }
        else
        {
            Edit(editor => editor.SetHeadingSize(size));
        }
    }

    // ---------------------------------------------------------------- current character (explicit only)

    /// <summary>Fills Home World and Data Center from the logged-in character.</summary>
    internal void UseCurrentWorld()
    {
        if (CharacterInfo.CurrentInfo is { } info && BasicPlateText.World(info.HomeWorld, info.DataCenter) is { Length: > 0 } world)
        {
            Edit(editor => editor.SetText(ProfileElementRole.BasicWorld, world));
        }
    }

    /// <summary>Fills the Favorite Job (and its level) from the logged-in character's current job.</summary>
    internal void UseCurrentJob()
    {
        if (CharacterInfo.CurrentInfo is { JobName.Length: > 0 } info)
        {
            Edit(editor =>
            {
                editor.SetFavoriteJob(info.JobId, info.JobName);
                if (info.Level > 0)
                {
                    editor.SetLevel(info.Level);
                }
            });
        }
    }

    /// <summary>Fills the Free Company from the logged-in character's tag.</summary>
    internal void UseCurrentFreeCompany()
    {
        if (CharacterInfo.CurrentInfo is { FreeCompanyTag.Length: > 0 } info)
        {
            Edit(editor => editor.SetText(ProfileElementRole.BasicFreeCompany, BasicPlateText.FreeCompanyTag(info.FreeCompanyTag)));
        }
    }

    // ---------------------------------------------------------------- portrait

    /// <summary>The portrait element, if one has been added, or null otherwise.</summary>
    internal ImageProfileElement? Portrait =>
        profileService.CurrentProfile is { } profile ? FindByRole(profile, ProfileElementRole.BasicPortrait) as ImageProfileElement : null;

    /// <summary>
    /// Imports an image and sets it as the portrait: replaces the existing portrait's asset in
    /// place (keeping its placement, fit, opacity... — resetting those is Reset Section) if one
    /// already exists, or creates a new portrait at the layout's placement otherwise.
    /// </summary>
    internal void SetPortrait(string sourceFilePath)
    {
        localErrorMessage = null;

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        if (FindByRole(profile, ProfileElementRole.BasicPortrait) is { } existing)
        {
            editorSession.ReplaceImage(existing.Id, sourceFilePath);
            return;
        }

        Guid assetId;
        try
        {
            assetId = assetStorage.ImportImage(sourceFilePath);
        }
        catch (Exception ex)
        {
            localErrorMessage = ex.Message;
            return;
        }

        Edit(editor => editor.CreatePortrait(assetId));
    }

    /// <summary>Removes the portrait element, if one exists (its image stays on disk for undo). A no-op otherwise.</summary>
    internal void RemovePortrait()
    {
        if (Portrait is not null)
        {
            Edit(editor => editor.RemovePortrait());
        }
    }

    // ---------------------------------------------------------------- theme

    /// <summary>Applies a theme: background colors and every Basic text color, one undo step.</summary>
    internal void ApplyTheme(ProfileThemePreset preset) => Edit(editor => editor.ApplyTheme(preset));

    // ---------------------------------------------------------------- layout

    internal static AdventurePlateOrientation GetOrientation(ProfileDocument profile) => BasicPlateEditor.GetOrientation(profile);

    internal static bool IsSectionCustomized(ProfileDocument profile, BasicSection section) =>
        BasicPlateEditor.IsSectionCustomized(profile, section);

    internal static List<BasicSection> CustomizedSections(ProfileDocument profile) => BasicPlateEditor.CustomizedSections(profile);

    /// <summary>Layout groups currently overlapping each other (see <see cref="BasicPlateEditor.FindOverlaps"/>).</summary>
    internal static List<(BasicSection First, BasicSection Second)> FindOverlaps(ProfileDocument profile) => BasicPlateEditor.FindOverlaps(profile);

    /// <summary>
    /// Switches orientation. Only sections still following the Basic layout move; customized ones
    /// stay exactly where they are (Apply Layout brings them along explicitly).
    /// </summary>
    internal void SetOrientation(AdventurePlateOrientation orientation)
    {
        if (profileService.CurrentProfile is { } profile && GetOrientation(profile) == orientation)
        {
            return;
        }

        localErrorMessage = null;
        Identity.EditPlateLayout(editor => editor.SetOrientation(orientation), onlyIfManaged: true);
    }

    /// <summary>Re-places one section at the layout's placement (its content and style are kept).</summary>
    internal void ApplySectionLayout(BasicSection section) => ApplySectionLayout([section]);

    /// <summary>Re-places sections shown together (e.g. Favorite Job and Level) as one undo step.</summary>
    internal void ApplySectionLayout(IReadOnlyList<BasicSection> sections)
    {
        localErrorMessage = null;
        if (sections.Contains(BasicSection.Identity))
        {
            Identity.ApplyLayout();
            return;
        }

        Edit(editor =>
        {
            foreach (var section in sections)
            {
                editor.ApplyLayout(section);
            }
        });
    }

    /// <summary>Re-places every existing Basic section for the current orientation, customized or not.</summary>
    internal void ApplyLayout()
    {
        localErrorMessage = null;
        Identity.EditPlateLayout(editor => editor.ApplyLayoutToAll(), onlyIfManaged: false);
    }

    /// <summary>Reset Section: one section back to the Classic defaults (placement and style; content kept).</summary>
    internal void ResetSection(BasicSection section) => ResetSection([section]);

    /// <summary>Resets sections shown together (e.g. Favorite Job and Level) as one undo step.</summary>
    internal void ResetSection(IReadOnlyList<BasicSection> sections)
    {
        localErrorMessage = null;
        if (sections.Contains(BasicSection.Identity))
        {
            Identity.ResetSection();
            return;
        }

        Edit(editor =>
        {
            foreach (var section in sections)
            {
                editor.ResetSection(section);
            }
        });
    }

    /// <summary>True if any Basic section element exists to reset — enables Reset Basic Layout.</summary>
    internal static bool CanResetLayout(ProfileDocument profile)
    {
        foreach (var definition in BasicSections.All)
        {
            if (BasicSections.Exists(profile, definition.Section))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reset Basic Layout (explicit and confirmed by the UI): the Normal orientation, and every
    /// existing Basic section — including the Identity Header's region — at its default placement,
    /// as ONE undo step. Content, styles, and visibility are kept; nothing is created or deleted;
    /// elements that aren't Basic sections and imported images are never touched.
    /// </summary>
    internal void ResetBasicLayout()
    {
        localErrorMessage = null;
        Identity.EditPlateLayout(editor => editor.ResetLayout(), onlyIfManaged: false);
    }

    // ---------------------------------------------------------------- internals

    private void Edit(Action<BasicPlateEditor> change)
    {
        localErrorMessage = null;
        editorSession.ApplyDocumentEdit(() => change(Editor()));
    }

    private void EditContinuous(Action<BasicPlateEditor> change)
    {
        localErrorMessage = null;
        editorSession.BeginOrContinueDocumentEdit(() => change(Editor()));
    }

    private BasicPlateEditor Editor()
    {
        profileService.RequireEditableProfile();
        var profile = profileService.CurrentProfile ?? throw new InvalidOperationException("No Plate is open.");
        return CreatePlateEditor(profileService, profile);
    }
}
