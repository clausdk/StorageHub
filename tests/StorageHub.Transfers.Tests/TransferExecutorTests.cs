using System.Security.Cryptography;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Transfers.Tests;

public sealed class TransferExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_StreamsAndCommitsAnyToAnyCopy()
    {
        var payload = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        var fixture = CreateFixture(payload);

        var result = await TransferExecutor.ExecuteAsync(
            fixture.Intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(BufferSize: 4_096));

        Assert.True(result.IsSuccess);
        Assert.Equal(payload.LongLength, result.Value.BytesTransferred);
        Assert.False(result.Value.SourceDeleted);
        Assert.Equal(payload, fixture.Destination.WrittenBytes);
        Assert.True(fixture.Destination.LastWriteHandle!.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_MoveDeletesSourceOnlyAfterVerifiedCommit()
    {
        var fixture = CreateFixture([1, 2, 3, 4], TransferOperationKind.Move);

        var result = await TransferExecutor.ExecuteAsync(
            fixture.Intent,
            fixture.Source,
            fixture.Destination);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.SourceDeleted);
        Assert.Equal(1, fixture.Source.DeleteCalls);
        Assert.True(fixture.Destination.LastWriteHandle!.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_SourceGrowthAbortsBeforeCommit()
    {
        var fixture = CreateFixture([1, 2, 3, 4]);
        fixture.Source.Entry = CreateEntry(fixture.Intent.Source, size: 3);
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            fixture.Intent.Source,
            fixture.Intent.Destination,
            expectedLength: null,
            TransferVerificationPolicy.Size,
            DateTimeOffset.UtcNow);

        var result = await TransferExecutor.ExecuteAsync(intent, fixture.Source, fixture.Destination);

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.source.grew", result.Error.Code);
        Assert.True(fixture.Destination.LastWriteHandle!.Aborted);
        Assert.False(fixture.Destination.LastWriteHandle.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_OverwriteStagesVerifiesAndConditionallyPromotes()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var versionedSource = StorageAddress.Create(
            fixture.Source.ProfileId,
            fixture.Source.RootIdentity,
            fixture.Intent.Source.CanonicalRelativePath,
            versionId: "source-v1").Value;
        var versionedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            fixture.Intent.Destination.CanonicalRelativePath,
            versionId: "destination-v1").Value;
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            fixture.Intent.Operation,
            versionedSource,
            versionedDestination,
            fixture.Intent.ExpectedLength,
            fixture.Intent.VerificationPolicy,
            fixture.Intent.CreatedAtUtc);
        fixture.Source.Entry = CreateEntry(versionedSource, 3);
        fixture.Source.Capabilities = Capabilities(StorageFeature.ReadStream, StorageFeature.ObjectVersioning);
        fixture.Destination.Entry = CreateEntry(versionedDestination, 1);
        fixture.Destination.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate,
            StorageFeature.ObjectVersioning,
            StorageFeature.TemporaryFiles,
            StorageFeature.FileMove,
            StorageFeature.AtomicRename);
        fixture.Destination.CommittedVersionId = "staging-v1";

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(Overwrite: true));

        Assert.True(result.IsSuccess);
        Assert.NotEqual(versionedDestination.CanonicalRelativePath,
            fixture.Destination.LastWriteRequest!.Destination.CanonicalRelativePath);
        Assert.Equal(StorageWriteMode.CreateNew, fixture.Destination.LastWriteRequest.Mode);
        Assert.Equal(1, fixture.Destination.MoveCalls);
        Assert.Equal("staging-v1", fixture.Destination.LastMoveRequest!.ExpectedSourceVersionId);
        Assert.Equal("destination-v1", fixture.Destination.LastMoveRequest.ExpectedDestinationVersionId);
        Assert.Equal(versionedDestination.CanonicalRelativePath,
            result.Value.Destination.Address.CanonicalRelativePath);
    }

    [Theory]
    [InlineData(StorageFeature.Move)]
    [InlineData(StorageFeature.DirectoryMove)]
    public async Task ExecuteAsync_StagedFileOverwriteRejectsNonFileMoveCapabilities(
        StorageFeature nonFileMoveFeature)
    {
        var fixture = CreateFixture([1, 2, 3]);
        var taggedSource = StorageAddress.Create(
            fixture.Source.ProfileId,
            fixture.Source.RootIdentity,
            fixture.Intent.Source.CanonicalRelativePath,
            entityTag: "source-etag").Value;
        var versionedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            fixture.Intent.Destination.CanonicalRelativePath,
            versionId: "destination-v1").Value;
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            taggedSource,
            versionedDestination,
            expectedLength: 3,
            TransferVerificationPolicy.Size,
            fixture.Intent.CreatedAtUtc);
        fixture.Source.Entry = CreateEntry(taggedSource, 3);
        fixture.Destination.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate,
            StorageFeature.ObjectVersioning,
            StorageFeature.TemporaryFiles,
            nonFileMoveFeature,
            StorageFeature.AtomicRename);

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(Overwrite: true));

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.conditional_mutation.unsupported", result.Error.Code);
        Assert.Contains(nameof(StorageFeature.FileMove), result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Source.GetEntryCalls);
        Assert.Null(fixture.Destination.LastWriteHandle);
    }

    [Fact]
    public async Task ExecuteAsync_AtomicConditionalOverwrite_writes_directly_with_bound_entity_tag()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var versionedSource = StorageAddress.Create(
            fixture.Source.ProfileId,
            fixture.Source.RootIdentity,
            fixture.Intent.Source.CanonicalRelativePath,
            versionId: "source-v1",
            entityTag: "source-etag").Value;
        var taggedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            fixture.Intent.Destination.CanonicalRelativePath,
            entityTag: "destination-etag").Value;
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            fixture.Intent.Operation,
            versionedSource,
            taggedDestination,
            fixture.Intent.ExpectedLength,
            fixture.Intent.VerificationPolicy,
            fixture.Intent.CreatedAtUtc);
        fixture.Source.Entry = CreateEntry(versionedSource, 3);
        fixture.Source.Capabilities = Capabilities(
            StorageFeature.ReadStream,
            StorageFeature.ObjectVersioning);
        fixture.Destination.Entry = CreateEntry(taggedDestination, 1);
        fixture.Destination.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalUpdate,
            StorageFeature.AtomicReplace);

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(Overwrite: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(StorageWriteMode.Overwrite, fixture.Destination.LastWriteRequest!.Mode);
        Assert.Equal(taggedDestination.CanonicalRelativePath,
            fixture.Destination.LastWriteRequest.Destination.CanonicalRelativePath);
        Assert.Equal("destination-etag",
            fixture.Destination.LastWriteRequest.ExpectedDestinationEntityTag);
        Assert.Equal("source-v1", fixture.Source.LastReadRequest!.ExpectedVersionId);
        Assert.Null(fixture.Source.LastReadRequest.ExpectedEntityTag);
        Assert.Equal(0, fixture.Destination.MoveCalls);
    }

    [Fact]
    public async Task ExecuteAsync_PlannedSha256AllowsUnversionedSourceAndIsCheckedBeforeOverwriteCommit()
    {
        byte[] payload = [1, 2, 3];
        var fixture = CreateFixture(payload);
        var taggedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            fixture.Intent.Destination.CanonicalRelativePath,
            entityTag: "destination-etag").Value;
        var sourceDigest = new PortableContentDigest(
            PortableChecksumAlgorithm.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(payload)));
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            fixture.Intent.Source,
            taggedDestination,
            payload.LongLength,
            TransferVerificationPolicy.Size,
            fixture.Intent.CreatedAtUtc,
            expectedSourceDigest: sourceDigest);
        fixture.Destination.Entry = CreateEntry(taggedDestination, 1);
        fixture.Destination.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalUpdate,
            StorageFeature.AtomicReplace);

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(Overwrite: true));

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Source.LastReadRequest!.ExpectedVersionId);
        Assert.Null(fixture.Source.LastReadRequest.ExpectedEntityTag);
        Assert.True(fixture.Destination.LastWriteHandle!.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_EntityTagSourceIsCheckedBeforeAndAfterStreamingWithoutTreatingItAsHash()
    {
        byte[] payload = [1, 2, 3];
        var fixture = CreateFixture(payload);
        var taggedSource = StorageAddress.Create(
            fixture.Source.ProfileId,
            fixture.Source.RootIdentity,
            fixture.Intent.Source.CanonicalRelativePath,
            entityTag: "source-etag").Value;
        var taggedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            fixture.Intent.Destination.CanonicalRelativePath,
            entityTag: "destination-etag").Value;
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            taggedSource,
            taggedDestination,
            payload.LongLength,
            TransferVerificationPolicy.Size,
            fixture.Intent.CreatedAtUtc);
        fixture.Source.Entry = CreateEntry(taggedSource, payload.LongLength);
        fixture.Destination.Entry = CreateEntry(taggedDestination, 1);
        fixture.Destination.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalUpdate,
            StorageFeature.AtomicReplace);

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(Overwrite: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Source.GetEntryCalls);
        Assert.Null(fixture.Source.LastReadRequest!.ExpectedEntityTag);
    }

    [Fact]
    public async Task ExecuteAsync_SourceSha256MismatchAbortsBeforePublish()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            fixture.Intent.Source,
            fixture.Intent.Destination,
            fixture.Intent.ExpectedLength,
            TransferVerificationPolicy.Size,
            fixture.Intent.CreatedAtUtc,
            expectedSourceDigest: new PortableContentDigest(
                PortableChecksumAlgorithm.Sha256,
                new string('f', 64)));

        var result = await TransferExecutor.ExecuteAsync(intent, fixture.Source, fixture.Destination);

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.verification.source_hash_mismatch", result.Error.Code);
        Assert.True(fixture.Destination.LastWriteHandle!.Aborted);
        Assert.False(fixture.Destination.LastWriteHandle.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_OverwriteWithoutConditionalAtomicCapabilitiesFailsBeforeIo()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var versionedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            "target.bin",
            versionId: "destination-v1").Value;
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            fixture.Intent.Source,
            versionedDestination,
            3,
            TransferVerificationPolicy.Size,
            fixture.Intent.CreatedAtUtc);

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(Overwrite: true));

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.conditional_mutation.unsupported", result.Error.Code);
        Assert.Equal(0, fixture.Source.GetEntryCalls);
        Assert.Null(fixture.Destination.LastWriteHandle);
    }

    [Fact]
    public async Task ExecuteAsync_NonAtomicCompatibilityCreatesOnLegacyDestination()
    {
        var fixture = CreateFixture([1, 2, 3]);
        fixture.Destination.Capabilities = Capabilities(StorageFeature.WriteStream);

        var result = await TransferExecutor.ExecuteAsync(
            fixture.Intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(AllowNonAtomicDestinationWrites: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(StorageWriteMode.Overwrite, fixture.Destination.LastWriteRequest!.Mode);
        Assert.Equal(fixture.Intent.Destination, fixture.Destination.LastWriteRequest.Destination);
        Assert.Equal(new byte[] { 1, 2, 3 }, fixture.Destination.WrittenBytes);
    }

    [Fact]
    public async Task ExecuteAsync_NonAtomicCompatibilityReplacesWithoutStaging()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var taggedSource = StorageAddress.Create(
            fixture.Source.ProfileId,
            fixture.Source.RootIdentity,
            fixture.Intent.Source.CanonicalRelativePath,
            entityTag: "source-etag").Value;
        var versionedDestination = StorageAddress.Create(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity,
            fixture.Intent.Destination.CanonicalRelativePath,
            versionId: "destination-v1").Value;
        fixture.Source.Entry = CreateEntry(taggedSource, 3);
        fixture.Destination.Entry = CreateEntry(versionedDestination, 1);
        fixture.Destination.Capabilities = Capabilities(StorageFeature.WriteStream);
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            taggedSource,
            versionedDestination,
            3,
            TransferVerificationPolicy.Size,
            fixture.Intent.CreatedAtUtc);

        var result = await TransferExecutor.ExecuteAsync(
            intent,
            fixture.Source,
            fixture.Destination,
            new TransferExecutionOptions(
                Overwrite: true,
                AllowNonAtomicDestinationWrites: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(StorageWriteMode.Overwrite, fixture.Destination.LastWriteRequest!.Mode);
        Assert.Equal(versionedDestination.CanonicalRelativePath,
            fixture.Destination.LastWriteRequest.Destination.CanonicalRelativePath);
        Assert.Null(fixture.Destination.LastWriteRequest.ExpectedDestinationVersionId);
        Assert.Equal(0, fixture.Destination.MoveCalls);
    }

    [Fact]
    public async Task ExecuteAsync_TruncatedSourceAbortsUncommittedDestination()
    {
        var fixture = CreateFixture([1, 2, 3]);
        fixture.Source.Entry = CreateEntry(fixture.Intent.Source, size: 12);

        var result = await TransferExecutor.ExecuteAsync(
            fixture.Intent with { },
            fixture.Source,
            fixture.Destination);

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.source.changed", result.Error.Code);
        Assert.Null(fixture.Destination.LastWriteHandle);

        var intentWithoutDeclaredLength = new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            fixture.Intent.Source,
            fixture.Intent.Destination,
            expectedLength: null,
            TransferVerificationPolicy.Size,
            DateTimeOffset.UtcNow);
        fixture.Source.Entry = CreateEntry(fixture.Intent.Source, size: 12);

        result = await TransferExecutor.ExecuteAsync(
            intentWithoutDeclaredLength,
            fixture.Source,
            fixture.Destination);

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.source.truncated", result.Error.Code);
        Assert.True(fixture.Destination.LastWriteHandle!.Aborted);
        Assert.False(fixture.Destination.LastWriteHandle.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_RequiredHashFailsBeforeOpeningStreamsWhenUnavailable()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            fixture.Intent.Operation,
            fixture.Intent.Source,
            fixture.Intent.Destination,
            fixture.Intent.ExpectedLength,
            TransferVerificationPolicy.StrongHashRequired,
            fixture.Intent.CreatedAtUtc);

        var result = await TransferExecutor.ExecuteAsync(intent, fixture.Source, fixture.Destination);

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.verification.hash_unavailable", result.Error.Code);
        Assert.Equal(0, fixture.Source.OpenReadCalls);
        Assert.Null(fixture.Destination.LastWriteHandle);
    }

    [Fact]
    public async Task ExecuteAsync_RequiredDestinationSha256DetectsProviderMismatch()
    {
        var fixture = CreateFixture([1, 2, 3]);
        var destination = new PortableChecksumSession(
            fixture.Destination.ProfileId,
            fixture.Destination.RootIdentity)
        {
            Capabilities = Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate),
            DigestOverride = new PortableContentDigest(
                PortableChecksumAlgorithm.Sha256,
                new string('f', 64)),
        };
        var intent = new TransferIntent(
            fixture.Intent.TransferJobId,
            TransferOperationKind.Copy,
            fixture.Intent.Source,
            fixture.Intent.Destination,
            fixture.Intent.ExpectedLength,
            TransferVerificationPolicy.StrongHashRequired,
            fixture.Intent.CreatedAtUtc);

        var result = await TransferExecutor.ExecuteAsync(intent, fixture.Source, destination);

        Assert.True(result.IsFailure);
        Assert.Equal("transfer.verification.destination_hash_mismatch", result.Error.Code);
        Assert.True(destination.LastWriteHandle!.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsSessionProfileMismatchBeforeProviderIo()
    {
        var fixture = CreateFixture([1, 2, 3]);
        fixture.Source.ProfileId = ConnectionProfileId.New();

        var result = await TransferExecutor.ExecuteAsync(
            fixture.Intent,
            fixture.Source,
            fixture.Destination);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.address.profile_mismatch", result.Error.Code);
        Assert.Equal(0, fixture.Source.GetEntryCalls);
    }

    private static TransferFixture CreateFixture(
        byte[] payload,
        TransferOperationKind operation = TransferOperationKind.Copy)
    {
        var sourceProfile = ConnectionProfileId.New();
        var destinationProfile = ConnectionProfileId.New();
        const string sourceRoot = "source-root-v1";
        const string destinationRoot = "destination-root-v1";
        var sourceAddress = StorageAddress.Create(
            sourceProfile,
            sourceRoot,
            "folder/source.bin",
            versionId: operation == TransferOperationKind.Move ? "source-v1" : null).Value;
        var destinationAddress = StorageAddress.Create(destinationProfile, destinationRoot, "target.bin").Value;
        var intent = new TransferIntent(
            TransferJobId.New(),
            operation,
            sourceAddress,
            destinationAddress,
            payload.LongLength,
            TransferVerificationPolicy.Size,
            DateTimeOffset.UtcNow);
        var source = new FakeSession(sourceProfile, sourceRoot)
        {
            ReadBytes = payload,
            Entry = CreateEntry(sourceAddress, payload.LongLength),
            Capabilities = operation == TransferOperationKind.Move
                ? Capabilities(
                    StorageFeature.ReadStream,
                    StorageFeature.Delete,
                    StorageFeature.ObjectVersioning,
                    StorageFeature.ConditionalDelete)
                : Capabilities(StorageFeature.ReadStream),
        };
        var destination = new FakeSession(destinationProfile, destinationRoot)
        {
            Capabilities = Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate),
        };
        return new TransferFixture(intent, source, destination);
    }

    private static EffectiveStorageCapabilities Capabilities(params StorageFeature[] features) =>
        new(features.Select(feature =>
            new KeyValuePair<StorageFeature, FeatureSupport>(feature, FeatureSupport.Native())));

    private static StorageEntry CreateEntry(StorageAddress address, long size, string? checksum = null) =>
        StorageEntry.Create(address, StorageEntryKind.File, size, checksum: checksum).Value;

    private sealed record TransferFixture(
        TransferIntent Intent,
        FakeSession Source,
        FakeSession Destination);

    private class FakeSession(ConnectionProfileId profileId, string rootIdentity) : IStorageEndpointSession
    {
        public ConnectionProfileId ProfileId { get; set; } = profileId;
        public string RootIdentity { get; } = rootIdentity;
        public EffectiveStorageCapabilities Capabilities { get; set; } = EffectiveStorageCapabilities.None;
        public byte[] ReadBytes { get; set; } = [];
        public byte[] WrittenBytes => LastWriteHandle?.Bytes ?? [];
        public StorageEntry? Entry { get; set; }
        public FakeWriteHandle? LastWriteHandle { get; private set; }
        public StorageWriteRequest? LastWriteRequest { get; private set; }
        public StorageReadRequest? LastReadRequest { get; private set; }
        public StorageMoveRequest? LastMoveRequest { get; private set; }
        public string? CommittedVersionId { get; set; }
        public int GetEntryCalls { get; private set; }
        public int OpenReadCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int MoveCalls { get; private set; }

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Success());

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            GetEntryCalls++;
            return ValueTask.FromResult(Entry is null
                ? StorageResult<StorageEntry>.Fail(new StorageFailure(
                    "storage.not_found",
                    StorageFailureKind.NotFound,
                    "Not found."))
                : StorageResult<StorageEntry>.Success(Entry));
        }

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenReadCalls++;
            LastReadRequest = request;
            return ValueTask.FromResult(StorageResult<Stream>.Success(
                new MemoryStream(ReadBytes, writable: false)));
        }

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastWriteRequest = request;
            LastWriteHandle = new FakeWriteHandle(request.Destination, CommittedVersionId);
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Success(LastWriteHandle));
        }

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return ValueTask.FromResult(StorageResult.Success());
        }

        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default)
        {
            MoveCalls++;
            LastMoveRequest = request;
            var destination = StorageAddress.Create(
                request.Destination.ProfileId,
                request.Destination.RootIdentity,
                request.Destination.CanonicalRelativePath,
                versionId: "destination-v2").Value;
            return ValueTask.FromResult(StorageResult<StorageEntry>.Success(
                StorageEntry.Create(destination, StorageEntryKind.File, LastWriteHandle?.Bytes.LongLength).Value));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PortableChecksumSession(
        ConnectionProfileId profileId,
        string rootIdentity) : FakeSession(profileId, rootIdentity), IStoragePortableChecksumSession
    {
        public PortableContentDigest? DigestOverride { get; init; }

        public ValueTask<StorageResult<PortableChecksumResult>> ComputePortableChecksumAsync(
            PortableChecksumRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = DigestOverride ?? new PortableContentDigest(
                PortableChecksumAlgorithm.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(WrittenBytes)));
            return ValueTask.FromResult(StorageResult<PortableChecksumResult>.Success(
                new PortableChecksumResult(digest, request.ExpectedEntry.Size!.Value)));
        }
    }

    private sealed class FakeWriteHandle(StorageAddress destination, string? committedVersionId) : IStorageWriteHandle
    {
        private readonly MemoryStream _stream = new();

        public StorageAddress Destination { get; } = destination;
        public Stream Content => _stream;
        public long AcceptedOffset => 0;
        public string? ResumeToken => null;
        public StorageWriteHandleState State { get; private set; } = StorageWriteHandleState.Open;
        public bool Committed { get; private set; }
        public bool Aborted { get; private set; }
        public byte[] Bytes => _stream.ToArray();

        public ValueTask<StorageResult<StorageEntry>> CommitAsync(
            CancellationToken cancellationToken = default)
        {
            Committed = true;
            State = StorageWriteHandleState.Committed;
            var committedAddress = StorageAddress.Create(
                Destination.ProfileId,
                Destination.RootIdentity,
                Destination.CanonicalRelativePath,
                versionId: committedVersionId).Value;
            var entry = StorageEntry.Create(committedAddress, StorageEntryKind.File, _stream.Length).Value;
            return ValueTask.FromResult(StorageResult<StorageEntry>.Success(entry));
        }

        public ValueTask<StorageResult> AbortAsync(CancellationToken cancellationToken = default)
        {
            Aborted = true;
            State = StorageWriteHandleState.Aborted;
            return ValueTask.FromResult(StorageResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
