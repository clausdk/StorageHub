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
    private readonly ISecretVault _vault;
    private readonly TimeProvider _timeProvider;

    public KeyStoreIpcCommandService(
        IKeyStoreRepository entries,
        ISecretVault vault,
        TimeProvider? timeProvider = null)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
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

        if (!SecretReference.TryParse(request.MaterialReference, out var material) ||
            !SecretReference.TryParse(request.PassphraseReference, out var passphrase))
        {
            return WriteFailure(KeyStoreIpcMessageTypes.CreateResponse, "The vault references are malformed.");
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
        // strand a profile whose reference still resolves.
        if (written.Status == KeyStoreWriteStatus.Succeeded && written.Entry is { } removed)
        {
            _ = await _vault.DeleteAsync(removed.MaterialReference, cancellationToken).ConfigureAwait(false);
            _ = await _vault.DeleteAsync(removed.PassphraseReference, cancellationToken).ConfigureAwait(false);
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
        SecretReference passphrase,
        CancellationToken cancellationToken)
    {
        if (!await _vault.ExistsAsync(material, cancellationToken).ConfigureAwait(false) ||
            !await _vault.ExistsAsync(passphrase, cancellationToken).ConfigureAwait(false))
        {
            return StorageResult<KeyMaterialSummary>.Fail(new StorageFailure(
                "keystore.reference.unresolved",
                StorageFailureKind.NotFound,
                "The enrolled material could not be found in the vault."));
        }

        await using var materialLease = await _vault.OpenAsync(material, cancellationToken).ConfigureAwait(false);
        await using var passphraseLease = await _vault.OpenAsync(passphrase, cancellationToken).ConfigureAwait(false);
        var secret = Encoding.UTF8.GetString(passphraseLease.Memory.Span);
        try
        {
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
            entry.PassphraseReference.Value,
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
