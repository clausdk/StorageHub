using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record ObjectInspectorState(
    ObjectInspectorAddress Address,
    IReadOnlyList<ObjectVersionSummary> Versions,
    string? VersionContinuationToken,
    IReadOnlyList<ObjectMetadataEntry> Metadata,
    IReadOnlyList<ObjectTagEntry> Tags,
    StorageIpcFailure? VersionsFailure = null,
    StorageIpcFailure? MetadataFailure = null,
    StorageIpcFailure? TagsFailure = null)
{
    public bool CanLoadMoreVersions =>
        VersionsFailure is null && VersionContinuationToken is not null;

    public static ObjectInspectorState Empty(ObjectInspectorAddress address) => new(
        address,
        [],
        VersionContinuationToken: null,
        [],
        [],
        VersionsFailure: null,
        MetadataFailure: null,
        TagsFailure: null);
}

/// <summary>Serializes refresh and version paging while retaining independent tab failures.</summary>
public sealed class ObjectInspectorController : IAsyncDisposable
{
    private readonly IObjectInspectorAgentClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public ObjectInspectorController(
        IObjectInspectorAgentClient client,
        ObjectInspectorAddress address,
        bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (address?.HasValidBounds != true)
        {
            throw new ArgumentException(
                "A bounded exact object address is required.",
                nameof(address));
        }

        _ownsClient = ownsClient;
        State = ObjectInspectorState.Empty(address);
    }

    public ObjectInspectorState State { get; private set; }

    public event EventHandler<ObjectInspectorState>? StateChanged;

    public async Task<ObjectInspectorState> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var address = State.Address;
            var versions = await _client.ListVersionsAsync(new ObjectVersionListRequest(
                ObjectInspectorIpcContract.CurrentVersion,
                WithoutVersion(address)), cancellationToken).ConfigureAwait(false);
            var metadata = await _client.GetMetadataAsync(new ObjectMetadataGetRequest(
                ObjectInspectorIpcContract.CurrentVersion,
                address), cancellationToken).ConfigureAwait(false);
            var tags = await _client.GetTagsAsync(new ObjectTagsGetRequest(
                ObjectInspectorIpcContract.CurrentVersion,
                address), cancellationToken).ConfigureAwait(false);

            State = new ObjectInspectorState(
                address,
                versions.Versions,
                versions.ContinuationToken,
                metadata.Metadata,
                tags.Tags,
                versions.Failure,
                metadata.Failure,
                tags.Failure);
            OnStateChanged();
            return State;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ObjectInspectorState> LoadMoreVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!State.CanLoadMoreVersions)
            {
                return State;
            }

            var response = await _client.ListVersionsAsync(new ObjectVersionListRequest(
                ObjectInspectorIpcContract.CurrentVersion,
                WithoutVersion(State.Address),
                ContinuationToken: State.VersionContinuationToken), cancellationToken)
                .ConfigureAwait(false);
            if (response.Failure is not null)
            {
                State = State with
                {
                    VersionContinuationToken = null,
                    VersionsFailure = response.Failure
                };
            }
            else
            {
                var combined = State.Versions.Concat(response.Versions).ToArray();
                if (combined.Select(static version => version.VersionId)
                    .Distinct(StringComparer.Ordinal).Count() != combined.Length)
                {
                    throw new InvalidDataException(
                        "The local agent returned a duplicate object version across pages.");
                }

                State = State with
                {
                    Versions = combined,
                    VersionContinuationToken = response.ContinuationToken,
                    VersionsFailure = null
                };
            }

            OnStateChanged();
            return State;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_ownsClient)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static ObjectInspectorAddress WithoutVersion(ObjectInspectorAddress address) => new(
        address.ConnectionId,
        address.RootIdentity,
        address.RelativePath,
        address.NativeItemId,
        VersionId: null,
        EntityTag: address.EntityTag);

    private void OnStateChanged() => StateChanged?.Invoke(this, State);
}
