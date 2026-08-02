using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using StorageHub.Persistence.Connections;
using StorageHub.Persistence.Trust;
using StorageHub.Security;
using DomainProfileWriteStatus = StorageHub.Application.Connections.ConnectionProfileWriteStatus;

namespace StorageHub.Agent.Windows.Tests;

public sealed class ConnectionTrustIpcCommandServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-trust-ipc-{Guid.NewGuid():N}");
    private SqliteDatabaseOptions _options = null!;
    private SqliteConnectionProfileRepository _profiles = null!;
    private SqliteTrustStore _trust = null!;
    private ConnectionTrustIpcCommandService _service = null!;

    public async Task InitializeAsync()
    {
        _options = new SqliteDatabaseOptions(Path.Combine(_root, "storagehub.db"), pooling: false);
        Assert.True((await new StorageHubDatabaseInitializer(_options).InitializeAsync()).IsReady);
        _profiles = new SqliteConnectionProfileRepository(_options, new FixedTimeProvider(Now));
        _trust = new SqliteTrustStore(new SingleWriterSqliteDatabase(_options));
        _service = new ConnectionTrustIpcCommandService(_profiles, _trust, new FixedTimeProvider(Now));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrollRejectAndRolloverRoundTripThroughAuthoritativeStore(bool useFtps)
    {
        var profile = await CreatePinnedProfileAsync(useFtps);
        var firstFingerprint = new string('A', 64);
        var enrolled = await DecideAsync(profile, firstFingerprint, ConnectionTrustDecision.Trusted);

        Assert.Equal(ConnectionTrustMutationStatus.Succeeded, enrolled.Status);
        var first = Assert.Single(enrolled.Snapshot!.Records);
        Assert.Equal(ConnectionTrustDecision.Trusted, first.Decision);
        Assert.Equal(
            useFtps ? ConnectionTrustArtifactKind.TlsCertificate : ConnectionTrustArtifactKind.SshHostKey,
            enrolled.Snapshot.Target.ArtifactKind);

        var rejected = await DecideAsync(
            profile,
            firstFingerprint,
            ConnectionTrustDecision.Rejected,
            first.TrustId,
            first.Version);
        var rejectedRecord = Assert.Single(rejected.Snapshot!.Records);
        Assert.Equal(ConnectionTrustDecision.Rejected, rejectedRecord.Decision);

        var retrusted = await DecideAsync(
            profile,
            firstFingerprint,
            ConnectionTrustDecision.Trusted,
            rejectedRecord.TrustId,
            rejectedRecord.Version);
        var active = Assert.Single(retrusted.Snapshot!.Records);
        var rollover = await RolloverAsync(profile, active, new string('B', 64));

        Assert.Equal(ConnectionTrustMutationStatus.Succeeded, rollover.Status);
        Assert.Equal(2, rollover.Snapshot!.Records.Length);
        Assert.Equal(ConnectionTrustDecision.Revoked,
            rollover.Snapshot.Records.Single(record => record.TrustId == active.TrustId).Decision);
        var replacement = rollover.Snapshot.Records.Single(record =>
            record.Decision == ConnectionTrustDecision.Trusted);
        Assert.Equal(new string('B', 64), replacement.Sha256Fingerprint);
        Assert.Equal(firstFingerprint, replacement.PreviousFingerprint);
    }

    [Fact]
    public async Task StaleProfileRevisionCannotEnrollTrustForChangedEndpoint()
    {
        var profile = await CreatePinnedProfileAsync(useFtps: false);
        var response = await SendMutationAsync(
            ConnectionTrustIpcMessageTypes.DecideRequest,
            new ConnectionTrustDecisionRequest(
                ConnectionTrustIpcContract.CurrentVersion,
                profile.Id.Value,
                ExpectedProfileVersion: profile.Version + 1,
                new string('A', 64),
                ConnectionTrustDecision.Trusted));

        Assert.Equal(ConnectionTrustMutationStatus.VersionConflict, response.Status);
        Assert.Empty(await _trust.FindAsync(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
    }

    [Fact]
    public async Task SystemTrustProfileCannotBeUsedToWriteArbitraryEndpointPins()
    {
        var profile = ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata("System FTPS"),
            new FtpsEndpoint(
                "ftps.example.test",
                21,
                FtpsTlsMode.Explicit,
                TlsCertificatePolicy.SystemTrust),
            new UsernamePasswordAuthentication("operator", SecretReference.Create()),
            Options(),
            Now);
        Assert.Equal(DomainProfileWriteStatus.Succeeded, (await _profiles.CreateAsync(profile)).Status);

        var response = await DecideAsync(profile, new string('A', 64), ConnectionTrustDecision.Trusted);

        Assert.Equal(ConnectionTrustMutationStatus.Unsupported, response.Status);
        Assert.Empty(await _trust.FindAsync(TrustArtifactKind.TlsCertificate, "ftps.example.test", 21));
    }

    [Fact]
    public async Task ExistingRecordIdCannotBeReboundToDifferentFingerprint()
    {
        var profile = await CreatePinnedProfileAsync(useFtps: false);
        var enrolled = await DecideAsync(profile, new string('A', 64), ConnectionTrustDecision.Trusted);
        var existing = Assert.Single(enrolled.Snapshot!.Records);

        var response = await DecideAsync(
            profile,
            new string('B', 64),
            ConnectionTrustDecision.Trusted,
            existing.TrustId,
            existing.Version);

        Assert.Equal(ConnectionTrustMutationStatus.ValidationFailed, response.Status);
        var stored = Assert.Single(await _trust.FindAsync(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        Assert.Equal(new string('A', 64), stored.Sha256Fingerprint);
    }

    [Fact]
    public async Task EquivalentFingerprintEncodingCannotCreateASecondRecordWithoutConcurrencyToken()
    {
        var profile = await CreatePinnedProfileAsync(useFtps: false);
        await DecideAsync(profile, new string('A', 64), ConnectionTrustDecision.Trusted);
        var alternateEncoding = "SHA256:" + Convert.ToBase64String(
            Convert.FromHexString(new string('A', 64))).TrimEnd('=');

        var response = await DecideAsync(profile, alternateEncoding, ConnectionTrustDecision.Rejected);

        Assert.Equal(ConnectionTrustMutationStatus.VersionConflict, response.Status);
        Assert.Single(await _trust.FindAsync(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
    }

    [Fact]
    public async Task RolloverRejectsStaleSourceWithoutAddingReplacement()
    {
        var profile = await CreatePinnedProfileAsync(useFtps: false);
        var enrolled = await DecideAsync(profile, new string('A', 64), ConnectionTrustDecision.Trusted);
        var current = Assert.Single(enrolled.Snapshot!.Records);

        var response = await SendMutationAsync(
            ConnectionTrustIpcMessageTypes.RolloverRequest,
            new ConnectionTrustRolloverRequest(
                ConnectionTrustIpcContract.CurrentVersion,
                profile.Id.Value,
                profile.Version,
                current.TrustId,
                ExpectedPreviousTrustVersion: current.Version + 1,
                new string('B', 64)));

        Assert.Equal(ConnectionTrustMutationStatus.VersionConflict, response.Status);
        var stored = Assert.Single(await _trust.FindAsync(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        Assert.Equal(TrustDecision.Trusted, stored.Decision);
    }

    private async Task<ConnectionProfile> CreatePinnedProfileAsync(bool useFtps)
    {
        ConnectionEndpoint endpoint = useFtps
            ? new FtpsEndpoint("ftps.example.test", 990, FtpsTlsMode.Implicit, TlsCertificatePolicy.Pinned)
            : new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned);
        var profile = ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata(useFtps ? "Pinned FTPS" : "Pinned SFTP"),
            endpoint,
            new UsernamePasswordAuthentication("operator", SecretReference.Create()),
            Options(),
            Now);
        Assert.Equal(DomainProfileWriteStatus.Succeeded, (await _profiles.CreateAsync(profile)).Status);
        return profile;
    }

    private Task<ConnectionTrustMutationResponse> DecideAsync(
        ConnectionProfile profile,
        string fingerprint,
        ConnectionTrustDecision decision,
        string? trustId = null,
        int? expectedTrustVersion = null) => SendMutationAsync(
        ConnectionTrustIpcMessageTypes.DecideRequest,
        new ConnectionTrustDecisionRequest(
            ConnectionTrustIpcContract.CurrentVersion,
            profile.Id.Value,
            profile.Version,
            fingerprint,
            decision,
            trustId,
            expectedTrustVersion));

    private Task<ConnectionTrustMutationResponse> RolloverAsync(
        ConnectionProfile profile,
        ConnectionTrustRecordDocument current,
        string replacementFingerprint) => SendMutationAsync(
        ConnectionTrustIpcMessageTypes.RolloverRequest,
        new ConnectionTrustRolloverRequest(
            ConnectionTrustIpcContract.CurrentVersion,
            profile.Id.Value,
            profile.Version,
            current.TrustId,
            current.Version,
            replacementFingerprint));

    private async Task<ConnectionTrustMutationResponse> SendMutationAsync<TRequest>(
        string messageType,
        TRequest payload)
        where TRequest : class
    {
        var result = await _service.HandleAsync(IpcEnvelope.Create(messageType, Guid.NewGuid(), 1, payload));
        return Assert.IsType<ConnectionTrustMutationResponse>(
            result.Payload.Deserialize<ConnectionTrustMutationResponse>());
    }

    private static ConnectionOperationalOptions Options() => new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        new ConnectionRetryPolicy(3, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5)),
        proxy: null,
        new ConnectionBandwidthLimits(null, null),
        "utf-8");

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
