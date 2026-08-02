using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Sync;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Exposes only bounded, read-only advanced object inspection over the normal authenticated pipe.
/// Saved profile resolution and the root-scoped provider connection are delegated to the same
/// connector used by background synchronization.
/// </summary>
public sealed class ObjectInspectorIpcCommandService : IAgentIpcCommandHandler
{
    private readonly ISyncEndpointConnector _connector;

    public ObjectInspectorIpcCommandService(ISyncEndpointConnector connector)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public bool CanHandle(string messageType) => messageType is
        ObjectInspectorIpcMessageTypes.VersionListRequest or
        ObjectInspectorIpcMessageTypes.MetadataGetRequest or
        ObjectInspectorIpcMessageTypes.TagsGetRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            ObjectInspectorIpcMessageTypes.VersionListRequest =>
                ListVersionsAsync(request, cancellationToken),
            ObjectInspectorIpcMessageTypes.MetadataGetRequest =>
                GetMetadataAsync(request, cancellationToken),
            ObjectInspectorIpcMessageTypes.TagsGetRequest =>
                GetTagsAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> ListVersionsAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ObjectVersionListRequest request;
        try
        {
            request = envelope.DeserializePayload<ObjectVersionListRequest>();
        }
        catch (JsonException)
        {
            return InvalidPayload();
        }

        if (!request.HasValidBounds)
        {
            return InvalidRequest(request.ContractVersion);
        }

        try
        {
            var opened = await _connector.OpenAsync(
                new ConnectionProfileId(request.Address.ConnectionId),
                cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return VersionFailure(request, SanitizeFailure(opened.Error));
            }

            await using var connection = opened.Value;
            var validated = ValidateAndCreateAddress(connection.Session, request.Address);
            if (validated.Failure is not null)
            {
                return VersionFailure(request, validated.Failure);
            }

            if (connection.Session is not IStorageAdvancedEndpointSession advanced)
            {
                return VersionFailure(request, AdvancedUnsupported());
            }

            var listed = await advanced.ListObjectVersionsAsync(
                validated.Address!,
                new StorageVersionListRequest(
                    request.PageSize,
                    request.ContinuationToken,
                    request.IncludeDeleteMarkers),
                cancellationToken).ConfigureAwait(false);
            if (listed.IsFailure)
            {
                return VersionFailure(request, SanitizeFailure(listed.Error));
            }

            var pageFailure = ValidateVersionPage(listed.Value, validated.Address!, request);
            if (pageFailure is not null)
            {
                return VersionFailure(request, pageFailure);
            }

            var versions = listed.Value.Versions.Select(static version => new ObjectVersionSummary(
                version.Address.VersionId!,
                version.Address.EntityTag,
                version.Size,
                version.LastModifiedUtc,
                version.IsLatest,
                version.IsDeleteMarker)).ToArray();
            return AgentIpcCommandResponse.Create(
                ObjectInspectorIpcMessageTypes.VersionListResponse,
                new ObjectVersionListResponse(
                    request.ContractVersion,
                    request.Address,
                    versions,
                    listed.Value.ContinuationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return VersionFailure(request, InspectorUnavailable());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetMetadataAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ObjectMetadataGetRequest request;
        try
        {
            request = envelope.DeserializePayload<ObjectMetadataGetRequest>();
        }
        catch (JsonException)
        {
            return InvalidPayload();
        }

        if (!request.HasValidBounds)
        {
            return InvalidRequest(request.ContractVersion);
        }

        try
        {
            var opened = await _connector.OpenAsync(
                new ConnectionProfileId(request.Address.ConnectionId),
                cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return MetadataFailure(request, SanitizeFailure(opened.Error));
            }

            await using var connection = opened.Value;
            var validated = ValidateAndCreateAddress(connection.Session, request.Address);
            if (validated.Failure is not null)
            {
                return MetadataFailure(request, validated.Failure);
            }

            if (connection.Session is not IStorageAdvancedEndpointSession advanced)
            {
                return MetadataFailure(request, AdvancedUnsupported());
            }

            var read = await advanced.GetMetadataAsync(validated.Address!, cancellationToken)
                .ConfigureAwait(false);
            if (read.IsFailure)
            {
                return MetadataFailure(request, SanitizeFailure(read.Error));
            }

            var metadata = read.Value.Values
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new ObjectMetadataEntry(pair.Key, pair.Value))
                .ToArray();
            var response = new ObjectMetadataGetResponse(
                request.ContractVersion,
                request.Address,
                metadata);
            return response.HasValidMetadataBounds
                ? AgentIpcCommandResponse.Create(
                    ObjectInspectorIpcMessageTypes.MetadataGetResponse,
                    response)
                : MetadataFailure(request, InvalidProviderResponse());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MetadataFailure(request, InspectorUnavailable());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetTagsAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ObjectTagsGetRequest request;
        try
        {
            request = envelope.DeserializePayload<ObjectTagsGetRequest>();
        }
        catch (JsonException)
        {
            return InvalidPayload();
        }

        if (!request.HasValidBounds)
        {
            return InvalidRequest(request.ContractVersion);
        }

        try
        {
            var opened = await _connector.OpenAsync(
                new ConnectionProfileId(request.Address.ConnectionId),
                cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return TagsFailure(request, SanitizeFailure(opened.Error));
            }

            await using var connection = opened.Value;
            var validated = ValidateAndCreateAddress(connection.Session, request.Address);
            if (validated.Failure is not null)
            {
                return TagsFailure(request, validated.Failure);
            }

            if (connection.Session is not IStorageAdvancedEndpointSession advanced)
            {
                return TagsFailure(request, AdvancedUnsupported());
            }

            var read = await advanced.GetTagsAsync(validated.Address!, cancellationToken)
                .ConfigureAwait(false);
            if (read.IsFailure)
            {
                return TagsFailure(request, SanitizeFailure(read.Error));
            }

            var tags = read.Value.Values
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new ObjectTagEntry(pair.Key, pair.Value))
                .ToArray();
            var response = new ObjectTagsGetResponse(
                request.ContractVersion,
                request.Address,
                tags);
            return response.HasValidTagBounds
                ? AgentIpcCommandResponse.Create(
                    ObjectInspectorIpcMessageTypes.TagsGetResponse,
                    response)
                : TagsFailure(request, InvalidProviderResponse());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TagsFailure(request, InspectorUnavailable());
        }
    }

    private static AddressValidation ValidateAndCreateAddress(
        IStorageEndpointSession? session,
        ObjectInspectorAddress requested)
    {
        var expectedProfileId = new ConnectionProfileId(requested.ConnectionId);
        if (session is null ||
            session.ProfileId != expectedProfileId ||
            !string.Equals(session.RootIdentity, requested.RootIdentity, StringComparison.Ordinal))
        {
            return new AddressValidation(null, new StorageIpcFailure(
                "storage.inspector.session_identity_mismatch",
                StorageIpcFailureCategory.Integrity,
                "The opened connection did not match the requested object root.",
                IsTransient: false));
        }

        var address = StorageAddress.Create(
            expectedProfileId,
            session.RootIdentity,
            requested.RelativePath,
            requested.NativeItemId,
            requested.VersionId,
            requested.EntityTag);
        if (address.IsFailure ||
            address.Value.IsRoot ||
            !string.Equals(
                address.Value.CanonicalRelativePath,
                requested.RelativePath,
                StringComparison.Ordinal))
        {
            return new AddressValidation(null, new StorageIpcFailure(
                "storage.inspector.address_invalid",
                StorageIpcFailureCategory.Validation,
                "The inspector requires an exact canonical object path.",
                IsTransient: false));
        }

        return new AddressValidation(address.Value, null);
    }

    private static StorageIpcFailure? ValidateVersionPage(
        StorageObjectVersionPage page,
        StorageAddress requested,
        ObjectVersionListRequest request)
    {
        if (page.Versions.Count > request.PageSize ||
            page.Versions.Count > ObjectInspectorIpcLimits.MaximumVersionPageSize ||
            !ObjectVersionListRequest.IsOptionalToken(page.ContinuationToken))
        {
            return InvalidProviderResponse();
        }

        var versionIds = new HashSet<string>(StringComparer.Ordinal);
        var latestCount = 0;
        foreach (var version in page.Versions)
        {
            if (version is null ||
                version.Address.ProfileId != requested.ProfileId ||
                !string.Equals(version.Address.RootIdentity, requested.RootIdentity, StringComparison.Ordinal) ||
                !string.Equals(
                    version.Address.CanonicalRelativePath,
                    requested.CanonicalRelativePath,
                    StringComparison.Ordinal) ||
                !ObjectInspectorAddress.IsRequiredOpaque(version.Address.VersionId) ||
                !ObjectInspectorAddress.IsOptionalOpaque(version.Address.EntityTag) ||
                version.Size is < 0 ||
                !request.IncludeDeleteMarkers && version.IsDeleteMarker ||
                !versionIds.Add(version.Address.VersionId!))
            {
                return InvalidProviderResponse();
            }

            if (version.IsLatest && ++latestCount > 1)
            {
                return InvalidProviderResponse();
            }
        }

        return null;
    }

    private static AgentIpcCommandResponse InvalidPayload() => AgentIpcCommandResponse.Error(
        "ipc.payload.invalid",
        "The inspector request payload was invalid.");

    private static AgentIpcCommandResponse InvalidRequest(int contractVersion) =>
        AgentIpcCommandResponse.Error(
            ObjectInspectorIpcContract.IsSupported(contractVersion)
                ? "ipc.payload.invalid"
                : "ipc.contract.unsupported",
            ObjectInspectorIpcContract.IsSupported(contractVersion)
                ? "The inspector request exceeded a permitted bound or contained an invalid value."
                : "The requested object inspector contract version is not supported.");

    private static StorageIpcFailure AdvancedUnsupported() => new(
        "storage.inspector.unsupported",
        StorageIpcFailureCategory.Unsupported,
        "The provider does not support advanced object inspection.",
        IsTransient: false);

    private static StorageIpcFailure InvalidProviderResponse() => new(
        "storage.inspector.provider_response_invalid",
        StorageIpcFailureCategory.Integrity,
        "The provider returned object details that could not be exposed safely.",
        IsTransient: false);

    private static StorageIpcFailure InspectorUnavailable() => new(
        "storage.inspector.unavailable",
        StorageIpcFailureCategory.Unavailable,
        "The object details are temporarily unavailable.",
        IsTransient: true);

    private static StorageIpcFailure SanitizeFailure(StorageFailure failure)
    {
        var category = failure.Kind switch
        {
            StorageFailureKind.Validation => StorageIpcFailureCategory.Validation,
            StorageFailureKind.NotFound => StorageIpcFailureCategory.NotFound,
            StorageFailureKind.Conflict => StorageIpcFailureCategory.Conflict,
            StorageFailureKind.Unsupported => StorageIpcFailureCategory.Unsupported,
            StorageFailureKind.Unauthorized => StorageIpcFailureCategory.Unauthorized,
            StorageFailureKind.Unavailable => StorageIpcFailureCategory.Unavailable,
            StorageFailureKind.Timeout => StorageIpcFailureCategory.Timeout,
            StorageFailureKind.Cancelled => StorageIpcFailureCategory.Cancelled,
            StorageFailureKind.Integrity => StorageIpcFailureCategory.Integrity,
            StorageFailureKind.Security => StorageIpcFailureCategory.Security,
            StorageFailureKind.Provider => StorageIpcFailureCategory.Provider,
            _ => StorageIpcFailureCategory.Unexpected
        };
        return new StorageIpcFailure(
            SafeFailureCode(category),
            category,
            SafeFailureMessage(category),
            failure.IsTransient);
    }

    private static string SafeFailureCode(StorageIpcFailureCategory category) => category switch
    {
        StorageIpcFailureCategory.Validation => "storage.inspector.validation",
        StorageIpcFailureCategory.NotFound => "storage.inspector.not_found",
        StorageIpcFailureCategory.Conflict => "storage.inspector.conflict",
        StorageIpcFailureCategory.Unsupported => "storage.inspector.unsupported",
        StorageIpcFailureCategory.Unauthorized => "storage.inspector.unauthorized",
        StorageIpcFailureCategory.Unavailable => "storage.inspector.unavailable",
        StorageIpcFailureCategory.Timeout => "storage.inspector.timeout",
        StorageIpcFailureCategory.Cancelled => "storage.inspector.cancelled",
        StorageIpcFailureCategory.Integrity => "storage.inspector.integrity",
        StorageIpcFailureCategory.Security => "storage.inspector.security",
        StorageIpcFailureCategory.Provider => "storage.inspector.provider",
        _ => "storage.inspector.failed"
    };

    private static string SafeFailureMessage(StorageIpcFailureCategory category) => category switch
    {
        StorageIpcFailureCategory.Validation => "The object inspection request was invalid.",
        StorageIpcFailureCategory.NotFound => "The requested object was not found.",
        StorageIpcFailureCategory.Conflict => "The object changed while it was being inspected.",
        StorageIpcFailureCategory.Unsupported => "The provider does not support this inspection safely.",
        StorageIpcFailureCategory.Unauthorized => "The provider rejected the saved credentials.",
        StorageIpcFailureCategory.Unavailable => "The storage provider is temporarily unavailable.",
        StorageIpcFailureCategory.Timeout => "The storage provider did not respond in time.",
        StorageIpcFailureCategory.Cancelled => "The object inspection was cancelled.",
        StorageIpcFailureCategory.Integrity => "The provider response failed an integrity check.",
        StorageIpcFailureCategory.Security => "The connection requires a security or trust decision.",
        StorageIpcFailureCategory.Provider => "The storage provider could not inspect the object.",
        _ => "The object could not be inspected."
    };

    private static AgentIpcCommandResponse VersionFailure(
        ObjectVersionListRequest request,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
        ObjectInspectorIpcMessageTypes.VersionListResponse,
        new ObjectVersionListResponse(
            request.ContractVersion,
            request.Address,
            [],
            ContinuationToken: null,
            failure));

    private static AgentIpcCommandResponse MetadataFailure(
        ObjectMetadataGetRequest request,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
        ObjectInspectorIpcMessageTypes.MetadataGetResponse,
        new ObjectMetadataGetResponse(
            request.ContractVersion,
            request.Address,
            [],
            failure));

    private static AgentIpcCommandResponse TagsFailure(
        ObjectTagsGetRequest request,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
        ObjectInspectorIpcMessageTypes.TagsGetResponse,
        new ObjectTagsGetResponse(
            request.ContractVersion,
            request.Address,
            [],
            failure));

    private sealed record AddressValidation(StorageAddress? Address, StorageIpcFailure? Failure);
}
