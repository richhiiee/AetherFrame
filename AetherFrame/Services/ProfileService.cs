using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;

namespace AetherFrame.Services;

/// <summary>
/// Owns the currently loaded character binding and profile.
/// Public members are safe to call from ImGui Draw (the render thread); any work that
/// must run on the Dalamud framework thread is dispatched internally via IFramework.Run.
/// </summary>
internal sealed class ProfileService
{
    private readonly object gate = new();
    private readonly CharacterBindingRepository bindingRepository;
    private readonly ProfileRepository profileRepository;
    private readonly CharacterIdentityService characterIdentity;

    private CharacterBinding? currentBinding;
    private ProfileDocument? currentProfile;
    private bool isBusy;

    internal ProfileService(
        CharacterBindingRepository bindingRepository,
        ProfileRepository profileRepository,
        CharacterIdentityService characterIdentity)
    {
        this.bindingRepository = bindingRepository;
        this.profileRepository = profileRepository;
        this.characterIdentity = characterIdentity;
    }

    internal CharacterBinding? CurrentBinding
    {
        get { lock (gate) return currentBinding; }
    }

    internal ProfileDocument? CurrentProfile
    {
        get { lock (gate) return currentProfile; }
    }

    internal bool IsBusy
    {
        get { lock (gate) return isBusy; }
    }

    /// <summary>
    /// Loads the character binding and active profile for the currently logged-in character.
    /// Safe to call from ImGui Draw; the actual load runs on the framework thread.
    /// </summary>
    internal async Task LoadForCurrentCharacterAsync()
    {
        lock (gate)
        {
            if (isBusy)
            {
                return;
            }

            isBusy = true;
        }

        try
        {
            await DalamudServices.Framework.Run(LoadForCurrentCharacterCoreAsync).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                isBusy = false;
            }
        }
    }

    /// <summary>
    /// Saves the currently loaded profile. Safe to call from ImGui Draw; the actual write
    /// runs on the framework thread.
    /// </summary>
    internal async Task SaveCurrentProfileAsync()
    {
        ProfileDocument snapshot;
        Guid profileId;
        ulong ownerContentId;
        int newRevision;
        DateTime updatedAtUtc;

        lock (gate)
        {
            var profile = RequireEditableProfileLocked();

            profileId = profile.ProfileId;
            ownerContentId = currentBinding!.ContentId;
            newRevision = profile.Revision + 1;
            updatedAtUtc = DateTime.UtcNow;

            // Capture ownership and content now, before dispatching to the framework thread,
            // so a later character/profile switch can't be attributed to this save.
            snapshot = CloneForSave(profile, newRevision, updatedAtUtc);

            isBusy = true;
        }

        var succeeded = false;
        try
        {
            await DalamudServices.Framework.Run(() => SaveCurrentProfileCoreAsync(snapshot)).ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            lock (gate)
            {
                isBusy = false;

                if (succeeded)
                {
                    var stillSameProfile = currentProfile is not null && currentProfile.ProfileId == profileId;
                    var stillSameCharacter = currentBinding is not null
                        && currentBinding.ContentId == ownerContentId
                        && characterIdentity.CurrentContentId == ownerContentId;

                    if (stillSameProfile && stillSameCharacter)
                    {
                        currentProfile!.Revision = newRevision;
                        currentProfile.UpdatedAtUtc = updatedAtUtc;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds a text element to the currently loaded profile. Synchronous UI mutation; safe to
    /// call directly from ImGui Draw. Returns the new element's id.
    /// </summary>
    internal Guid AddTextElement(string text)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            EnsureCapacityLocked(profile);

            var content = text ?? string.Empty;
            if (content.Length > TextProfileElement.MaxTextLength)
            {
                content = content[..TextProfileElement.MaxTextLength];
            }

            var element = new TextProfileElement
            {
                Text = content,
                ZIndex = NextZIndexLocked(profile),
                // Explicit, not the property's own default: new text should use AetherFrame's
                // fully-styled default family, while legacy/unset elements must keep resolving
                // to TextProfileElement.FontFamily's own default (Dalamud Default) — see that
                // property's doc comment for why those two defaults must stay independent.
                FontFamily = ProfileFontFamilies.AetherFrameSans,
            };
            profile.Elements.Add(element);
            return element.Id;
        }
    }

    /// <summary>
    /// Adds a fully-formed element (id, role, position, styling, etc. already set by the
    /// caller) to the currently loaded profile, assigning it the next Z-index. Unlike
    /// <see cref="AddTextElement"/>, which applies its own defaults, this lets a caller (the
    /// Advanced editor's image import, or the Basic editor's role-tagged elements) fully control
    /// the new element's initial layout and styling. Synchronous UI mutation; safe to call
    /// directly from ImGui Draw.
    /// </summary>
    internal Guid AddElement(ProfileElement element)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            EnsureCapacityLocked(profile);

            element.ZIndex = NextZIndexLocked(profile);
            profile.Elements.Add(element);
            return element.Id;
        }
    }

    /// <summary>
    /// Removes an element from the currently loaded profile. Synchronous UI mutation; safe to
    /// call directly from ImGui Draw.
    /// </summary>
    internal void RemoveElement(Guid elementId)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            profile.Elements.RemoveAll(element => element.Id == elementId);
        }
    }

    /// <summary>
    /// Mutates an existing element of the currently loaded profile. Synchronous UI mutation;
    /// safe to call directly from ImGui Draw. Throws <see cref="InvalidOperationException"/>
    /// if the profile isn't editable (busy, no character, wrong character, etc.) or if no
    /// element with the given id exists.
    /// </summary>
    internal void UpdateElement(Guid elementId, Action<ProfileElement> update)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            update(FindElementLocked(profile, elementId));
        }
    }

    /// <summary>Returns an independent copy of an existing element, e.g. for undo/redo snapshots.</summary>
    internal ProfileElement CloneElement(Guid elementId)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            return FindElementLocked(profile, elementId).Clone();
        }
    }

    /// <summary>
    /// Inserts a fully-formed element (e.g. an undo/redo snapshot, or a duplicate) into the
    /// currently loaded profile, replacing any existing element with the same id.
    /// </summary>
    internal void InsertElement(ProfileElement element)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();

            profile.Elements.RemoveAll(e => e.Id == element.Id);
            EnsureCapacityLocked(profile);
            profile.Elements.Add(element);
        }
    }

    /// <summary>
    /// Duplicates an existing element: new id, same editable properties, offset and clamped
    /// position, placed above the source in z-order. Returns the duplicate's id.
    /// </summary>
    internal Guid DuplicateElement(Guid elementId)
    {
        const float offset = 16f;

        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            var source = FindElementLocked(profile, elementId);
            EnsureCapacityLocked(profile);

            var duplicate = source.Clone();
            duplicate.Id = Guid.NewGuid();

            var maxX = Math.Max(0f, profile.CanvasWidth - duplicate.Size.X);
            var maxY = Math.Max(0f, profile.CanvasHeight - duplicate.Size.Y);
            duplicate.Position = new Vector2(
                Math.Clamp(duplicate.Position.X + offset, 0f, maxX),
                Math.Clamp(duplicate.Position.Y + offset, 0f, maxY));

            duplicate.ZIndex = NextZIndexLocked(profile);

            profile.Elements.Add(duplicate);
            return duplicate.Id;
        }
    }

    /// <summary>Moves an element one step towards the front of the visual stacking order.</summary>
    internal void BringForward(Guid elementId) => ReorderZIndex(elementId, static (ordered, index) =>
    {
        if (index < ordered.Count - 1)
        {
            (ordered[index], ordered[index + 1]) = (ordered[index + 1], ordered[index]);
        }
    });

    /// <summary>Moves an element one step towards the back of the visual stacking order.</summary>
    internal void SendBackward(Guid elementId) => ReorderZIndex(elementId, static (ordered, index) =>
    {
        if (index > 0)
        {
            (ordered[index], ordered[index - 1]) = (ordered[index - 1], ordered[index]);
        }
    });

    /// <summary>Moves an element to the very front of the visual stacking order.</summary>
    internal void BringToFront(Guid elementId) => ReorderZIndex(elementId, static (ordered, index) =>
    {
        var element = ordered[index];
        ordered.RemoveAt(index);
        ordered.Add(element);
    });

    /// <summary>Moves an element to the very back of the visual stacking order.</summary>
    internal void SendToBack(Guid elementId) => ReorderZIndex(elementId, static (ordered, index) =>
    {
        var element = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(0, element);
    });

    /// <summary>Captures every element's current ZIndex, e.g. for an undo/redo snapshot.</summary>
    internal Dictionary<Guid, int> SnapshotZOrder()
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            return profile.Elements.ToDictionary(e => e.Id, e => e.ZIndex);
        }
    }

    /// <summary>Restores a previously captured ZIndex snapshot (see <see cref="SnapshotZOrder"/>).</summary>
    internal void RestoreZOrder(Dictionary<Guid, int> snapshot)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();

            foreach (var element in profile.Elements)
            {
                if (snapshot.TryGetValue(element.Id, out var zIndex))
                {
                    element.ZIndex = zIndex;
                }
            }
        }
    }

    /// <summary>
    /// Mutates the current profile's background fields (asset, fit mode, opacity). Kept
    /// separate from <see cref="Elements"/> since the background isn't itself a
    /// <see cref="ProfileElement"/> (no Z order, not hit-testable).
    /// </summary>
    internal void UpdateBackground(Action<ProfileDocument> update)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            update(profile);
        }
    }

    /// <summary>Captures the current profile's background fields, e.g. for an undo/redo snapshot.</summary>
    internal BackgroundState CaptureBackgroundState()
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            return new BackgroundState(profile.BackgroundAssetId, profile.BackgroundFitMode, profile.BackgroundOpacity);
        }
    }

    /// <summary>Restores a previously captured background snapshot (see <see cref="CaptureBackgroundState"/>).</summary>
    internal void RestoreBackgroundState(BackgroundState state)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            profile.BackgroundAssetId = state.AssetId;
            profile.BackgroundFitMode = state.FitMode;
            profile.BackgroundOpacity = state.Opacity;
        }
    }

    /// <summary>
    /// Resizes the current profile's canvas. When <paramref name="scaleContentsProportionally"/>
    /// is true, every element's Position/Size is scaled by the same width/height ratio the
    /// canvas itself changes by, so layouts stay proportioned to the new canvas; otherwise every
    /// element's Position/Size is left exactly as-is (only the canvas bounds change). Rotation
    /// values are never touched by either mode. A profile with an unresolved (zero) canvas size
    /// scales as if it were the legacy 1920x1080 size, matching what it would already be
    /// rendered/edited against.
    /// </summary>
    internal void ResizeCanvas(float newWidth, float newHeight, bool scaleContentsProportionally)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();

            if (scaleContentsProportionally)
            {
                var oldWidth = profile.CanvasWidth > 0f ? profile.CanvasWidth : ProfileDocument.LegacyCanvasWidth;
                var oldHeight = profile.CanvasHeight > 0f ? profile.CanvasHeight : ProfileDocument.LegacyCanvasHeight;
                var scale = new Vector2(newWidth / oldWidth, newHeight / oldHeight);

                foreach (var element in profile.Elements)
                {
                    element.Position *= scale;
                    element.Size *= scale;
                }
            }

            profile.CanvasWidth = newWidth;
            profile.CanvasHeight = newHeight;
        }
    }

    /// <summary>Captures the current profile's canvas size and every element's Position/Size, e.g. for an undo/redo snapshot of a canvas resize.</summary>
    internal CanvasLayoutState CaptureCanvasLayoutState()
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            var elementLayouts = profile.Elements.ToDictionary(e => e.Id, e => (e.Position, e.Size));
            return new CanvasLayoutState(profile.CanvasWidth, profile.CanvasHeight, elementLayouts);
        }
    }

    /// <summary>Restores a previously captured canvas layout snapshot (see <see cref="CaptureCanvasLayoutState"/>).</summary>
    internal void RestoreCanvasLayoutState(CanvasLayoutState state)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();
            profile.CanvasWidth = state.CanvasWidth;
            profile.CanvasHeight = state.CanvasHeight;

            foreach (var element in profile.Elements)
            {
                if (state.ElementLayouts.TryGetValue(element.Id, out var layout))
                {
                    element.Position = layout.Position;
                    element.Size = layout.Size;
                }
            }
        }
    }

    /// <summary>
    /// Verifies the current profile is safe to edit or save:
    /// a profile and binding are loaded, no operation is busy, a character is logged in,
    /// and the logged-in character matches the loaded binding's character.
    /// Throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    internal void RequireEditableProfile()
    {
        lock (gate)
        {
            RequireEditableProfileLocked();
        }
    }

    /// <summary>Must be called while holding <see cref="gate"/>.</summary>
    private static ProfileElement FindElementLocked(ProfileDocument profile, Guid elementId) =>
        profile.Elements.Find(e => e.Id == elementId)
            ?? throw new InvalidOperationException($"No element with id {elementId} exists in the current profile.");

    /// <summary>Must be called while holding <see cref="gate"/>.</summary>
    private static void EnsureCapacityLocked(ProfileDocument profile)
    {
        if (profile.Elements.Count >= ProfileDocument.MaxElementCount)
        {
            throw new InvalidOperationException(
                $"Profile already has the maximum of {ProfileDocument.MaxElementCount} elements.");
        }
    }

    /// <summary>Must be called while holding <see cref="gate"/>.</summary>
    private static int NextZIndexLocked(ProfileDocument profile) =>
        profile.Elements.Count == 0 ? 0 : profile.Elements.Max(e => e.ZIndex) + 1;

    /// <summary>
    /// Applies a reordering operation to the elements sorted by their current ZIndex (ties
    /// broken by list order), then renumbers every element's ZIndex to a compact 0..N-1
    /// sequence matching the new order, so values never grow unbounded.
    /// </summary>
    private void ReorderZIndex(Guid elementId, Action<List<ProfileElement>, int> reorder)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();

            var ordered = profile.Elements.OrderBy(e => e.ZIndex).ToList();
            var index = ordered.FindIndex(e => e.Id == elementId);
            if (index < 0)
            {
                throw new InvalidOperationException($"No element with id {elementId} exists in the current profile.");
            }

            reorder(ordered, index);

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].ZIndex = i;
            }
        }
    }

    private async Task LoadForCurrentCharacterCoreAsync()
    {
        AssertFrameworkThread();

        if (!characterIdentity.IsCharacterLoggedIn)
        {
            lock (gate)
            {
                currentBinding = null;
                currentProfile = null;
            }

            return;
        }

        var contentId = characterIdentity.CurrentContentId!.Value;

        var binding = await bindingRepository.LoadOrCreateAsync(contentId).ConfigureAwait(false);

        ProfileDocument? profile = binding.ActiveProfileId is { } activeProfileId
            ? await profileRepository.LoadAsync(activeProfileId).ConfigureAwait(false)
            : null;

        if (profile is not null)
        {
            // Repair an invalid (legacy) canvas size before repairing elements against it, so
            // their own repair clamps against the correct bounds. Neither repair is persisted
            // here; a subsequent manual save writes them back to disk.
            profile.NormalizeLegacyCanvasSize();

            foreach (var element in profile.Elements)
            {
                element.NormalizeLegacyLayout(profile.CanvasWidth, profile.CanvasHeight);
            }
        }

        if (profile is null)
        {
            profile = CreateDefaultProfile(contentId);
            binding.ActiveProfileId = profile.ProfileId;

            if (!binding.ProfileIds.Contains(profile.ProfileId))
            {
                binding.ProfileIds.Add(profile.ProfileId);
            }

            binding.UpdatedAtUtc = DateTime.UtcNow;

            await profileRepository.SaveAsync(profile).ConfigureAwait(false);
            await bindingRepository.SaveAsync(binding).ConfigureAwait(false);
        }

        lock (gate)
        {
            // Guard against a character switch that happened while this load was in flight.
            if (characterIdentity.CurrentContentId == contentId)
            {
                currentBinding = binding;
                currentProfile = profile;
            }
        }
    }

    private async Task SaveCurrentProfileCoreAsync(ProfileDocument snapshot)
    {
        AssertFrameworkThread();

        await profileRepository.SaveAsync(snapshot).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the current profile is safe to mutate or save. Must be called while holding <see cref="gate"/>.
    /// </summary>
    private ProfileDocument RequireEditableProfileLocked()
    {
        if (currentProfile is null)
        {
            throw new InvalidOperationException("No profile is currently loaded.");
        }

        if (currentBinding is null)
        {
            throw new InvalidOperationException("No character binding is currently loaded.");
        }

        if (isBusy)
        {
            throw new InvalidOperationException("A profile operation is already in progress.");
        }

        if (!characterIdentity.IsCharacterLoggedIn)
        {
            throw new InvalidOperationException("No character is currently logged in.");
        }

        if (characterIdentity.CurrentContentId != currentBinding.ContentId)
        {
            throw new InvalidOperationException(
                "The active character does not match the loaded profile's character binding.");
        }

        return currentProfile;
    }

    private static ProfileDocument CreateDefaultProfile(ulong ownerContentId) => new()
    {
        Version = 2,
        ProfileId = Guid.NewGuid(),
        OwnerContentId = ownerContentId,
        Name = "Default",
        Revision = 0,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        CanvasWidth = ProfileDocument.DefaultCanvasWidth,
        CanvasHeight = ProfileDocument.DefaultCanvasHeight,
        Elements = new List<ProfileElement>(),
    };

    private static ProfileDocument CloneForSave(ProfileDocument source, int revision, DateTime updatedAtUtc) => new()
    {
        Version = source.Version,
        ProfileId = source.ProfileId,
        OwnerContentId = source.OwnerContentId,
        Name = source.Name,
        Revision = revision,
        CreatedAtUtc = source.CreatedAtUtc,
        UpdatedAtUtc = updatedAtUtc,
        CanvasWidth = source.CanvasWidth,
        CanvasHeight = source.CanvasHeight,
        BackgroundAssetId = source.BackgroundAssetId,
        BackgroundFitMode = source.BackgroundFitMode,
        BackgroundOpacity = source.BackgroundOpacity,
        Elements = new List<ProfileElement>(source.Elements),
    };

    /// <summary>Immutable snapshot of a profile's background fields, for undo/redo.</summary>
    internal readonly record struct BackgroundState(Guid? AssetId, BackgroundFitMode FitMode, float Opacity);

    /// <summary>Immutable snapshot of a profile's canvas size and every element's Position/Size, for undo/redo of a canvas resize.</summary>
    internal readonly record struct CanvasLayoutState(float CanvasWidth, float CanvasHeight, Dictionary<Guid, (Vector2 Position, Vector2 Size)> ElementLayouts);

    private static void AssertFrameworkThread()
    {
        if (!DalamudServices.Framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException("This operation must run on the Dalamud framework thread.");
        }
    }
}
