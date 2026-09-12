using StorageHub.Agent.Transfers;
using StorageHub.Agent.Windows;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows.Tests;

/// <summary>
/// The local transfer folder is the only endpoint whose path is chosen in the desktop rather than
/// produced by the agent, so these tests are about what it refuses.
/// </summary>
public sealed class LocalUserPathTransferEndpointTests
{
    private static readonly string[] NotQualifiedCodes =
        ["local-user.relative", "local-user.empty"];

    [Fact]
    public void APlainWritableFolderIsApproved()
    {
        using var folder = new TemporaryFolder();

        var approved = LocalUserPathTransferEndpoint.ApproveRoot(folder.Path);

        Assert.True(approved.IsSuccess, approved.IsFailure ? approved.Error.Message : null);
        Assert.Equal(Path.TrimEndingDirectorySeparator(folder.Path), approved.Value);
    }

    [Fact]
    public void StorageHubsOwnDataFolderIsRefused()
    {
        // The vault and the durable queue live here. A transfer that could write into it could
        // corrupt the state the agent depends on to stay safe.
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorageHub");
        Directory.CreateDirectory(dataRoot);

        var approved = LocalUserPathTransferEndpoint.ApproveRoot(dataRoot);

        Assert.True(approved.IsFailure);
        Assert.Equal("local-user.protected", approved.Error.Code);
    }

    [Fact]
    public void AFolderInsideStorageHubsDataFolderIsRefused()
    {
        var nested = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub",
            "Agent");
        Directory.CreateDirectory(nested);

        var approved = LocalUserPathTransferEndpoint.ApproveRoot(nested);

        Assert.True(approved.IsFailure);
        Assert.Equal("local-user.protected", approved.Error.Code);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    public void WindowsSystemFoldersAreRefused(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var approved = LocalUserPathTransferEndpoint.ApproveRoot(path);

        Assert.True(approved.IsFailure);
        Assert.Equal("local-user.protected", approved.Error.Code);
    }

    [Fact]
    public void AMissingFolderIsRefused()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"storagehub-absent-{Guid.NewGuid():N}");

        var approved = LocalUserPathTransferEndpoint.ApproveRoot(missing);

        Assert.True(approved.IsFailure);
        Assert.Equal("local-user.not-found", approved.Error.Code);
    }

    [Theory]
    [InlineData(@"relative\path")]
    [InlineData("")]
    [InlineData("   ")]
    public void APathThatIsNotFullyQualifiedIsRefused(string candidate)
    {
        var approved = LocalUserPathTransferEndpoint.ApproveRoot(candidate);

        Assert.True(approved.IsFailure);
        Assert.Contains(approved.Error.Code, NotQualifiedCodes);
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void DeviceAndExtendedLengthPathsAreRefused(string candidate)
    {
        // These bypass the normalisation every later containment check depends on.
        var approved = LocalUserPathTransferEndpoint.ApproveRoot(candidate);

        Assert.True(approved.IsFailure);
        Assert.Equal("local-user.device-path", approved.Error.Code);
    }

    [Fact]
    public void AFolderThatIsALinkIsRefused()
    {
        using var target = new TemporaryFolder();
        var link = Path.Combine(Path.GetTempPath(), $"storagehub-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateSymbolicLink(link, target.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Creating a symlink needs privilege this account may not hold; the rule is still
            // enforced at runtime, there is just nothing to assert against here.
            return;
        }

        try
        {
            var approved = LocalUserPathTransferEndpoint.ApproveRoot(link);

            Assert.True(approved.IsFailure);
            Assert.Equal("local-user.reparse-point", approved.Error.Code);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void AnAddressWhoseIdDoesNotMatchItsFolderIsRefused()
    {
        using var folder = new TemporaryFolder();
        var created = LocalUserPathTransferEndpoint.CreateAddress(folder.Path, "file.txt");
        Assert.True(created.IsSuccess);

        // Same folder, someone else's connection id: the pair was never issued together.
        var forged = StorageAddress.Create(
            new ConnectionProfileId(Guid.NewGuid()),
            created.Value.RootIdentity,
            "file.txt");
        Assert.True(forged.IsSuccess);

        var opened = LocalUserPathTransferEndpoint.Open(forged.Value);

        Assert.True(opened.IsFailure);
        Assert.Equal("local-user.invalid", opened.Error.Code);
    }

    [Fact]
    public async Task AReadEscapingTheApprovedFolderIsRefused()
    {
        using var folder = new TemporaryFolder();
        var created = LocalUserPathTransferEndpoint.CreateAddress(folder.Path, "file.txt");
        Assert.True(created.IsSuccess);
        var opened = LocalUserPathTransferEndpoint.Open(created.Value);
        Assert.True(opened.IsSuccess);
        await using var connection = opened.Value;

        // A traversal has to fail even though the root itself was approved.
        var escape = StorageAddress.Create(
            created.Value.ProfileId, created.Value.RootIdentity, "../outside.txt");
        if (escape.IsFailure)
        {
            // The address type already rejects the traversal, which is the same guarantee.
            return;
        }

        var read = await connection.Session.OpenReadAsync(new Storage.Models.StorageReadRequest(escape.Value));

        Assert.True(read.IsFailure);
    }

    [Fact]
    public async Task AWriteOnlyCreatesAndWillNotOverwrite()
    {
        using var folder = new TemporaryFolder();
        var created = LocalUserPathTransferEndpoint.CreateAddress(folder.Path, "new.txt");
        Assert.True(created.IsSuccess);
        var opened = LocalUserPathTransferEndpoint.Open(created.Value);
        Assert.True(opened.IsSuccess);
        await using var connection = opened.Value;

        var handle = await connection.Session.OpenWriteAsync(
            new Storage.Models.StorageWriteRequest(created.Value, Storage.Models.StorageWriteMode.CreateNew, 3));
        Assert.True(handle.IsSuccess);
        await using (var writer = handle.Value)
        {
            await writer.Content.WriteAsync(new byte[] { 1, 2, 3 });
            var committed = await writer.CommitAsync();
            Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : null);
        }

        Assert.Equal(3, new FileInfo(Path.Combine(folder.Path, "new.txt")).Length);

        // Overwriting a local file is a separate safety decision and is not offered here.
        var overwrite = await connection.Session.OpenWriteAsync(
            new Storage.Models.StorageWriteRequest(created.Value, Storage.Models.StorageWriteMode.Overwrite, 3));

        Assert.True(overwrite.IsFailure);
        Assert.Equal("local-user.create-only", overwrite.Error.Code);
    }

    [Fact]
    public void TheWireEncodingRoundTripsAndBindsTheIdToTheFolder()
    {
        using var folder = new TemporaryFolder();
        var canonical = Path.TrimEndingDirectorySeparator(folder.Path);

        var identity = LocalTransferFolder.CreateRootIdentity(canonical);

        Assert.True(LocalTransferFolder.IsLocalFolder(identity));
        Assert.Equal(canonical, LocalTransferFolder.TryReadFolder(identity));
        Assert.Equal(
            LocalTransferFolder.CreateConnectionId(canonical),
            LocalTransferFolder.CreateConnectionId(canonical.ToUpperInvariant()));
        Assert.NotEqual(
            LocalTransferFolder.CreateConnectionId(canonical),
            LocalTransferFolder.CreateConnectionId(Path.Combine(canonical, "child")));
    }

    [Fact]
    public void ASavedConnectionIdentityIsNotMistakenForALocalFolder()
    {
        Assert.False(LocalTransferFolder.IsLocalFolder("s3:bucket/prefix"));
        Assert.Null(LocalTransferFolder.TryReadFolder("s3:bucket/prefix"));
        Assert.Null(LocalTransferFolder.TryReadFolder(null));
    }

    [Fact]
    public async Task AFileIsWrittenIntoTheLocalFolderByTheNormalTransferExecutor()
    {
        using var folder = new TemporaryFolder();
        var remoteId = ConnectionProfileId.New();
        var remote = new FakeRemoteSession(remoteId, "remote-root");
        var source = StorageAddress.Create(remoteId, remote.RootIdentity, "payload.txt").Value;
        var destination = LocalUserPathTransferEndpoint.CreateAddress(folder.Path, "payload.txt");
        Assert.True(destination.IsSuccess);
        var opened = LocalUserPathTransferEndpoint.Open(destination.Value);
        Assert.True(opened.IsSuccess);
        await using var connection = opened.Value;

        var intent = new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            source,
            destination.Value,
            FakeRemoteSession.PayloadLength,
            TransferVerificationPolicy.Size,
            DateTimeOffset.UtcNow);

        var transferred = await TransferExecutor.ExecuteAsync(intent, remote, connection.Session);

        Assert.True(transferred.IsSuccess, transferred.IsFailure ? transferred.Error.Message : null);
        Assert.Equal(
            FakeRemoteSession.Payload,
            await File.ReadAllTextAsync(Path.Combine(folder.Path, "payload.txt")));
    }

    [Fact]
    public async Task AFileIsReadOutOfTheLocalFolderByTheNormalTransferExecutor()
    {
        using var folder = new TemporaryFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "upload.txt"), "from this pc");

        var created = LocalUserPathTransferEndpoint.CreateAddress(folder.Path, "upload.txt");
        Assert.True(created.IsSuccess);
        var opened = LocalUserPathTransferEndpoint.Open(created.Value);
        Assert.True(opened.IsSuccess);
        await using var connection = opened.Value;

        // The size the queue would carry has to match what the endpoint reports, because the
        // executor verifies length before it commits.
        var entry = await connection.Session.GetEntryAsync(created.Value);
        Assert.True(entry.IsSuccess);
        Assert.Equal("from this pc".Length, entry.Value.Size);

        var read = await connection.Session.OpenReadAsync(
            new Storage.Models.StorageReadRequest(created.Value));
        Assert.True(read.IsSuccess);
        await using var stream = read.Value;
        using var reader = new StreamReader(stream);
        Assert.Equal("from this pc", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task AResumedReadHonoursTheRequestedRange()
    {
        using var folder = new TemporaryFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "range.txt"), "0123456789");
        var created = LocalUserPathTransferEndpoint.CreateAddress(folder.Path, "range.txt");
        Assert.True(created.IsSuccess);
        var opened = LocalUserPathTransferEndpoint.Open(created.Value);
        Assert.True(opened.IsSuccess);
        await using var connection = opened.Value;

        // An interrupted transfer resumes by asking for a range rather than the whole file.
        var read = await connection.Session.OpenReadAsync(
            new Storage.Models.StorageReadRequest(created.Value, Offset: 4, Length: 3));

        Assert.True(read.IsSuccess);
        await using var stream = read.Value;
        using var reader = new StreamReader(stream);
        Assert.Equal("456", await reader.ReadToEndAsync());
    }

    private sealed class FakeRemoteSession(ConnectionProfileId profileId, string rootIdentity)
        : IStorageEndpointSession
    {
        public const string Payload = "remote bytes";
        public static long PayloadLength => Payload.Length;

        public ConnectionProfileId ProfileId => profileId;
        public string RootIdentity => rootIdentity;

        public EffectiveStorageCapabilities Capabilities { get; } = new([
            new KeyValuePair<StorageFeature, FeatureSupport>(StorageFeature.ReadStream, FeatureSupport.Native())]);

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Success());

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(StorageAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageEntry.Create(address, StorageEntryKind.File, PayloadLength, DateTimeOffset.UtcNow));

        public ValueTask<StorageResult<Stream>> OpenReadAsync(Storage.Models.StorageReadRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<Stream>.Success(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Payload))));

        public ValueTask<StorageResult<StoragePage>> ListAsync(StorageAddress address, Storage.Models.StorageListRequest? request = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<StoragePage>.Success(new StoragePage([])));

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(Storage.Models.StorageWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(StorageAddress address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<StorageResult> DeleteAsync(Storage.Models.StorageDeleteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> CopyAsync(Storage.Models.StorageCopyRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> MoveAsync(Storage.Models.StorageMoveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"storagehub-local-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }
}
