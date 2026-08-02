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

    public Task<ConnectionTrustGetResponse> GetTrustAsync(
        ConnectionProfileDocument current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return _profiles.GetTrustAsync(
            new ConnectionTrustGetRequest(
                ConnectionTrustIpcContract.CurrentVersion,
                current.ConnectionId,
                current.Version),
            cancellationToken);
    }

    public async Task<ConnectionTrustMutationResponse> TrustOrRolloverAsync(
        ConnectionProfileDocument current,
        string sha256Fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var trust = await GetTrustAsync(current, cancellationToken).ConfigureAwait(false);
        if (trust.Snapshot is null)
        {
            return FailedTrustMutation(trust.Failure);
        }

        var matching = trust.Snapshot.Records.Where(record =>
            EquivalentFingerprint(record.Sha256Fingerprint, sha256Fingerprint)).ToArray();
        if (matching.Length > 1)
        {
            return AmbiguousTrustHistory();
        }

        if (matching is [var existing])
        {
            var currentTime = DateTimeOffset.UtcNow;
            if (existing.Decision == ConnectionTrustDecision.Trusted &&
                (existing.ExpiresUtc is null || existing.ExpiresUtc > currentTime))
            {
                return new ConnectionTrustMutationResponse(
                    ConnectionTrustIpcContract.CurrentVersion,
                    ConnectionTrustMutationStatus.Succeeded,
                    trust.Snapshot);
            }

            return await _profiles.DecideTrustAsync(
                new ConnectionTrustDecisionRequest(
                    ConnectionTrustIpcContract.CurrentVersion,
                    current.ConnectionId,
                    current.Version,
                    sha256Fingerprint,
                    ConnectionTrustDecision.Trusted,
                    existing.TrustId,
                    existing.Version),
                cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var active = trust.Snapshot.Records
            .Where(record => record.Decision == ConnectionTrustDecision.Trusted &&
                (record.ExpiresUtc is null || record.ExpiresUtc > now))
            .ToArray();
        if (active.Length > 1)
        {
            return FailedTrustMutation(new StorageIpcFailure(
                "connection.trust.multiple_active",
                StorageIpcFailureCategory.Conflict,
                "Multiple active trust records require manual reconciliation before rollover.",
                IsTransient: false));
        }

        return active.Length == 1
            ? await _profiles.RolloverTrustAsync(
                new ConnectionTrustRolloverRequest(
                    ConnectionTrustIpcContract.CurrentVersion,
                    current.ConnectionId,
                    current.Version,
                    active[0].TrustId,
                    active[0].Version,
                    sha256Fingerprint),
                cancellationToken).ConfigureAwait(false)
            : await _profiles.DecideTrustAsync(
                new ConnectionTrustDecisionRequest(
                    ConnectionTrustIpcContract.CurrentVersion,
                    current.ConnectionId,
                    current.Version,
                    sha256Fingerprint,
                    ConnectionTrustDecision.Trusted),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectionTrustMutationResponse> RejectAsync(
        ConnectionProfileDocument current,
        string sha256Fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var trust = await GetTrustAsync(current, cancellationToken).ConfigureAwait(false);
        if (trust.Snapshot is null)
        {
            return FailedTrustMutation(trust.Failure);
        }

        var matching = trust.Snapshot.Records.Where(record =>
            EquivalentFingerprint(record.Sha256Fingerprint, sha256Fingerprint)).ToArray();
        if (matching.Length > 1)
        {
            return AmbiguousTrustHistory();
        }

        var existing = matching.SingleOrDefault();
        return await _profiles.DecideTrustAsync(
            new ConnectionTrustDecisionRequest(
                ConnectionTrustIpcContract.CurrentVersion,
                current.ConnectionId,
                current.Version,
                sha256Fingerprint,
                ConnectionTrustDecision.Rejected,
                existing?.TrustId,
                existing?.Version),
            cancellationToken).ConfigureAwait(false);
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

    private static ConnectionTrustMutationResponse FailedTrustMutation(StorageIpcFailure? failure) => new(
        ConnectionTrustIpcContract.CurrentVersion,
        failure?.Category switch
        {
            StorageIpcFailureCategory.NotFound => ConnectionTrustMutationStatus.NotFound,
            StorageIpcFailureCategory.Conflict => ConnectionTrustMutationStatus.VersionConflict,
            StorageIpcFailureCategory.Unsupported => ConnectionTrustMutationStatus.Unsupported,
            StorageIpcFailureCategory.Validation => ConnectionTrustMutationStatus.ValidationFailed,
            _ => ConnectionTrustMutationStatus.Unavailable
        },
        Snapshot: null,
        failure ?? new StorageIpcFailure(
            "connection.trust.unavailable",
            StorageIpcFailureCategory.Unavailable,
            "Server trust records are temporarily unavailable.",
            IsTransient: true));

    private static ConnectionTrustMutationResponse AmbiguousTrustHistory() => FailedTrustMutation(
        new StorageIpcFailure(
            "connection.trust.ambiguous_history",
            StorageIpcFailureCategory.Conflict,
            "Duplicate server identity records require manual reconciliation before trust can change.",
            IsTransient: false));

    private static bool EquivalentFingerprint(string left, string right) =>
        TryDecodeFingerprint(left, out var leftBytes) &&
        TryDecodeFingerprint(right, out var rightBytes) &&
        leftBytes.AsSpan().SequenceEqual(rightBytes);

    private static bool TryDecodeFingerprint(string value, out byte[] bytes)
    {
        var normalized = value.Trim();
        var hexadecimal = normalized.Replace(":", string.Empty, StringComparison.Ordinal);
        try
        {
            bytes = hexadecimal.Length == 64 && hexadecimal.All(Uri.IsHexDigit)
                ? Convert.FromHexString(hexadecimal)
                : normalized.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(normalized[7..].PadRight((normalized.Length - 7 + 3) / 4 * 4, '='))
                    : [];
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
