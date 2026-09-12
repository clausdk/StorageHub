using System.Text;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The export payload is read from files users can edit, copy between machines, or pick by
/// mistake, so every refusal has to be a reported result rather than an exception.
/// </summary>
public sealed class SettingsExportSerializerTests
{
    [Fact]
    public void ADocumentRoundTripsThroughEverySection()
    {
        var document = Populated();
        var written = SettingsExportSerializer.Serialize(document);

        var restored = Read(written);

        // Compared as re-serialized bytes rather than as records: a record holding collections
        // compares those by reference, so record equality would pass while a dropped list element
        // went unnoticed. Re-serializing proves every property survived.
        Assert.Equal(
            Encoding.UTF8.GetString(written),
            Encoding.UTF8.GetString(SettingsExportSerializer.Serialize(restored)));
        Assert.Equal(
            Enum.GetValues<SettingsSectionId>().ToHashSet(),
            restored.PresentSections());
        Assert.Equal(document.MachineFingerprint, restored.MachineFingerprint);
        Assert.Equal(document.DesktopGeneral, restored.DesktopGeneral);
    }

    [Fact]
    public void ASectionIsPresentExactlyWhenItsPropertyIsSet()
    {
        var document = SettingsExportSerializer.Create(
            DateTimeOffset.UnixEpoch, "StorageHub 1.0.0", "fingerprint") with
        {
            Shortcuts = new Dictionary<string, Keys> { ["edit.copy"] = Keys.Control | Keys.C }
        };

        var restored = Read(SettingsExportSerializer.Serialize(document));

        Assert.Equal([SettingsSectionId.Shortcuts], restored.PresentSections());
        Assert.Null(restored.DesktopGeneral);
        Assert.Null(restored.Connections);
        Assert.Equal(1, restored.CountIn(SettingsSectionId.Shortcuts));
    }

    [Fact]
    public void AnEmptySectionIsStillPresentAndDistinctFromAnAbsentOne()
    {
        // "Export connections, of which I have none" must not read back as "connections were not
        // exported" — the import preview says different things about the two.
        var document = SettingsExportSerializer.Create(
            DateTimeOffset.UnixEpoch, "StorageHub 1.0.0", null) with
        {
            Connections = []
        };

        var restored = Read(SettingsExportSerializer.Serialize(document));

        Assert.Contains(SettingsSectionId.Connections, restored.PresentSections());
        Assert.Empty(restored.Connections!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"formatId":"something.else"}""")]
    [InlineData("""{"schemaVersion":0,"formatId":"storagehub.settings-export"}""")]
    public void SomethingThatIsNotAnExportIsReportedNotThrown(string content)
    {
        var result = SettingsExportSerializer.Read(Encoding.UTF8.GetBytes(content));

        Assert.Null(result.Document);
        Assert.Equal(SettingsExportReadFailure.NotAnExport, result.Failure);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void AFileFromANewerStorageHubSaysSoRatherThanBlamingTheFile()
    {
        var result = SettingsExportSerializer.Read(Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":{{SettingsExportSerializer.CurrentSchemaVersion + 1}},"formatId":"storagehub.settings-export"}"""));

        Assert.Equal(SettingsExportReadFailure.UnsupportedVersion, result.Failure);
        Assert.Contains("newer version", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APayloadLargerThanTheCapIsRefusedBeforeParsing()
    {
        var oversized = new byte[SettingsExportEnvelopeLimits.MaximumPayloadBytes + 1];

        var result = SettingsExportSerializer.Read(oversized);

        Assert.Equal(SettingsExportReadFailure.TooLarge, result.Failure);
    }

    [Fact]
    public void AFileClaimingMoreItemsThanTheAgentCouldReturnIsRefused()
    {
        // Refused on the count, before anything walks the list and spends a round trip per entry.
        var document = SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub", null) with
        {
            Connections = [.. Enumerable.Range(0, SettingsExportSerializer.MaximumConnections + 1)
                .Select(index => new ConnectionExportEntry(Guid.NewGuid(), Draft($"Connection {index}")))]
        };

        var result = SettingsExportSerializer.Read(SettingsExportSerializer.Serialize(document));

        Assert.Equal(SettingsExportReadFailure.TooManyItems, result.Failure);
    }

    [Fact]
    public void TheSerializedFormIsReadableJsonWithTheFormatIdInIt()
    {
        // The unencrypted form is meant to be diffable and reviewable; that is the whole reason
        // the password is optional.
        var json = Encoding.UTF8.GetString(SettingsExportSerializer.Serialize(Populated()));

        Assert.Contains(SettingsExportSerializer.FormatId, json, StringComparison.Ordinal);
        Assert.Contains("\n", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentSectionsAreOmittedRatherThanWrittenAsNull()
    {
        // Keeps a one-section export small and readable, and is what makes "absent" the default
        // when a future version adds a section older files cannot carry.
        var json = Encoding.UTF8.GetString(SettingsExportSerializer.Serialize(
            SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub", null)));

        Assert.DoesNotContain("connections", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedPayloadReadsBackIdenticallyToThePlainOne()
    {
        // The envelope is a pure wrapper, so both forms must yield the same document.
        var payload = SettingsExportSerializer.Serialize(Populated());

        var sealedPayload = StorageHub.Security.SettingsExportEnvelope.Protect(
            payload, "a good long password", StorageHub.Security.SettingsExportEnvelope.MinimumIterations);
        var opened = StorageHub.Security.SettingsExportEnvelope.Unprotect(sealedPayload, "a good long password");

        Assert.Equal(payload, opened);
        Assert.Equal(
            Encoding.UTF8.GetString(SettingsExportSerializer.Serialize(Read(payload))),
            Encoding.UTF8.GetString(SettingsExportSerializer.Serialize(Read(opened))));
    }

    private static SettingsExportDocument Read(ReadOnlySpan<byte> payload)
    {
        var result = SettingsExportSerializer.Read(payload);
        Assert.Equal(SettingsExportReadFailure.None, result.Failure);
        return Assert.IsType<SettingsExportDocument>(result.Document);
    }

    private static SettingsExportDocument Populated() => SettingsExportSerializer.Create(
        DateTimeOffset.UnixEpoch, "StorageHub 1.2.3", "fingerprint") with
    {
        DesktopGeneral = new DesktopGeneralSection(
            CheckAutomatically: false,
            DownloadAutomatically: true,
            RestartAutomatically: false,
            IncludePrereleases: true,
            SshHostKeyDiscoveryMode.Automatic,
            MaximumEditableFileBytes: 1024,
            WarnBeforeUnsafeExternalEdit: true,
            AdaptiveConcurrency: false,
            MinimumConcurrency: 2,
            MaximumTransferConcurrency: 6,
            PerConnectionConcurrency: 3,
            MaximumSyncConcurrency: 4,
            DesktopAppearance.Dark,
            WorkspaceLayout.TopAndBottom,
            new SshTerminalPreferences("screen-256color"),
            ReconnectRemotePanesAutomatically: false,
            ConfirmBeforeClearingTransferHistory: true,
            ConfirmBeforeDeletingItems: false,
            DefaultWorkspacePaneCount: 3),
        Shortcuts = new Dictionary<string, Keys> { ["edit.copy"] = Keys.Control | Keys.C },
        ConnectionDefaults = new Dictionary<string, string> { ["Sftp.port"] = "22" },
        MachineSpecific = new MachineSpecificSection(
            @"C:\Tools\editor.exe",
            [new WorkspaceShortcutEntry(@"C:\work\nightly.shw", "Nightly", DateTimeOffset.UnixEpoch)],
            [],
            new Dictionary<string, string> { ["Ssh.privateKeyReference"] = "shs_reference" }),
        Connections = [new ConnectionExportEntry(Guid.NewGuid(), Draft("Bucket"))],
        SyncProfiles = [new SyncProfileExportEntry(Guid.NewGuid(), SyncDraft())],
        Schedules = [new ScheduleExportEntry(Guid.NewGuid(), new ScheduleDraftDocument(
            Guid.NewGuid(), "0 0 * * *", "UTC", 300, QueueOneWhileRunning: true, Enabled: true))]
    };

    private static ConnectionProfileDraft Draft(string name) => new(
        new ConnectionProfileMetadataDocument(name, "/", [], IsFavorite: false),
        new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: @"C:\data"),
        new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
        new ConnectionOperationalOptionsDocument());

    private static SyncProfileDraftDocument SyncDraft() => new(
        "Nightly",
        Guid.NewGuid(),
        "/left",
        Guid.NewGuid(),
        "/right",
        SyncIpcDirection.LeftToRight,
        SyncIpcDeletionMode.Disabled,
        SyncIpcConflictPolicy.Block,
        MaximumDeletionCount: 10,
        MaximumDeletionPercentage: 5m,
        Overwrite: true,
        TransferBufferSize: 65536,
        Enabled: true);
}
