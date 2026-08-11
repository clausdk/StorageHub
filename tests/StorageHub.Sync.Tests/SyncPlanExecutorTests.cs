using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Transfers;

namespace StorageHub.Sync.Tests;

public sealed class SyncPlanExecutorTests
{
    [Fact]
    public async Task Preview_preflights_entire_plan_without_provider_side_effects()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.CreateDirectory(0, fixture.RightAddress("folder")),
            SyncPlanOperation.Copy(
                1,
                fixture.LeftAddress("source.bin"),
                fixture.RightAddress("folder/source.bin"),
                fixture.Payload.LongLength),
            SyncPlanOperation.Delete(2, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var events = new RecordingProgress();

        var result = await SyncPlanExecutor.ExecuteAsync(
            CreateRequest(fixture, plan, SyncPlanExecutionMode.Preview),
            events);

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncPlanExecutionMode.Preview, result.Value.Mode);
        Assert.Equal(3, result.Value.ProcessedOperations);
        Assert.Equal(0, result.Value.ExecutedOperations);
        Assert.Equal(1, result.Value.PlannedDeletionCount);
        Assert.True(result.Value.DeletionDecision.Allowed);
        Assert.Equal(0, fixture.TotalProviderCalls);
        Assert.Equal(3, events.Events.Count(item =>
            item.Kind == SyncPlanExecutionEventKind.OperationPreviewed));
        Assert.Equal(SyncPlanExecutionEventKind.PlanCompleted, events.Events[^1].Kind);
    }

    [Fact]
    public async Task Execute_runs_create_copy_and_delete_sequentially()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.CreateDirectory(0, fixture.RightAddress("folder")),
            SyncPlanOperation.Copy(
                1,
                fixture.LeftAddress("source.bin"),
                fixture.RightAddress("folder/source.bin"),
                fixture.Payload.LongLength),
            SyncPlanOperation.Delete(2, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var events = new RecordingProgress();

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(fixture, plan), events);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.ProcessedOperations);
        Assert.Equal(3, result.Value.ExecutedOperations);
        Assert.Equal(fixture.Payload.LongLength, result.Value.BytesTransferred);
        Assert.Equal(["mkdir:folder", "write:folder/source.bin", "delete:obsolete.bin"], fixture.Right.SideEffects);
        Assert.Equal(fixture.Payload, fixture.Right.WrittenBytes);
        Assert.Contains(events.Events, item =>
            item.Kind == SyncPlanExecutionEventKind.OperationProgress &&
            item.OperationSequence == 1 &&
            item.BytesTransferred == fixture.Payload.LongLength);
    }

    [Fact]
    public async Task Digest_mismatch_is_rejected_before_provider_io()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(SyncPlanOperation.Delete(0, fixture.RightAddress("obsolete.bin")));
        var otherPlan = ImmutableSyncPlan.Create(
            OperationPlanId.New(),
            plan.ProfileId,
            plan.BaselineGeneration,
            plan.Operations,
            plan.CreatedAtUtc);
        var request = CreateRequest(fixture, plan) with { ApprovedDigest = otherPlan.Digest };

        var result = await SyncPlanExecutor.ExecuteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.plan.digest_mismatch", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Stale_root_identity_is_rejected_before_provider_io()
    {
        var fixture = CreateFixture();
        var staleTarget = StorageAddress.Create(
            fixture.Right.ProfileId,
            "right-root-old",
            "obsolete.bin").Value;
        var plan = CreatePlan(SyncPlanOperation.Delete(0, staleTarget));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(fixture, plan));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.address.root_mismatch", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Incomplete_listing_blocks_all_operations_when_plan_contains_deletion()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.Copy(
                0,
                fixture.LeftAddress("source.bin"),
                fixture.RightAddress("source.bin"),
                fixture.Payload.LongLength),
            SyncPlanOperation.Delete(1, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var incomplete = new SnapshotCompleteness(
            endpointAvailable: true,
            rootIdentityVerified: true,
            enumerationCompleted: false,
            paginationCompleted: false,
            permissionsIntact: true,
            unexpectedlyEmpty: false,
            totalItemCount: 50);
        var request = CreateRequest(
            fixture,
            plan,
            snapshots: CreateSnapshots(fixture, incomplete, SnapshotCompleteness.Complete(50)));

        var result = await SyncPlanExecutor.ExecuteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.plan.deletion_blocked", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Snapshot_substitution_is_rejected_by_execution_approval_before_provider_io()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.Delete(0, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var request = CreateRequest(fixture, plan) with
        {
            Snapshots = CreateSnapshots(
                fixture,
                SnapshotCompleteness.Complete(99),
                SnapshotCompleteness.Complete(100)),
        };

        var result = await SyncPlanExecutor.ExecuteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.execution.approval_mismatch", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Destructive_execution_with_legacy_plan_digest_is_rejected()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.Delete(0, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var sessions = CreateSessions(fixture);
        var request = new SyncPlanExecutionRequest(
            plan,
            plan.Digest,
            sessions,
            CreateSnapshots(fixture));

        var result = await SyncPlanExecutor.ExecuteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.execution.approval_required", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Deletion_limit_substitution_is_rejected_by_execution_approval()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.Delete(0, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var sessions = CreateSessions(fixture);
        var snapshots = CreateSnapshots(fixture);
        var approvedPolicy = DeletionSafetyPolicy.Default;
        var approval = SyncExecutionApproval.Create(
            plan,
            sessions,
            snapshots,
            deletionPolicy: approvedPolicy);
        var substitutedPolicy = new DeletionSafetyPolicy(
            maximumDeletionCount: 99,
            maximumDeletionPercentage: approvedPolicy.MaximumDeletionPercentage);
        var request = new SyncPlanExecutionRequest(
            plan,
            approval,
            sessions,
            snapshots,
            deletionPolicy: substitutedPolicy);

        var result = await SyncPlanExecutor.ExecuteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.execution.approval_mismatch", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task NonAtomic_write_policy_substitution_is_rejected_by_execution_approval()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.Delete(0, fixture.RightAddress("obsolete.bin", versionId: "v1")));
        var sessions = CreateSessions(fixture);
        var snapshots = CreateSnapshots(fixture);
        var approval = SyncExecutionApproval.Create(plan, sessions, snapshots);
        var request = new SyncPlanExecutionRequest(
            plan,
            approval,
            sessions,
            snapshots,
            transferOptions: new TransferExecutionOptions(AllowNonAtomicDestinationWrites: true));

        var result = await SyncPlanExecutor.ExecuteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("sync.execution.approval_mismatch", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task NonAtomic_write_policy_executes_new_file_on_legacy_destination()
    {
        var fixture = CreateFixture();
        fixture.Right.Capabilities = Capabilities(StorageFeature.WriteStream);
        var plan = CreatePlan(SyncPlanOperation.Copy(
            0,
            fixture.LeftAddress("source.bin"),
            fixture.RightAddress("source.bin"),
            fixture.Payload.LongLength));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(
            fixture,
            plan,
            transferOptions: new TransferExecutionOptions(
                BufferSize: 4_096,
                AllowNonAtomicDestinationWrites: true)));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.ExecutedOperations);
        Assert.Equal(fixture.Payload, fixture.Right.WrittenBytes);
    }

    [Fact]
    public async Task Overwrite_without_version_or_entity_tag_identity_is_rejected_in_full_preflight()
    {
        var fixture = CreateFixture();
        fixture.Right.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate,
            StorageFeature.ObjectVersioning,
            StorageFeature.TemporaryFiles,
            StorageFeature.FileMove,
            StorageFeature.AtomicRename);
        var plan = CreatePlan(
            SyncPlanOperation.Copy(
                0,
                fixture.LeftAddress("source.bin"),
                fixture.RightAddress("source.bin"),
                fixture.Payload.LongLength,
                destinationExisted: true));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(
            fixture,
            plan,
            transferOptions: new TransferExecutionOptions(Overwrite: true)));

        Assert.True(result.IsFailure);
        Assert.Equal("sync.overwrite.identity_required", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Theory]
    [InlineData(StorageFeature.Move)]
    [InlineData(StorageFeature.DirectoryMove)]
    public async Task Staged_file_overwrite_rejects_non_file_move_capabilities(
        StorageFeature nonFileMoveFeature)
    {
        var fixture = CreateFixture();
        fixture.Right.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate,
            StorageFeature.ObjectVersioning,
            StorageFeature.TemporaryFiles,
            nonFileMoveFeature,
            StorageFeature.AtomicRename);
        var plan = CreatePlan(SyncPlanOperation.Copy(
            0,
            fixture.LeftAddress("source.bin", versionId: "source-v1"),
            fixture.RightAddress("source.bin", versionId: "destination-v1"),
            fixture.Payload.LongLength,
            destinationExisted: true));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(
            fixture,
            plan,
            transferOptions: new TransferExecutionOptions(Overwrite: true)));

        Assert.True(result.IsFailure);
        Assert.Equal("sync.overwrite.atomic_unsupported", result.Error.Code);
        Assert.Contains(nameof(StorageFeature.FileMove), result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Preview_staged_file_overwrite_accepts_native_file_move()
    {
        var fixture = CreateFixture();
        fixture.Right.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate,
            StorageFeature.ObjectVersioning,
            StorageFeature.TemporaryFiles,
            StorageFeature.FileMove,
            StorageFeature.AtomicRename);
        var plan = CreatePlan(SyncPlanOperation.Copy(
            0,
            fixture.LeftAddress("source.bin", versionId: "source-v1"),
            fixture.RightAddress("source.bin", versionId: "destination-v1"),
            fixture.Payload.LongLength,
            destinationExisted: true));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(
            fixture,
            plan,
            SyncPlanExecutionMode.Preview,
            transferOptions: new TransferExecutionOptions(Overwrite: true)));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Unsupported_late_operation_blocks_plan_before_earlier_copy()
    {
        var fixture = CreateFixture();
        fixture.Right.Capabilities = Capabilities(
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate);
        var plan = CreatePlan(
            SyncPlanOperation.Copy(
                0,
                fixture.LeftAddress("source.bin"),
                fixture.RightAddress("source.bin"),
                fixture.Payload.LongLength),
            SyncPlanOperation.CreateDirectory(1, fixture.RightAddress("folder")));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(fixture, plan));

        Assert.True(result.IsFailure);
        Assert.Equal("sync.plan.operation_unsupported", result.Error.Code);
        Assert.Equal(StorageFailureKind.Unsupported, result.Error.Kind);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Provider_conflict_stops_following_operations()
    {
        var fixture = CreateFixture();
        fixture.Left.GetEntryFailure = new StorageFailure(
            "storage.version.conflict",
            StorageFailureKind.Conflict,
            "The source version changed.");
        var plan = CreatePlan(
            SyncPlanOperation.Copy(
                0,
                fixture.LeftAddress("source.bin", versionId: "v1"),
                fixture.RightAddress("source.bin"),
                fixture.Payload.LongLength),
            SyncPlanOperation.CreateDirectory(1, fixture.RightAddress("must-not-run")));

        var result = await SyncPlanExecutor.ExecuteAsync(CreateRequest(fixture, plan));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.version.conflict", result.Error.Code);
        Assert.Equal(0, fixture.Right.CreateDirectoryCalls);
        Assert.Empty(fixture.Right.SideEffects);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_has_no_side_effects_when_already_requested()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(SyncPlanOperation.Delete(0, fixture.RightAddress("obsolete.bin")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await SyncPlanExecutor.ExecuteAsync(
            CreateRequest(fixture, plan),
            cancellationToken: cancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(StorageFailureKind.Cancelled, result.Error.Kind);
        Assert.Equal("sync.execution.cancelled", result.Error.Code);
        Assert.Equal(0, fixture.TotalProviderCalls);
    }

    [Fact]
    public async Task Cancellation_between_operations_stops_the_remaining_plan()
    {
        var fixture = CreateFixture();
        var plan = CreatePlan(
            SyncPlanOperation.CreateDirectory(0, fixture.RightAddress("created")),
            SyncPlanOperation.Delete(1, fixture.RightAddress("must-not-delete", versionId: "v1")));
        using var cancellation = new CancellationTokenSource();
        var events = new CancellingProgress(cancellation);

        var result = await SyncPlanExecutor.ExecuteAsync(
            CreateRequest(fixture, plan),
            events,
            cancellationToken: cancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(StorageFailureKind.Cancelled, result.Error.Kind);
        Assert.Equal(1, fixture.Right.CreateDirectoryCalls);
        Assert.Equal(0, fixture.Right.DeleteCalls);
        Assert.Equal(SyncPlanExecutionEventKind.PlanCancelled, events.Events[^1].Kind);
        Assert.Equal(1, events.Events[^1].ProcessedOperations);
    }

    private static ImmutableSyncPlan CreatePlan(params SyncPlanOperation[] operations) =>
        ImmutableSyncPlan.Create(
            OperationPlanId.New(),
            SyncProfileId.New(),
            baselineGeneration: 7,
            operations,
            createdAtUtc: DateTimeOffset.UnixEpoch);

    private static SyncPlanExecutionRequest CreateRequest(
        Fixture fixture,
        ImmutableSyncPlan plan,
        SyncPlanExecutionMode mode = SyncPlanExecutionMode.Execute,
        SyncExecutionSnapshots? snapshots = null,
        DeletionSafetyPolicy? deletionPolicy = null,
        TransferExecutionOptions? transferOptions = null)
    {
        var sessions = CreateSessions(fixture);
        snapshots ??= CreateSnapshots(fixture);
        deletionPolicy ??= DeletionSafetyPolicy.Default;
        transferOptions ??= new TransferExecutionOptions(Overwrite: false, BufferSize: 4_096);
        var approval = SyncExecutionApproval.Create(
            plan,
            sessions,
            snapshots,
            mode,
            deletionPolicy,
            transferOptions);
        return new SyncPlanExecutionRequest(
            plan,
            approval,
            sessions,
            snapshots,
            mode,
            deletionPolicy,
            transferOptions);
    }

    private static Dictionary<ConnectionProfileId, IStorageEndpointSession> CreateSessions(
        Fixture fixture) => new Dictionary<ConnectionProfileId, IStorageEndpointSession>
        {
            [fixture.Left.ProfileId] = fixture.Left,
            [fixture.Right.ProfileId] = fixture.Right,
        };

    private static SyncExecutionSnapshots CreateSnapshots(
        Fixture fixture,
        SnapshotCompleteness? left = null,
        SnapshotCompleteness? right = null) => new(
        left ?? SnapshotCompleteness.Complete(100),
        right ?? SnapshotCompleteness.Complete(100),
        baselineItemCount: 100,
        new Dictionary<ConnectionProfileId, string>
        {
            [fixture.Left.ProfileId] = fixture.Left.RootIdentity,
            [fixture.Right.ProfileId] = fixture.Right.RootIdentity,
        });

    private static Fixture CreateFixture()
    {
        var left = new FakeSession(ConnectionProfileId.New(), "left-root-v1");
        var right = new FakeSession(ConnectionProfileId.New(), "right-root-v1");
        var payload = Enumerable.Range(0, 16_000).Select(index => (byte)(index % 251)).ToArray();
        left.ReadBytes = payload;
        left.EntryFactory = address => StorageEntry.Create(
            address,
            StorageEntryKind.File,
            payload.LongLength).Value;
        return new Fixture(left, right, payload);
    }

    private static EffectiveStorageCapabilities Capabilities(params StorageFeature[] features) =>
        new(features.Select(feature =>
            new KeyValuePair<StorageFeature, FeatureSupport>(feature, FeatureSupport.Native())));

    private sealed record Fixture(FakeSession Left, FakeSession Right, byte[] Payload)
    {
        public int TotalProviderCalls => Left.TotalProviderCalls + Right.TotalProviderCalls;

        public StorageAddress LeftAddress(string path, string? versionId = null) =>
            StorageAddress.Create(Left.ProfileId, Left.RootIdentity, path, versionId: versionId).Value;

        public StorageAddress RightAddress(string path, string? versionId = null) =>
            StorageAddress.Create(Right.ProfileId, Right.RootIdentity, path, versionId: versionId).Value;
    }

    private sealed class RecordingProgress : IProgress<SyncPlanExecutionEvent>
    {
        public List<SyncPlanExecutionEvent> Events { get; } = [];

        public void Report(SyncPlanExecutionEvent value) => Events.Add(value);
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation)
        : IProgress<SyncPlanExecutionEvent>
    {
        public List<SyncPlanExecutionEvent> Events { get; } = [];

        public void Report(SyncPlanExecutionEvent value)
        {
            Events.Add(value);
            if (value.Kind == SyncPlanExecutionEventKind.OperationCompleted)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class FakeSession(ConnectionProfileId profileId, string rootIdentity) : IStorageEndpointSession
    {
        public ConnectionProfileId ProfileId { get; } = profileId;
        public string RootIdentity { get; } = rootIdentity;
        public EffectiveStorageCapabilities Capabilities { get; set; } = SyncPlanExecutorTests.Capabilities(
            StorageFeature.ReadStream,
            StorageFeature.WriteStream,
            StorageFeature.ConditionalCreate,
            StorageFeature.CreateDirectory,
            StorageFeature.Delete,
            StorageFeature.ConditionalDelete,
            StorageFeature.ObjectVersioning);
        public byte[] ReadBytes { get; set; } = [];
        public byte[] WrittenBytes => LastWriteHandle?.Bytes ?? [];
        public Func<StorageAddress, StorageEntry>? EntryFactory { get; set; }
        public StorageFailure? GetEntryFailure { get; set; }
        public FakeWriteHandle? LastWriteHandle { get; private set; }
        public List<string> SideEffects { get; } = [];
        public int HealthCalls { get; private set; }
        public int GetEntryCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int OpenReadCalls { get; private set; }
        public int OpenWriteCalls { get; private set; }
        public int CreateDirectoryCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int TotalProviderCalls =>
            HealthCalls + GetEntryCalls + ListCalls + OpenReadCalls + OpenWriteCalls +
            CreateDirectoryCalls + DeleteCalls;

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            return ValueTask.FromResult(StorageResult.Success());
        }

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            GetEntryCalls++;
            return ValueTask.FromResult(GetEntryFailure is not null
                ? StorageResult<StorageEntry>.Fail(GetEntryFailure)
                : StorageResult<StorageEntry>.Success(
                    EntryFactory?.Invoke(address) ?? StorageEntry.Create(
                        address,
                        StorageEntryKind.File,
                        ReadBytes.LongLength).Value));
        }

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            throw new NotSupportedException();
        }

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenReadCalls++;
            return ValueTask.FromResult(StorageResult<Stream>.Success(
                new MemoryStream(ReadBytes, writable: false)));
        }

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenWriteCalls++;
            SideEffects.Add($"write:{request.Destination.CanonicalRelativePath}");
            LastWriteHandle = new FakeWriteHandle(request.Destination);
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Success(LastWriteHandle));
        }

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            CreateDirectoryCalls++;
            SideEffects.Add($"mkdir:{address.CanonicalRelativePath}");
            return ValueTask.FromResult(StorageResult<StorageEntry>.Success(
                StorageEntry.Create(address, StorageEntryKind.Directory).Value));
        }

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            SideEffects.Add($"delete:{request.Address.CanonicalRelativePath}");
            return ValueTask.FromResult(StorageResult.Success());
        }

        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWriteHandle(StorageAddress destination) : IStorageWriteHandle
    {
        private readonly MemoryStream _content = new();

        public StorageAddress Destination { get; } = destination;
        public Stream Content => _content;
        public long AcceptedOffset => 0;
        public string? ResumeToken => null;
        public StorageWriteHandleState State { get; private set; } = StorageWriteHandleState.Open;
        public byte[] Bytes => _content.ToArray();

        public ValueTask<StorageResult<StorageEntry>> CommitAsync(
            CancellationToken cancellationToken = default)
        {
            State = StorageWriteHandleState.Committed;
            return ValueTask.FromResult(StorageResult<StorageEntry>.Success(
                StorageEntry.Create(Destination, StorageEntryKind.File, _content.Length).Value));
        }

        public ValueTask<StorageResult> AbortAsync(CancellationToken cancellationToken = default)
        {
            State = StorageWriteHandleState.Aborted;
            return ValueTask.FromResult(StorageResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            _content.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
