using System.Text;
using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Application.Credentials;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;
using StorageHub.Storage.CodeLogic;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Versioned normal-IPC management for the key and certificate store.
///
/// Material never crosses this surface. The desktop enrolls bytes on the dedicated secret pipe and
/// sends only the resulting opaque references here; this handler opens them against the vault, in
/// the agent, purely to derive the non-sensitive summary. Nothing it returns contains key material,
/// and a caller cannot describe material as something it is not, because the description is
/// computed here rather than accepted.
/// </summary>
public sealed class KeyStoreIpcCommandService : IAgentIpcCommandHandler
{
    private readonly IKeyStoreRepository _entries;
    private readonly Func<ISecretVault> _vaultProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The vault is resolved per request, never at construction. The vault subsystem only creates it
    /// during initialization, and in recovery-only mode it is never created at all, so binding it
    /// eagerly would fault the whole agent at composition time instead of failing one command.
    /// </summary>
    public KeyStoreIpcCommandService(
        IKeyStoreRepository entries,
        Func<ISecretVault> vaultProvider,
        TimeProvider? timeProvider = null)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _vaultProvider = vaultProvider ?? throw new ArgumentNullException(nameof(vaultProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CanHandle(string messageType) => messageType is
        KeyStoreIpcMessageTypes.ListRequest or
        KeyStoreIpcMessageTypes.CreateRequest or
        KeyStoreIpcMessageTypes.UpdateRequest or
        KeyStoreIpcMessageTypes.DeleteRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            KeyStoreIpcMessageTypes.ListRequest => ListAsync(request, cancellationToken),
            KeyStoreIpcMessageTypes.CreateRequest => CreateAsync(request, cancellationToken),
            KeyStoreIpcMessageTypes.UpdateRequest => UpdateAsync(request, cancellationToken),
            KeyStoreIpcMessageTypes.DeleteRequest => DeleteAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> ListAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<KeyStoreListRequest>();
        if (!KeyStoreIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return ListFailure("The key store query is outside the negotiated contract bounds.");
        }

        var found = await _entries.SearchAsync(
            new KeyStoreSearch(request.Text, Map(request.Kind), request.Tag, request.Limit),
            cancellationToken).ConfigureAwait(false);

        return AgentIpcCommandResponse.Create(
            KeyStoreIpcMessageTypes.ListResponse,
            new KeyStoreListResponse(
                KeyStoreIpcContract.CurrentVersion,
                [.. found.Select(Map)]));
    }

    private async ValueTask<AgentIpcCommandResponse> CreateAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<KeyStoreCreateRequest>();
        if (!KeyStoreIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return WriteFailure(
                KeyStoreIpcMessageTypes.CreateResponse,
                "The key store entry is outside the negotiated contract bounds.");
        }

        if (!SecretReference.TryParse(request.MaterialReference, out var material))
        {
            return WriteFailure(KeyStoreIpcMessageTypes.CreateResponse, "The vault references are malformed.");
        }

        // A password-less certificate enrolls no passphrase at all, so there is no second reference.
        SecretReference? passphrase = null;
        if (request.PassphraseReference is not null)
        {
            if (!SecretReference.TryParse(request.PassphraseReference, out var parsed))
            {
                return WriteFailure(KeyStoreIpcMessageTypes.CreateResponse, "The vault references are malformed.");
            }

            passphrase = parsed;
        }
        else if (request.Kind is KeyStoreMaterialKind.SshPrivateKey)
        {
            return WriteFailure(
                KeyStoreIpcMessageTypes.CreateResponse,
                "An SSH private key requires a passphrase.");
        }

        var described = await DescribeAsync(request, material, passphrase, cancellationToken).ConfigureAwait(false);
        if (described.IsFailure)
        {
            return WriteFailure(KeyStoreIpcMessageTypes.CreateResponse, described.Error.Message);
        }

        KeyStoreEntry entry;
        try
        {
            entry = KeyStoreEntry.Create(
                KeyStoreEntryId.New(),
                Map(request.Kind),
                request.DisplayName,
                material,
                passphrase,
                described.Value,
                _timeProvider.GetUtcNow(),
                request.Description,
                request.Tags);
        }
        catch (Exception error) when (error is ArgumentException or ArgumentOutOfRangeException)
        {
            return WriteFailure(KeyStoreIpcMessageTypes.CreateResponse, "The key store entry is not valid.");
        }

        var written = await _entries.CreateAsync(entry, cancellationToken).ConfigureAwait(false);
        return WriteResponse(KeyStoreIpcMessageTypes.CreateResponse, written);
    }

    private async ValueTask<AgentIpcCommandResponse> UpdateAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<KeyStoreUpdateRequest>();
        if (!KeyStoreIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return WriteFailure(
                KeyStoreIpcMessageTypes.UpdateResponse,
                "The key store update is outside the negotiated contract bounds.");
        }

        var existing = await _entries.GetAsync(new KeyStoreEntryId(request.EntryId), cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return WriteResponse(
                KeyStoreIpcMessageTypes.UpdateResponse,
                new KeyStoreWriteResult(KeyStoreWriteStatus.NotFound));
        }

        KeyStoreEntry renamed;
        try
        {
            // Only the label, description, and tags are editable here. Replacing material is a
            // vault rotation against the same reference, which never changes this row's identity.
            renamed = existing.WithDetails(
                request.DisplayName,
                request.Description,
                request.Tags,
                _timeProvider.GetUtcNow());
        }
        catch (Exception error) when (error is ArgumentException or ArgumentOutOfRangeException)
        {
            return WriteFailure(KeyStoreIpcMessageTypes.UpdateResponse, "The key store entry is not valid.");
        }

        var written = await _entries.UpdateAsync(renamed, request.ExpectedVersion, cancellationToken)
            .ConfigureAwait(false);
        return WriteResponse(KeyStoreIpcMessageTypes.UpdateResponse, written);
    }

    private async ValueTask<AgentIpcCommandResponse> DeleteAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<KeyStoreDeleteRequest>();
        if (!KeyStoreIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return WriteFailure(
                KeyStoreIpcMessageTypes.DeleteResponse,
                "The key store deletion is outside the negotiated contract bounds.");
        }

        var written = await _entries.DeleteAsync(
            new KeyStoreEntryId(request.EntryId),
            request.ExpectedVersion,
            cancellationToken).ConfigureAwait(false);

        // The vault envelopes are removed only after the row is gone, so a refused delete can never
        // strand a profile whose reference still resolves. A vault that is unavailable leaves the
        // envelopes behind rather than failing a delete that already committed.
        if (written.Status == KeyStoreWriteStatus.Succeeded && written.Entry is { } removed &&
            TryGetVault() is { } vault)
        {
            _ = await vault.DeleteAsync(removed.MaterialReference, cancellationToken).ConfigureAwait(false);
            if (removed.PassphraseReference is { } passphrase)
            {
                _ = await vault.DeleteAsync(passphrase, cancellationToken).ConfigureAwait(false);
            }
        }

        return WriteResponse(KeyStoreIpcMessageTypes.DeleteResponse, written);
    }

    /// <summary>
    /// Opens the enrolled material just long enough to describe it. The lease zeroes its buffer on
    /// disposal, and only the derived summary survives this method.
    /// </summary>
    private async ValueTask<StorageResult<KeyMaterialSummary>> DescribeAsync(
        KeyStoreCreateRequest request,
        SecretReference material,
        SecretReference? passphrase,
        CancellationToken cancellationToken)
    {
        if (TryGetVault() is not { } vault)
        {
            return StorageResult<KeyMaterialSummary>.Fail(new StorageFailure(
                "keystore.vault.unavailable",
                StorageFailureKind.Unavailable,
                "The credential vault is unavailable, so material cannot be imported."));
        }

        if (!await vault.ExistsAsync(material, cancellationToken).ConfigureAwait(false) ||
            (passphrase is { } declared &&
             !await vault.ExistsAsync(declared, cancellationToken).ConfigureAwait(false)))
        {
            return StorageResult<KeyMaterialSummary>.Fail(new StorageFailure(
                "keystore.reference.unresolved",
                StorageFailureKind.NotFound,
                "The enrolled material could not be found in the vault."));
        }

        await using var materialLease = await vault.OpenAsync(material, cancellationToken).ConfigureAwait(false);
        // A password-less certificate has no passphrase envelope to open; PKCS#12 treats an empty
        // password as "no password", which is what the loader expects for such a bundle.
        SecretLease? passphraseLease = null;
        var secret = string.Empty;
        try
        {
            if (passphrase is { } reference)
            {
                passphraseLease = await vault.OpenAsync(reference, cancellationToken).ConfigureAwait(false);
                secret = Encoding.UTF8.GetString(passphraseLease.Memory.Span);
            }

            return request.Kind is KeyStoreMaterialKind.Pkcs12Certificate
                ? KeyMaterialInspector.InspectPkcs12(materialLease.Memory.Span, secret)
                : KeyMaterialInspector.InspectSshPrivateKey(
                    materialLease.Memory,
                    secret,
                    MapFormat(request.KeyFormat!.Value));
        }
        finally
        {
            secret = string.Empty;
            if (passphraseLease is not null)
            {
                await passphraseLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Resolves the vault if the subsystem has one. Recovery-only startup never creates it, so an
    /// absent vault is an expected state that must degrade one command rather than fault the agent.
    /// </summary>
    private ISecretVault? TryGetVault()
    {
        try
        {
            return _vaultProvider();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static AgentIpcCommandResponse WriteResponse(string responseType, KeyStoreWriteResult result) =>
        AgentIpcCommandResponse.Create(responseType, new KeyStoreWriteResponse(
            KeyStoreIpcContract.CurrentVersion,
            Map(result.Status),
            result.Entry is { } entry ? Map(new KeyStoreEntryUsage(entry, result.ReferencedBy ?? [])) : null,
            result.ActualVersion,
            result.ReferencedBy?.ToArray()));

    private static AgentIpcCommandResponse WriteFailure(string responseType, string message) =>
        AgentIpcCommandResponse.Create(responseType, new KeyStoreWriteResponse(
            KeyStoreIpcContract.CurrentVersion,
            KeyStoreWriteOutcome.Rejected,
            Failure: new StorageIpcFailure(
                "keystore.request.invalid",
                StorageIpcFailureCategory.Validation,
                message,
                IsTransient: false)));

    private static AgentIpcCommandResponse ListFailure(string message) =>
        AgentIpcCommandResponse.Create(KeyStoreIpcMessageTypes.ListResponse, new KeyStoreListResponse(
            KeyStoreIpcContract.CurrentVersion,
            [],
            new StorageIpcFailure(
                "keystore.request.invalid",
                StorageIpcFailureCategory.Validation,
                message,
                IsTransient: false)));

    private static KeyStoreEntryDocument Map(KeyStoreEntryUsage usage)
    {
        var entry = usage.Entry;
        return new KeyStoreEntryDocument(
            entry.Id.Value,
            Map(entry.Kind),
            entry.DisplayName,
            entry.Description,
            [.. entry.Tags],
            entry.MaterialReference.Value,
            entry.PassphraseReference?.Value,
            Map(entry.Summary),
            entry.Version,
            entry.CreatedUtc,
            entry.UpdatedUtc,
            [.. usage.ReferencedByProfileNames.Take(KeyStoreIpcLimits.MaximumReferencedProfiles)]);
    }

    private static KeyStoreSummaryDocument Map(KeyMaterialSummary summary) => summary switch
    {
        Pkcs12CertificateSummary certificate => new KeyStoreSummaryDocument(
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore,
            certificate.NotAfter,
            certificate.Sha256Thumbprint,
            certificate.KeyAlgorithm,
            certificate.KeySizeBits,
            certificate.HasPrivateKey,
            certificate.ChainLength),
        SshPrivateKeySummary key => new KeyStoreSummaryDocument(
            KeyFormat: MapFormat(key.Format),
            PublicKeyAlgorithm: key.PublicKeyAlgorithm,
            Sha256Fingerprint: key.Sha256Fingerprint,
            Comment: key.Comment),
        _ => new KeyStoreSummaryDocument()
    };

    private static KeyStoreWriteOutcome Map(KeyStoreWriteStatus status) => status switch
    {
        KeyStoreWriteStatus.Succeeded => KeyStoreWriteOutcome.Applied,
        KeyStoreWriteStatus.NotFound => KeyStoreWriteOutcome.NotFound,
        KeyStoreWriteStatus.VersionConflict => KeyStoreWriteOutcome.VersionConflict,
        KeyStoreWriteStatus.NameConflict => KeyStoreWriteOutcome.NameConflict,
        KeyStoreWriteStatus.StillReferenced => KeyStoreWriteOutcome.StillReferenced,
        _ => KeyStoreWriteOutcome.Rejected
    };

    private static KeyMaterialKind Map(KeyStoreMaterialKind kind) => kind switch
    {
        KeyStoreMaterialKind.Pkcs12Certificate => KeyMaterialKind.Pkcs12Certificate,
        _ => KeyMaterialKind.SshPrivateKey
    };

    private static KeyMaterialKind? Map(KeyStoreMaterialKind? kind) => kind is { } value ? Map(value) : null;

    private static KeyStoreMaterialKind Map(KeyMaterialKind kind) => kind switch
    {
        KeyMaterialKind.Pkcs12Certificate => KeyStoreMaterialKind.Pkcs12Certificate,
        _ => KeyStoreMaterialKind.SshPrivateKey
    };

    private static SftpPrivateKeyFormat MapFormat(KeyStorePrivateKeyFormat format) => format switch
    {
        KeyStorePrivateKeyFormat.OpenSsh => SftpPrivateKeyFormat.OpenSsh,
        KeyStorePrivateKeyFormat.Pem => SftpPrivateKeyFormat.Pem,
        _ => SftpPrivateKeyFormat.Pkcs8
    };

    private static KeyStorePrivateKeyFormat MapFormat(SftpPrivateKeyFormat format) => format switch
    {
        SftpPrivateKeyFormat.OpenSsh => KeyStorePrivateKeyFormat.OpenSsh,
        SftpPrivateKeyFormat.Pem => KeyStorePrivateKeyFormat.Pem,
        _ => KeyStorePrivateKeyFormat.Pkcs8
    };
}
