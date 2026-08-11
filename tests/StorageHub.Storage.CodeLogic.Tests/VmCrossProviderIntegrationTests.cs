using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage;
using CL.Storage.Configuration;
using CodeLogic;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using StorageHub.Transfers;

namespace StorageHub.Storage.CodeLogic.Tests;

[Collection(ProviderIntegrationFixtureGroup.Name)]
public sealed class VmCrossProviderIntegrationTests : IAsyncLifetime
{
    private readonly List<RuntimeStorageConnection> _connections = [];
    private string? _testRoot;
    private bool _started;

    public async Task InitializeAsync()
    {
        if (!Required())
        {
            return;
        }

        _testRoot = Path.Combine(Path.GetTempPath(), $"storagehub-vm-cross-{Guid.NewGuid():N}");
        var initialized = await global::CodeLogic.CodeLogic.InitializeAsync(options =>
        {
            options.FrameworkRootPath = Path.Combine(_testRoot, "framework");
            options.ApplicationRootPath = Path.Combine(_testRoot, "application");
            options.AppVersion = "test";
            options.HandleShutdownSignals = false;
        });
        Assert.True(initialized.Success);
        await Libraries.LoadAsync<StorageLibrary>();
        Libraries.OverrideConfig<StorageConfig>(
            "CL.Storage", "storage", configuration => configuration.Enabled = false);
        await global::CodeLogic.CodeLogic.ConfigureAsync();
        await global::CodeLogic.CodeLogic.StartAsync();
        _started = true;
    }

    [Fact]
    [Trait("Category", "VmCrossProviderIntegration")]
    public async Task RealVmStreamsSftpAndFtpsDirectlyIntoS3AndFailsClosedForUnsafeReverseCreates()
    {
        if (!Required())
        {
            return;
        }

        var endpoint = RequiredUri("STORAGEHUB_MINIO_ENDPOINT");
        var accessKey = RequiredValue("STORAGEHUB_MINIO_ACCESS_KEY");
        var secretKey = RequiredValue("STORAGEHUB_MINIO_SECRET_KEY");
        var bucket = RequiredValue("STORAGEHUB_MINIO_BUCKET");
        using (var s3Admin = new AmazonS3Client(
                   new BasicAWSCredentials(accessKey, secretKey),
                   new AmazonS3Config
                   {
                       ServiceURL = endpoint.AbsoluteUri,
                       AuthenticationRegion = "us-east-1",
                       ForcePathStyle = true,
                       MaxErrorRetry = 0
                   }))
        {
            try
            {
                await s3Admin.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            }
            catch (BucketAlreadyOwnedByYouException)
            {
                // A previous isolated conformance process may have created the shared lab bucket.
            }
        }

        var factory = new CodeLogicStorageSessionFactory(
            Libraries.Get<StorageLibrary>() ?? throw new InvalidOperationException("Storage library unavailable."));
        var s3 = await RegisterAsync(factory, new S3ConnectionConfig
        {
            Bucket = bucket,
            Prefix = $"cross-provider/{Guid.NewGuid():N}",
            ServiceUrl = endpoint.AbsoluteUri,
            Region = "us-east-1",
            AuthenticationMode = S3AuthenticationMode.StaticCredentials,
            AccessKey = accessKey,
            SecretKey = secretKey,
            ForcePathStyle = true,
            AllowInsecureHttp = true,
            TimeoutSeconds = 10,
            MaxRetries = 0,
            Enabled = true
        }, "s3");
        var sftp = await RegisterAsync(factory, new SftpConnectionConfig
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = RequiredPort("STORAGEHUB_SFTP_PASSWORD_PORT"),
            Root = "mounted",
            Username = RequiredValue("STORAGEHUB_SFTP_USERNAME"),
            AuthenticationMode = SftpAuthenticationMode.Password,
            Password = RequiredValue("STORAGEHUB_SFTP_PASSWORD"),
            HostKeyFingerprints = [RequiredValue("STORAGEHUB_SFTP_HOST_SHA256")],
            TimeoutSeconds = 10
        }, "sftp");
        var ftps = await RegisterAsync(factory, new FtpConnectionConfig
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = RequiredPort("STORAGEHUB_FTP_EXPLICIT_PORT"),
            Root = "mounted",
            EncryptionMode = StorageFtpEncryptionMode.Explicit,
            DataConnectionMode = StorageFtpDataConnectionMode.Pasv,
            Username = RequiredValue("STORAGEHUB_FTP_USERNAME"),
            Password = RequiredValue("STORAGEHUB_FTP_PASSWORD"),
            TrustedCertificateSha256 = [RequiredValue("STORAGEHUB_FTP_SERVER_SHA256")],
            TimeoutSeconds = 10
        }, "ftps");

        await AssertTransferToS3Async(sftp, s3, "sftp-to-s3.bin", 173_111, 239);
        await AssertTransferToS3Async(ftps, s3, "ftps-to-s3.bin", 211_337, 251);
        await AssertRealSyncEngineAsync(sftp, s3);
        await AssertUnsafeNewDestinationFailsClosedAsync(s3, sftp, "s3-to-sftp-must-not-start.bin");
        await AssertUnsafeNewDestinationFailsClosedAsync(s3, ftps, "s3-to-ftps-must-not-start.bin");
        await AssertNonAtomicCompatibilityTransferAsync(s3, sftp, "s3-to-sftp-opt-in.bin");
        await AssertNonAtomicCompatibilityTransferAsync(s3, ftps, "s3-to-ftps-opt-in.bin");
    }

    private static async Task AssertRealSyncEngineAsync(
        RegisteredEndpoint source,
        RegisteredEndpoint destination)
    {
        var runId = Guid.NewGuid().ToString("N");
        var sourceRoot = Address(source, $"sync-source/{runId}");
        var destinationRoot = Address(destination, $"sync-destination/{runId}");
        await CreateDirectoryTreeAsync(source.Connection.Session, sourceRoot);
        await CreateDirectoryTreeAsync(destination.Connection.Session, destinationRoot);

        var first = Enumerable.Range(0, 81_337).Select(index => (byte)(index % 241)).ToArray();
        var second = Enumerable.Range(0, 19_777).Select(index => (byte)(index % 197)).ToArray();
        var nested = Enumerable.Range(0, 41_123).Select(index => (byte)(index % 181)).ToArray();
        await CreateDirectoryTreeAsync(source.Connection.Session, Child(sourceRoot, "nested/deeper"));
        await CreateDirectoryTreeAsync(source.Connection.Session, Child(sourceRoot, "empty"));
        await WriteAsync(source.Connection.Session, Child(sourceRoot, "alpha.bin"), first);
        await WriteAsync(source.Connection.Session, Child(sourceRoot, "beta.bin"), second);
        await WriteAsync(source.Connection.Session, Child(sourceRoot, "nested/deeper/gamma.bin"), nested);

        var scanOptions = new SyncSnapshotScanOptions(
            pageSize: 2,
            portableHashMode: SyncPortableHashMode.AllFiles,
            maximumConcurrentHashes: 2);
        var leftScan = await SyncSnapshotScanner.ScanAsync(
            source.Connection.Session, sourceRoot, scanOptions);
        var rightScan = await SyncSnapshotScanner.ScanAsync(
            destination.Connection.Session, destinationRoot, scanOptions);
        Assert.True(leftScan.IsSuccess, Failure(leftScan.Error));
        Assert.True(rightScan.IsSuccess, Failure(rightScan.Error));

        var profileId = SyncProfileId.New();
        var now = DateTimeOffset.UtcNow;
        var profile = new SyncProfile(
            profileId,
            "Real VM SFTP to S3",
            source.ProfileId,
            sourceRoot.CanonicalRelativePath,
            destination.ProfileId,
            destinationRoot.CanonicalRelativePath,
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror,
            SyncConflictPolicy.Block,
            new DeletionSafetyPolicy(maximumDeletionCount: 100, maximumDeletionPercentage: 50),
            new TransferExecutionOptions(Overwrite: true, BufferSize: 32 * 1024),
            enabled: true,
            revision: 1,
            createdAtUtc: now,
            updatedAtUtc: now);
        var built = SyncPlanBuilder.Build(new SyncPlanBuildRequest(
            OperationPlanId.New(),
            profileId,
            baselineGeneration: 0,
            sourceRoot,
            destinationRoot,
            leftScan.Value,
            rightScan.Value,
            new Dictionary<string, SyncBaselineObservation>(),
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror,
            now));
        Assert.True(built.IsSuccess, Failure(built.Error));
        Assert.Equal(3, built.Value.Plan.Operations.Count(operation =>
            operation.Kind == SyncPlanOperationKind.Copy));
        Assert.True(built.Value.Plan.Operations.Count(operation =>
            operation.Kind == SyncPlanOperationKind.CreateDirectory) >= 3);

        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions =
            new Dictionary<ConnectionProfileId, IStorageEndpointSession>
            {
                [source.ProfileId] = source.Connection.Session,
                [destination.ProfileId] = destination.Connection.Session
            };
        var previewApproval = SyncExecutionApproval.Create(
            built.Value.Plan,
            sessions,
            built.Value.Snapshots,
            SyncPlanExecutionMode.Preview,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions,
            profile.PolicySha256);
        var preview = await SyncPlanExecutor.ExecuteAsync(new SyncPlanExecutionRequest(
            built.Value.Plan,
            previewApproval,
            sessions,
            built.Value.Snapshots,
            SyncPlanExecutionMode.Preview,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions,
            profile.PolicySha256));
        Assert.True(preview.IsSuccess, Failure(preview.Error));
        Assert.Equal(0, preview.Value.ExecutedOperations);

        var executeApproval = SyncExecutionApproval.Create(
            built.Value.Plan,
            sessions,
            built.Value.Snapshots,
            SyncPlanExecutionMode.Execute,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions,
            profile.PolicySha256);
        var executed = await SyncPlanExecutor.ExecuteAsync(new SyncPlanExecutionRequest(
            built.Value.Plan,
            executeApproval,
            sessions,
            built.Value.Snapshots,
            SyncPlanExecutionMode.Execute,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions,
            profile.PolicySha256));
        Assert.True(executed.IsSuccess, Failure(executed.Error));
        Assert.Equal(built.Value.Plan.Operations.Length, executed.Value.ExecutedOperations);
        Assert.Equal(first.LongLength + second.LongLength + nested.LongLength, executed.Value.BytesTransferred);
        Assert.Equal(first, await ReadAsync(
            destination.Connection.Session, Child(destinationRoot, "alpha.bin")));
        Assert.Equal(second, await ReadAsync(
            destination.Connection.Session, Child(destinationRoot, "beta.bin")));
        Assert.Equal(nested, await ReadAsync(
            destination.Connection.Session, Child(destinationRoot, "nested/deeper/gamma.bin")));

        var freshLeft = await SyncSnapshotScanner.ScanAsync(
            source.Connection.Session, sourceRoot, scanOptions);
        var freshRight = await SyncSnapshotScanner.ScanAsync(
            destination.Connection.Session, destinationRoot, scanOptions);
        Assert.True(freshLeft.IsSuccess, Failure(freshLeft.Error));
        Assert.True(freshRight.IsSuccess, Failure(freshRight.Error));
        var baseline = VerifiedSyncBaselineBuilder.Build(
            profile,
            built.Value.Plan,
            new SyncBaselineSnapshot(
                profileId,
                Generation: 0,
                Revision: 0,
                new Dictionary<string, SyncBaselineObservation>(),
                Sha256Digest: string.Empty,
                UpdatedAtUtc: now),
            freshLeft.Value,
            freshRight.Value);
        Assert.True(
            baseline.IsSuccess,
            $"{Failure(baseline.Error)} Left=[{DescribeSnapshot(freshLeft.Value)}] Right=[{DescribeSnapshot(freshRight.Value)}]");
        Assert.Equal(6, baseline.Value.Count);

        var updated = Enumerable.Range(0, 97_531).Select(index => (byte)(255 - index % 233)).ToArray();
        var added = Enumerable.Range(0, 27_777).Select(index => (byte)(index % 173)).ToArray();
        await WriteOverwriteAsync(source.Connection.Session, Child(sourceRoot, "alpha.bin"), updated);
        var removed = await source.Connection.Session.DeleteAsync(new StorageDeleteRequest(
            Child(sourceRoot, "beta.bin")));
        Assert.True(removed.IsSuccess, Failure(removed.Error));
        await WriteAsync(source.Connection.Session, Child(sourceRoot, "delta.bin"), added);

        var changedLeft = await SyncSnapshotScanner.ScanAsync(
            source.Connection.Session, sourceRoot, scanOptions);
        var unchangedRight = await SyncSnapshotScanner.ScanAsync(
            destination.Connection.Session, destinationRoot, scanOptions);
        Assert.True(changedLeft.IsSuccess, Failure(changedLeft.Error));
        Assert.True(unchangedRight.IsSuccess, Failure(unchangedRight.Error));
        var changedPlan = SyncPlanBuilder.Build(new SyncPlanBuildRequest(
            OperationPlanId.New(),
            profileId,
            baselineGeneration: 1,
            sourceRoot,
            destinationRoot,
            changedLeft.Value,
            unchangedRight.Value,
            baseline.Value,
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror,
            DateTimeOffset.UtcNow));
        Assert.True(changedPlan.IsSuccess, Failure(changedPlan.Error));
        Assert.Contains(changedPlan.Value.Plan.Operations, operation =>
            operation.Kind == SyncPlanOperationKind.Copy &&
            operation.Destination?.CanonicalRelativePath.EndsWith("/alpha.bin", StringComparison.Ordinal) == true);
        Assert.Contains(changedPlan.Value.Plan.Operations, operation =>
            operation.Kind == SyncPlanOperationKind.Copy &&
            operation.Destination?.CanonicalRelativePath.EndsWith("/delta.bin", StringComparison.Ordinal) == true);
        Assert.Contains(changedPlan.Value.Plan.Operations, operation =>
            operation.Kind == SyncPlanOperationKind.Delete &&
            operation.SourceOrTarget.CanonicalRelativePath.EndsWith("/beta.bin", StringComparison.Ordinal));

        var changedExecution = await ExecutePlanAsync(
            changedPlan.Value,
            profile,
            sessions,
            SyncPlanExecutionMode.Execute);
        Assert.True(changedExecution.IsSuccess, Failure(changedExecution.Error));
        Assert.Equal(updated, await ReadAsync(
            destination.Connection.Session, Child(destinationRoot, "alpha.bin")));
        Assert.Equal(added, await ReadAsync(
            destination.Connection.Session, Child(destinationRoot, "delta.bin")));
        var deletedDestination = await destination.Connection.Session.GetEntryAsync(
            Child(destinationRoot, "beta.bin"));
        Assert.True(deletedDestination.IsFailure);
        Assert.Equal(StorageFailureKind.NotFound, deletedDestination.Error.Kind);
    }

    private static async Task<StorageResult<SyncPlanExecutionReport>> ExecutePlanAsync(
        SyncPlanBuildResult plan,
        SyncProfile profile,
        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions,
        SyncPlanExecutionMode mode)
    {
        var approval = SyncExecutionApproval.Create(
            plan.Plan,
            sessions,
            plan.Snapshots,
            mode,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions,
            profile.PolicySha256);
        return await SyncPlanExecutor.ExecuteAsync(new SyncPlanExecutionRequest(
            plan.Plan,
            approval,
            sessions,
            plan.Snapshots,
            mode,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions,
            profile.PolicySha256));
    }

    private static string DescribeSnapshot(SyncEndpointSnapshot snapshot) => string.Join(
        ", ",
        snapshot.Entries.Select(entry => $"{entry.Key}:{entry.Value.Kind}:{entry.Value.Size}"));

    private static async Task CreateDirectoryTreeAsync(
        CodeLogicStorageEndpointSession session,
        StorageAddress directory)
    {
        var current = string.Empty;
        foreach (var segment in directory.CanonicalRelativePath.Split('/'))
        {
            current = string.IsNullOrEmpty(current) ? segment : $"{current}/{segment}";
            var created = await session.CreateDirectoryAsync(StorageAddress.Create(
                session.ProfileId, session.RootIdentity, current).Value);
            Assert.True(created.IsSuccess, Failure(created.Error));
        }
    }

    private static StorageAddress Child(StorageAddress root, string relativePath) =>
        StorageAddress.Create(
            root.ProfileId,
            root.RootIdentity,
            $"{root.CanonicalRelativePath}/{relativePath}").Value;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
        if (_started)
        {
            await global::CodeLogic.CodeLogic.StopAsync();
        }
        if (_testRoot is not null && Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static async Task AssertTransferToS3Async(
        RegisteredEndpoint source,
        RegisteredEndpoint destination,
        string name,
        int length,
        int modulus)
    {
        var payload = Enumerable.Range(0, length).Select(index => (byte)(index % modulus)).ToArray();
        var sourceAddress = Address(source, $"cross-source/{Guid.NewGuid():N}-{name}");
        var committedSource = await WriteAsync(source.Connection.Session, sourceAddress, payload);
        var destinationAddress = Address(destination, $"incoming/{name}");
        var copied = await TransferExecutor.ExecuteAsync(
            new TransferIntent(
                TransferJobId.New(),
                TransferOperationKind.Copy,
                committedSource.Address,
                destinationAddress,
                payload.LongLength,
                TransferVerificationPolicy.StrongHashWhenAvailable,
                DateTimeOffset.UtcNow),
            source.Connection.Session,
            destination.Connection.Session);
        Assert.True(copied.IsSuccess, Failure(copied.Error));
        Assert.Equal(payload.LongLength, copied.Value.BytesTransferred);
        Assert.Equal(payload, await ReadAsync(destination.Connection.Session, destinationAddress));
    }

    private static async Task AssertUnsafeNewDestinationFailsClosedAsync(
        RegisteredEndpoint source,
        RegisteredEndpoint destination,
        string name)
    {
        var payload = Enumerable.Range(0, 32_000).Select(index => (byte)(index % 223)).ToArray();
        var sourceAddress = Address(source, $"outgoing/{Guid.NewGuid():N}-{name}");
        var committedSource = await WriteAsync(source.Connection.Session, sourceAddress, payload);
        var destinationAddress = Address(destination, $"incoming/{name}");
        var result = await TransferExecutor.ExecuteAsync(
            new TransferIntent(
                TransferJobId.New(),
                TransferOperationKind.Copy,
                committedSource.Address,
                destinationAddress,
                payload.LongLength,
                TransferVerificationPolicy.StrongHashWhenAvailable,
                DateTimeOffset.UtcNow),
            source.Connection.Session,
            destination.Connection.Session);
        Assert.True(result.IsFailure);
        Assert.Equal("transfer.conditional_mutation.unsupported", result.Error.Code);
        var lookup = await destination.Connection.Session.GetEntryAsync(destinationAddress);
        Assert.True(lookup.IsFailure);
        Assert.Equal(StorageFailureKind.NotFound, lookup.Error.Kind);
    }

    private static async Task AssertNonAtomicCompatibilityTransferAsync(
        RegisteredEndpoint source,
        RegisteredEndpoint destination,
        string name)
    {
        var payload = Enumerable.Range(0, 37_777).Select(index => (byte)(index % 227)).ToArray();
        var sourceAddress = Address(source, $"outgoing/{Guid.NewGuid():N}-{name}");
        var committedSource = await WriteAsync(source.Connection.Session, sourceAddress, payload);
        var destinationAddress = Address(destination, $"incoming/{Guid.NewGuid():N}-{name}");
        var result = await TransferExecutor.ExecuteAsync(
            new TransferIntent(
                TransferJobId.New(),
                TransferOperationKind.Copy,
                committedSource.Address,
                destinationAddress,
                payload.LongLength,
                TransferVerificationPolicy.StrongHashWhenAvailable,
                DateTimeOffset.UtcNow),
            source.Connection.Session,
            destination.Connection.Session,
            new TransferExecutionOptions(AllowNonAtomicDestinationWrites: true));

        Assert.True(result.IsSuccess, Failure(result.Error));
        Assert.Equal(payload, await ReadAsync(destination.Connection.Session, destinationAddress));
    }

    private async Task<RegisteredEndpoint> RegisterAsync(
        CodeLogicStorageSessionFactory factory,
        object configuration,
        string name)
    {
        var profileId = ConnectionProfileId.New();
        var rootIdentity = $"vm-{name}-{Guid.NewGuid():N}";
        var result = configuration switch
        {
            S3ConnectionConfig value => await factory.RegisterAsync(profileId, rootIdentity, value),
            SftpConnectionConfig value => await factory.RegisterAsync(profileId, rootIdentity, value),
            FtpConnectionConfig value => await factory.RegisterAsync(profileId, rootIdentity, value),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
        Assert.True(result.IsSuccess, Failure(result.Error));
        _connections.Add(result.Value);
        return new RegisteredEndpoint(profileId, rootIdentity, result.Value);
    }

    private static async Task<StorageEntry> WriteAsync(
        CodeLogicStorageEndpointSession session,
        StorageAddress address,
        byte[] payload)
    {
        var opened = await session.OpenWriteAsync(new StorageWriteRequest(
            address, StorageWriteMode.Overwrite, payload.LongLength));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using var handle = opened.Value;
        await handle.Content.WriteAsync(payload);
        var committed = await handle.CommitAsync();
        Assert.True(committed.IsSuccess, Failure(committed.Error));
        return committed.Value;
    }

    private static async Task<byte[]> ReadAsync(
        CodeLogicStorageEndpointSession session,
        StorageAddress address)
    {
        var opened = await session.OpenReadAsync(new StorageReadRequest(address));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using var source = opened.Value;
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static async Task WriteOverwriteAsync(
        CodeLogicStorageEndpointSession session,
        StorageAddress address,
        byte[] payload)
    {
        var opened = await session.OpenWriteAsync(new StorageWriteRequest(
            address,
            StorageWriteMode.Overwrite,
            payload.LongLength));
        Assert.True(opened.IsSuccess, Failure(opened.Error));
        await using var handle = opened.Value;
        await handle.Content.WriteAsync(payload);
        var committed = await handle.CommitAsync();
        Assert.True(committed.IsSuccess, Failure(committed.Error));
    }

    private static StorageAddress Address(RegisteredEndpoint endpoint, string path)
    {
        var address = StorageAddress.Create(endpoint.ProfileId, endpoint.RootIdentity, path);
        Assert.True(address.IsSuccess, Failure(address.Error));
        return address.Value;
    }

    private static bool Required() =>
        string.Equals(Environment.GetEnvironmentVariable("STORAGEHUB_REQUIRE_VM_CROSS_PROVIDER"), "1", StringComparison.Ordinal);

    private static string RequiredValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"The required VM fixture setting {name} is missing.");
    }

    private static Uri RequiredUri(string name) => new(RequiredValue(name), UriKind.Absolute);

    private static int RequiredPort(string name) =>
        int.TryParse(RequiredValue(name), out var port) && port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException($"The required VM fixture port {name} is invalid.");

    private static string Failure(StorageFailure? failure) => failure is null
        ? "The operation failed without a structured failure."
        : $"{failure.Code}: {failure.Message} (provider: {failure.ProviderCode ?? "none"})";

    private sealed record RegisteredEndpoint(
        ConnectionProfileId ProfileId,
        string RootIdentity,
        RuntimeStorageConnection Connection);
}
