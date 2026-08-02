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
