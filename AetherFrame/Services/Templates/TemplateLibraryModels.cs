using System;

namespace AetherFrame.Services.Templates;

internal enum TemplateKind
{
    /// <summary>Ships with AetherFrame. Never persisted, never renamed/duplicated/deleted.</summary>
    BuiltIn,

    /// <summary>Saved locally by the player (Save as Template / Duplicate Template).</summary>
    UserSaved,
}

internal enum TemplateStatus
{
    /// <summary>Loaded and fully usable.</summary>
    Ready,

    /// <summary>Saved by a newer AetherFrame — either the envelope or the embedded document.
    /// Listed, but never opened, edited, or rewritten.</summary>
    NewerVersion,

    /// <summary>The file (and any backup of it) couldn't be read. Listed so it isn't silently lost.</summary>
    Unreadable,
}

/// <summary>An immutable snapshot of one Template for the Library UI. <see cref="SupportsPreview"/>
/// is a real per-Template capability (see <c>BuiltInTemplateDefinition</c>) — UI must gate Preview
/// on this field, never on <see cref="DisplayName"/> or any other string.</summary>
internal sealed record TemplateSummary(
    Guid TemplateId,
    TemplateKind Kind,
    TemplateStatus Status,
    string DisplayName,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    string? Problem,
    bool HasUnsupportedElements,
    bool SupportsPreview)
{
    internal bool IsReady => Status == TemplateStatus.Ready;

    internal bool IsBuiltIn => Kind == TemplateKind.BuiltIn;
}

/// <summary>A Library operation refused for a reason the player should see (message is player-facing).</summary>
internal sealed class TemplateLibraryException : Exception
{
    internal TemplateLibraryException(string message)
        : base(message)
    {
    }
}
