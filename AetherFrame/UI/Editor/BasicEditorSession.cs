using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Translates the Basic (Adventure Plate style) editor's semantic actions — set the portrait,
/// write the message, reset the layout — into calls on the same <see cref="EditorSession"/>/
/// <see cref="ProfileService"/> the Advanced editor uses, operating on the reserved elements it
/// identifies via <see cref="ProfileElementRole"/> rather than by list position or a hardcoded
/// id. This keeps Basic and Advanced editing the same <see cref="ProfileDocument"/> instance,
/// sharing one undo/redo history and one dirty-state calculation, with no second persistence or
/// undo system of its own. The Identity Header (name, title, tagline) lives in
/// <see cref="BasicIdentitySession"/>, exposed here as <see cref="Identity"/>.
///
/// Opening the Basic editor changes nothing: a reserved element is only created by the first
/// explicit edit that needs it. Every mutation only touches the specific field it owns (e.g.
/// the message text) — never Position, Rotation, or Opacity — so edits made in the Advanced
/// editor survive returning to Basic mode. The one deliberate exception is
/// <see cref="ResetBasicLayout"/>, an explicit, user-confirmed action.
/// </summary>
internal sealed class BasicEditorSession
{
    // Default Adventure-Plate-style layout, defined on the legacy 1920x1080 canvas and scaled to
    // the profile's actual canvas. Sensible starting points only — every element remains fully
    // editable in the Advanced editor afterwards.
    private const float ReferenceCanvasWidth = 1920f;
    private const float ReferenceCanvasHeight = 1080f;
    private static readonly Vector2 DefaultPortraitPosition = new(60f, 60f);
    private static readonly Vector2 DefaultPortraitSize = new(620f, 960f);
    private static readonly Vector2 DefaultMessagePosition = new(740f, 380f);
    private static readonly Vector2 DefaultMessageSize = new(1120f, 620f);

    private static readonly Vector4 DefaultMessageColor = new(0.92f, 0.92f, 0.92f, 1f);
    private const float DefaultMessageFontSize = 26f;

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
        BasicIdentitySession identity)
    {
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.assetStorage = assetStorage;
        Identity = identity;
    }

    /// <summary>The Identity Header (character name, title, tagline).</summary>
    internal BasicIdentitySession Identity { get; }

    internal string? ErrorMessage => localErrorMessage ?? editorSession.ErrorMessage;

    internal static ProfileElement? FindByRole(ProfileDocument profile, ProfileElementRole role) =>
        profile.Elements.Find(e => e.Role == role);

    /// <summary>
    /// Applies a live, in-progress edit to the text of the reserved element with the given role
    /// (e.g. every keystroke in the Message field), creating that element with its defaults on
    /// the first keystroke if it doesn't exist yet. Call <see cref="CommitTextEdit"/> once the
    /// edit completes: the whole typing run (including any creation) is one undo step.
    /// </summary>
    internal void SetText(ProfileElementRole role, string text)
    {
        localErrorMessage = null;

        var content = text ?? string.Empty;
        if (content.Length > TextProfileElement.MaxTextLength)
        {
            content = content[..TextProfileElement.MaxTextLength];
        }

        editorSession.BeginOrContinueDocumentEdit(() =>
        {
            profileService.RequireEditableProfile();
            var profile = profileService.CurrentProfile!;
            if (FindByRole(profile, role) is not TextProfileElement element)
            {
                element = CreateTextElement(profile, role);
                profileService.AddElement(element);
            }

            element.Text = content;
        });
    }

    /// <summary>Finalizes a pending text edit started by <see cref="SetText"/>.</summary>
    internal void CommitTextEdit() => editorSession.CommitPendingDocumentEdit();

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

        var (position, size) = Scaled(profile, DefaultPortraitPosition, DefaultPortraitSize);
        editorSession.AddElement(new ImageProfileElement
        {
            Role = ProfileElementRole.BasicPortrait,
            AssetId = assetId,
            Position = position,
            Size = size,
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
        || FindByRole(profile, ProfileElementRole.BasicMessage) is not null
        || !BasicIdentitySession.HasNoHeader(profile);

    /// <summary>
    /// Restores every reserved Basic element currently present back to its default Basic
    /// placement — the portrait and message to their default rectangles, and the Identity Header
    /// to its default region with its current layout — as ONE undo step. The ONLY action that
    /// does this; nothing else in Basic mode resets placement. Leaves content (text, portrait
    /// asset) and every other property untouched, and never creates an element.
    /// </summary>
    internal void ResetBasicLayout()
    {
        localErrorMessage = null;

        Identity.ResetLayout(profile =>
        {
            ResetElementRect(profile, ProfileElementRole.BasicPortrait, DefaultPortraitPosition, DefaultPortraitSize);
            ResetElementRect(profile, ProfileElementRole.BasicMessage, DefaultMessagePosition, DefaultMessageSize);
        });
    }

    private static void ResetElementRect(ProfileDocument profile, ProfileElementRole role, Vector2 position, Vector2 size)
    {
        if (FindByRole(profile, role) is { } element)
        {
            (element.Position, element.Size) = Scaled(profile, position, size);
        }
    }

    private static TextProfileElement CreateTextElement(ProfileDocument profile, ProfileElementRole role)
    {
        // Only the message is created here; identity elements belong to BasicIdentitySession.
        var (position, size) = Scaled(profile, DefaultMessagePosition, DefaultMessageSize);
        return new TextProfileElement
        {
            Role = role,
            Position = position,
            Size = size,
            FontFamily = ProfileFontFamilies.AetherFrameSans,
            FontSize = Math.Clamp(MathF.Round(DefaultMessageFontSize * profile.CanvasHeight / ReferenceCanvasHeight), TextProfileElement.MinFontSize, TextProfileElement.MaxFontSize),
            Color = DefaultMessageColor,
            Alignment = TextAlignment.Left,
            Wrap = true,
        };
    }

    /// <summary>A reference-canvas rectangle scaled to the profile's actual canvas.</summary>
    private static (Vector2 Position, Vector2 Size) Scaled(ProfileDocument profile, Vector2 position, Vector2 size)
    {
        var scale = new Vector2(profile.CanvasWidth / ReferenceCanvasWidth, profile.CanvasHeight / ReferenceCanvasHeight);
        return (position * scale, size * scale);
    }
}
