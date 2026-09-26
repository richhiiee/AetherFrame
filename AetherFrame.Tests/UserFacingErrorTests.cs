using System;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A failure's own text can name local paths (and with them the Windows account name): the player
/// only ever sees AetherFrame's own refusals, or a plain description.
/// </summary>
public class UserFacingErrorTests
{
    private const string Fallback = "The image couldn't be added.";

    public static TheoryData<Exception> PathBearingFailures => new()
    {
        new IOException(@"The process cannot access the file 'C:\Users\Someone\Pictures\art.png' because it is being used by another process."),
        new UnauthorizedAccessException(@"Access to the path 'C:\Users\Someone\AppData\Roaming\XIVLauncher\pluginConfigs\AetherFrame' is denied."),
        new FileNotFoundException("Could not find file '/home/someone/art.png'."),
        new DirectoryNotFoundException(@"Could not find a part of the path '\\nas\Someone\art.png'."),
        new InvalidOperationException(@"Refusing C:\Users\Someone\art.png"),
        new ObjectDisposedException(@"C:\Users\Someone\art.png"),
        new Exception(@"Something failed at C:/Users/Someone/art.png"),
    };

    [Theory]
    [MemberData(nameof(PathBearingFailures))]
    public void FailureText_NamingAPath_IsNeverShown(Exception failure)
    {
        var shown = UserFacingError.Describe(failure, Fallback);

        Assert.StartsWith(Fallback, shown);
        Assert.DoesNotContain("Someone", shown, StringComparison.OrdinalIgnoreCase);
        Assert.False(UserFacingError.ContainsPath(shown));
    }

    [Fact]
    public void AetherFramesOwnRefusals_AreShownAsWritten()
    {
        Assert.Equal("That Plate no longer exists.", UserFacingError.Describe(new PlateLibraryException("That Plate no longer exists."), Fallback));
        Assert.Equal("Templates are still loading.", UserFacingError.Describe(new TemplateLibraryException("Templates are still loading."), Fallback));
        Assert.Equal("The image is too large. The limit is 32 MB.", UserFacingError.Describe(new InvalidOperationException("The image is too large. The limit is 32 MB."), Fallback));
    }

    [Fact]
    public async Task ImageImport_ThatFailsOnDisk_ShowsNoPath()
    {
        using var harness = await BasicHarness.NewClassicAsync();
        var source = TestImages.Write(Path.Combine(harness.Fixture.Root, "source"), "art.png", TestImages.Png(4, 4));

        // The managed image folder can't be created (a file is in the way): an IOException naming it.
        Directory.CreateDirectory(Path.GetDirectoryName(harness.Fixture.Paths.AssetsDirectory)!);
        if (Directory.Exists(harness.Fixture.Paths.AssetsDirectory))
        {
            Directory.Delete(harness.Fixture.Paths.AssetsDirectory, recursive: true);
        }

        File.WriteAllText(harness.Fixture.Paths.AssetsDirectory, "in the way");

        harness.Session.AddImageElement(source);

        Assert.NotNull(harness.Session.ErrorMessage);
        Assert.StartsWith(EditorSession.ImageImportFailedMessage, harness.Session.ErrorMessage);
        Assert.DoesNotContain(harness.Fixture.Root, harness.Session.ErrorMessage);
    }

    [Fact]
    public async Task SaveThatFailsOnDisk_ShowsNoPath_AndIsStillLogged()
    {
        var store = new FaultInjectingStore();
        using var fixture = new LibraryFixture(store);
        var library = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, Characters.Alice);
        var profiles = new ProfileService(library);
        profiles.OpenPlate(created.PlateId);
        var assets = new AssetStorageService(fixture.Paths.AssetsDirectory, fixture.Paths.AssetStagingDirectory, new AssetMetadataStore(fixture.Paths.AssetMetadataDirectory));
        var session = new EditorSession(profiles, assets, new FakeImages(), fixture.Log, () => 1);
        session.SyncWithCurrentProfile();
        session.AddTextElement("Unsaved");

        store.FailWrite = _ => true;
        Assert.False(await session.SaveProfileAsync());

        Assert.StartsWith(EditorSession.SaveFailedMessage, session.ErrorMessage);
        Assert.DoesNotContain(fixture.Root, session.ErrorMessage);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E AetherFrame failed to save", StringComparison.Ordinal));
        Assert.True(session.IsDirty);
    }
}
