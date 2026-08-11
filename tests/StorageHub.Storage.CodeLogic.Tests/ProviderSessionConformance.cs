using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Transfers;
using CL.Storage.Configuration;

namespace StorageHub.Storage.CodeLogic.Tests;

internal static class ProviderSessionConformance
{
    public static async Task AssertTransfersToAndFromLocalAsync(
        CodeLogicStorageSessionFactory factory,
        IStorageEndpointSession remoteSession,
        ConnectionProfileId remoteProfileId,
        string remoteRootIdentity,
        string testRoot,
        StorageWriteMode remoteWriteMode = StorageWriteMode.CreateNew,
        bool supportsSafeRemoteCreate = true)
    {
        var localRoot = Path.Combine(testRoot, $"transfer-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localRoot);
        var localProfileId = ConnectionProfileId.New();
        var localRootIdentity = $"local-transfer-root-{Guid.NewGuid():N}";
        var registered = await factory.RegisterLocalAsync(
            localProfileId,
            localRootIdentity,
            new LocalConnectionConfig { RootPath = localRoot, Enabled = true });
        Assert.True(registered.IsSuccess, Failure(registered.Error));

        await using var localConnection = registered.Value;
        var localToRemotePayload = Enumerable.Range(0, 96_000)
            .Select(index => (byte)(index % 239))
            .ToArray();
        const string localSourcePath = "outbound/local-to-remote.bin";
        var localSourceFile = Path.Combine(localRoot, "outbound", "local-to-remote.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(localSourceFile)!);
        await File.WriteAllBytesAsync(localSourceFile, localToRemotePayload);

        var localSource = Address(localProfileId, localRootIdentity, localSourcePath);
        var remoteDestination = Address(
            remoteProfileId,
            remoteRootIdentity,
            $"transfers/local-to-remote-{Guid.NewGuid():N}.bin");
        var outbound = await TransferExecutor.ExecuteAsync(
            new TransferIntent(
                TransferJobId.New(),
                TransferOperationKind.Copy,
                localSource,
                remoteDestination,
                localToRemotePayload.LongLength,
                TransferVerificationPolicy.StrongHashWhenAvailable,
                DateTimeOffset.UtcNow),
            localConnection.Session,
            remoteSession);
        if (supportsSafeRemoteCreate)
        {
            Assert.True(outbound.IsSuccess, Failure(outbound.Error));
            Assert.Equal(localToRemotePayload, await ReadAllAsync(remoteSession, remoteDestination));
        }
        else
        {
            Assert.True(outbound.IsFailure);
            Assert.Equal("transfer.conditional_mutation.unsupported", outbound.Error.Code);
        }

        var remoteToLocalPayload = Enumerable.Range(0, 112_000)
            .Select(index => (byte)(255 - (index % 251)))
            .ToArray();
        var remoteSource = Address(
            remoteProfileId,
            remoteRootIdentity,
            $"transfers/remote-to-local-{Guid.NewGuid():N}.bin");
        var committedRemote = await WriteAsync(
            remoteSession,
            remoteSource,
            remoteToLocalPayload,
            remoteWriteMode);
        var localDestination = Address(
            localProfileId,
            localRootIdentity,
            "inbound/remote-to-local.bin");
        var inbound = await TransferExecutor.ExecuteAsync(
            new TransferIntent(
                TransferJobId.New(),
                TransferOperationKind.Copy,
                committedRemote.Address,
                localDestination,
                remoteToLocalPayload.LongLength,
                TransferVerificationPolicy.StrongHashWhenAvailable,
                DateTimeOffset.UtcNow),
            remoteSession,
            localConnection.Session);
        Assert.True(inbound.IsSuccess, Failure(inbound.Error));
        Assert.Equal(
            remoteToLocalPayload,
            await File.ReadAllBytesAsync(Path.Combine(localRoot, "inbound", "remote-to-local.bin")));
    }

    public static async Task AssertBoundedRoundTripAsync(
        IStorageEndpointSession session,
        ConnectionProfileId profileId,
        string rootIdentity,
        StorageWriteMode writeMode = StorageWriteMode.CreateNew,
        bool assertCommittedSize = true)
    {
        var payload = Enumerable.Range(0, 180_000).Select(index => (byte)(index % 241)).ToArray();
        var destination = Address(profileId, rootIdentity, "nested/naïve-東京.bin");

        var committed = await WriteAsync(session, destination, payload, writeMode);
        if (assertCommittedSize)
        {
            Assert.Equal(payload.LongLength, committed.Size);
        }

        var read = await session.OpenReadAsync(new StorageReadRequest(destination));
        Assert.True(read.IsSuccess, Failure(read.Error));
        await using var source = read.Value;
        using var copied = new MemoryStream();
        await source.CopyToAsync(copied);
        Assert.Equal(payload, copied.ToArray());

        var folder = Address(profileId, rootIdentity, "nested");
        var listing = await session.ListAsync(folder, new StorageListRequest(PageSize: 1));
        Assert.True(listing.IsSuccess, Failure(listing.Error));
        Assert.Contains(listing.Value.Entries, entry => entry.Name == "naïve-東京.bin");
    }

    public static async Task AssertCreateNewCollisionPreservesOriginalAsync(
        IStorageEndpointSession session,
        ConnectionProfileId profileId,
        string rootIdentity)
    {
        var destination = Address(profileId, rootIdentity, "collision.bin");
        var original = new byte[] { 9, 8, 7, 6 };
        await WriteAsync(session, destination, original, StorageWriteMode.CreateNew);

        var opened = await session.OpenWriteAsync(new StorageWriteRequest(
            destination,
            StorageWriteMode.CreateNew,
            expectedLength: 1));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using (var handle = opened.Value)
        {
            await handle.Content.WriteAsync(new byte[] { 5 });
            var collision = await handle.CommitAsync();
            Assert.True(collision.IsFailure);
            Assert.Equal(StorageFailureKind.Conflict, collision.Error.Kind);
        }

        Assert.Equal(original, await ReadAllAsync(session, destination));
    }

    public static async Task AssertAbortDoesNotPublishAsync(
        IStorageEndpointSession session,
        ConnectionProfileId profileId,
        string rootIdentity,
        StorageWriteMode writeMode = StorageWriteMode.CreateNew)
    {
        var destination = Address(profileId, rootIdentity, "aborted.bin");
        var opened = await session.OpenWriteAsync(new StorageWriteRequest(
            destination,
            writeMode));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using (var handle = opened.Value)
        {
            await handle.Content.WriteAsync(new byte[] { 1, 2, 3 });
            var aborted = await handle.AbortAsync();
            Assert.True(aborted.IsSuccess, Failure(aborted.Error));
        }

        var lookup = await session.GetEntryAsync(destination);
        Assert.True(lookup.IsFailure);
        Assert.Equal(StorageFailureKind.NotFound, lookup.Error.Kind);
    }

    public static async Task AssertAddressSubstitutionFailsBeforeProviderIoAsync(
        IStorageEndpointSession session,
        ConnectionProfileId profileId,
        string rootIdentity)
    {
        var otherProfile = Address(ConnectionProfileId.New(), rootIdentity, "forbidden.bin");
        var profileMismatch = await session.GetEntryAsync(otherProfile);
        Assert.True(profileMismatch.IsFailure);
        Assert.Equal("storage.address.profile_mismatch", profileMismatch.Error.Code);

        var staleRoot = Address(profileId, rootIdentity + "-stale", "forbidden.bin");
        var rootMismatch = await session.GetEntryAsync(staleRoot);
        Assert.True(rootMismatch.IsFailure);
        Assert.Equal("storage.address.root_mismatch", rootMismatch.Error.Code);

        var traversal = StorageAddress.Create(profileId, rootIdentity, "safe/%252e%252e/escape.bin");
        Assert.True(traversal.IsFailure);
        Assert.Equal("storage.address.invalid_path", traversal.Error.Code);
    }

    private static async Task<StorageEntry> WriteAsync(
        IStorageEndpointSession session,
        StorageAddress destination,
        byte[] payload,
        StorageWriteMode mode)
    {
        var opened = await session.OpenWriteAsync(new StorageWriteRequest(
            destination,
            mode,
            payload.LongLength));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using var handle = opened.Value;
        await handle.Content.WriteAsync(payload);
        var committed = await handle.CommitAsync();
        Assert.True(committed.IsSuccess, Failure(committed.Error));
        return committed.Value;
    }

    private static async Task<byte[]> ReadAllAsync(
        IStorageEndpointSession session,
        StorageAddress address)
    {
        var opened = await session.OpenReadAsync(new StorageReadRequest(address));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using var source = opened.Value;
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static StorageAddress Address(
        ConnectionProfileId profileId,
        string rootIdentity,
        string path)
    {
        var address = StorageAddress.Create(profileId, rootIdentity, path);
        Assert.True(address.IsSuccess, Failure(address.Error));
        return address.Value;
    }

    private static string Failure(StorageFailure? failure) => failure is null
        ? "The operation failed without a structured failure."
        : $"{failure.Code}: {failure.Message}" +
          (string.IsNullOrWhiteSpace(failure.ProviderCode)
              ? string.Empty
              : $" (provider: {failure.ProviderCode})");
}
