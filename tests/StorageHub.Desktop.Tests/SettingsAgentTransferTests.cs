using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The agent half of an import: what happens to identifiers that no longer mean anything, to
/// credentials that could not travel, and to items that are already here.
/// </summary>
public sealed class SettingsAgentTransferTests
{
    [Fact]
    public async Task CapturingReadsEveryConnectionSyncTaskAndSchedule()
    {
        var agent = Agent();
        agent.Storage.Add("Bucket");
        agent.Storage.Add("Archive");

        var document = await SettingsAgentTransfer.CaptureAsync(
            Document(), [.. Enum.GetValues<SettingsSectionId>()], agent.Clients, default);

        Assert.Equal(2, document.Connections!.Count);
        Assert.NotNull(document.SyncProfiles);
        Assert.NotNull(document.Schedules);
    }

    [Fact]
    public async Task CapturingOnlyWhatWasAskedForLeavesTheRestAbsent()
    {
        var agent = Agent();
        agent.Storage.Add("Bucket");

        var document = await SettingsAgentTransfer.CaptureAsync(
            Document(), [SettingsSectionId.Connections], agent.Clients, default);

        Assert.NotNull(document.Connections);
        Assert.Null(document.SyncProfiles);
        Assert.Null(document.Schedules);
    }

    [Fact]
    public async Task AConnectionFromThisMachineKeepsItsCredentialsAndStaysEnabled()
    {
        // Restoring a backup in place: the vault references still resolve, so the connection is
        // usable the moment it lands.
        var agent = Agent();
        var document = Document() with
        {
            Connections = [new ConnectionExportEntry(Guid.NewGuid(), SftpDraft("Server"))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Connections], agent.Clients,
            SettingsConflictPolicy.Skip, sameMachine: true, default);

        Assert.Equal("Added", Assert.Single(result.Connections).Result);
        Assert.False(Assert.Single(result.Connections).NeedsCredentials);
        var created = Assert.Single(agent.Profiles.Created);
        Assert.True(created.IsEnabled);
        Assert.DoesNotContain(SettingsAgentTransfer.NeedsCredentialsTag, created.Metadata.Tags ?? []);
    }

    [Fact]
    public async Task AConnectionFromAnotherMachineArrivesDisabledAndTagged()
    {
        // Its references point into a vault this machine cannot read, and they cannot be blanked
        // because a profile without them fails validation. So it is brought in visibly incomplete
        // rather than looking ready to use.
        var agent = Agent();
        var document = Document() with
        {
            Connections = [new ConnectionExportEntry(Guid.NewGuid(), SftpDraft("Server"))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Connections], agent.Clients,
            SettingsConflictPolicy.Skip, sameMachine: false, default);

        Assert.True(Assert.Single(result.Connections).NeedsCredentials);
        var created = Assert.Single(agent.Profiles.Created);
        Assert.False(created.IsEnabled);
        Assert.Contains(SettingsAgentTransfer.NeedsCredentialsTag, created.Metadata.Tags ?? []);
    }

    [Fact]
    public async Task AConnectionThatNeedsNoCredentialsIsNeverDisabled()
    {
        var agent = Agent();
        var document = Document() with
        {
            Connections = [new ConnectionExportEntry(Guid.NewGuid(), LocalDraft("Downloads"))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Connections], agent.Clients,
            SettingsConflictPolicy.Skip, sameMachine: false, default);

        Assert.False(Assert.Single(result.Connections).NeedsCredentials);
        Assert.True(Assert.Single(agent.Profiles.Created).IsEnabled);
    }

    [Fact]
    public async Task ASkippedConnectionIsLeftAloneButStillFoundBySyncTasks()
    {
        // The subtle one: "skipped" means the connection is already here, so a sync task naming it
        // must be pointed at the copy that exists rather than orphaned.
        var agent = Agent();
        var local = agent.Storage.Add("Bucket");
        var exportedId = Guid.NewGuid();
        var document = Document() with
        {
            Connections = [new ConnectionExportEntry(exportedId, LocalDraft("Bucket"))],
            SyncProfiles = [new SyncProfileExportEntry(Guid.NewGuid(), SyncDraft("Nightly", exportedId, exportedId))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document,
            [SettingsSectionId.Connections, SettingsSectionId.SyncProfiles],
            agent.Clients,
            SettingsConflictPolicy.Skip,
            sameMachine: false,
            default);

        Assert.Contains("left alone", Assert.Single(result.Connections).Result, StringComparison.Ordinal);
        Assert.Empty(agent.Profiles.Created);
        Assert.Equal("Added", Assert.Single(result.SyncProfiles).Result);
        var created = Assert.Single(agent.Sync.Created);
        Assert.Equal(local, created.LeftConnectionId);
    }

    [Fact]
    public async Task ASyncTaskFollowsTheConnectionToItsNewIdentity()
    {
        // Creating a connection mints a fresh id on the agent, so the exported one is stale the
        // moment it lands.
        var agent = Agent();
        var exportedId = Guid.NewGuid();
        var document = Document() with
        {
            Connections = [new ConnectionExportEntry(exportedId, LocalDraft("Bucket"))],
            SyncProfiles = [new SyncProfileExportEntry(Guid.NewGuid(), SyncDraft("Nightly", exportedId, exportedId))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document,
            [SettingsSectionId.Connections, SettingsSectionId.SyncProfiles],
            agent.Clients,
            SettingsConflictPolicy.Skip,
            sameMachine: true,
            default);

        Assert.Equal("Added", Assert.Single(result.SyncProfiles).Result);
        var created = Assert.Single(agent.Sync.Created);
        Assert.NotEqual(exportedId, created.LeftConnectionId);
        Assert.Contains(agent.Storage.Ids, id => id == created.LeftConnectionId);
    }

    [Fact]
    public async Task ASyncTaskWhoseConnectionsAreMissingIsReportedNotCreatedBroken()
    {
        var agent = Agent();
        var document = Document() with
        {
            SyncProfiles = [new SyncProfileExportEntry(
                Guid.NewGuid(), SyncDraft("Nightly", Guid.NewGuid(), Guid.NewGuid()))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.SyncProfiles], agent.Clients,
            SettingsConflictPolicy.Skip, sameMachine: false, default);

        Assert.Contains("connections are not here", Assert.Single(result.SyncProfiles).Result, StringComparison.Ordinal);
        Assert.Empty(agent.Sync.Created);
    }

    [Fact]
    public async Task AScheduleNamingATimeZoneThisComputerLacksIsSkipped()
    {
        // It would be accepted and then never fire, which is worse than saying so.
        var agent = Agent();
        var profileId = agent.Sync.Add("Nightly");
        var document = Document() with
        {
            Schedules = [new ScheduleExportEntry(Guid.NewGuid(), new ScheduleDraftDocument(
                profileId, "0 0 * * *", "Mars/Olympus_Mons", 300, true, true))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Schedules], agent.Clients,
            SettingsConflictPolicy.Skip, sameMachine: true, default);

        Assert.Contains("time zone", Assert.Single(result.Schedules).Result, StringComparison.Ordinal);
        Assert.Empty(agent.Schedules.Created);
    }

    [Fact]
    public async Task AScheduleFollowsItsSyncTaskAndIsCreatedWhenTheZoneExists()
    {
        var agent = Agent();
        var profileId = agent.Sync.Add("Nightly");
        var document = Document() with
        {
            Schedules = [new ScheduleExportEntry(Guid.NewGuid(), new ScheduleDraftDocument(
                profileId, "0 0 * * *", TimeZoneInfo.Local.Id, 300, true, true))]
        };

        var result = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Schedules], agent.Clients,
            SettingsConflictPolicy.Skip, sameMachine: true, default);

        Assert.Equal("Added", Assert.Single(result.Schedules).Result);
        Assert.Equal(profileId, Assert.Single(agent.Schedules.Created).ProfileId);
    }

    [Fact]
    public async Task ReplacingOverwritesWhatIsHereAndCopyingKeepsBoth()
    {
        var agent = Agent();
        _ = agent.Storage.Add("Bucket");
        var document = Document() with
        {
            Connections = [new ConnectionExportEntry(Guid.NewGuid(), LocalDraft("Bucket"))]
        };

        var replaced = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Connections], agent.Clients,
            SettingsConflictPolicy.Replace, sameMachine: true, default);
        Assert.Equal("Replaced", Assert.Single(replaced.Connections).Result);
        Assert.Single(agent.Profiles.Updated);

        var copied = await SettingsAgentTransfer.ApplyAsync(
            document, [SettingsSectionId.Connections], agent.Clients,
            SettingsConflictPolicy.ImportAsCopy, sameMachine: true, default);
        Assert.Equal("Added", Assert.Single(copied.Connections).Result);
        Assert.Contains("(imported)", Assert.Single(agent.Profiles.Created).Metadata.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void ACopyNameTrimsTheNameNotTheSuffix()
    {
        var long_ = new string('n', ConnectionProfileIpcLimits.MaximumDisplayNameLength);

        var derived = SettingsAgentTransfer.FreeName(long_, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.EndsWith(" (imported)", derived, StringComparison.Ordinal);
        Assert.True(derived.Length <= ConnectionProfileIpcLimits.MaximumDisplayNameLength);
    }

    [Fact]
    public void ACopyNameKeepsCountingWhileNamesAreTaken()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bucket (imported)",
            "Bucket (imported 2)"
        };

        Assert.Equal("Bucket (imported 3)", SettingsAgentTransfer.FreeName("Bucket", taken));
    }

    private static SettingsExportDocument Document() =>
        SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub test", null);

    private static AgentFixture Agent()
    {
        var storage = new FakeSettingsStorageClient();
        var profiles = new FakeSettingsProfileClient(storage);
        var sync = new FakeSettingsSyncClient();
        var schedules = new FakeSettingsScheduleClient();
        return new AgentFixture(
            storage, profiles, sync, schedules,
            new SettingsAgentClients(storage, profiles, sync, schedules));
    }

    private static ConnectionProfileDraft LocalDraft(string name) => new(
        new ConnectionProfileMetadataDocument(name, "/", [], IsFavorite: false),
        new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: @"C:\data"),
        new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
        new ConnectionOperationalOptionsDocument());

    private static ConnectionProfileDraft SftpDraft(string name) => new(
        new ConnectionProfileMetadataDocument(name, "/", [], IsFavorite: false),
        new ConnectionEndpointDocument(StorageConnectionProvider.Sftp, Host: "example.test", Port: 22),
        new ConnectionAuthenticationDocument(
            ConnectionAuthenticationKind.UsernamePassword,
            Username: "user",
            PasswordReference: "shs_" + new string('p', 43)),
        new ConnectionOperationalOptionsDocument());

    private static SyncProfileDraftDocument SyncDraft(string name, Guid left, Guid right) => new(
        name, left, "/left", right, "/right",
        SyncIpcDirection.LeftToRight,
        SyncIpcDeletionMode.Disabled,
        SyncIpcConflictPolicy.Block,
        MaximumDeletionCount: 10,
        MaximumDeletionPercentage: 5m,
        Overwrite: true,
        TransferBufferSize: 65536,
        Enabled: true);

    private sealed record AgentFixture(
        FakeSettingsStorageClient Storage,
        FakeSettingsProfileClient Profiles,
        FakeSettingsSyncClient Sync,
        FakeSettingsScheduleClient Schedules,
        SettingsAgentClients Clients);
}
