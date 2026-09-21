using System;
using System.Collections.Generic;
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
    /// call directly from ImGui Draw.
    /// </summary>
    internal void AddTextElement(string text)
    {
        lock (gate)
        {
            var profile = RequireEditableProfileLocked();

            if (profile.Elements.Count >= ProfileDocument.MaxElementCount)
            {
                throw new InvalidOperationException(
                    $"Profile already has the maximum of {ProfileDocument.MaxElementCount} elements.");
            }

            var content = text ?? string.Empty;
            if (content.Length > TextProfileElement.MaxTextLength)
            {
                content = content[..TextProfileElement.MaxTextLength];
            }

            profile.Elements.Add(new TextProfileElement { Text = content });
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

            var element = profile.Elements.Find(e => e.Id == elementId)
                ?? throw new InvalidOperationException($"No element with id {elementId} exists in the current profile.");

            update(element);
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
            // Repair any elements with an invalid (legacy or corrupt) canvas size in memory.
            // Not persisted here; a subsequent manual save writes the repair back to disk.
            foreach (var element in profile.Elements)
            {
                element.NormalizeLegacyLayout();
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
        ProfileId = Guid.NewGuid(),
        OwnerContentId = ownerContentId,
        Name = "Default",
        Revision = 0,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
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
        Elements = new List<ProfileElement>(source.Elements),
    };

    private static void AssertFrameworkThread()
    {
        if (!DalamudServices.Framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException("This operation must run on the Dalamud framework thread.");
        }
    }
}
