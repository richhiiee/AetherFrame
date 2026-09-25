namespace AetherFrame.UI.Editor;

/// <summary>Where the one persisted guidance flag lives (the plugin configuration).</summary>
internal interface IBasicGuidanceStore
{
    /// <summary>Whether the Basic-first suggestion has been handled (never shown again once true).</summary>
    bool BasicGuidanceHandled { get; set; }

    /// <summary>Persists the configuration.</summary>
    void Save();
}

/// <summary>How the configuration holding the guidance flag was found when the plugin loaded.</summary>
internal enum GuidanceConfigOrigin
{
    /// <summary>No configuration file: a brand-new install, or an install from before AetherFrame saved one.</summary>
    Missing,

    /// <summary>A configuration saved before the guidance flag existed.</summary>
    Legacy,

    /// <summary>A configuration that already records the guidance flag.</summary>
    Current,
}

/// <summary>
/// The one-time "New to AetherFrame?" suggestion: the first time a player chooses the Advanced
/// Editor themselves, before they've ever used the Basic Editor, Basic is suggested once — never
/// required. Choosing either editor in that prompt (or closing it, which continues to Advanced)
/// handles it for good, and so does simply opening the Basic Editor first. One persisted flag.
///
/// <para><b>Existing installs.</b> An update must not nag players who already know AetherFrame, so
/// the flag is only left unhandled for a genuinely new player: a configuration saved before the flag
/// existed, or a missing configuration with Plates already in the Library, counts as handled. Until
/// that's been decided (the Library has loaded) nothing is suggested at all; if the Library can't
/// load, it never is.</para>
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
    /// Whether an install found with <paramref name="origin"/> has already handled the guidance: a
    /// current configuration says so itself; an older one belongs to an established player; a
    /// missing one does too when their Library already holds Plates.
    /// </summary>
    internal static bool IsHandled(GuidanceConfigOrigin origin, bool storedHandled, bool libraryHasPlates) => origin switch
    {
        GuidanceConfigOrigin.Current => storedHandled,
        GuidanceConfigOrigin.Legacy => true,
        _ => libraryHasPlates,
    };

    /// <summary>
    /// Once the Library has loaded: decides the flag for this install (never un-handling it) and,
    /// for a configuration that didn't record it yet, saves it — so the decision is made only once.
    /// </summary>
    internal void Resolve(GuidanceConfigOrigin origin, bool libraryHasPlates)
    {
        var handled = store.BasicGuidanceHandled || IsHandled(origin, store.BasicGuidanceHandled, libraryHasPlates);
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
