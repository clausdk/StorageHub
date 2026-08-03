using System.Globalization;
using System.Security.Cryptography;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Persistence;
using StorageHub.Persistence.Transfers;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Transfers;

namespace StorageHub.Agent.IntegrationTests;

public sealed class TransferQueueAgentSubsystemTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-transfer-worker-{Guid.NewGuid():N}");

    [Fact]
    public async Task Claim_executes_streaming_copy_persists_progress_and_completes_fenced_job()
    {
        var payload = Enumerable.Range(0, 32_768).Select(index => (byte)(index % 251)).ToArray();
        var sourceDigest = new PortableContentDigest(
            PortableChecksumAlgorithm.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(payload)));
        var fixture = await CreateFixtureAsync(sourceDigest);
        var source = new FakeSession(
            fixture.Intent.Source.ProfileId,
            fixture.Intent.Source.RootIdentity,
            Capabilities(StorageFeature.ReadStream))
        {
            Entry = StorageEntry.Create(
                fixture.Intent.Source,
                StorageEntryKind.File,
                payload.LongLength).Value,
            ReadStreamFactory = () => new SlowMemoryStream(payload, TimeSpan.FromMilliseconds(2))
        };
        var destination = new FakeSession(
            fixture.Intent.Destination.ProfileId,
            fixture.Intent.Destination.RootIdentity,
            Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate));
        var connector = new FakeConnector(new Dictionary<ConnectionProfileId, Func<FakeConnection>>
        {
            [source.ProfileId] = () => new FakeConnection(source),
            [destination.ProfileId] = () => new FakeConnection(destination)
        });
        await using var worker = CreateWorker(fixture.Store, connector);

        await worker.InitializeAsync(CancellationToken.None);
        Assert.True(await worker.RunClaimOnceAsync());

        var completed = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Completed, completed.State.State);
        Assert.Equal(1, completed.State.Attempt);
        Assert.Null(completed.ActiveLease);
        Assert.Null(await fixture.InnerStore.FindCheckpointAsync(fixture.Intent.TransferJobId));
        Assert.True(fixture.Store.SaveCheckpointCalls > 0);
        Assert.Equal(sourceDigest.AlgorithmName, fixture.Store.LastCheckpoint!.SourceDigest!.Algorithm);
        Assert.Equal(sourceDigest.Value, fixture.Store.LastCheckpoint.SourceDigest.Value);
        Assert.Equal(payload, destination.WrittenBytes);
        Assert.True(source.ConnectionDisposeCount > 0);
        Assert.True(destination.ConnectionDisposeCount > 0);
    }

    [Fact]
    public async Task Transient_connection_failure_enters_delayed_retry_and_releases_lease()
    {
        var fixture = await CreateFixtureAsync();
        var connector = new FailureConnector(new StorageFailure(
            "storage.connection.unavailable",
            StorageFailureKind.Unavailable,
            "Endpoint unavailable.",
            isTransient: true));
        var timeProvider = new MutableUtcTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = CreateWorker(fixture.Store, connector, timeProvider: timeProvider);

        await worker.InitializeAsync(CancellationToken.None);
        fixture.Store.AfterRecovery = () => timeProvider.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await worker.RunClaimOnceAsync());

        var retrying = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Retrying, retrying.State.State);
        Assert.NotNull(retrying.RetryAvailableAtUtc);
        Assert.Null(retrying.ActiveLease);
        Assert.Equal("transfer.retry.transient", retrying.LastError?.Code);
    }

    [Fact]
    public async Task Graceful_stop_cancels_provider_io_and_records_interrupted_while_lease_is_owned()
    {
        var fixture = await CreateFixtureAsync();
        var blockingStream = new BlockingReadStream();
        var source = new FakeSession(
            fixture.Intent.Source.ProfileId,
            fixture.Intent.Source.RootIdentity,
            Capabilities(StorageFeature.ReadStream))
        {
            Entry = StorageEntry.Create(
                fixture.Intent.Source,
                StorageEntryKind.File,
                fixture.Intent.ExpectedLength).Value,
            ReadStreamFactory = () => blockingStream
        };
        var destination = new FakeSession(
            fixture.Intent.Destination.ProfileId,
            fixture.Intent.Destination.RootIdentity,
            Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate));
        var connector = new FakeConnector(new Dictionary<ConnectionProfileId, Func<FakeConnection>>
        {
            [source.ProfileId] = () => new FakeConnection(source),
            [destination.ProfileId] = () => new FakeConnection(destination)
        });
        await using var worker = CreateWorker(fixture.Store, connector);
        await worker.InitializeAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);
        await blockingStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var interrupted = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Interrupted, interrupted.State.State);
        Assert.Equal(TransferStatusCode.Interrupted, interrupted.State.StatusCode);
        Assert.Null(interrupted.ActiveLease);
        Assert.True(destination.LastWriteHandle?.Aborted);
    }

    [Fact]
    public async Task Active_user_cancel_is_revision_checked_and_worker_records_fenced_cancelled_state()
    {
        var fixture = await CreateFixtureAsync();
        var blockingStream = new BlockingReadStream();
        var source = new FakeSession(
            fixture.Intent.Source.ProfileId,
            fixture.Intent.Source.RootIdentity,
            Capabilities(StorageFeature.ReadStream))
        {
            Entry = StorageEntry.Create(
                fixture.Intent.Source,
                StorageEntryKind.File,
                fixture.Intent.ExpectedLength).Value,
            ReadStreamFactory = () => blockingStream
        };
        var destination = new FakeSession(
            fixture.Intent.Destination.ProfileId,
            fixture.Intent.Destination.RootIdentity,
            Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate));
        var connector = new FakeConnector(new Dictionary<ConnectionProfileId, Func<FakeConnection>>
        {
            [source.ProfileId] = () => new FakeConnection(source),
            [destination.ProfileId] = () => new FakeConnection(destination)
        });
        await using var worker = CreateWorker(fixture.Store, connector);
        await worker.InitializeAsync(CancellationToken.None);

        var execution = worker.RunClaimOnceAsync().AsTask();
        await blockingStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));

        Assert.Equal(
            ActiveTransferCancellationResult.RevisionConflict,
            worker.TryRequestActiveCancellation(
                fixture.Intent.TransferJobId,
                active.State.Revision - 1));
        Assert.Equal(
            ActiveTransferCancellationResult.Accepted,
            worker.TryRequestActiveCancellation(
                fixture.Intent.TransferJobId,
                active.State.Revision));
        Assert.Equal(
            ActiveTransferCancellationResult.AlreadyRequested,
            worker.TryRequestActiveCancellation(
                fixture.Intent.TransferJobId,
                active.State.Revision));
        Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(5)));

        var cancelled = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Cancelled, cancelled.State.State);
        Assert.Null(cancelled.ActiveLease);
        Assert.True(destination.LastWriteHandle?.Aborted);
        Assert.Equal(
            ActiveTransferCancellationResult.NotActive,
            worker.TryRequestActiveCancellation(
                fixture.Intent.TransferJobId,
                cancelled.State.Revision));
    }

    [Fact]
    public async Task Renewal_loss_cancels_io_and_stale_worker_does_not_record_terminal_state()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Store.LoseRenewals = true;
        var blockingStream = new BlockingReadStream();
        var source = new FakeSession(
            fixture.Intent.Source.ProfileId,
            fixture.Intent.Source.RootIdentity,
            Capabilities(StorageFeature.ReadStream))
        {
            Entry = StorageEntry.Create(
                fixture.Intent.Source,
                StorageEntryKind.File,
                fixture.Intent.ExpectedLength).Value,
            ReadStreamFactory = () => blockingStream
        };
        var destination = new FakeSession(
            fixture.Intent.Destination.ProfileId,
            fixture.Intent.Destination.RootIdentity,
            Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate));
        var connector = new FakeConnector(new Dictionary<ConnectionProfileId, Func<FakeConnection>>
        {
            [source.ProfileId] = () => new FakeConnection(source),
            [destination.ProfileId] = () => new FakeConnection(destination)
        });
        await using var worker = CreateWorker(
            fixture.Store,
            connector,
            Options() with { LeaseRenewalInterval = TimeSpan.FromMilliseconds(20) });
        await worker.InitializeAsync(CancellationToken.None);

        Assert.True(await worker.RunClaimOnceAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        var stillOwned = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Transferring, stillOwned.State.State);
        Assert.NotNull(stillOwned.ActiveLease);
        Assert.True(destination.LastWriteHandle?.Aborted);
        var health = await worker.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Degraded, health.Level);
        Assert.Contains("lost", health.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initialization_recovers_expired_owner_to_interrupted_without_unsafe_restart()
    {
        var fixture = await CreateFixtureAsync();
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var claim = Assert.IsType<TransferJobClaim>(await fixture.InnerStore.TryClaimNextAsync(
            new TransferClaimRequest("expired-owner", claimedAt, TimeSpan.FromMinutes(1))));
        Assert.Equal(TransferState.Preparing, claim.Job.State.State);
        await using var worker = CreateWorker(
            fixture.Store,
            new FailureConnector(new StorageFailure(
                "unused",
                StorageFailureKind.Unexpected,
                "Unused.")));

        var result = await worker.InitializeAsync(CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(1, worker.RecoveredInterruptedCount);
        var recovered = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Interrupted, recovered.State.State);
        Assert.Null(recovered.ActiveLease);
    }

    [Fact]
    public async Task Background_workers_never_exceed_configured_concurrency()
    {
        var fixture = await CreateFixtureAsync();
        var second = CreateSiblingIntent(fixture.Intent, "second.bin");
        var third = CreateSiblingIntent(fixture.Intent, "third.bin");
        Assert.True(await fixture.InnerStore.TryEnqueueAsync(second));
        Assert.True(await fixture.InnerStore.TryEnqueueAsync(third));
        var entered = 0;
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSession(
            fixture.Intent.Source.ProfileId,
            fixture.Intent.Source.RootIdentity,
            Capabilities(StorageFeature.ReadStream))
        {
            Entry = StorageEntry.Create(
                fixture.Intent.Source,
                StorageEntryKind.File,
                fixture.Intent.ExpectedLength).Value,
            ReadStreamFactory = () => new CountingBlockingReadStream(() =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    twoEntered.TrySetResult();
                }
            })
        };
        var destination = new FakeSession(
            fixture.Intent.Destination.ProfileId,
            fixture.Intent.Destination.RootIdentity,
            Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate));
        var connector = new FakeConnector(new Dictionary<ConnectionProfileId, Func<FakeConnection>>
        {
            [source.ProfileId] = () => new FakeConnection(source),
            [destination.ProfileId] = () => new FakeConnection(destination)
        });
        await using var worker = CreateWorker(
            fixture.Store,
            connector,
            Options() with { MaximumConcurrency = 2 });
        await worker.InitializeAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);

        await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, worker.ActiveExecutionCount);
        var states = await Task.WhenAll(
            fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId).AsTask(),
            fixture.InnerStore.FindAsync(second.TransferJobId).AsTask(),
            fixture.InnerStore.FindAsync(third.TransferJobId).AsTask());
        Assert.Equal(2, states.Count(job => job?.State.State == TransferState.Transferring));
        Assert.Equal(1, states.Count(job => job?.State.State == TransferState.Pending));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Per_connection_limit_applies_before_provider_io()
    {
        var fixture = await CreateFixtureAsync();
        Assert.True(await fixture.InnerStore.TryEnqueueAsync(CreateSiblingIntent(fixture.Intent, "second.bin")));
        var entered = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSession(
            fixture.Intent.Source.ProfileId,
            fixture.Intent.Source.RootIdentity,
            Capabilities(StorageFeature.ReadStream))
        {
            Entry = StorageEntry.Create(
                fixture.Intent.Source,
                StorageEntryKind.File,
                fixture.Intent.ExpectedLength).Value,
            ReadStreamFactory = () => new CountingBlockingReadStream(() =>
            {
                Interlocked.Increment(ref entered);
                firstEntered.TrySetResult();
            })
        };
        var destination = new FakeSession(
            fixture.Intent.Destination.ProfileId,
            fixture.Intent.Destination.RootIdentity,
            Capabilities(StorageFeature.WriteStream, StorageFeature.ConditionalCreate));
        var connector = new FakeConnector(new Dictionary<ConnectionProfileId, Func<FakeConnection>>
        {
            [source.ProfileId] = () => new FakeConnection(source),
            [destination.ProfileId] = () => new FakeConnection(destination)
        });
        await using var worker = CreateWorker(
            fixture.Store,
            connector,
            Options() with
            {
                MaximumConcurrency = 2,
                PerConnectionConcurrency = 1
            });
        await worker.InitializeAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.Equal(1, worker.ActiveExecutionCount);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Polling_recovers_a_lease_that_expires_after_initialization()
    {
        var fixture = await CreateFixtureAsync();
        await using var worker = CreateWorker(
            fixture.Store,
            new FailureConnector(new StorageFailure(
                "unused",
                StorageFailureKind.Unexpected,
                "Unused.")));
        await worker.InitializeAsync(CancellationToken.None);
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        _ = Assert.IsType<TransferJobClaim>(await fixture.InnerStore.TryClaimNextAsync(
            new TransferClaimRequest("expired-after-start", claimedAt, TimeSpan.FromMinutes(1))));

        Assert.False(await worker.RunClaimOnceAsync());

        var recovered = Assert.IsType<DurableTransferJob>(
            await fixture.InnerStore.FindAsync(fixture.Intent.TransferJobId));
        Assert.Equal(TransferState.Interrupted, recovered.State.State);
        Assert.Equal(1, worker.RecoveredInterruptedCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<Fixture> CreateFixtureAsync(PortableContentDigest? expectedSourceDigest = null)
    {
        var options = new SqliteDatabaseOptions(Path.Combine(_directory, "storagehub.db"), pooling: false);
        var initialization = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialization.IsReady, initialization.Message);
        var database = new SingleWriterSqliteDatabase(options);
        var sourceProfile = ConnectionProfileId.New();
        var destinationProfile = ConnectionProfileId.New();
        await SeedProfileAsync(database, sourceProfile, $"Source-{sourceProfile}");
        await SeedProfileAsync(database, destinationProfile, $"Destination-{destinationProfile}");
        var source = Address(sourceProfile, "source-root", "folder/source.bin");
        var destination = Address(destinationProfile, "destination-root", "folder/destination.bin");
        var intent = new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            source,
            destination,
            expectedLength: 32_768,
            TransferVerificationPolicy.Size,
            DateTimeOffset.UtcNow,
            expectedSourceDigest: expectedSourceDigest);
        var innerStore = new SqliteTransferJobStore(database);
        Assert.True(await innerStore.TryEnqueueAsync(intent));
        var store = new RecordingTransferStore(innerStore);
        return new Fixture(database, innerStore, store, intent);
    }

    private static TransferQueueAgentSubsystem CreateWorker(
        ITransferJobStore store,
        ITransferEndpointConnector connector,
        TransferQueueWorkerOptions? options = null,
        TimeProvider? timeProvider = null) => new(
        store,
        connector,
        options ?? Options(),
        timeProvider,
        ownerId: $"test-worker-{Guid.NewGuid():N}");

    private static TransferQueueWorkerOptions Options() => new()
    {
        MaximumConcurrency = 1,
        PollInterval = TimeSpan.FromMilliseconds(10),
        LeaseDuration = TimeSpan.FromMinutes(2),
        LeaseRenewalInterval = TimeSpan.FromMilliseconds(250),
        CheckpointInterval = TimeSpan.FromMilliseconds(5),
        InitialRetryDelay = TimeSpan.FromMilliseconds(200),
        MaximumRetryDelay = TimeSpan.FromSeconds(1),
        MaximumAttempts = 3,
        BufferSize = 1_024
    };

    private static EffectiveStorageCapabilities Capabilities(params StorageFeature[] features) => new(
        features.Select(feature => new KeyValuePair<StorageFeature, FeatureSupport>(
            feature,
            FeatureSupport.Native())));

    private static StorageAddress Address(ConnectionProfileId profileId, string root, string path) =>
        StorageAddress.Create(profileId, root, path).Value;

    private static TransferIntent CreateSiblingIntent(TransferIntent template, string destinationName) => new(
        TransferJobId.New(),
        template.Operation,
        template.Source,
        Address(
            template.Destination.ProfileId,
            template.Destination.RootIdentity,
            $"folder/{destinationName}"),
        template.ExpectedLength,
        template.VerificationPolicy,
        template.CreatedAtUtc);

    private static async Task SeedProfileAsync(
        SingleWriterSqliteDatabase database,
        ConnectionProfileId profileId,
        string name)
    {
        await using var writer = await database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_profiles
            (
                profile_id, provider, display_name, tags_json, metadata_json, endpoint_json,
                authentication_json, operational_options_json, is_favorite, is_enabled,
                version, created_utc, updated_utc
            )
            VALUES ($id, 'local', $name, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync();
    }

    private sealed record Fixture(
        SingleWriterSqliteDatabase Database,
        SqliteTransferJobStore InnerStore,
        RecordingTransferStore Store,
        TransferIntent Intent);

    private sealed class FakeConnector(
        IReadOnlyDictionary<ConnectionProfileId, Func<FakeConnection>> factories)
        : ITransferEndpointConnector
    {
        public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
            ConnectionProfileId profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(factories.TryGetValue(profileId, out var factory)
                ? StorageResult<ITransferEndpointConnection>.Success(factory())
                : StorageResult<ITransferEndpointConnection>.Fail(new StorageFailure(
                    "storage.profile.not_found",
                    StorageFailureKind.NotFound,
                    "Profile not found.")));
        }
    }

    private sealed class FailureConnector(StorageFailure failure) : ITransferEndpointConnector
    {
        public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
            ConnectionProfileId profileId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            StorageResult<ITransferEndpointConnection>.Fail(failure));
    }

    private sealed class MutableUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _utcTicks = utcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(elapsed, TimeSpan.Zero);
            _ = Interlocked.Add(ref _utcTicks, elapsed.Ticks);
        }
    }

    private sealed class FakeConnection(FakeSession session) : ITransferEndpointConnection
    {
        public IStorageEndpointSession Session => session;

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.Increment(ref session.ConnectionDisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession(
        ConnectionProfileId profileId,
        string rootIdentity,
        EffectiveStorageCapabilities capabilities) : IStorageEndpointSession
    {
        public ConnectionProfileId ProfileId { get; } = profileId;
        public string RootIdentity { get; } = rootIdentity;
        public EffectiveStorageCapabilities Capabilities { get; } = capabilities;
        public StorageEntry? Entry { get; init; }
        public Func<Stream>? ReadStreamFactory { get; init; }
        public FakeWriteHandle? LastWriteHandle { get; private set; }
        public byte[] WrittenBytes => LastWriteHandle?.Bytes ?? [];
        public int ConnectionDisposeCount;

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Success());

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            Entry is null
                ? StorageResult<StorageEntry>.Fail(new StorageFailure(
                    "storage.not_found",
                    StorageFailureKind.NotFound,
                    "Not found."))
                : StorageResult<StorageEntry>.Success(Entry));

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            StorageResult<Stream>.Success(ReadStreamFactory?.Invoke() ?? new MemoryStream()));

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastWriteHandle = new FakeWriteHandle(request.Destination);
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Success(LastWriteHandle));
        }

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
        public StorageWriteHandleState State { get; private set; }
        public bool Aborted { get; private set; }
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
            Aborted = true;
            State = StorageWriteHandleState.Aborted;
            return ValueTask.FromResult(StorageResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            _content.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SlowMemoryStream(byte[] buffer, TimeSpan delay) : MemoryStream(buffer, writable: false)
    {
        public override async Task<int> ReadAsync(
            byte[] destination,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(destination.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(destination, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class CountingBlockingReadStream(Action onRead) : Stream
    {
        private int _started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            SignalStarted();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            SignalStarted();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        private void SignalStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                onRead();
            }
        }
    }

    private sealed class RecordingTransferStore(ITransferJobStore inner) : ITransferJobStore
    {
        public int SaveCheckpointCalls;
        public TransferCheckpoint? LastCheckpoint;
        public bool LoseRenewals { get; set; }
        public Action? AfterRecovery { get; set; }

        public ValueTask<bool> TryEnqueueAsync(TransferIntent intent, int priority = 0, CancellationToken cancellationToken = default) =>
            inner.TryEnqueueAsync(intent, priority, cancellationToken);

        public ValueTask<DurableTransferJob?> FindAsync(TransferJobId transferJobId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(transferJobId, cancellationToken);

        public ValueTask<TransferJobClaim?> TryClaimNextAsync(TransferClaimRequest request, CancellationToken cancellationToken = default) =>
            inner.TryClaimNextAsync(request, cancellationToken);

        public ValueTask<TransferStoreResult<TransferJobLease>> TryRenewLeaseAsync(TransferLeaseRenewal renewal, CancellationToken cancellationToken = default) =>
            LoseRenewals
                ? ValueTask.FromResult(new TransferStoreResult<TransferJobLease>(TransferStoreMutationStatus.LeaseLost, null))
                : inner.TryRenewLeaseAsync(renewal, cancellationToken);

        public ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionAsync(TransferStateTransitionRequest request, CancellationToken cancellationToken = default) =>
            inner.TryTransitionAsync(request, cancellationToken);

        public ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionControlStateAsync(TransferControlStateTransitionRequest request, CancellationToken cancellationToken = default) =>
            inner.TryTransitionControlStateAsync(request, cancellationToken);

        public ValueTask<PersistedTransferCheckpoint?> FindCheckpointAsync(TransferJobId transferJobId, CancellationToken cancellationToken = default) =>
            inner.FindCheckpointAsync(transferJobId, cancellationToken);

        public ValueTask<TransferStoreResult<PersistedTransferCheckpoint>> TrySaveCheckpointAsync(TransferCheckpointWriteRequest request, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref SaveCheckpointCalls);
            LastCheckpoint = request.Checkpoint;
            return inner.TrySaveCheckpointAsync(request, cancellationToken);
        }

        public ValueTask<TransferStoreMutationStatus> TryClearCheckpointAsync(TransferCheckpointClearRequest request, CancellationToken cancellationToken = default) =>
            inner.TryClearCheckpointAsync(request, cancellationToken);

        public async ValueTask<int> RecoverInterruptedAsync(
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            var recovered = await inner.RecoverInterruptedAsync(observedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            AfterRecovery?.Invoke();
            return recovered;
        }
    }
}
