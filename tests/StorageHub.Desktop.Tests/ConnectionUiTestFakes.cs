using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// Agent doubles shared by the connection UI tests. Lifted out of ShellWiringTests when the saved
/// connection list moved into the shell panel: the shell, the panel, and the editor all need the
/// same doubles, and three copies would drift.
/// </summary>
internal static class ConnectionUiTestFakes
{
    internal static ConnectionSummary Summary(
        string name,
        StorageConnectionProvider provider,
        string? folder = null,
        string[]? tags = null,
        bool favorite = false,
        bool enabled = true,
        ConnectionProfileType type = ConnectionProfileType.Storage,
        ConnectionHealthSnapshot? health = null,
        Guid? connectionId = null,
        long version = 1) => new(
            connectionId ?? Guid.NewGuid(),
            name,
            provider,
            folder,
            tags ?? [],
            favorite,
            enabled,
            provider.ToString(),
            AccentColor: null,
            Version: version,
            Type: type,
            Health: health);
}

internal sealed class FakeStorageClient : IRemoteStorageAgentClient
{
    private readonly ConnectionSummary[] _connections;

    internal FakeStorageClient(Guid connectionId)
        : this(
            [new ConnectionSummary(
                connectionId,
                "Saved archive",
                StorageConnectionProvider.S3,
                FolderPath: null,
                Tags: ["archive", "production"],
                IsFavorite: false,
                IsEnabled: true,
                IconKey: "s3",
                AccentColor: null,
                Version: 1)])
    {
    }

    internal FakeStorageClient(ConnectionSummary[] connections)
    {
        _connections = connections;
    }

    /// <summary>The listing requests this client was asked for, so tests can assert their shape.</summary>
    internal List<ConnectionListRequest> ListRequests { get; } = [];

    internal List<ConnectionTestRequest> TestRequests { get; } = [];

    public Task<ConnectionListResponse> ListConnectionsAsync(
        ConnectionListRequest request,
        CancellationToken cancellationToken = default)
    {
        ListRequests.Add(request);
        return Task.FromResult(new ConnectionListResponse(request.ContractVersion, _connections));
    }

    public Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken = default)
    {
        TestRequests.Add(request);
        return Task.FromResult(new ConnectionTestResponse(request.ContractVersion, request.ConnectionId, true, 12));
    }

    public Task<StorageListPageResponse> ListStorageAsync(
        StorageListPageRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProfileClient(ConnectionProfileDocument? profile = null) : IRemoteConnectionProfileClient
{
    internal List<ConnectionProfileDeleteRequest> DeleteRequests { get; } = [];

    internal List<ConnectionProfileUpdateRequest> UpdateRequests { get; } = [];

    /// <summary>The status delete replies with, so a version conflict can be exercised.</summary>
    internal ConnectionProfileWriteStatus DeleteStatus { get; set; } = ConnectionProfileWriteStatus.Succeeded;

    public Task<ConnectionProfileGetResponse> GetAsync(
        ConnectionProfileGetRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectionProfileGetResponse(ConnectionProfileIpcContract.CurrentVersion, profile));

    public Task<ConnectionProfileWriteResponse> CreateAsync(
        ConnectionProfileCreateRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ConnectionProfileWriteResponse> UpdateAsync(
        ConnectionProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        UpdateRequests.Add(request);
        return Task.FromResult(new ConnectionProfileWriteResponse(
            ConnectionProfileIpcContract.CurrentVersion,
            ConnectionProfileWriteStatus.Succeeded,
            profile));
    }

    public Task<ConnectionProfileWriteResponse> DeleteAsync(
        ConnectionProfileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        DeleteRequests.Add(request);
        return Task.FromResult(new ConnectionProfileWriteResponse(
            ConnectionProfileIpcContract.CurrentVersion,
            DeleteStatus,
            Profile: null));
    }

    public Task<ConnectionTrustGetResponse> GetTrustAsync(
        ConnectionTrustGetRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ConnectionTrustMutationResponse> DecideTrustAsync(
        ConnectionTrustDecisionRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ConnectionTrustMutationResponse> RolloverTrustAsync(
        ConnectionTrustRolloverRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSecretVaultClient : IRemoteSecretVaultClient
{
    public Task<SecretVaultResponse> EnrollAsync(
        SecretMaterialPurpose purpose,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecretVaultResponse> UpdateAsync(
        string reference,
        SecretMaterialPurpose purpose,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecretVaultResponse> DeleteAsync(
        string reference,
        SecretMaterialPurpose purpose,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
