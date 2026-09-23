using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Characters;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

public class LegacyMigrationTests
{
    private const ulong Owner = 4242;

    [Fact]
    public async Task SingleProfileCharacter_BecomesPlateLibrary_WithThatPlateActive()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        fixture.WriteBindingJson(Owner, LegacyData.VersionOneBinding(Owner, profileId, profileId));

        var library = await fixture.LoadAsync();

        var plate = Assert.Single(library.GetOrderedPlates());
        Assert.Equal(profileId, plate.PlateId);
        Assert.Equal("Default", plate.DisplayName);
        Assert.Equal(PlateStatus.Ready, plate.Status);
        Assert.Equal(profileId, library.GetActivePlateId(Owner));
        Assert.Equal([profileId], fixture.ReadLibraryOrder());

        var binding = fixture.ReadBinding(Owner);
        Assert.Equal(CharacterBinding.CurrentVersion, binding.GetProperty("Version").GetInt32());
        Assert.Equal(profileId, binding.GetProperty("ActiveProfileId").GetGuid());
        Assert.Equal([profileId], binding.GetProperty("ProfileIds").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public async Task Migration_NeverRewritesDocuments()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        var original = LegacyData.VersionOneDocument(profileId, Owner);
        fixture.WritePlateJson(profileId, original);
        fixture.WriteBindingJson(Owner, LegacyData.VersionOneBinding(Owner, profileId, profileId));

        await fixture.LoadAsync();
        await fixture.LoadAsync();

        Assert.Equal(original, fixture.ReadPlateJson(profileId));
    }

    [Fact]
    public async Task Migration_PreservesEveryPieceOfCreativeContent()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));

        var library = await fixture.LoadAsync();
        var document = library.OpenDocumentForEditing(profileId);

        Assert.Equal(profileId, document.ProfileId);
        Assert.Equal(3, document.Revision);

        // The legacy canvas is resolved in memory exactly as before (no rescaling of content).
        Assert.Equal(ProfileDocument.LegacyCanvasWidth, document.CanvasWidth);
        Assert.Equal(ProfileDocument.LegacyCanvasHeight, document.CanvasHeight);

        var text = Assert.IsType<TextProfileElement>(document.Elements[0]);
        Assert.Equal("Hello from the past", text.Text);
        Assert.Equal(22f, text.FontSize);
        Assert.Equal(new Vector4(0.5f, 0.25f, 1f, 1f), text.Color);
        Assert.Equal(new Vector2(123.5f, 456.25f), text.Position);
        Assert.Equal(new Vector2(300f, 60f), text.Size);

        var image = Assert.IsType<ImageProfileElement>(document.Elements[1]);
        Assert.Equal(LegacyData.PortraitAsset, image.AssetId);
        Assert.Equal(ProfileElementRole.BasicPortrait, image.Role);
        Assert.Equal(new Vector2(10f, 20f), image.Position);

        Assert.Equal(ProfileBackgroundMode.Image, document.Background!.Mode);
        Assert.Equal(LegacyData.BackgroundAsset, document.Background.ImageAssetId);
        Assert.Equal(0.8f, document.Background.Opacity, 3);
    }

    [Fact]
    public async Task Migration_PreservesBasicIdentityMetadata()
    {
        using var fixture = new LibraryFixture();
        var plateId = Guid.NewGuid();
        var document = SampleDocuments.Rich(plateId, "With identity", fixture.Clock.Now);
        document.Version = 1;
        fixture.WritePlateJson(plateId, JsonSerializer.Serialize(document, JsonOptions.Default));

        var library = await fixture.LoadAsync();
        var loaded = library.OpenDocumentForEditing(plateId);

        Assert.Equal(42u, loaded.BasicIdentity!.GameTitleId);
        Assert.True(loaded.BasicIdentity.GameTitleIsPrefix);
        Assert.Equal(IdentityTitleLayout.Classic, loaded.BasicIdentity.Layout);
        Assert.Equal(new Vector2(12, 34), loaded.BasicIdentity.RegionPosition);
        Assert.Equal(560f, loaded.BasicIdentity.RegionWidth);
    }

    [Fact]
    public async Task Migration_IsIdempotent()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        fixture.WriteBindingJson(Owner, LegacyData.VersionOneBinding(Owner, profileId, profileId));

        await fixture.LoadAsync();
        var bindingAfterFirst = fixture.ReadBindingJson(Owner);
        var libraryAfterFirst = File.ReadAllText(fixture.Paths.LibraryFile);

        for (var i = 0; i < 3; i++)
        {
            fixture.Clock.Tick(60);
            var again = await fixture.LoadAsync();
            Assert.Single(again.GetOrderedPlates());
            Assert.Equal(profileId, again.GetActivePlateId(Owner));
        }

        Assert.Equal(bindingAfterFirst, fixture.ReadBindingJson(Owner));
        Assert.Equal(libraryAfterFirst, File.ReadAllText(fixture.Paths.LibraryFile));
        Assert.Single(Directory.GetFiles(fixture.Paths.CharactersDirectory));
        Assert.Single(Directory.GetFiles(fixture.Paths.PlatesDirectory));
    }

    [Fact]
    public async Task Migration_KeepsAnUntouchedCopyOfOldBindings()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        var legacyBinding = LegacyData.VersionOneBinding(Owner, profileId, profileId);
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        fixture.WriteBindingJson(Owner, legacyBinding);

        await fixture.LoadAsync();

        var backup = Path.Combine(fixture.Paths.MigrationBackupDirectory, "Characters", $"{Owner}.json");
        Assert.Equal(legacyBinding, File.ReadAllText(backup));
    }

    [Fact]
    public async Task AmbiguousActiveState_KeepsEveryPlate_AndLeavesActiveUnset()
    {
        using var fixture = new LibraryFixture();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fixture.WritePlateJson(a, LegacyData.VersionOneDocument(a, Owner, "A"));
        fixture.WritePlateJson(b, LegacyData.VersionOneDocument(b, Owner, "B"));
        fixture.WriteBindingJson(Owner, LegacyData.VersionOneBinding(Owner, null, a, b));

        var library = await fixture.LoadAsync();

        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.Null(library.GetActivePlateId(Owner));
        Assert.Equal([a, b], library.GetBinding(Owner)!.PlateIds);
        Assert.Equal(JsonValueKind.Null, fixture.ReadBinding(Owner).GetProperty("ActiveProfileId").ValueKind);
    }

    [Fact]
    public async Task ActiveProfileMissingFromList_IsAssociated_AndStaysActive()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        fixture.WriteBindingJson(Owner, LegacyData.VersionOneBinding(Owner, profileId));

        var library = await fixture.LoadAsync();

        Assert.Equal(profileId, library.GetActivePlateId(Owner));
        Assert.Equal([profileId], library.GetBinding(Owner)!.PlateIds);
    }

    [Fact]
    public async Task OrphanedLegacyDocument_IsAssociatedWithItsOwner_ButNotActive()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));

        var library = await fixture.LoadAsync();

        var binding = library.GetBinding(Owner);
        Assert.NotNull(binding);
        Assert.Equal([profileId], binding!.PlateIds);
        Assert.Null(binding.ActivePlateId);
        Assert.True(File.Exists(fixture.Paths.GetBindingPath(Owner)));
    }

    [Fact]
    public async Task MultipleCharacters_KeepTheirOwnActivePlates()
    {
        using var fixture = new LibraryFixture();
        var alices = Guid.NewGuid();
        var bobs = Guid.NewGuid();
        fixture.WritePlateJson(alices, LegacyData.VersionOneDocument(alices, 1));
        fixture.WritePlateJson(bobs, LegacyData.VersionOneDocument(bobs, 2));
        fixture.WriteBindingJson(1, LegacyData.VersionOneBinding(1, alices, alices));
        fixture.WriteBindingJson(2, LegacyData.VersionOneBinding(2, bobs, bobs));

        var library = await fixture.LoadAsync();

        Assert.Equal(alices, library.GetActivePlateId(1));
        Assert.Equal(bobs, library.GetActivePlateId(2));
        Assert.Equal(2, library.GetOrderedPlates().Count);
    }

    [Fact]
    public async Task MigratedLibrary_ListsNewestFirst()
    {
        using var fixture = new LibraryFixture();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        fixture.WritePlateJson(older, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, older, "Older", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), JsonOptions.Default));
        fixture.WritePlateJson(newer, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, newer, "Newer", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)), JsonOptions.Default));

        var library = await fixture.LoadAsync();

        Assert.Equal([newer, older], library.GetOrderedPlates().Select(p => p.PlateId));
    }

    [Fact]
    public async Task EmptyConfigDirectory_LoadsAnEmptyLibrary()
    {
        using var fixture = new LibraryFixture();

        var library = await fixture.LoadAsync();

        Assert.True(library.IsLoaded);
        Assert.Empty(library.GetOrderedPlates());
        Assert.Empty(fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task SavingAMigratedDocument_WritesTheCurrentSchema()
    {
        using var fixture = new LibraryFixture();
        var profileId = Guid.NewGuid();
        fixture.WritePlateJson(profileId, LegacyData.VersionOneDocument(profileId, Owner));
        var library = await fixture.LoadAsync();

        var document = library.OpenDocumentForEditing(profileId);
        await library.SavePlateDocumentAsync(document);

        var saved = JsonNode.Parse(fixture.ReadPlateJson(profileId))!.AsObject();
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, saved["Version"]!.GetValue<int>());
        Assert.False(saved.ContainsKey("BackgroundAssetId"));
        Assert.Equal(LegacyData.BackgroundAsset, saved["Background"]!["ImageAssetId"]!.GetValue<Guid>());
    }
}

public class SchemaMigrationTests
{
    [Fact]
    public void FutureVersion_IsReported_AndLeftUntouched()
    {
        var json = JsonNode.Parse("""{ "Version": 99, "Anything": [1, 2, 3] }""")!.AsObject();
        var before = json.ToJsonString();

        var result = PersistenceSchemas.ProfileDocument.Migrate(json);

        Assert.Equal(SchemaMigrationOutcome.NewerVersion, result.Outcome);
        Assert.Equal(99, result.OriginalVersion);
        Assert.Equal(before, json.ToJsonString());
    }

    [Fact]
    public void MissingVersion_MeansVersionOne()
    {
        var json = JsonNode.Parse("""{ "Name": "x" }""")!.AsObject();

        var result = PersistenceSchemas.ProfileDocument.Migrate(json);

        Assert.Equal(SchemaMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(1, result.OriginalVersion);
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, json["Version"]!.GetValue<int>());
    }

    [Fact]
    public void CurrentVersion_IsUnchanged()
    {
        var json = JsonNode.Parse($$"""{ "Version": {{ProfileDocument.CurrentSchemaVersion}} }""")!.AsObject();

        Assert.Equal(SchemaMigrationOutcome.Current, PersistenceSchemas.ProfileDocument.Migrate(json).Outcome);
    }

    [Fact]
    public void UnreadableVersion_IsInvalid()
    {
        var json = JsonNode.Parse("""{ "Version": "two" }""")!.AsObject();

        Assert.Equal(SchemaMigrationOutcome.Invalid, PersistenceSchemas.ProfileDocument.Migrate(json).Outcome);
    }

    [Fact]
    public void StepsRunInOrder_OnePerVersion()
    {
        var applied = new System.Collections.Generic.List<int>();
        var schema = new SchemaDefinition("Test", 4, 1, 1,
        [
            new SchemaMigrationStep(1, "one", j => { applied.Add(1); j["A"] = 1; }),
            new SchemaMigrationStep(2, "two", j => { applied.Add(2); j["B"] = j["A"]!.GetValue<int>() + 1; }),
            new SchemaMigrationStep(3, "three", j => { applied.Add(3); }),
        ]);
        var json = JsonNode.Parse("""{ "Version": 1, "Keep": "me" }""")!.AsObject();

        var result = schema.Migrate(json);

        Assert.Equal([1, 2, 3], applied);
        Assert.Equal(4, result.Version);
        Assert.Equal(2, json["B"]!.GetValue<int>());
        Assert.Equal("me", json["Keep"]!.GetValue<string>());
    }

    [Fact]
    public void MigrationChainWithAGap_FailsAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new SchemaDefinition("Broken", 3, 1, 1,
            [new SchemaMigrationStep(1, "one", _ => { })]));
    }

    [Fact]
    public void BindingV1ToV2_AddsActiveToAssociations_WithoutRemovingAnything()
    {
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        var json = JsonNode.Parse(LegacyData.VersionOneBinding(7, active, other))!.AsObject();

        var result = PersistenceSchemas.CharacterBinding.Migrate(json);

        Assert.Equal(SchemaMigrationOutcome.Migrated, result.Outcome);
        var ids = json["ProfileIds"]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>())).ToList();
        Assert.Equal([other, active], ids);
        Assert.Equal(active.ToString(), json["ActiveProfileId"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownProperties_SurviveALoadSaveRoundTrip()
    {
        var plateId = Guid.NewGuid();
        var json = JsonNode.Parse(JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "x", DateTime.UtcNow), JsonOptions.Default))!.AsObject();
        json["FutureFeature"] = new JsonObject { ["Enabled"] = true };

        var document = PlateDocuments.Materialize(json);
        var saved = PlateDocuments.ToJson(document);

        Assert.True(saved["FutureFeature"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void UnknownBindingProperties_SurviveARewrite()
    {
        var json = """{ "Version": 2, "ContentId": 5, "ProfileIds": [], "SomethingNew": 12 }""";
        var result = VersionedJson.Parse<CharacterBinding>(json, PersistenceSchemas.CharacterBinding);

        var rewritten = JsonNode.Parse(VersionedJson.Serialize(result.Value!.Clone()))!.AsObject();

        Assert.Equal(12, rewritten["SomethingNew"]!.GetValue<int>());
    }
}

public class FailureIsolationTests
{
    [Fact]
    public async Task OneUnreadablePlate_DoesNotAffectTheOthers_AndIsLeftUntouched()
    {
        using var fixture = new LibraryFixture();
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        fixture.WritePlateJson(good, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, good, "Good", fixture.Clock.Now), JsonOptions.Default));
        fixture.WritePlateJson(bad, "{ \"Version\": 2, \"Elements\": [ broken");

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(good)!.Status);
        Assert.Equal(PlateStatus.Unreadable, library.FindPlate(bad)!.Status);
        Assert.NotNull(library.FindPlate(bad)!.Problem);
        Assert.Throws<PlateLibraryException>(() => library.OpenDocumentForEditing(bad));
        Assert.Equal("{ \"Version\": 2, \"Elements\": [ broken", fixture.ReadPlateJson(bad));
        Assert.NotNull(library.OpenDocumentForEditing(good));
    }

    [Fact]
    public async Task NewerVersionPlate_IsListed_ButNeverOpenedOrRewritten()
    {
        using var fixture = new LibraryFixture();
        var future = Guid.NewGuid();
        var json = $$"""{ "Version": 99, "ProfileId": "{{future}}", "Name": "From the future", "Hologram": { "x": 1 } }""";
        fixture.WritePlateJson(future, json);

        var library = await fixture.LoadAsync();
        var plate = library.FindPlate(future)!;

        Assert.Equal(PlateStatus.NewerVersion, plate.Status);
        Assert.Equal("From the future", plate.DisplayName);
        Assert.Throws<PlateLibraryException>(() => library.OpenDocumentForEditing(future));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.RenamePlateAsync(future, "x"));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.DuplicatePlateAsync(future, null));
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, future));
        Assert.Null(library.GetSavedDocument(future));
        Assert.Equal(json, fixture.ReadPlateJson(future));
    }

    [Fact]
    public async Task NewerVersionPlate_IsNeverReplacedByAnOlderBackup()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        var path = fixture.Paths.GetPlatePath(plateId);
        store.Backups[path] = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Old backup", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(plateId, $$"""{ "Version": 50, "ProfileId": "{{plateId}}", "Name": "Newer" }""");

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.NewerVersion, library.FindPlate(plateId)!.Status);
        Assert.Equal("Newer", library.FindPlate(plateId)!.DisplayName);
    }

    [Fact]
    public async Task DamagedPlate_RecoversFromReliableStorageBackup()
    {
        var store = new BackupSimulatingStore();
        using var fixture = new LibraryFixture(store);
        var plateId = Guid.NewGuid();
        store.Backups[fixture.Paths.GetPlatePath(plateId)] = JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "From backup", fixture.Clock.Now), JsonOptions.Default);
        fixture.WritePlateJson(plateId, "{ truncated");

        var library = await fixture.LoadAsync();

        Assert.Equal(PlateStatus.Ready, library.FindPlate(plateId)!.Status);
        Assert.Equal("From backup", library.FindPlate(plateId)!.DisplayName);
    }

    [Fact]
    public async Task CorruptLibraryIndex_IsRebuiltFromPlates_AndKeptInRecovery()
    {
        using var fixture = new LibraryFixture();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fixture.WritePlateJson(a, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, a, "A", fixture.Clock.Now), JsonOptions.Default));
        fixture.WritePlateJson(b, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, b, "B", fixture.Clock.Now.AddDays(1)), JsonOptions.Default));
        fixture.WriteLibraryJson("{{{{ not an index");

        var library = await fixture.LoadAsync();

        Assert.Equal([b, a], library.GetOrderedPlates().Select(p => p.PlateId));
        Assert.Equal([b, a], fixture.ReadLibraryOrder());
        var preserved = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal("{{{{ not an index", File.ReadAllText(preserved));
    }

    [Fact]
    public async Task LostLibraryIndex_IsRebuiltFromPlates()
    {
        using var fixture = new LibraryFixture();
        var library = await fixture.LoadAsync();
        var a = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        fixture.Clock.Tick();
        var b = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        File.Delete(fixture.Paths.LibraryFile);

        var reloaded = await fixture.LoadAsync();

        Assert.Equal(2, reloaded.GetOrderedPlates().Count);
        Assert.Equal(a.PlateId, reloaded.GetActivePlateId(Characters.Alice.ContentId));
        Assert.Equal([b.PlateId, a.PlateId], fixture.ReadLibraryOrder());
    }

    [Fact]
    public async Task NewerVersionLibraryIndex_IsUsedReadOnly_AndNeverOverwritten()
    {
        using var fixture = new LibraryFixture();
        var a = Guid.NewGuid();
        fixture.WritePlateJson(a, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, a, "A", fixture.Clock.Now), JsonOptions.Default));
        var futureIndex = $$"""{ "Version": 9, "OrderedPlateIds": ["{{a}}"], "Folders": [] }""";
        fixture.WriteLibraryJson(futureIndex);

        var library = await fixture.LoadAsync();
        await library.CreatePlateAsync(PlateStartingLayout.Blank, null);

        Assert.Equal(2, library.GetOrderedPlates().Count);
        Assert.Equal(futureIndex, File.ReadAllText(fixture.Paths.LibraryFile));
    }

    [Fact]
    public async Task InvalidActivePlateId_IsIgnored_AndNothingIsDeleted()
    {
        using var fixture = new LibraryFixture();
        var real = Guid.NewGuid();
        var ghost = Guid.NewGuid();
        fixture.WritePlateJson(real, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, real, "Real", fixture.Clock.Now), JsonOptions.Default));
        fixture.WriteLibraryJson($$"""{ "Version": 1, "OrderedPlateIds": ["{{real}}"] }""");
        var binding = $$"""{ "Version": 2, "ContentId": 77, "ActiveProfileId": "{{ghost}}", "ProfileIds": ["{{ghost}}", "{{real}}"] }""";
        fixture.WriteBindingJson(77, binding);

        var library = await fixture.LoadAsync();

        Assert.Null(library.GetActivePlateId(77));
        Assert.Equal(ghost, library.GetBinding(77)!.ActivePlateId);
        Assert.Equal(binding, fixture.ReadBindingJson(77));
        Assert.True(File.Exists(fixture.Paths.GetPlatePath(real)));
    }

    [Fact]
    public async Task CorruptBinding_IsLeftAlone_UntilAWrite_ThenPreservedFirst()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteBindingJson(Characters.Alice.ContentId, "not json at all");
        var library = await fixture.LoadAsync();
        Assert.Equal("not json at all", fixture.ReadBindingJson(Characters.Alice.ContentId));

        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);

        Assert.Equal(plate.PlateId, library.GetActivePlateId(Characters.Alice.ContentId));
        var preserved = Assert.Single(Directory.GetFiles(fixture.Paths.RecoveryDirectory));
        Assert.Equal("not json at all", File.ReadAllText(preserved));
    }

    [Fact]
    public async Task NewerVersionBinding_IsNeverWritten()
    {
        using var fixture = new LibraryFixture();
        var future = """{ "Version": 7, "ContentId": 1001, "ActiveProfileId": null, "ProfileIds": [] }""";
        fixture.WriteBindingJson(Characters.Alice.ContentId, future);
        var library = await fixture.LoadAsync();

        var plate = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        await Assert.ThrowsAsync<PlateLibraryException>(() => library.SetActivePlateAsync(Characters.Alice, plate.PlateId));

        Assert.Equal(future, fixture.ReadBindingJson(Characters.Alice.ContentId));
        Assert.NotNull(library.FindPlate(plate.PlateId));
    }

    [Fact]
    public async Task PlateFileWithMismatchedId_UsesTheFileName()
    {
        using var fixture = new LibraryFixture();
        var fileId = Guid.NewGuid();
        var declared = Guid.NewGuid();
        fixture.WritePlateJson(fileId, JsonSerializer.Serialize(PlateFactory.Create(PlateStartingLayout.Blank, declared, "Copied by hand", fixture.Clock.Now), JsonOptions.Default));

        var library = await fixture.LoadAsync();
        var document = library.OpenDocumentForEditing(fileId);
        await library.SavePlateDocumentAsync(document);

        Assert.Equal(fileId, document.ProfileId);
        Assert.False(File.Exists(fixture.Paths.GetPlatePath(declared)));
    }

    [Fact]
    public async Task StrayFiles_InThePlatesFolder_AreIgnored()
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.Paths.PlatesDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.PlatesDirectory, "notes.json"), "{}");

        var library = await fixture.LoadAsync();

        Assert.Empty(library.GetOrderedPlates());
        Assert.True(File.Exists(Path.Combine(fixture.Paths.PlatesDirectory, "notes.json")));
    }
}
