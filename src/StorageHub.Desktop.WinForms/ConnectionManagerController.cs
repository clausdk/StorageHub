using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>Protocol-aware orchestration used by the WinForms Connection Manager.</summary>
public sealed class ConnectionManagerController
{
    private readonly IRemoteConnectionProfileClient _profiles;
    private readonly IRemoteSecretVaultClient _secrets;

    public ConnectionManagerController(
        IRemoteConnectionProfileClient profiles,
        IRemoteSecretVaultClient secrets)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public Task<ConnectionProfileGetResponse> GetAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default) => _profiles.GetAsync(
        new ConnectionProfileGetRequest(ConnectionProfileIpcContract.CurrentVersion, connectionId),
        cancellationToken);

    public Task<ConnectionProfileWriteResponse> SaveAsync(
        ConnectionProfileDraft draft,
        ConnectionProfileDocument? current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return current is null
            ? _profiles.CreateAsync(
                new ConnectionProfileCreateRequest(ConnectionProfileIpcContract.CurrentVersion, draft),
                cancellationToken)
            : _profiles.UpdateAsync(
                new ConnectionProfileUpdateRequest(
                    ConnectionProfileIpcContract.CurrentVersion,
                    current.ConnectionId,
                    current.Version,
                    draft),
                cancellationToken);
    }

    public Task<ConnectionProfileWriteResponse> DeleteAsync(
        ConnectionProfileDocument current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return _profiles.DeleteAsync(
            new ConnectionProfileDeleteRequest(
                ConnectionProfileIpcContract.CurrentVersion,
                current.ConnectionId,
                current.Version),
            cancellationToken);
    }

    public Task<SecretVaultResponse> EnrollOrUpdateSecretAsync(
        SecretMaterialPurpose purpose,
        string? existingReference,
        ReadOnlyMemory<byte> material,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(existingReference)
            ? _secrets.EnrollAsync(purpose, material, cancellationToken)
            : _secrets.UpdateAsync(existingReference, purpose, material, cancellationToken);

    public Task<SecretVaultResponse> DeleteSecretAsync(
        string reference,
        SecretMaterialPurpose purpose,
        CancellationToken cancellationToken = default) =>
        _secrets.DeleteAsync(reference, purpose, cancellationToken);
}
