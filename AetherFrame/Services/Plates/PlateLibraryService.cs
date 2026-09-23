using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Plates;

/// <summary>
/// The local Plate Library: every saved Plate, their manual order, and each character's
/// associated and Active Plates. Deliberately independent of Dalamud and game state (the
/// character is always passed in), so all of it runs in tests against plain files.
///
/// <para><b>Sources of truth.</b> Which Plates exist is always the Plate documents on disk; the
/// library index (<see cref="PlateLibraryState"/>) only orders them and is rebuilt from the
/// documents when missing or damaged. Character associations and Active state live in
/// <see cref="CharacterBinding"/>s, never in documents.</para>
///
/// <para><b>Write ordering.</b> Every multi-file operation writes the Plate document first,
/// then character bindings, then the index — so an interruption can only ever leave a Plate
/// that the next startup finds and re-lists, never an index or binding pointing at nothing that
/// matters. Deleting moves the document to the trash FIRST for the same reason: afterwards a
/// stale reference is harmless and ignored.</para>
///
/// <para><b>Threading.</b> Operations are serialized and run through the supplied dispatcher
/// (the framework thread in game). Query members are safe from any thread, including ImGui Draw:
/// they only read immutable snapshots under a lock.</para>
/// </summary>
internal sealed class PlateLibraryService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly PlateStoragePaths paths;
    private readonly IPlateFileStore store;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly Func<Func<Task>, Task> dispatch;

    private readonly Dictionary<Guid, PlateRecord> plates = new();
    private readonly Dictionary<ulong, CharacterBinding> bindings = new();

    // Bindings whose file exists but couldn't be read. Never overwritten without first keeping a
    // recovery copy (see WriteBindingAsync).
    private readonly HashSet<ulong> unreadableBindings = new();

    // Bindings saved by a newer AetherFrame: never interpreted and never written by this build.
    private readonly HashSet<ulong> newerVersionBindings = new();

    private PlateLibraryState library = new();

    // False when library.json was written by a newer build: ordering then works in memory but is
    // never written over that file.
    private bool libraryWritable = true;

    private bool isLoaded;
    private int generation;
    private IReadOnlyList<PlateSummary>? orderedSummaries;

    internal PlateLibraryService(
        PlateStoragePaths paths,
        IPlateFileStore store,
        IAetherFrameLog? log = null,
        Func<DateTime>? utcNow = null,
        Func<Func<Task>, Task>? dispatch = null)
    {
        this.paths = paths;
        this.store = store;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.dispatch = dispatch ?? (work => work());
    }

    /// <summary>Raised (on the operation's thread) after a Plate is renamed.</summary>
    internal event Action<Guid, string>? PlateRenamed;

    /// <summary>Raised (on the operation's thread) after a Plate is deleted.</summary>
    internal event Action<Guid>? PlateDeleted;

    /// <summary>Raised (on the operation's thread) after a Plate's document is saved.</summary>
    internal event Action<Guid>? PlateSaved;

    internal bool IsLoaded
    {
        get { lock (gate) return isLoaded; }
    }

    /// <summary>Changes whenever anything visible in the Library changes (cheap cache key for UI).</summary>
    internal int Generation
    {
        get { lock (gate) return generation; }
    }

    internal PlateStoragePaths Paths => paths;

    // ---------------------------------------------------------------- queries (any thread)

    /// <summary>Every known Plate in manual order. Cached until the next change.</summary>
    internal IReadOnlyList<PlateSummary> GetOrderedPlates()
    {
        lock (gate)
        {
            return orderedSummaries ??= BuildOrderedSummariesLocked();
        }
    }

    /// <summary>Case-insensitive search over Plate names and associated character names.</summary>
    internal IReadOnlyList<PlateSummary> Search(string? query)
    {
        var all = GetOrderedPlates();
        return string.IsNullOrWhiteSpace(query)
            ? all
            : all.Where(p => PlateSearch.Matches(query, p.DisplayName, p.CharacterNames)).ToList();
    }

    internal PlateSummary? FindPlate(Guid plateId) => GetOrderedPlates().FirstOrDefault(p => p.PlateId == plateId);

    /// <summary>
    /// The saved state of a Plate for read-only display (Plate Viewer, cards), or null when it
    /// can't be shown. Callers must never mutate it; editing always gets its own copy via
    /// <see cref="OpenDocumentForEditing"/>.
    /// </summary>
    internal ProfileDocument? GetSavedDocument(Guid plateId)
    {
        lock (gate)
        {
            return plates.TryGetValue(plateId, out var record) ? record.Preview : null;
        }
    }

    /// <summary>
    /// A brand-new, independent document of a Plate's saved state to edit. Never shares anything
    /// with the Library's own copy. Throws <see cref="PlateLibraryException"/> when the Plate
    /// can't be opened.
    /// </summary>
    internal ProfileDocument OpenDocumentForEditing(Guid plateId)
    {
        string rawJson;
        lock (gate)
        {
            var record = RequireReadyLocked(plateId, "opened");
            rawJson = record.RawJson!;
        }

        return PlateDocuments.Materialize(ParseObject(rawJson));
    }

    /// <summary>The character's Active Plate, only if that Plate currently exists.</summary>
    internal Guid? GetActivePlateId(ulong contentId)
    {
        lock (gate)
        {
            return bindings.TryGetValue(contentId, out var binding) && binding.ActivePlateId is { } id && plates.ContainsKey(id)
                ? id
                : null;
        }
    }

    /// <summary>The last-known name recorded for a character, or null.</summary>
    internal string? GetLastKnownCharacterName(ulong contentId)
    {
        lock (gate)
        {
            return bindings.TryGetValue(contentId, out var binding) ? binding.LastKnownCharacterName : null;
        }
    }

    /// <summary>A copy of the character's binding (including a stale Active id, untouched), or null.</summary>
    internal CharacterBinding? GetBinding(ulong contentId)
    {
        lock (gate)
        {
            return bindings.TryGetValue(contentId, out var binding) ? binding.Clone() : null;
        }
    }

    // ---------------------------------------------------------------- load & migration

    /// <summary>
    /// Loads everything and brings it up to date. Safe to run on every startup: it only writes
    /// when something actually needs writing, and never duplicates Plates or bindings. One
    /// unreadable file never prevents the rest from loading.
    /// </summary>
    internal Task InitializeAsync() => RunExclusiveAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        var loadedPlates = await LoadPlatesAsync().ConfigureAwait(false);
        var (loadedBindings, badBindings, newerBindings, migratedBindings) = await LoadBindingsAsync().ConfigureAwait(false);

        lock (gate)
        {
            plates.Clear();
            foreach (var record in loadedPlates)
            {
                plates[record.Id] = record;
            }

            bindings.Clear();
            foreach (var binding in loadedBindings)
            {
                bindings[binding.ContentId] = binding;
            }

            unreadableBindings.Clear();
            unreadableBindings.UnionWith(badBindings);
            newerVersionBindings.Clear();
            newerVersionBindings.UnionWith(newerBindings);
            Changed();
        }

        var bindingsToWrite = new HashSet<ulong>();
        var writeLibrary = false;

        if (!store.FileExists(paths.LibraryFile))
        {
            // First run of the Plate Library over single-profile data (or a lost index).
            log.Information("AetherFrame is building the Plate Library from existing Plates.");
            BackUpLegacyBindings();

            bindingsToWrite.UnionWith(migratedBindings);
            bindingsToWrite.UnionWith(AssociateLegacyOwners());

            lock (gate)
            {
                library = new PlateLibraryState { OrderedPlateIds = PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                libraryWritable = true;
            }

            writeLibrary = true;
        }
        else
        {
            writeLibrary = await LoadLibraryIndexAsync().ConfigureAwait(false);
        }

        lock (gate)
        {
            writeLibrary |= PlateOrdering.Reconcile(library.OrderedPlateIds, ExistingPlatesLocked());

            var missing = library.OrderedPlateIds.Count(id => !plates.ContainsKey(id));
            if (missing > 0)
            {
                log.Warning($"AetherFrame's Plate order mentions {missing} Plate(s) that no longer exist; they are skipped.");
            }
        }

        // Bindings before the index: the index is written last so its existence marks a
        // completed migration (see class doc).
        foreach (var contentId in bindingsToWrite)
        {
            CharacterBinding? binding;
            lock (gate)
            {
                bindings.TryGetValue(contentId, out binding);
            }

            if (binding is not null)
            {
                await WriteBindingAsync(binding).ConfigureAwait(false);
            }
        }

        if (writeLibrary)
        {
            await WriteLibraryAsync().ConfigureAwait(false);
        }

        lock (gate)
        {
            isLoaded = true;
            Changed();
        }

        log.Information($"AetherFrame Plate Library loaded: {loadedPlates.Count} Plate(s), {loadedBindings.Count} character binding(s).");
    }

    private async Task<List<PlateRecord>> LoadPlatesAsync()
    {
        var records = new List<PlateRecord>();

        foreach (var path in store.ListFiles(paths.PlatesDirectory, "*.json"))
        {
            if (!PlateStoragePaths.TryParsePlateFileName(path, out var plateId))
            {
                log.Warning($"AetherFrame ignored a file in the Plates folder that isn't a Plate: {Path.GetFileName(path)}");
                continue;
            }

            records.Add(await LoadPlateAsync(path, plateId).ConfigureAwait(false));
        }

        return records;
    }

    private async Task<PlateRecord> LoadPlateAsync(string path, Guid plateId)
    {
        try
        {
            var result = await VersionedJson.ReadAsync<ProfileDocument>(store, path, PersistenceSchemas.ProfileDocument).ConfigureAwait(false);

            if (result.IsNewerVersion)
            {
                var (name, created, modified) = PlateDocuments.ReadDisplayFields(result.Raw!);
                log.Warning($"AetherFrame found a Plate saved by a newer version ({plateId}); it is listed but won't be opened or changed.");
                return new PlateRecord(plateId, PlateStatus.NewerVersion, null, null, name ?? "Plate", created ?? DateTime.MinValue, modified ?? DateTime.MinValue, 0, result.Migration.Version,
                    "This Plate was saved by a newer version of AetherFrame. Update AetherFrame to open it.");
            }

            // The file name is the Plate's identity: a document whose own id disagrees (e.g. a
            // hand-copied file) is treated as the Plate its file says, so saving it can never
            // overwrite a different Plate.
            var raw = result.Raw!;
            var document = result.Value!;
            if (document.ProfileId != plateId)
            {
                log.Warning($"AetherFrame Plate file {plateId} declared a different id ({document.ProfileId}); using the file's id.");
                document.ProfileId = plateId;
                raw[nameof(ProfileDocument.ProfileId)] = plateId;
            }

            PlateDocuments.ApplyLegacyRepairs(document);

            return new PlateRecord(plateId, PlateStatus.Ready, VersionedJson.Serialize(raw), document, document.Name, document.CreatedAtUtc, document.UpdatedAtUtc,
                document.Revision, result.Migration.Version, null);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"AetherFrame could not read Plate {plateId}; it is listed as unreadable and its file is left untouched.");
            return new PlateRecord(plateId, PlateStatus.Unreadable, null, null, "Unreadable Plate", DateTime.MinValue, DateTime.MinValue, 0, 0,
                "This Plate's file is damaged and couldn't be read. It has been left untouched.");
        }
    }

    private async Task<(List<CharacterBinding> Loaded, List<ulong> Unreadable, List<ulong> Newer, List<ulong> Migrated)> LoadBindingsAsync()
    {
        var loaded = new List<CharacterBinding>();
        var unreadable = new List<ulong>();
        var newer = new List<ulong>();
        var migrated = new List<ulong>();

        foreach (var path in store.ListFiles(paths.CharactersDirectory, "*.json"))
        {
            if (!PlateStoragePaths.TryParseBindingFileName(path, out var contentId))
            {
                continue;
            }

            try
            {
                var result = await VersionedJson.ReadAsync<CharacterBinding>(store, path, PersistenceSchemas.CharacterBinding).ConfigureAwait(false);
                if (result.IsNewerVersion)
                {
                    log.Warning($"AetherFrame found a character binding saved by a newer version ({Path.GetFileName(path)}); it is left untouched.");
                    newer.Add(contentId);
                    continue;
                }

                var binding = result.Value!;
                binding.ContentId = contentId;
                binding.PlateIds = binding.PlateIds.Where(id => id != Guid.Empty).Distinct().ToList();
                loaded.Add(binding);

                if (result.WasMigrated)
                {
                    migrated.Add(contentId);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AetherFrame could not read character binding {Path.GetFileName(path)}; it is left untouched.");
                unreadable.Add(contentId);
            }
        }

        return (loaded, unreadable, newer, migrated);
    }

    /// <returns>True when the index needs (re)writing.</returns>
    private async Task<bool> LoadLibraryIndexAsync()
    {
        try
        {
            var result = await VersionedJson.ReadAsync<PlateLibraryState>(store, paths.LibraryFile, PersistenceSchemas.PlateLibrary).ConfigureAwait(false);
            if (result.IsNewerVersion)
            {
                log.Warning("AetherFrame's Plate order was saved by a newer version; it is used read-only and never overwritten.");
                lock (gate)
                {
                    library = new PlateLibraryState { OrderedPlateIds = ReadNewerOrder(result.Raw!) ?? PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                    libraryWritable = false;
                }

                return false;
            }

            lock (gate)
            {
                library = result.Value!;
                library.OrderedPlateIds ??= new List<Guid>();
                libraryWritable = true;
            }

            return result.WasMigrated;
        }
        catch (Exception ex)
        {
            log.Error(ex, "AetherFrame's Plate order is damaged; rebuilding it from the saved Plates (the damaged file is kept in Recovery).");
            PreserveDamagedFile(paths.LibraryFile);

            lock (gate)
            {
                library = new PlateLibraryState { OrderedPlateIds = PlateOrdering.BuildInitialOrder(ExistingPlatesLocked()) };
                libraryWritable = true;
            }

            return true;
        }
    }

    /// <summary>Keeps an untouched copy of every pre-Plate-Library binding (once).</summary>
    private void BackUpLegacyBindings()
    {
        foreach (var path in store.ListFiles(paths.CharactersDirectory, "*.json"))
        {
            var backup = Path.Combine(paths.MigrationBackupDirectory, "Characters", Path.GetFileName(path));
            try
            {
                if (!store.FileExists(backup))
                {
                    store.CopyFile(path, backup);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AetherFrame could not back up {Path.GetFileName(path)} before migrating; continuing (the original is not modified destructively).");
            }
        }
    }

    /// <summary>
    /// Single-profile documents recorded the character they were made for. Any such Plate not
    /// already associated with that character is associated (never made Active). A character
    /// whose binding file exists but is unreadable is skipped rather than overwritten.
    /// </summary>
    /// <returns>Characters whose bindings changed.</returns>
    private List<ulong> AssociateLegacyOwners()
    {
        var changed = new List<ulong>();
        var now = utcNow();

        lock (gate)
        {
            foreach (var record in plates.Values.Where(r => r.Status == PlateStatus.Ready).OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id))
            {
                var owner = record.Preview!.OwnerContentId;
                if (owner == 0 || unreadableBindings.Contains(owner) || newerVersionBindings.Contains(owner))
                {
                    continue;
                }

                if (!bindings.TryGetValue(owner, out var binding))
                {
                    binding = new CharacterBinding { ContentId = owner, CreatedAtUtc = now, UpdatedAtUtc = now };
                    bindings[owner] = binding;
                }

                if (!binding.PlateIds.Contains(record.Id))
                {
                    binding.PlateIds.Add(record.Id);
                    binding.UpdatedAtUtc = now;
                    changed.Add(owner);
                }
            }

            if (changed.Count > 0)
            {
                Changed();
            }
        }

        return changed;
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// Creates and saves a new Plate first in the Library. With a character, the Plate is
    /// associated with it — and becomes its Active Plate only if the character had no Plates yet.
    /// Without one, the Plate stays unbound.
    /// </summary>
    internal Task<PlateCreationResult> CreatePlateAsync(PlateStartingLayout layout, CharacterContext? character, string? name = null) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var now = utcNow();
            var plateId = Guid.NewGuid();
            string plateName;
            lock (gate)
            {
                plateName = ResolveNewName(name, PlateFactory.DefaultNameFor(layout));
            }

            var document = PlateFactory.Create(layout, plateId, plateName, now);
            var raw = PlateDocuments.ToJson(document);
            await WritePlateAsync(plateId, raw).ConfigureAwait(false);

            lock (gate)
            {
                plates[plateId] = ReadyRecord(plateId, raw);
                PlateOrdering.InsertAtFront(library.OrderedPlateIds, plateId);
                Changed();
            }

            var becameActive = false;
            if (character is { } who && PrepareBindingForWrite(who, now) is { } binding)
            {
                lock (gate)
                {
                    var hadPlates = binding.PlateIds.Any(id => id != plateId && plates.ContainsKey(id));
                    if (!binding.PlateIds.Contains(plateId))
                    {
                        binding.PlateIds.Add(plateId);
                    }

                    // First Plate for this character: Active automatically. Never otherwise.
                    if (!hadPlates && (binding.ActivePlateId is null || !plates.ContainsKey(binding.ActivePlateId.Value)))
                    {
                        binding.ActivePlateId = plateId;
                        becameActive = true;
                    }
                }

                await CommitBindingAsync(binding).ConfigureAwait(false);
            }

            await WriteLibraryAsync().ConfigureAwait(false);
            log.Information($"AetherFrame created Plate {plateId} ({layout}).");
            return new PlateCreationResult(plateId, becameActive);
        });

    /// <summary>
    /// Saves an independent copy of a Plate's SAVED state (unsaved editor changes are not part of
    /// it) directly after the source, named "Name Copy". Same asset references, no image bytes
    /// copied, never Active. With a character, the copy is associated with it (not Active).
    /// </summary>
    internal Task<Guid> DuplicatePlateAsync(Guid sourcePlateId, CharacterContext? character) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var now = utcNow();
            var newId = Guid.NewGuid();
            JsonObject copy;
            lock (gate)
            {
                var source = RequireReadyLocked(sourcePlateId, "duplicated");
                var name = PlateNaming.MakeCopyName(source.Name, plates.Values.Select(p => p.Name));
                copy = PlateDocuments.CreateDuplicate(ParseObject(source.RawJson!), newId, name, now);
            }

            await WritePlateAsync(newId, copy).ConfigureAwait(false);

            lock (gate)
            {
                plates[newId] = ReadyRecord(newId, copy);
                PlateOrdering.InsertAfter(library.OrderedPlateIds, sourcePlateId, newId);
                Changed();
            }

            if (character is { } who && PrepareBindingForWrite(who, now) is { } binding)
            {
                if (!binding.PlateIds.Contains(newId))
                {
                    binding.PlateIds.Add(newId);
                }

                await CommitBindingAsync(binding).ConfigureAwait(false);
            }

            await WriteLibraryAsync().ConfigureAwait(false);
            log.Information($"AetherFrame duplicated Plate {sourcePlateId} as {newId}.");
            return newId;
        });

    /// <summary>
    /// Renames a Plate: trimmed, non-empty, duplicates allowed. Only the name (and modified time)
    /// change — identity, associations, Active state, and content stay exactly as they were.
    /// Throws <see cref="PlateLibraryException"/> for an invalid name.
    /// </summary>
    internal Task RenamePlateAsync(Guid plateId, string? newName) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (!PlateNaming.TryNormalizeName(newName, out var name, out var error))
            {
                throw new PlateLibraryException(error!);
            }

            var now = utcNow();
            JsonObject renamed;
            lock (gate)
            {
                var record = RequireReadyLocked(plateId, "renamed");
                renamed = ParseObject(record.RawJson!);
                PlateDocuments.SetName(renamed, name, now);
            }

            await WritePlateAsync(plateId, renamed).ConfigureAwait(false);

            var updated = ReadyRecord(plateId, renamed);
            lock (gate)
            {
                if (plates.ContainsKey(plateId))
                {
                    plates[plateId] = updated;
                }

                Changed();
            }

            PlateRenamed?.Invoke(plateId, name);
        });

    /// <summary>
    /// Deletes a Plate: its document moves to the Plate trash (kept, never destroyed), then every
    /// character association is removed — clearing Active where it was this Plate, with no other
    /// Plate chosen in its place — then the index entry. Image assets are never touched.
    /// </summary>
    internal Task<PlateDeletionResult> DeletePlateAsync(Guid plateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            lock (gate)
            {
                if (!plates.ContainsKey(plateId))
                {
                    throw new PlateLibraryException("That Plate no longer exists.");
                }
            }

            var now = utcNow();
            var documentPath = paths.GetPlatePath(plateId);
            if (store.FileExists(documentPath))
            {
                // Step 1, and the only one that can abort the delete: nothing else has changed yet.
                store.MoveFile(documentPath, paths.GetTrashPlatePath(plateId, now));
            }

            var affected = new List<CharacterBinding>();
            var clearedActive = new List<ulong>();
            lock (gate)
            {
                plates.Remove(plateId);

                foreach (var existing in bindings.Values.ToList())
                {
                    if (!existing.PlateIds.Contains(plateId) && existing.ActivePlateId != plateId)
                    {
                        continue;
                    }

                    var binding = existing.Clone();
                    binding.PlateIds.RemoveAll(id => id == plateId);
                    if (binding.ActivePlateId == plateId)
                    {
                        // Left unset on purpose: another Plate is never chosen automatically.
                        binding.ActivePlateId = null;
                        clearedActive.Add(binding.ContentId);
                    }

                    binding.UpdatedAtUtc = now;
                    affected.Add(binding);

                    // Memory reflects the delete even if a write below fails.
                    bindings[binding.ContentId] = binding;
                }

                library.OrderedPlateIds.RemoveAll(id => id == plateId);
                Changed();
            }

            // The Plate is already gone from the Library; a failed binding/index write below only
            // leaves a stale reference, which is ignored everywhere.
            foreach (var binding in affected)
            {
                try
                {
                    await WriteBindingAsync(binding).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"AetherFrame deleted Plate {plateId} but could not update a character binding; the stale reference is ignored.");
                }
            }

            try
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"AetherFrame deleted Plate {plateId} but could not update the Plate order; the stale entry is ignored.");
            }

            log.Information($"AetherFrame deleted Plate {plateId} (moved to the Plate trash).");
            PlateDeleted?.Invoke(plateId);
            return new PlateDeletionResult(plateId, clearedActive);
        });

    /// <summary>
    /// Makes a Plate this character's Active Plate (associating it if needed). Only the given
    /// character's binding changes.
    /// </summary>
    internal Task SetActivePlateAsync(CharacterContext character, Guid plateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            lock (gate)
            {
                RequireReadyLocked(plateId, "made Active");
            }

            var binding = PrepareBindingForWrite(character, utcNow())
                ?? throw new PlateLibraryException("This character's Plate settings were saved by a newer version of AetherFrame and can't be changed.");

            if (!binding.PlateIds.Contains(plateId))
            {
                binding.PlateIds.Add(plateId);
            }

            binding.ActivePlateId = plateId;
            await CommitBindingAsync(binding).ConfigureAwait(false);
            log.Information($"AetherFrame set Plate {plateId} Active for a character.");
        });

    /// <summary>Moves a Plate next to another in the manual order and saves the order.</summary>
    internal Task MovePlateAsync(Guid plateId, Guid targetPlateId, bool placeAfter) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            bool moved;
            lock (gate)
            {
                moved = PlateOrdering.Move(library.OrderedPlateIds, plateId, targetPlateId, placeAfter);
                if (moved)
                {
                    Changed();
                }
            }

            if (moved)
            {
                await WriteLibraryAsync().ConfigureAwait(false);
            }
        });

    /// <summary>
    /// Writes an editor's document as the Plate's saved state. Refused when the Plate was
    /// deleted (a save must never bring a deleted Plate back) or can't be written by this build.
    /// The Library's current name always wins over the snapshot's, so a rename made while the
    /// Plate was open is never reverted by the next save.
    /// </summary>
    internal Task SavePlateDocumentAsync(ProfileDocument snapshot) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var plateId = snapshot.ProfileId;
            lock (gate)
            {
                if (!plates.TryGetValue(plateId, out var record))
                {
                    throw new PlateLibraryException("This Plate was deleted from My Plates, so it can't be saved.");
                }

                if (record.Status != PlateStatus.Ready)
                {
                    throw new PlateLibraryException("This Plate can't be saved by this version of AetherFrame.");
                }

                snapshot.Name = record.Name;
            }

            snapshot.Version = ProfileDocument.CurrentSchemaVersion;
            var raw = PlateDocuments.ToJson(snapshot);
            await WritePlateAsync(plateId, raw).ConfigureAwait(false);

            lock (gate)
            {
                if (plates.ContainsKey(plateId))
                {
                    plates[plateId] = ReadyRecord(plateId, raw);
                }

                Changed();
            }

            PlateSaved?.Invoke(plateId);
        });

    /// <summary>
    /// Every asset referenced by any saved Plate and any Plate in the trash (restorable, so its
    /// images stay protected). Incomplete — and so unusable for cleanup — if any document can't
    /// be read or was saved by a newer version.
    /// </summary>
    internal Task<AssetReferenceScan> ScanAssetReferencesAsync() =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var referenced = new HashSet<Guid>();
            var problems = new List<string>();

            List<PlateRecord> snapshot;
            lock (gate)
            {
                snapshot = plates.Values.ToList();
            }

            foreach (var record in snapshot)
            {
                if (record.Status == PlateStatus.Ready)
                {
                    AssetReferenceScanner.Collect(PlateDocuments.Materialize(ParseObject(record.RawJson!)), referenced);
                }
                else
                {
                    problems.Add($"Plate {record.Id} is {record.Status}.");
                }
            }

            foreach (var path in store.ListFiles(paths.PlateTrashDirectory, "*.json"))
            {
                try
                {
                    var result = await VersionedJson.ReadAsync<ProfileDocument>(store, path, PersistenceSchemas.ProfileDocument).ConfigureAwait(false);
                    if (result.IsUsable)
                    {
                        AssetReferenceScanner.Collect(result.Value!, referenced);
                    }
                    else
                    {
                        problems.Add($"Trashed Plate {Path.GetFileName(path)} was saved by a newer version.");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"Trashed Plate {Path.GetFileName(path)} is unreadable: {ex.Message}");
                }
            }

            return new AssetReferenceScan(problems.Count == 0, referenced, problems);
        });

    // ---------------------------------------------------------------- internals

    private async Task RunExclusiveAsync(Func<Task> work) =>
        await RunExclusiveAsync<bool>(async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            T result = default!;
            await dispatch(async () => result = await work().ConfigureAwait(false)).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private void RequireLoaded()
    {
        if (!IsLoaded)
        {
            throw new PlateLibraryException("My Plates is still loading.");
        }
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private PlateRecord RequireReadyLocked(Guid plateId, string action)
    {
        if (!plates.TryGetValue(plateId, out var record))
        {
            throw new PlateLibraryException("That Plate no longer exists.");
        }

        return record.Status switch
        {
            PlateStatus.Ready => record,
            PlateStatus.NewerVersion => throw new PlateLibraryException($"This Plate was saved by a newer version of AetherFrame and can't be {action}."),
            _ => throw new PlateLibraryException($"This Plate's file is damaged and it can't be {action}."),
        };
    }

    /// <summary>
    /// A working COPY of the binding to change for <paramref name="character"/> (new when there is
    /// none), with its descriptive metadata refreshed; <see cref="CommitBindingAsync"/> writes it
    /// and only then makes it live, so a failed write never leaves memory claiming otherwise. A
    /// binding whose file is unreadable is replaced by a fresh one (WriteBindingAsync keeps a
    /// recovery copy of the damaged file first). Null for a newer-version binding, which this
    /// build never writes.
    /// </summary>
    private CharacterBinding? PrepareBindingForWrite(CharacterContext character, DateTime now)
    {
        lock (gate)
        {
            if (newerVersionBindings.Contains(character.ContentId))
            {
                log.Warning("AetherFrame left a character's Plate settings unchanged: they were saved by a newer version.");
                return null;
            }

            var binding = bindings.TryGetValue(character.ContentId, out var existing)
                ? existing.Clone()
                : new CharacterBinding { ContentId = character.ContentId, CreatedAtUtc = now };

            if (!string.IsNullOrWhiteSpace(character.Name))
            {
                binding.LastKnownCharacterName = character.Name;
            }

            if (!string.IsNullOrWhiteSpace(character.HomeWorld))
            {
                binding.LastKnownHomeWorld = character.HomeWorld;
            }

            binding.Version = CharacterBinding.CurrentVersion;
            binding.UpdatedAtUtc = now;
            return binding;
        }
    }

    /// <summary>Writes a binding prepared by <see cref="PrepareBindingForWrite"/>, then makes it live.</summary>
    private async Task CommitBindingAsync(CharacterBinding binding)
    {
        await WriteBindingAsync(binding).ConfigureAwait(false);
        lock (gate)
        {
            bindings[binding.ContentId] = binding;
            Changed();
        }
    }

    private async Task WritePlateAsync(Guid plateId, JsonObject raw) =>
        await store.WriteTextAsync(paths.GetPlatePath(plateId), VersionedJson.Serialize(raw)).ConfigureAwait(false);

    private async Task WriteBindingAsync(CharacterBinding binding)
    {
        string json;
        bool replacesDamaged;
        lock (gate)
        {
            binding.Version = CharacterBinding.CurrentVersion;
            json = VersionedJson.Serialize(binding);
            replacesDamaged = unreadableBindings.Contains(binding.ContentId);
        }

        var path = paths.GetBindingPath(binding.ContentId);
        if (replacesDamaged)
        {
            // Throws (aborting the write) if the damaged file can't be preserved first.
            PreserveDamagedFile(path);
            lock (gate)
            {
                unreadableBindings.Remove(binding.ContentId);
            }
        }

        await store.WriteTextAsync(path, json).ConfigureAwait(false);
    }

    private async Task WriteLibraryAsync()
    {
        string json;
        lock (gate)
        {
            if (!libraryWritable)
            {
                return;
            }

            library.Version = PlateLibraryState.CurrentVersion;
            json = VersionedJson.Serialize(library);
        }

        await store.WriteTextAsync(paths.LibraryFile, json).ConfigureAwait(false);
    }

    /// <summary>Copies a damaged file into Recovery before anything is written over it.</summary>
    private void PreserveDamagedFile(string path)
    {
        if (!store.FileExists(path))
        {
            return;
        }

        var destination = paths.GetRecoveryPath(path, utcNow());
        store.CopyFile(path, destination);
        log.Warning($"AetherFrame kept a copy of damaged file {Path.GetFileName(path)} at {destination}.");
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private string ResolveNewName(string? requested, string fallback)
    {
        if (requested is not null)
        {
            return PlateNaming.TryNormalizeName(requested, out var normalized, out var error)
                ? normalized
                : throw new PlateLibraryException(error!);
        }

        return PlateNaming.MakeUniqueName(fallback, plates.Values.Select(p => p.Name));
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private List<(Guid Id, DateTime CreatedUtc)> ExistingPlatesLocked() =>
        plates.Values.Select(p => (p.Id, p.CreatedUtc)).ToList();

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private IReadOnlyList<PlateSummary> BuildOrderedSummariesLocked()
    {
        var namesByPlate = new Dictionary<Guid, List<string>>();
        var activeFor = new Dictionary<Guid, List<ulong>>();
        foreach (var binding in bindings.Values)
        {
            if (binding.ActivePlateId is { } activeId)
            {
                if (!activeFor.TryGetValue(activeId, out var contentIds))
                {
                    activeFor[activeId] = contentIds = new List<ulong>();
                }

                contentIds.Add(binding.ContentId);
            }

            if (string.IsNullOrWhiteSpace(binding.LastKnownCharacterName))
            {
                continue;
            }

            foreach (var id in binding.PlateIds)
            {
                if (!namesByPlate.TryGetValue(id, out var names))
                {
                    namesByPlate[id] = names = new List<string>();
                }

                names.Add(binding.LastKnownCharacterName);
            }
        }

        var order = PlateOrdering.ResolveDisplayOrder(library.OrderedPlateIds, ExistingPlatesLocked());
        return order.Select(id =>
        {
            var record = plates[id];
            return new PlateSummary(record.Id, record.Status, record.Name, record.CreatedUtc, record.ModifiedUtc, record.Revision, record.Problem,
                namesByPlate.TryGetValue(id, out var names) ? names : [],
                activeFor.TryGetValue(id, out var active) ? active : []);
        }).ToList();
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private void Changed()
    {
        generation++;
        orderedSummaries = null;
    }

    private static PlateRecord ReadyRecord(Guid plateId, JsonObject raw)
    {
        var rawJson = VersionedJson.Serialize(raw);
        var document = PlateDocuments.Materialize(ParseObject(rawJson));
        return new PlateRecord(plateId, PlateStatus.Ready, rawJson, document, document.Name, document.CreatedAtUtc, document.UpdatedAtUtc, document.Revision,
            document.Version, null);
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Plate JSON is not an object.");

    private static List<Guid>? ReadNewerOrder(JsonObject raw)
    {
        try
        {
            return raw[nameof(PlateLibraryState.OrderedPlateIds)]?.Deserialize<List<Guid>>(JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// One Plate as loaded. Immutable apart from <see cref="Preview"/>'s Name on rename: the saved
    /// JSON is kept as a string (safe to read from any thread, unlike a lazily-built JsonObject)
    /// and every change replaces the record.
    /// </summary>
    private sealed record PlateRecord(
        Guid Id,
        PlateStatus Status,
        string? RawJson,
        ProfileDocument? Preview,
        string Name,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        int Revision,
        int SchemaVersion,
        string? Problem);
}
