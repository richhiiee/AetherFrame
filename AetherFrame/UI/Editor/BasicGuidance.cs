using AetherFrame.Domain.Profiles;

namespace AetherFrame.UI.Editor;

/// <summary>Where the one persisted guidance flag lives (the plugin configuration).</summary>
internal interface IBasicGuidanceStore
{
    /// <summary>Whether the Basic-first suggestion has been handled (never shown again once true).</summary>
    bool BasicGuidanceHandled { get; set; }

    /// <summary>Persists the configuration.</summary>
    void Save();
}

/// <summary>What to ask before the Advanced Editor opens (see <see cref="BasicGuidance.PromptBeforeAdvanced"/>).</summary>
internal enum BasicGuidancePrompt
{
    /// <summary>Nothing: open the Advanced Editor.</summary>
    None,

    /// <summary>Suggest Basic, offering to open this Plate there (Try Basic Editor) or continue to Advanced.</summary>
    OfferBasic,

    /// <summary>
    /// The Plate is freeform and can't sensibly open in Basic: explain Basic (and how to start with
    /// it) and continue to Advanced — never offering to open this Plate in Basic.
    /// </summary>
    AdvancedOnly,
}

/// <summary>How the player answered the one-time suggestion.</summary>
internal enum BasicGuidanceAnswer
{
    /// <summary>Try Basic Editor (only offered for a Plate that suits Basic).</summary>
    TryBasicEditor,

    /// <summary>Continue to Advanced.</summary>
    ContinueToAdvanced,

    /// <summary>Closed the prompt with its close button: continues to Advanced.</summary>
    Closed,
}

/// <summary>How the configuration holding the guidance flag was found when the plugin loaded.</summary>
internal enum GuidanceConfigOrigin
{
    /// <summary>
    /// No configuration file. Every install from before this setting existed looks like this (no
    /// earlier AetherFrame build ever saved a configuration), so it says nothing about whether the
    /// player has used the Advanced Editor: the guidance is still to come.
    /// </summary>
    Missing,

    /// <summary>A configuration saved before the guidance flag existed.</summary>
    Legacy,

    /// <summary>A configuration that already records the guidance flag.</summary>
    Current,
}

/// <summary>
/// The one-time "New to AetherFrame?" suggestion: before a player enters the Advanced Editor for
/// the first time — by any route: Open in Advanced Editor, Edit or double-click on a Plate that
/// opens in Advanced, creating a Blank Canvas or other freeform Plate — Basic is suggested once,
/// never required. Answering the prompt (or closing it, which continues to Advanced) handles it
/// for good, and so does simply opening the Basic Editor first; so switching to Advanced from
/// Basic never asks. One persisted flag.
///
/// <para><b>The stored flag is authoritative.</b> A current (Version 2) configuration's flag always
/// wins — true or false — whatever the Library holds. Only while migrating is it decided: a
/// configuration saved by an older version (before the flag existed) counts as handled; a missing
/// configuration starts unhandled and is saved at once, so from then on the stored flag decides.
/// Saved Plates are deliberately not treated as "already used Advanced": they don't show it.</para>
/// </summary>
internal sealed class BasicGuidance
{
    private readonly IBasicGuidanceStore store;
    private bool resolved;

    internal BasicGuidance(IBasicGuidanceStore store)
    {
        this.store = store;
    }

    /// <summary>Whether choosing the Advanced Editor should suggest the Basic Editor first.</summary>
    internal bool ShouldSuggestBasic => resolved && !store.BasicGuidanceHandled;

    /// <summary>
    /// What to ask before the Advanced Editor opens <paramref name="plate"/>: nothing once the
    /// guidance is handled (or not yet decided), or when the Advanced Editor is already showing (the
    /// player is already in it); otherwise the suggestion — offering to open this Plate in Basic only
    /// when its content suits Basic (<see cref="EditorSurfaceChooser"/>), so a freeform Plate is
    /// never sent to an editor it doesn't work in.
    /// </summary>
    internal BasicGuidancePrompt PromptBeforeAdvanced(bool advancedAlreadyOpen, ProfileDocument? plate)
    {
        if (!ShouldSuggestBasic || advancedAlreadyOpen)
        {
            return BasicGuidancePrompt.None;
        }

        return EditorSurfaceChooser.ForDocument(plate) == EditorSurfaceKind.Basic ? BasicGuidancePrompt.OfferBasic : BasicGuidancePrompt.AdvancedOnly;
    }

    /// <summary>How a loaded configuration relates to the guidance flag.</summary>
    /// <param name="configurationFound">Whether a saved configuration was loaded.</param>
    /// <param name="version">Its Version.</param>
    /// <param name="currentVersion">The Version that added the flag.</param>
    internal static GuidanceConfigOrigin OriginOf(bool configurationFound, int version, int currentVersion) =>
        !configurationFound ? GuidanceConfigOrigin.Missing
        : version < currentVersion ? GuidanceConfigOrigin.Legacy
        : GuidanceConfigOrigin.Current;

    /// <summary>
    /// Whether an install found with <paramref name="origin"/> has already handled the guidance: a
    /// current configuration says so itself (its flag wins); one saved by an older version belongs
    /// to an established player (migration only); a missing one hasn't.
    /// </summary>
    internal static bool IsHandled(GuidanceConfigOrigin origin, bool storedHandled) => origin switch
    {
        GuidanceConfigOrigin.Current => storedHandled,
        GuidanceConfigOrigin.Legacy => true,
        _ => false,
    };

    /// <summary>
    /// At load: decides the flag for this install (never un-handling it) and, for a configuration
    /// that didn't record it yet, saves it — so the decision is made only once, and afterwards the
    /// stored flag is authoritative.
    /// </summary>
    internal void Resolve(GuidanceConfigOrigin origin)
    {
        var handled = store.BasicGuidanceHandled || IsHandled(origin, store.BasicGuidanceHandled);
        var changed = handled != store.BasicGuidanceHandled;
        store.BasicGuidanceHandled = handled;
        resolved = true;

        if (changed || origin != GuidanceConfigOrigin.Current)
        {
            store.Save();
        }
    }

    /// <summary>The guidance has been handled (the Basic Editor opened, or the prompt answered): never suggest again.</summary>
    internal void MarkHandled()
    {
        if (store.BasicGuidanceHandled)
        {
            return;
        }

        store.BasicGuidanceHandled = true;
        store.Save();
    }
}

/// <summary>
/// Holds a request to open the Advanced Editor back until the one-time suggestion (see
/// <see cref="BasicGuidance"/>) is answered — the one place that decides whether to ask, what the
/// prompt offers, and which editor opens once it's answered. My Plates, where every normal way into
/// Advanced starts, routes its Advanced opens through <see cref="TryEnterAdvanced"/> and draws the
/// prompt while <see cref="IsWaiting"/>. Advanced never opens while the prompt is waiting, and
/// Basic is never forced: every answer except Try Basic Editor (offered only for a Plate that suits
/// Basic) continues to Advanced.
/// </summary>
internal sealed class AdvancedEntryGate
{
    private readonly BasicGuidance guidance;
    private bool promptRequested;

    internal AdvancedEntryGate(BasicGuidance guidance)
    {
        this.guidance = guidance;
    }

    /// <summary>The prompt a held Advanced request is waiting on (None when nothing is waiting).</summary>
    internal BasicGuidancePrompt Pending { get; private set; }

    internal bool IsWaiting => Pending != BasicGuidancePrompt.None;

    /// <summary>Whether the waiting prompt offers Try Basic Editor (only for a Plate that suits Basic).</summary>
    internal bool OffersTryBasic => Pending == BasicGuidancePrompt.OfferBasic;

    /// <summary>Whether the waiting prompt has actually been on screen (so closing it is an answer).</summary>
    internal bool WasShown { get; private set; }

    /// <summary>
    /// A request to open the Advanced Editor on <paramref name="plate"/>: true when it may open now;
    /// false when it's held for the suggestion (the prompt is requested) or already held.
    /// </summary>
    internal bool TryEnterAdvanced(bool advancedAlreadyOpen, ProfileDocument? plate)
    {
        if (IsWaiting)
        {
            return false;
        }

        var prompt = guidance.PromptBeforeAdvanced(advancedAlreadyOpen, plate);
        if (prompt == BasicGuidancePrompt.None)
        {
            return true;
        }

        Pending = prompt;
        WasShown = false;
        promptRequested = true;
        return false;
    }

    /// <summary>True once per held request: the window opens its prompt.</summary>
    internal bool ConsumePromptRequest()
    {
        var requested = promptRequested;
        promptRequested = false;
        return requested;
    }

    /// <summary>The prompt was drawn this frame.</summary>
    internal void MarkShown()
    {
        if (IsWaiting)
        {
            WasShown = true;
        }
    }

    /// <summary>
    /// The answer: the guidance is handled for good, and the editor to open now is returned — Basic
    /// only for Try Basic Editor on a Plate that suits Basic, Advanced otherwise. Null (nothing
    /// changes) when no request was waiting.
    /// </summary>
    internal EditorSurfaceKind? Answer(BasicGuidanceAnswer answer)
    {
        if (!IsWaiting)
        {
            return null;
        }

        var tryBasic = answer == BasicGuidanceAnswer.TryBasicEditor && OffersTryBasic;
        Clear();
        guidance.MarkHandled();
        return tryBasic ? EditorSurfaceKind.Basic : EditorSurfaceKind.Advanced;
    }

    /// <summary>
    /// The held request is dropped unanswered (My Plates closed meanwhile): nothing opens and nothing
    /// is handled, so the next Advanced request asks again.
    /// </summary>
    internal void Abandon() => Clear();

    private void Clear()
    {
        Pending = BasicGuidancePrompt.None;
        WasShown = false;
        promptRequested = false;
    }
}
