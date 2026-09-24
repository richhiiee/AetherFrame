using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;

namespace AetherFrame.Services.Templates;

/// <summary>
/// The local Template Library: Built-In Templates (never persisted — see
/// <see cref="BuiltInTemplateCatalog"/>) plus every Template the player saved. Deliberately
/// independent of Dalamud and game state, like <see cref="PlateLibraryService"/>, whose Plate
/// storage this depends on one-way for Save-as-Template and Use-Template — never the reverse.
///
/// <para><b>Sources of truth.</b> Which Templates exist is always the Template files on disk;
/// there is no separate ordering index (Templates have no manual reorder). A Template's embedded
/// document keeps <c>ProfileDocument</c>'s own schema/migration exactly as a Plate's does; this
/// envelope versions itself independently (see <c>TemplateDocuments</c>).</para>
///
/// <para><b>Threading.</b> Operations are serialized and run through the supplied dispatcher, like
/// <see cref="PlateLibraryService"/>. Query members are safe from any thread.</para>
/// </summary>
internal sealed class TemplateLibraryService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly PlateStoragePaths paths;
    private readonly IPlateFileStore store;
    private readonly PlateLibraryService plateLibrary;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;
    private readonly Func<Func<Task>, Task> dispatch;

    private readonly Dictionary<Guid, TemplateRecord> templates = new();

    private bool isLoaded;
    private int generation;
    private IReadOnlyList<TemplateSummary>? orderedSummaries;

    internal TemplateLibraryService(
        PlateStoragePaths paths,
        IPlateFileStore store,
        PlateLibraryService plateLibrary,
        IAetherFrameLog? log = null,
        Func<DateTime>? utcNow = null,
        Func<Func<Task>, Task>? dispatch = null)
    {
        this.paths = paths;
        this.store = store;
        this.plateLibrary = plateLibrary;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.dispatch = dispatch ?? (work => work());
    }

    internal bool IsLoaded
    {
        get { lock (gate) return isLoaded; }
    }

    /// <summary>Changes whenever anything visible in the Library changes (cheap cache key for UI).</summary>
    internal int Generation
    {
        get { lock (gate) return generation; }
    }

    // ---------------------------------------------------------------- queries (any thread)

    /// <summary>Built-in Templates first (catalog order), then user Templates newest-created-first.
    /// Cached until the next change.</summary>
    internal IReadOnlyList<TemplateSummary> GetOrderedTemplates()
    {
        lock (gate)
        {
            return orderedSummaries ??= BuildOrderedSummariesLocked();
        }
    }

    /// <summary>Case-insensitive search over Template names.</summary>
    internal IReadOnlyList<TemplateSummary> Search(string? query)
    {
        var all = GetOrderedTemplates();
        return string.IsNullOrWhiteSpace(query)
            ? all
            : all.Where(t => PlateSearch.Matches(query, t.DisplayName)).ToList();
    }

    internal TemplateSummary? FindTemplate(Guid templateId) => GetOrderedTemplates().FirstOrDefault(t => t.TemplateId == templateId);

    /// <summary>
    /// The saved state of a Template for read-only display (Preview, cards), or null when it
    /// can't be shown. Callers must never mutate it. For a built-in id, a fresh document is
    /// regenerated every call (<paramref name="starterForBuiltIn"/> only affects it); for a user
    /// Template, the exact content last saved. Never mutates anything.
    /// </summary>
    internal ProfileDocument? GetSavedDocument(Guid templateId, PlateStarterContent? starterForBuiltIn = null)
    {
        if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
        {
            return BuiltInTemplateCatalog.CreateDocument(templateId, utcNow(), starterForBuiltIn);
        }

        lock (gate)
        {
            return templates.TryGetValue(templateId, out var record) && record.Status == TemplateStatus.Ready
                ? record.Template!.Document
                : null;
        }
    }

    // ---------------------------------------------------------------- load

    /// <summary>
    /// Loads every saved Template. Safe to run on every startup. One unreadable file never
    /// prevents the rest from loading. Never writes anything (unlike Plates, Templates have no
    /// legacy format to migrate on disk and no index to rebuild).
    /// </summary>
    internal Task InitializeAsync() => RunExclusiveAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        var loaded = new List<TemplateRecord>();
        foreach (var path in store.ListFiles(paths.TemplatesDirectory, "*.json"))
        {
            if (!PlateStoragePaths.TryParseTemplateFileName(path, out var templateId))
            {
                log.Warning($"AetherFrame ignored a file in the Templates folder that isn't a Template: {Path.GetFileName(path)}");
                continue;
            }

            loaded.Add(await LoadTemplateAsync(path, templateId).ConfigureAwait(false));
        }

        lock (gate)
        {
            templates.Clear();
            foreach (var record in loaded)
            {
                templates[record.Id] = record;
            }

            isLoaded = true;
            Changed();
        }

        log.Information($"AetherFrame Template Library loaded: {loaded.Count} Template(s).");
    }

    private async Task<TemplateRecord> LoadTemplateAsync(string path, Guid templateId)
    {
        try
        {
            var result = await ReadTemplateFileAsync(path).ConfigureAwait(false);

            if (result.Status == TemplateStatus.NewerVersion)
            {
                var (name, created, modified) = TemplateDocuments.ReadDisplayFields(result.Raw!);
                log.Warning($"AetherFrame found a Template saved by a newer version ({templateId}); it is listed but won't be opened or changed.");
                return new TemplateRecord(templateId, TemplateStatus.NewerVersion, null, null, name ?? "Template",
                    created ?? DateTime.MinValue, modified ?? DateTime.MinValue, result.Problem);
            }

            // The file name is the Template's identity: a document whose own id disagrees (e.g. a
            // hand-copied file) is treated as the Template its file says, mirroring Plates.
            var template = result.Template!;
            if (template.TemplateId != templateId)
            {
                log.Warning($"AetherFrame Template file {templateId} declared a different id ({template.TemplateId}); using the file's id.");
                template.TemplateId = templateId;
                result.Raw![nameof(PlateTemplate.TemplateId)] = templateId;
            }

            var rawJson = VersionedJson.Serialize(result.Raw!);
            return new TemplateRecord(templateId, TemplateStatus.Ready, rawJson, template, template.Name, template.CreatedAtUtc, template.UpdatedAtUtc, null);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"AetherFrame could not read Template {templateId}; it is listed as unreadable and its file is left untouched.");
            return new TemplateRecord(templateId, TemplateStatus.Unreadable, null, null, "Unreadable Template", DateTime.MinValue, DateTime.MinValue,
                "This Template's file is damaged and couldn't be read. It has been left untouched.");
        }
    }

    /// <summary>
    /// Reads and migrates both axes of one Template file: the envelope (see
    /// <c>PersistenceSchemas.Template</c>) and, only if that's usable, the embedded document (see
    /// <c>PersistenceSchemas.ProfileDocument</c>). Either being newer than this build knows makes
    /// the whole Template <see cref="TemplateStatus.NewerVersion"/> — never opened, file left
    /// exactly as read. Invalid content throws inside the reader (matching
    /// <see cref="VersionedJson.ReadAsync{T}"/>'s convention) so a store with backups retries from
    /// its backup copy; a newer-version result deliberately does not throw, so it's never retried
    /// from a stale backup.
    /// </summary>
    private async Task<TemplateFileReadResult> ReadTemplateFileAsync(string path)
    {
        TemplateFileReadResult? result = null;

        await store.ReadTextAsync(path, text =>
        {
            if (JsonNode.Parse(text) is not JsonObject raw)
            {
                throw new InvalidDataException("Template is not a JSON object.");
            }

            var envelopeMigration = PersistenceSchemas.Template.Migrate(raw);
            if (envelopeMigration.Outcome == SchemaMigrationOutcome.NewerVersion)
            {
                result = new TemplateFileReadResult(raw, null, TemplateStatus.NewerVersion,
                    "This Template was saved by a newer version of AetherFrame. Update AetherFrame to open it.");
                return;
            }

            if (!envelopeMigration.IsUsable)
            {
                throw new InvalidDataException(envelopeMigration.Error ?? "Template is unreadable.");
            }

            if (raw[nameof(PlateTemplate.Document)] is not JsonObject documentRaw)
            {
                throw new InvalidDataException("Template has no embedded document.");
            }

            var documentMigration = PersistenceSchemas.ProfileDocument.Migrate(documentRaw);
            if (documentMigration.Outcome == SchemaMigrationOutcome.NewerVersion)
            {
                result = new TemplateFileReadResult(raw, null, TemplateStatus.NewerVersion,
                    "This Template's content was saved by a newer version of AetherFrame. Update AetherFrame to open it.");
                return;
            }

            if (!documentMigration.IsUsable)
            {
                throw new InvalidDataException(documentMigration.Error ?? "Template's content is unreadable.");
            }

            var template = TemplateDocuments.Materialize(raw);
            result = new TemplateFileReadResult(raw, template, TemplateStatus.Ready, null);
        }).ConfigureAwait(false);

        return result ?? throw new InvalidDataException("Template could not be read.");
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// Saves an independent Template from a Plate's last SAVED state (unsaved editor changes are
    /// never included). Always a brand-new Template (new id) — never overwrites an existing one,
    /// even by matching name. Scrubs the same identity fields <see cref="PlateDocuments.CreateDuplicate"/>
    /// already does for a duplicated Plate, including <see cref="ProfileDocument.OwnerContentId"/>.
    /// </summary>
    internal Task<Guid> SaveAsTemplateAsync(Guid sourcePlateId, string? requestedName) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var (sourceJson, plateName) = plateLibrary.GetSavedJsonForExport(sourcePlateId, "saved as a Template");

            var now = utcNow();
            string name;
            lock (gate)
            {
                name = requestedName is not null
                    ? (TemplateNaming.TryNormalizeName(requestedName, out var normalized, out var error) ? normalized : throw new TemplateLibraryException(error!))
                    : TemplateNaming.MakeUniqueName(plateName, templates.Values.Select(t => t.Name));
            }

            var embeddedRaw = PlateDocuments.CreateDuplicate(ParseObject(sourceJson), Guid.NewGuid(), name, now);
            var embeddedDocument = PlateDocuments.Materialize(embeddedRaw);

            var templateId = Guid.NewGuid();
            var template = new PlateTemplate
            {
                Version = PlateTemplate.CurrentSchemaVersion,
                TemplateId = templateId,
                Name = name,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Origin = new TemplateOrigin { Kind = TemplateOriginKind.SavedFromPlate },
                Document = embeddedDocument,
            };

            var raw = TemplateDocuments.ToJson(template);
            await WriteTemplateAsync(templateId, raw).ConfigureAwait(false);

            lock (gate)
            {
                templates[templateId] = ReadyRecord(templateId, raw);
                Changed();
            }

            log.Information($"AetherFrame saved Plate {sourcePlateId} as Template {templateId}.");
            return templateId;
        });

    /// <summary>Renames a Template: trimmed, non-empty, duplicates allowed. Throws for a built-in id.</summary>
    internal Task RenameTemplateAsync(Guid templateId, string? newName) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                throw new TemplateLibraryException("Built-in Templates can't be renamed.");
            }

            if (!TemplateNaming.TryNormalizeName(newName, out var name, out var error))
            {
                throw new TemplateLibraryException(error!);
            }

            var now = utcNow();
            JsonObject renamed;
            lock (gate)
            {
                var record = RequireReadyLocked(templateId, "renamed");
                renamed = ParseObject(record.RawJson!);
                TemplateDocuments.SetName(renamed, name, now);
            }

            await WriteTemplateAsync(templateId, renamed).ConfigureAwait(false);

            var updated = ReadyRecord(templateId, renamed);
            lock (gate)
            {
                if (templates.ContainsKey(templateId))
                {
                    templates[templateId] = updated;
                }

                Changed();
            }
        });

    /// <summary>
    /// An independent copy of a user Template under a new identity: fresh Template Guid, the
    /// source's creative content unchanged (same managed asset references — never duplicated
    /// bytes), never a Plate. Never mutates the original. Throws for a built-in id.
    /// </summary>
    internal Task<Guid> DuplicateTemplateAsync(Guid sourceTemplateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (BuiltInTemplateCatalog.IsBuiltIn(sourceTemplateId))
            {
                throw new TemplateLibraryException("Built-in Templates can't be duplicated.");
            }

            var now = utcNow();
            var newId = Guid.NewGuid();
            JsonObject copy;
            lock (gate)
            {
                var source = RequireReadyLocked(sourceTemplateId, "duplicated");
                var name = TemplateNaming.MakeCopyName(source.Name, templates.Values.Select(t => t.Name));
                copy = TemplateDocuments.CreateDuplicate(ParseObject(source.RawJson!), newId, name, now);
            }

            await WriteTemplateAsync(newId, copy).ConfigureAwait(false);

            lock (gate)
            {
                templates[newId] = ReadyRecord(newId, copy);
                Changed();
            }

            log.Information($"AetherFrame duplicated Template {sourceTemplateId} as {newId}.");
            return newId;
        });

    /// <summary>
    /// Deletes a user Template: moved to the Template trash (kept, never destroyed). Image assets
    /// are never touched — the same as deleting a Plate. Throws for a built-in id.
    /// </summary>
    internal Task DeleteTemplateAsync(Guid templateId) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                throw new TemplateLibraryException("Built-in Templates can't be deleted.");
            }

            lock (gate)
            {
                if (!templates.ContainsKey(templateId))
                {
                    throw new TemplateLibraryException("That Template no longer exists.");
                }
            }

            var now = utcNow();
            var path = paths.GetTemplatePath(templateId);
            if (store.FileExists(path))
            {
                store.MoveFile(path, paths.GetTrashTemplatePath(templateId, now));
            }

            lock (gate)
            {
                templates.Remove(templateId);
                Changed();
            }

            log.Information($"AetherFrame deleted Template {templateId} (moved to the Template trash).");
        });

    /// <summary>
    /// Use Template: creates a brand-new, independent Plate from a Template's saved content (a
    /// built-in's is regenerated fresh; a user Template's is exactly what was last saved). The
    /// Template itself — built-in or user — is only ever read here, never mutated. See
    /// <see cref="PlateLibraryService.CreatePlateFromTemplateAsync"/> for the guarantees this
    /// relies on (fresh Plate Guid, no character binding copied, existing Active-Plate rules
    /// unchanged, shared asset ids with no bytes duplicated).
    /// </summary>
    internal Task<PlateCreationResult> InstantiateAsync(Guid templateId, CharacterContext? character, PlateStarterContent? starterForBuiltIn = null) =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            JsonObject rawDocument;
            string name;

            if (BuiltInTemplateCatalog.IsBuiltIn(templateId))
            {
                var definition = BuiltInTemplateCatalog.Find(templateId)!;
                var document = BuiltInTemplateCatalog.CreateDocument(templateId, utcNow(), starterForBuiltIn);
                rawDocument = PlateDocuments.ToJson(document);
                name = definition.Name;
            }
            else
            {
                lock (gate)
                {
                    var record = RequireReadyLocked(templateId, "used");
                    rawDocument = PlateDocuments.ToJson(record.Template!.Document);
                    name = record.Name;
                }
            }

            return await plateLibrary.CreatePlateFromTemplateAsync(rawDocument, name, character).ConfigureAwait(false);
        });

    /// <summary>
    /// Every asset referenced by any Template — built-in (currently none, but scanned for
    /// symmetry) and user, including trashed ones (restorable, so their images stay protected).
    /// Incomplete — and so unusable for cleanup — if any Template can't be read. This is only HALF
    /// of what's actually live: see <c>LiveAssetReferences.ComputeAsync</c> for the authoritative
    /// union with <see cref="PlateLibraryService.ScanAssetReferencesAsync"/>.
    /// </summary>
    internal Task<AssetReferenceScan> ScanAssetReferencesAsync() =>
        RunExclusiveAsync(async () =>
        {
            RequireLoaded();

            var referenced = new HashSet<Guid>();
            var problems = new List<string>();

            foreach (var definition in BuiltInTemplateCatalog.All)
            {
                AssetReferenceScanner.Collect(BuiltInTemplateCatalog.CreateDocument(definition.TemplateId, utcNow(), null), referenced);
            }

            List<TemplateRecord> snapshot;
            lock (gate)
            {
                snapshot = templates.Values.ToList();
            }

            foreach (var record in snapshot)
            {
                if (record.Status == TemplateStatus.Ready)
                {
                    AssetReferenceScanner.Collect(record.Template!.Document, referenced);
                }
                else
                {
                    problems.Add($"Template {record.Id} is {record.Status}.");
                }
            }

            foreach (var path in store.ListFiles(paths.TemplateTrashDirectory, "*.json"))
            {
                try
                {
                    var result = await ReadTemplateFileAsync(path).ConfigureAwait(false);
                    if (result.Status == TemplateStatus.Ready)
                    {
                        AssetReferenceScanner.Collect(result.Template!.Document, referenced);
                    }
                    else
                    {
                        problems.Add($"Trashed Template {Path.GetFileName(path)} was saved by a newer version.");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"Trashed Template {Path.GetFileName(path)} is unreadable: {ex.Message}");
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
            throw new TemplateLibraryException("Templates are still loading.");
        }
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private TemplateRecord RequireReadyLocked(Guid templateId, string action)
    {
        if (!templates.TryGetValue(templateId, out var record))
        {
            throw new TemplateLibraryException("That Template no longer exists.");
        }

        return record.Status switch
        {
            TemplateStatus.Ready => record,
            TemplateStatus.NewerVersion => throw new TemplateLibraryException($"This Template was saved by a newer version of AetherFrame and can't be {action}."),
            _ => throw new TemplateLibraryException($"This Template's file is damaged and it can't be {action}."),
        };
    }

    private async Task WriteTemplateAsync(Guid templateId, JsonObject raw) =>
        await store.WriteTextAsync(paths.GetTemplatePath(templateId), VersionedJson.Serialize(raw)).ConfigureAwait(false);

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private IReadOnlyList<TemplateSummary> BuildOrderedSummariesLocked()
    {
        var builtIns = BuiltInTemplateCatalog.All.Select(d =>
            new TemplateSummary(d.TemplateId, TemplateKind.BuiltIn, TemplateStatus.Ready, d.Name, DateTime.MinValue, DateTime.MinValue, null, false, d.SupportsPreview));

        var userTemplates = templates.Values
            .OrderByDescending(r => r.CreatedUtc)
            .ThenBy(r => r.Id)
            .Select(r => new TemplateSummary(r.Id, TemplateKind.UserSaved, r.Status, r.Name, r.CreatedUtc, r.ModifiedUtc, r.Problem,
                r.Template?.Document.HasUnsupportedElements ?? false, SupportsPreview: true));

        return builtIns.Concat(userTemplates).ToList();
    }

    /// <summary>Must hold <see cref="gate"/>.</summary>
    private void Changed()
    {
        generation++;
        orderedSummaries = null;
    }

    private static TemplateRecord ReadyRecord(Guid templateId, JsonObject raw)
    {
        var rawJson = VersionedJson.Serialize(raw);
        var template = TemplateDocuments.Materialize(ParseObject(rawJson));
        return new TemplateRecord(templateId, TemplateStatus.Ready, rawJson, template, template.Name, template.CreatedAtUtc, template.UpdatedAtUtc, null);
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Template JSON is not an object.");

    private readonly record struct TemplateFileReadResult(JsonObject? Raw, PlateTemplate? Template, TemplateStatus Status, string? Problem);

    /// <summary>
    /// One Template as loaded. Immutable apart from replacement on rename: the saved JSON is kept
    /// as a string (safe to read from any thread) and every change replaces the record.
    /// </summary>
    private sealed record TemplateRecord(
        Guid Id,
        TemplateStatus Status,
        string? RawJson,
        PlateTemplate? Template,
        string Name,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        string? Problem);
}
