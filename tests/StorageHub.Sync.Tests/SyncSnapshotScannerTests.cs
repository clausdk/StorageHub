using System.Security.Cryptography;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Sync.Tests;

public sealed class SyncSnapshotScannerTests
{
    [Fact]
    public async Task Scan_paginates_each_directory_and_returns_root_relative_entries()
    {
        var profileId = ConnectionProfileId.New();
        const string rootIdentity = "fake-root";
        var root = SyncTestEntries.Address(profileId, rootIdentity, "base");
        await using var session = new FakeEndpointSession(
            profileId,
            rootIdentity,
            [
                SyncTestEntries.Directory(profileId, rootIdentity, "base/folder"),
                SyncTestEntries.File(profileId, rootIdentity, "base/a.txt", 1, "01"),
                SyncTestEntries.File(profileId, rootIdentity, "base/b.txt", 2, "02"),
                SyncTestEntries.File(profileId, rootIdentity, "base/folder/c.txt", 3, "03")
            ]);

        var result = await SyncSnapshotScanner.ScanAsync(
            session,
            root,
            new SyncSnapshotScanOptions(pageSize: 1, maximumEntries: 10));

        Assert.True(result.IsSuccess);
        Assert.Equal(["a.txt", "b.txt", "folder", "folder/c.txt"], result.Value.Entries.Keys);
        Assert.True(result.Value.Completeness.IsComplete);
        Assert.Equal(4, result.Value.Completeness.TotalItemCount);
    }

    [Fact]
    public async Task Scan_rejects_a_repeated_continuation_token()
    {
        var profileId = ConnectionProfileId.New();
        const string rootIdentity = "fake-root";
        var root = SyncTestEntries.Address(profileId, rootIdentity, string.Empty);
        await using var session = new FakeEndpointSession(
            profileId,
            rootIdentity,
            [
                SyncTestEntries.File(profileId, rootIdentity, "a.txt", 1),
                SyncTestEntries.File(profileId, rootIdentity, "b.txt", 2),
                SyncTestEntries.File(profileId, rootIdentity, "c.txt", 3)
            ],
            repeatContinuationToken: true);

        var result = await SyncSnapshotScanner.ScanAsync(
            session,
            root,
            new SyncSnapshotScanOptions(pageSize: 1, maximumEntries: 10));

        Assert.True(result.IsFailure);
        Assert.Equal("sync.scan.continuation_cycle", result.Error.Code);
    }

    [Fact]
    public async Task Scan_rejects_case_collisions_for_an_insensitive_endpoint()
    {
        var profileId = ConnectionProfileId.New();
        const string rootIdentity = "fake-root";
        var root = SyncTestEntries.Address(profileId, rootIdentity, string.Empty);
        await using var session = new FakeEndpointSession(
            profileId,
            rootIdentity,
            [
                SyncTestEntries.File(profileId, rootIdentity, "File.txt", 1),
                SyncTestEntries.File(profileId, rootIdentity, "file.txt", 1)
            ],
            StorageCaseSensitivity.Insensitive);

        var result = await SyncSnapshotScanner.ScanAsync(session, root);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.scan.path_collision", result.Error.Code);
    }

    [Fact]
    public async Task Scan_rejects_entries_outside_the_verified_root()
    {
        var profileId = ConnectionProfileId.New();
        const string rootIdentity = "fake-root";
        var root = SyncTestEntries.Address(profileId, rootIdentity, "expected");
        await using var session = new FakeEndpointSession(
            profileId,
            rootIdentity,
            [SyncTestEntries.File(profileId, rootIdentity, "escape.txt", 1)],
            returnEntriesOutsideRequestedDirectory: true);

        var result = await SyncSnapshotScanner.ScanAsync(session, root);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.scan.outside_root", result.Error.Code);
    }

    [Fact]
    public async Task Scan_hashes_only_files_without_provider_identity_with_bounded_parallelism()
    {
        var profileId = ConnectionProfileId.New();
        const string rootIdentity = "fake-root";
        var active = 0;
        var maximumActive = 0;
        async ValueTask<StorageResult<PortableChecksumResult>> Hash(
            PortableChecksumRequest request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    request.ExpectedEntry.Address.CanonicalRelativePath);
                return StorageResult<PortableChecksumResult>.Success(new PortableChecksumResult(
                    new PortableContentDigest(
                        PortableChecksumAlgorithm.Sha256,
                        Convert.ToHexStringLower(SHA256.HashData(bytes))),
                    request.ExpectedEntry.Size!.Value));
            }
            finally
            {
                _ = Interlocked.Decrement(ref active);
            }
        }

        await using var session = new FakeEndpointSession(
            profileId,
            rootIdentity,
            [
                SyncTestEntries.File(profileId, rootIdentity, "local-a.bin", 4),
                SyncTestEntries.File(profileId, rootIdentity, "local-b.bin", 5),
                SyncTestEntries.File(profileId, rootIdentity, "versioned.bin", 6, versionId: "v1"),
                SyncTestEntries.File(profileId, rootIdentity, "etagged.bin", 7, entityTag: "etag-v1")
            ],
            checksumHandler: Hash);

        var result = await SyncSnapshotScanner.ScanAsync(
            session,
            SyncTestEntries.Address(profileId, rootIdentity, string.Empty),
            new SyncSnapshotScanOptions(
                portableHashMode: SyncPortableHashMode.FilesWithoutStableIdentity,
                maximumHashedFiles: 2,
                maximumTotalHashBytes: 9,
                maximumConcurrentHashes: 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(["local-a.bin", "local-b.bin"], result.Value.PortableDigests.Keys);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task Scan_fails_before_hashing_when_portable_file_budget_is_exceeded()
    {
        var profileId = ConnectionProfileId.New();
        const string rootIdentity = "fake-root";
        var calls = 0;
        await using var session = new FakeEndpointSession(
            profileId,
            rootIdentity,
            [
                SyncTestEntries.File(profileId, rootIdentity, "a.bin", 1),
                SyncTestEntries.File(profileId, rootIdentity, "b.bin", 1)
            ],
            checksumHandler: (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Hashing must not begin after budget rejection.");
            });

        var result = await SyncSnapshotScanner.ScanAsync(
            session,
            SyncTestEntries.Address(profileId, rootIdentity, string.Empty),
            new SyncSnapshotScanOptions(
                portableHashMode: SyncPortableHashMode.FilesWithoutStableIdentity,
                maximumHashedFiles: 1));

        Assert.True(result.IsFailure);
        Assert.Equal("sync.scan.portable_hash_limit_exceeded", result.Error.Code);
        Assert.Equal(0, calls);
    }
}
