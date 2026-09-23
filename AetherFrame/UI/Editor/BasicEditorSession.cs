using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Translates the Basic (Adventure Plate style) editor's semantic actions — set the name, add a
/// portrait, reset the layout — into calls on the same <see cref="EditorSession"/>/
/// <see cref="ProfileService"/> the Advanced editor uses, operating on the reserved elements it
/// identifies via <see cref="ProfileElementRole"/> rather than by list position or a hardcoded
/// id. This keeps Basic and Advanced editing the same <see cref="ProfileDocument"/> instance,
/// sharing one undo/redo history and one dirty-state calculation, with no second persistence or
/// undo system of its own.
///
/// Every mutation here only ever touches the specific field it owns (e.g. setting the name only
/// changes that element's Text) — never an unrelated property like Position, Rotation, or
/// Opacity — so edits made in the Advanced editor (moving the portrait, changing its opacity,
/// adding custom elements) survive returning to Basic mode. The one deliberate exception is
/// <see cref="ResetBasicLayout"/>, an explicit, user-confirmed action.
/// </summary>
internal sealed class BasicEditorSession
{
    // Default Adventure-Plate-style layout, in the shared 1920x1080 logical profile canvas.
    // Sensible starting points only — every element remains fully editable in the Advanced
    // editor afterwards.
    private static readonly Vector2 DefaultPortraitPosition = new(60f, 60f);
    private static readonly Vector2 DefaultPortraitSize = new(620f, 960f);
    private static readonly Vector2 DefaultNamePosition = new(740f, 80f);
    private static readonly Vector2 DefaultNameSize = new(1120f, 100f);
    private static readonly Vector2 DefaultTitlePosition = new(740f, 190f);
    private static readonly Vector2 DefaultTitleSize = new(1120f, 60f);
    private static readonly Vector2 DefaultMessagePosition = new(740f, 280f);
    private static readonly Vector2 DefaultMessageSize = new(1120f, 720f);

    private static readonly Vector4 DefaultAccentColor = new(0.85f, 0.68f, 0.25f, 1f);
    private static readonly Vector4 DefaultMessageColor = new(0.92f, 0.92f, 0.92f, 1f);

    private const float DefaultNameFontSize = 56f;
    private const float DefaultTitleFontSize = 30f;
    private const float DefaultMessageFontSize = 26f;

    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly AssetStorageService assetStorage;
    private readonly CharacterIdentityService characterIdentity;

    // Set only by operations this class performs itself (currently just portrait asset import,
    // which happens before any EditorSession call exists to own the failure). Anything routed
    // through EditorSession surfaces its own ErrorMessage instead; see the ErrorMessage getter.
    private string? localErrorMessage;

    internal BasicEditorSession(
        ProfileService profileService,
        EditorSession editorSession,
        AssetStorageService assetStorage,
        CharacterIdentityService characterIdentity)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.assetStorage = assetStorage;
        this.characterIdentity = characterIdentity;
    }

    internal string? ErrorMessage => localErrorMessage ?? editorSession.ErrorMessage;

    internal static ProfileElement? FindByRole(ProfileDocument profile, ProfileElementRole role) =>
        profile.Elements.Find(e => e.Role == role);

    /// <summary>
    /// Creates whichever of the reserved Basic text elements (Name/Title/Message) don't yet
    /// exist in the current profile, with sensible defaults. Safe to call every frame: a no-op
    /// once all three exist, and never touches one that already exists — including one left
    /// behind after its role was removed some other way — so it can't clobber prior edits.
    /// </summary>
    internal void EnsureBasicContentInitialized()
    {
        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        if (FindByRole(profile, ProfileElementRole.BasicName) is null)
        {
            editorSession.AddElement(new TextProfileElement
            {
                Role = ProfileElementRole.BasicName,
                Position = DefaultNamePosition,
                Size = DefaultNameSize,
                Text = characterIdentity.CurrentCharacterName ?? string.Empty,
                FontSize = DefaultNameFontSize,
                Color = DefaultAccentColor,
                Alignment = TextAlignment.Left,
                Wrap = false,
            });
        }

        if (FindByRole(profile, ProfileElementRole.BasicTitle) is null)
        {
            editorSession.AddElement(new TextProfileElement
            {
                Role = ProfileElementRole.BasicTitle,
                Position = DefaultTitlePosition,
                Size = DefaultTitleSize,
                Text = string.Empty,
                FontSize = DefaultTitleFontSize,
                Color = DefaultAccentColor,
                Alignment = TextAlignment.Left,
                Wrap = false,
            });
        }

        if (FindByRole(profile, ProfileElementRole.BasicMessage) is null)
        {
            editorSession.AddElement(new TextProfileElement
            {
                Role = ProfileElementRole.BasicMessage,
                Position = DefaultMessagePosition,
                Size = DefaultMessageSize,
                Text = string.Empty,
                FontSize = DefaultMessageFontSize,
                Color = DefaultMessageColor,
                Alignment = TextAlignment.Left,
                Wrap = true,
            });
        }
    }

    /// <summary>
    /// Applies a live, in-progress edit to the text of the reserved element with the given role
    /// (e.g. every keystroke in the Name field), without recording history yet. Call
    /// <see cref="CommitTextEdit"/> once the edit completes to record a single history entry.
    /// A no-op if that role's element doesn't exist.
    /// </summary>
    internal void SetText(ProfileElementRole role, string text)
    {
        localErrorMessage = null;

        var profile = profileService.CurrentProfile;
        if (profile is null || FindByRole(profile, role) is not { } element)
        {
            return;
        }

        var content = text ?? string.Empty;
        if (content.Length > TextProfileElement.MaxTextLength)
        {
            content = content[..TextProfileElement.MaxTextLength];
        }

        editorSession.BeginOrContinueEdit(element.Id, e =>
        {
            if (e is TextProfileElement textElement)
            {
                textElement.Text = content;
            }
        });
    }

    /// <summary>Finalizes a pending text edit started by <see cref="SetText"/>, or the accent
    /// color edit started by <see cref="SetAccentColor"/> — both share the same single pending
    /// slot in <see cref="EditorSession"/>, which only one Basic field can be actively editing
    /// at a time.</summary>
    internal void CommitTextEdit() => editorSession.CommitPendingEdit();

    /// <summary>
    /// Applies a live, in-progress accent color edit to the Name element's text color — the
    /// single "theme color" Basic mode exposes. Call <see cref="CommitTextEdit"/> once the edit
    /// completes. A no-op if the Name element doesn't exist.
    /// </summary>
    internal void SetAccentColor(Vector4 color)
    {
        localErrorMessage = null;

        var profile = profileService.CurrentProfile;
        if (profile is null || FindByRole(profile, ProfileElementRole.BasicName) is not { } element)
        {
            return;
        }

        editorSession.BeginOrContinueEdit(element.Id, e =>
        {
            if (e is TextProfileElement textElement)
            {
                textElement.Color = color;
            }
        });
    }

    /// <summary>The current accent color, read from the Name element, or null if it doesn't exist yet.</summary>
    internal Vector4? CurrentAccentColor
    {
        get
        {
            var profile = profileService.CurrentProfile;
            return profile is not null && FindByRole(profile, ProfileElementRole.BasicName) is TextProfileElement text
                ? text.Color
                : null;
        }
    }

    /// <summary>The portrait element, if one has been added, or null otherwise.</summary>
    internal ImageProfileElement? Portrait
    {
        get
        {
            var profile = profileService.CurrentProfile;
            return profile is not null ? FindByRole(profile, ProfileElementRole.BasicPortrait) as ImageProfileElement : null;
        }
    }

    /// <summary>
    /// Imports an image and sets it as the portrait: replaces the existing portrait's asset in
    /// place (preserving its Advanced-editor position, size, opacity, etc.) if one already
    /// exists, or creates a new default-positioned portrait element otherwise.
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

        editorSession.AddElement(new ImageProfileElement
        {
            Role = ProfileElementRole.BasicPortrait,
            AssetId = assetId,
            Position = DefaultPortraitPosition,
            Size = DefaultPortraitSize,
        });
    }

    /// <summary>Removes the portrait element, if one exists. A no-op otherwise.</summary>
    internal void RemovePortrait()
    {
        localErrorMessage = null;

        var profile = profileService.CurrentProfile;
        if (profile is not null && FindByRole(profile, ProfileElementRole.BasicPortrait) is { } existing)
        {
            editorSession.RemoveElement(existing.Id);
        }
    }

    /// <summary>True if any reserved Basic element exists to reset — used to enable/disable the
    /// Reset Basic Layout button.</summary>
    internal bool CanResetLayout(ProfileDocument profile) =>
        FindByRole(profile, ProfileElementRole.BasicPortrait) is not null
        || FindByRole(profile, ProfileElementRole.BasicName) is not null
        || FindByRole(profile, ProfileElementRole.BasicTitle) is not null
        || FindByRole(profile, ProfileElementRole.BasicMessage) is not null;

    /// <summary>
    /// Restores every reserved Basic element currently present back to its default Basic
    /// Position/Size. The ONLY action that does this — nothing else in Basic mode ever touches
    /// an element's layout. Leaves content (text, portrait asset) and every other property
    /// (opacity, rotation, visibility, lock state, unrelated Advanced elements) untouched, and
    /// never creates an element that doesn't already exist.
    /// </summary>
    internal void ResetBasicLayout()
    {
        localErrorMessage = null;

        var profile = profileService.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        ResetElementRect(profile, ProfileElementRole.BasicPortrait, DefaultPortraitPosition, DefaultPortraitSize);
        ResetElementRect(profile, ProfileElementRole.BasicName, DefaultNamePosition, DefaultNameSize);
        ResetElementRect(profile, ProfileElementRole.BasicTitle, DefaultTitlePosition, DefaultTitleSize);
        ResetElementRect(profile, ProfileElementRole.BasicMessage, DefaultMessagePosition, DefaultMessageSize);
    }

    private void ResetElementRect(ProfileDocument profile, ProfileElementRole role, Vector2 position, Vector2 size)
    {
        if (FindByRole(profile, role) is not { } element || (element.Position == position && element.Size == size))
        {
            return;
        }

        editorSession.ApplyImmediateEdit(element.Id, e =>
        {
            e.Position = position;
            e.Size = size;
        });
    }
}
