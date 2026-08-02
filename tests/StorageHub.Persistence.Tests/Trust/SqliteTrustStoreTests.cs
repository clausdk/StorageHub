using StorageHub.Persistence.Trust;
using StorageHub.Security;
using Xunit;

namespace StorageHub.Persistence.Tests.Trust;

public sealed class SqliteTrustStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-trust-{Guid.NewGuid():N}");
    private SqliteTrustStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = new SqliteDatabaseOptions(Path.Combine(_root, "storagehub.db"), pooling: false);
        var initialized = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialized.IsReady);
        _store = new SqliteTrustStore(new SingleWriterSqliteDatabase(options));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpsertFindUpdateAndRemovePreserveTrustHistoryMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var record = CreateRecord(now);
        await _store.UpsertAsync(record);

        var found = await _store.FindAsync(TrustArtifactKind.SshHostKey, "SFTP.Example.Test.", 22);

        var stored = Assert.Single(found);
        Assert.Equal("sftp.example.test", stored.CanonicalHost);
        Assert.Equal(record.Sha256Fingerprint, stored.Sha256Fingerprint);

        var revoked = stored with
        {
            Decision = TrustDecision.Revoked,
            LastSeenUtc = now.AddMinutes(1),
            PreviousFingerprint = stored.Sha256Fingerprint,
            Version = 2
        };
        await _store.UpsertAsync(revoked);

        stored = Assert.Single(await _store.FindAsync(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        Assert.Equal(TrustDecision.Revoked, stored.Decision);
        Assert.Equal(2, stored.Version);
        Assert.True(await _store.RemoveAsync(stored.TrustId, stored.Version));
        Assert.Empty(await _store.FindAsync(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
    }

    [Fact]
    public async Task StaleVersionFailsClosed()
    {
        var record = CreateRecord(DateTimeOffset.UtcNow);
        await _store.UpsertAsync(record);

        await Assert.ThrowsAsync<TrustRecordConcurrencyException>(
            async () => await _store.UpsertAsync(record with { Decision = TrustDecision.Rejected }));
    }

    [Fact]
    public async Task InvalidFingerprintIsRejectedBeforeDatabaseWrite()
    {
        var invalid = CreateRecord(DateTimeOffset.UtcNow) with { Sha256Fingerprint = "MD5:unsafe" };

        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.UpsertAsync(invalid));
    }

    [Fact]
    public async Task New_records_start_at_one_and_endpoint_identity_is_immutable()
    {
        var record = CreateRecord(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<TrustRecordConcurrencyException>(
            async () => await _store.UpsertAsync(record with { Version = 2 }));

        await _store.UpsertAsync(record);
        await Assert.ThrowsAsync<TrustRecordConcurrencyException>(async () => await _store.UpsertAsync(record with
        {
            CanonicalHost = "other.example.test",
            LastSeenUtc = record.LastSeenUtc.AddMinutes(1),
            Version = 2
        }));
    }

    [Fact]
    public async Task Expiry_and_removal_are_concurrency_checked()
    {
        var record = CreateRecord(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.UpsertAsync(record with
        {
            ExpiresUtc = record.FirstSeenUtc.AddSeconds(-1)
        }));

        await _store.UpsertAsync(record);
        Assert.False(await _store.RemoveAsync(record.TrustId, expectedVersion: 2));
        Assert.True(await _store.RemoveAsync(record.TrustId, expectedVersion: 1));
    }

    [Fact]
    public async Task RolloverAtomicallyRevokesOldFingerprintAndTrustsReplacement()
    {
        var now = DateTimeOffset.UtcNow;
        var current = CreateRecord(now);
        await _store.UpsertAsync(current);
        var replacement = CreateRecord(now.AddMinutes(1)) with
        {
            TrustId = $"trust-{Guid.NewGuid():N}",
            Sha256Fingerprint = new string('B', 64),
            PreviousFingerprint = current.Sha256Fingerprint
        };

        await _store.RolloverAsync(
            current with
            {
                Decision = TrustDecision.Revoked,
                LastSeenUtc = now.AddMinutes(1),
                Version = 2
            },
            replacement);

        var records = await _store.FindAsync(TrustArtifactKind.SshHostKey, current.CanonicalHost, current.Port);
        Assert.Equal(2, records.Count);
        Assert.Equal(TrustDecision.Revoked, records.Single(record => record.TrustId == current.TrustId).Decision);
        var trusted = records.Single(record => record.TrustId == replacement.TrustId);
        Assert.Equal(TrustDecision.Trusted, trusted.Decision);
        Assert.Equal(current.Sha256Fingerprint, trusted.PreviousFingerprint);
    }

    [Fact]
    public async Task ConflictingReplacementRollsBackRevocation()
    {
        var now = DateTimeOffset.UtcNow;
        var current = CreateRecord(now);
        var conflicting = CreateRecord(now.AddMinutes(1)) with
        {
            TrustId = $"trust-{Guid.NewGuid():N}",
            Sha256Fingerprint = new string('B', 64),
            Decision = TrustDecision.Rejected
        };
        await _store.UpsertAsync(current);
        await _store.UpsertAsync(conflicting);

        await Assert.ThrowsAsync<TrustRecordConcurrencyException>(async () => await _store.RolloverAsync(
            current with
            {
                Decision = TrustDecision.Revoked,
                LastSeenUtc = now.AddMinutes(2),
                Version = 2
            },
            conflicting with
            {
                TrustId = $"trust-{Guid.NewGuid():N}",
                Decision = TrustDecision.Trusted,
                FirstSeenUtc = now.AddMinutes(2),
                LastSeenUtc = now.AddMinutes(2),
                PreviousFingerprint = current.Sha256Fingerprint
            }));

        var records = await _store.FindAsync(TrustArtifactKind.SshHostKey, current.CanonicalHost, current.Port);
        Assert.Equal(TrustDecision.Trusted, records.Single(record => record.TrustId == current.TrustId).Decision);
        Assert.Equal(1, records.Single(record => record.TrustId == current.TrustId).Version);
    }

    [Fact]
    public async Task RolloverRejectsCrossEndpointReplacementBeforeWriting()
    {
        var now = DateTimeOffset.UtcNow;
        var current = CreateRecord(now);
        await _store.UpsertAsync(current);

        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.RolloverAsync(
            current with
            {
                Decision = TrustDecision.Revoked,
                LastSeenUtc = now.AddMinutes(1),
                Version = 2
            },
            CreateRecord(now.AddMinutes(1)) with
            {
                TrustId = $"trust-{Guid.NewGuid():N}",
                CanonicalHost = "attacker.example.test",
                Sha256Fingerprint = new string('B', 64),
                PreviousFingerprint = current.Sha256Fingerprint
            }));

        Assert.Equal(TrustDecision.Trusted, Assert.Single(
            await _store.FindAsync(TrustArtifactKind.SshHostKey, current.CanonicalHost, current.Port)).Decision);
    }

    [Fact]
    public async Task RejectedRecordCannotBeUsedAsRolloverSource()
    {
        var now = DateTimeOffset.UtcNow;
        var rejected = CreateRecord(now) with { Decision = TrustDecision.Rejected };
        await _store.UpsertAsync(rejected);

        await Assert.ThrowsAsync<TrustRecordConcurrencyException>(async () => await _store.RolloverAsync(
            rejected with
            {
                Decision = TrustDecision.Revoked,
                LastSeenUtc = now.AddMinutes(1),
                Version = 2
            },
            CreateRecord(now.AddMinutes(1)) with
            {
                TrustId = $"trust-{Guid.NewGuid():N}",
                Sha256Fingerprint = new string('B', 64),
                PreviousFingerprint = rejected.Sha256Fingerprint
            }));

        Assert.Equal(TrustDecision.Rejected, Assert.Single(
            await _store.FindAsync(TrustArtifactKind.SshHostKey, rejected.CanonicalHost, rejected.Port)).Decision);
    }

    private static TrustRecord CreateRecord(DateTimeOffset now) => new(
        $"trust-{Guid.NewGuid():N}",
        TrustArtifactKind.SshHostKey,
        "sftp.example.test",
        22,
        "ssh-ed25519",
        new string('A', 64),
        TrustDecision.Trusted,
        TrustDecisionSource.UserVerified,
        now,
        now,
        null,
        null,
        1);
}
