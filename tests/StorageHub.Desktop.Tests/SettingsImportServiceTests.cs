using System.Text;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The gates an import runs before it is allowed to touch anything, and the safety net it leaves
/// behind when it does.
/// </summary>
public sealed class SettingsImportServiceTests : IDisposable
{
    private const string Password = "a good long password";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"storagehub-settings-import-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void APlainExportRoundTripsThroughAFile()
    {
        var fixture = Fixture();
        var path = Path.Combine(_directory, "plain.shsettings");

        SettingsExportService.Write(path, fixture.Exporter.Capture(Everything), password: null);

        Assert.Equal(SettingsFileKind.PlainText, SettingsImportService.Inspect(path).Kind);
        var result = SettingsImportService.Open(path, password: null);
        Assert.Equal(SettingsExportReadFailure.None, result.Failure);
        Assert.NotNull(result.Document!.DesktopGeneral);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void AProtectedExportNeedsItsPassword()
    {
        var fixture = Fixture();
        var path = Path.Combine(_directory, "sealed.shsettings");

        SettingsExportService.Write(path, fixture.Exporter.Capture(Everything), Password);

        Assert.Equal(SettingsFileKind.PasswordProtected, SettingsImportService.Inspect(path).Kind);
        Assert.Equal(SettingsExportReadFailure.None, SettingsImportService.Open(path, Password).Failure);

        var wrong = SettingsImportService.Open(path, "not the password");
        Assert.Null(wrong.Document);
        Assert.NotNull(wrong.Message);

        var missing = SettingsImportService.Open(path, password: null);
        Assert.Null(missing.Document);
        Assert.Contains("password", missing.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProtectedFileDoesNotContainItsSettingsInTheClear()
    {
        var fixture = Fixture();
        fixture.Store.Save(DesktopUpdatePreferences.Defaults with
        {
            ExternalEditorPath = @"C:\Tools\distinctive-editor-name.exe"
        });
        var path = Path.Combine(_directory, "sealed.shsettings");

        SettingsExportService.Write(
            path, fixture.Exporter.Capture(Everything), Password);

        Assert.DoesNotContain(
            "distinctive-editor-name",
            Encoding.Latin1.GetString(File.ReadAllBytes(path)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an export at all")]
    public void SomethingThatIsNotAnExportIsRefusedWithAnExplanation(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "foreign.shsettings");
        File.WriteAllText(path, content);

        var inspection = SettingsImportService.Inspect(path);

        if (content.Length == 0)
        {
            // Rejected from the directory entry, without the file ever being opened.
            Assert.Equal(SettingsFileKind.Unreadable, inspection.Kind);
            Assert.NotNull(inspection.Message);
            return;
        }

        Assert.Equal(SettingsFileKind.PlainText, inspection.Kind);
        Assert.Equal(SettingsExportReadFailure.NotAnExport, SettingsImportService.Open(path, null).Failure);
    }

    [Fact]
    public void AMissingFileIsReportedRatherThanThrowing()
    {
        var inspection = SettingsImportService.Inspect(
            Path.Combine(_directory, "absent.shsettings"));

        Assert.Equal(SettingsFileKind.Unreadable, inspection.Kind);
        Assert.NotNull(inspection.Message);
    }

    [Fact]
    public void ImportingWritesABackupThatCanItselfBeImported()
    {
        // The way back from an import the user regrets, and the reason the backup is produced by
        // the exporter rather than by a copy.
        var fixture = Fixture();
        fixture.Store.Save(DesktopUpdatePreferences.Defaults with { Appearance = DesktopAppearance.Dark });
        var document = SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub", null) with
        {
            DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(
                DesktopUpdatePreferences.Defaults with { Appearance = DesktopAppearance.Light })
        };

        var report = fixture.Importer.Apply(document, [SettingsSectionId.DesktopGeneral]);

        Assert.True(report.Succeeded);
        Assert.Equal(DesktopAppearance.Light, fixture.Store.Load().Appearance);
        Assert.NotNull(report.BackupPath);

        var restored = SettingsImportService.Open(report.BackupPath!, null);
        Assert.Equal(SettingsExportReadFailure.None, restored.Failure);
        Assert.Equal(DesktopAppearance.Dark, restored.Document!.DesktopGeneral!.Appearance);
    }

    [Fact]
    public void ImportingNothingWritesNoBackupAndChangesNoSettings()
    {
        var fixture = Fixture();
        fixture.Store.Save(DesktopUpdatePreferences.Defaults with { Appearance = DesktopAppearance.Dark });

        var report = fixture.Importer.Apply(
            SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub", null), Everything);

        Assert.True(report.Succeeded);
        Assert.Null(report.BackupPath);
        Assert.Empty(report.Applied);
        Assert.Equal(DesktopAppearance.Dark, fixture.Store.Load().Appearance);
    }

    [Fact]
    public void OnlyTheNewestBackupsAreKept()
    {
        var fixture = Fixture();
        var expected = SettingsImportService.MaximumBackups;

        for (var index = 0; index < expected + 4; index++)
        {
            Assert.NotNull(fixture.Importer.TryWriteBackup());
        }

        Assert.Equal(
            expected,
            Directory.GetFiles(fixture.BackupDirectory, "settings-import-*.shsettings").Length);
    }

    [Fact]
    public void AChangedConcurrencyValueIsReportedBecauseTheAgentOnlyReadsItAtStartup()
    {
        var fixture = Fixture();
        var document = SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub", null) with
        {
            DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(
                DesktopUpdatePreferences.Defaults with { MaximumTransferConcurrency = 7 })
        };

        var report = fixture.Importer.Apply(document, [SettingsSectionId.DesktopGeneral]);

        Assert.True(report.ConcurrencyChanged);
        Assert.False(fixture.Importer.Apply(document, [SettingsSectionId.DesktopGeneral]).ConcurrencyChanged);
    }

    [Fact]
    public void AnExportPathMustBeAbsolute()
    {
        var fixture = Fixture();

        _ = Assert.Throws<ArgumentException>(() => SettingsExportService.Write(
            "settings.shsettings", fixture.Exporter.Capture(Everything), null));
    }

    [Fact]
    public void ExportingOnlyOneSectionLeavesTheOthersOutOfTheFile()
    {
        var fixture = Fixture();

        var document = fixture.Exporter.Capture([SettingsSectionId.Shortcuts]);

        Assert.Equal([SettingsSectionId.Shortcuts], document.PresentSections());
    }

    [Fact]
    public void ExportingSchedulesAlsoCarriesWhatTheyDependOn()
    {
        var fixture = Fixture();

        // Agent sections are not captured yet, but the dependency expansion is what guarantees a
        // file never describes a schedule whose sync task it omitted.
        var expanded = SettingsSectionCatalog.ExpandForExport([SettingsSectionId.Schedules]);

        Assert.Contains(SettingsSectionId.Connections, expanded);
        Assert.NotNull(fixture.Exporter.Capture([SettingsSectionId.Schedules]));
    }

    private static IReadOnlyCollection<SettingsSectionId> Everything => [.. Enum.GetValues<SettingsSectionId>()];

    private ImportFixture Fixture()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var store = new DesktopUpdatePreferencesStore(settingsPath);
        var exporter = new SettingsExportService(
            store,
            clock: () => DateTimeOffset.UnixEpoch,
            application: () => "StorageHub test",
            fingerprint: () => "test-fingerprint");
        var backups = SettingsImportService.DefaultBackupDirectory(settingsPath);
        return new ImportFixture(
            store,
            exporter,
            new SettingsImportService(store, exporter, backups, NextTimestamp),
            backups);
    }

    private int _tick;

    /// <summary>
    /// Backups are named by timestamp and pruned by name, so the fixture advances a second per
    /// call rather than relying on the wall clock to tick between writes.
    /// </summary>
    private DateTimeOffset NextTimestamp() => DateTimeOffset.UnixEpoch.AddSeconds(_tick++);

    private sealed record ImportFixture(
        DesktopUpdatePreferencesStore Store,
        SettingsExportService Exporter,
        SettingsImportService Importer,
        string BackupDirectory);
}
