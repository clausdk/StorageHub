using System.Text;
using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public enum RemoteBrowserOperationStatus
{
    Succeeded,
    Failed,
    Canceled,
    Superseded,
    NoTarget
}

public enum RemoteBrowserNavigationKind
{
    Navigate,
    Back,
    Forward,
    Up,
    Refresh
}

public sealed record RemoteBrowserSnapshot(
    ConnectionSummary Connection,
    string RelativePath,
    IReadOnlyList<StorageListItem> Entries,
    string? ContinuationToken,
    string? RootIdentity = null)
{
    public bool HasMore => !string.IsNullOrEmpty(ContinuationToken);

    public string DisplayPath => RelativePath.Length == 0 ? "/" : "/" + RelativePath;
}

public sealed record RemoteConnectionLoadResult(
    RemoteBrowserOperationStatus Status,
    IReadOnlyList<ConnectionSummary> Connections,
    string? ErrorMessage = null);

public sealed record RemoteBrowserNavigationResult(
    RemoteBrowserOperationStatus Status,
    RemoteBrowserSnapshot? Snapshot = null,
    string? ErrorMessage = null);

public static class RemoteBrowserPath
{
    public static bool TryNormalize(string? value, out string path, out string? errorMessage)
    {
        path = string.Empty;
        errorMessage = null;
        if (value is null)
        {
            errorMessage = "Enter a path relative to the connection root.";
            return false;
        }

        var candidate = value;
        if (candidate.Length > StorageIpcLimits.MaximumRelativePathLength || candidate.Any(char.IsControl))
        {
            errorMessage = "The remote path is too long or contains unsupported characters.";
            return false;
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.StartsWith("\\\\", StringComparison.Ordinal) ||
            candidate.Length >= 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':')
        {
            errorMessage = "Enter a path relative to the selected connection root.";
            return false;
        }

        candidate = candidate.Replace('\\', '/');
        if (candidate.Length > 0 && candidate[0] == '/')
        {
            candidate = candidate[1..];
        }

        var segments = candidate.Normalize(NormalizationForm.FormC)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var canonical = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." || ContainsEncodedTraversal(segment))
            {
                errorMessage = "Parent traversal is not allowed in a remote path.";
                return false;
            }

            canonical.Add(segment);
        }

        path = string.Join('/', canonical);
        if (path.Length > StorageIpcLimits.MaximumRelativePathLength)
        {
            path = string.Empty;
            errorMessage = "The normalized remote path is too long.";
            return false;
        }

        return true;
    }

    public static string GetParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static bool ContainsEncodedTraversal(string segment)
    {
        if (!segment.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var decoded = segment;
            for (var pass = 0; pass < 2; pass++)
            {
                var previous = decoded;
                decoded = Uri.UnescapeDataString(decoded).Replace('\\', '/');
                if (decoded.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(static part => part == ".."))
                {
                    return true;
                }

                if (decoded == previous)
                {
                    break;
                }
            }

            return decoded is "." or "..";
        }
        catch (UriFormatException)
        {
            return true;
        }
    }
}

public sealed class RemoteBrowserController : IAsyncDisposable
{
    public const int DefaultPageSize = StorageIpcLimits.MaximumStableIdentityPageSize;
    public const int MaximumAccumulatedEntries = 10_000;

    private readonly IRemoteStorageAgentClient _client;
    private readonly object _operationLock = new();
    private readonly RemoteBrowserHistory _history = new();
    private CancellationTokenSource? _activeOperation;
    private long _operationSequence;
    private bool _disposed;

    public RemoteBrowserController(IRemoteStorageAgentClient? client = null)
    {
        _client = client ?? new NamedPipeRemoteStorageAgentClient();
    }

    public IReadOnlyList<ConnectionSummary> Connections { get; private set; } = [];

    public ConnectionSummary? SelectedConnection { get; private set; }

    public RemoteBrowserSnapshot? CurrentSnapshot { get; private set; }

    public bool CanGoBack => SelectedConnection is not null && _history.CanGoBack;

    public bool CanGoForward => SelectedConnection is not null && _history.CanGoForward;

    public bool CanGoUp => CurrentSnapshot is { RelativePath.Length: > 0 };

    public async Task<RemoteConnectionLoadResult> LoadConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = BeginOperation(cancellationToken);
        try
        {
            var response = await _client.ListConnectionsAsync(
                new ConnectionListRequest(
                    StorageIpcContract.CurrentVersion,
                    IncludeDisabled: false,
                    Limit: StorageIpcLimits.MaximumConnectionResults),
                operation.Cancellation.Token).ConfigureAwait(false);
            if (!IsCurrent(operation.Sequence))
            {
                return new(RemoteBrowserOperationStatus.Superseded, []);
            }

            if (response.Failure is not null)
            {
                return new(
                    RemoteBrowserOperationStatus.Failed,
                    [],
                    RemoteBrowserErrors.ForFailure(response.Failure));
            }

            Connections = response.Connections
                .Where(static connection => connection.IsEnabled)
                .OrderByDescending(static connection => connection.IsFavorite)
                .ThenBy(static connection => connection.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static connection => connection.ConnectionId)
                .ToArray();
            if (SelectedConnection is not null &&
                !Connections.Any(connection => connection.ConnectionId == SelectedConnection.ConnectionId))
            {
                SelectedConnection = null;
                CurrentSnapshot = null;
                _history.Clear();
            }

            return new(RemoteBrowserOperationStatus.Succeeded, Connections);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RemoteBrowserOperationStatus.Canceled, []);
        }
        catch (OperationCanceledException)
        {
            return new(RemoteBrowserOperationStatus.Superseded, []);
        }
        catch (Exception error) when (RemoteBrowserErrors.IsExpected(error))
        {
            return new(
                RemoteBrowserOperationStatus.Failed,
                [],
                RemoteBrowserErrors.ForException(error));
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public async Task<RemoteBrowserNavigationResult> SelectConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = Connections.FirstOrDefault(candidate =>
            candidate.ConnectionId == connectionId && candidate.IsEnabled);
        if (connection is null)
        {
            return new(
                RemoteBrowserOperationStatus.Failed,
                ErrorMessage: "The saved connection is no longer available.");
        }

        var operation = BeginOperation(cancellationToken);
        try
        {
            var test = await _client.TestConnectionAsync(
                new ConnectionTestRequest(StorageIpcContract.CurrentVersion, connectionId),
                operation.Cancellation.Token).ConfigureAwait(false);
            if (!IsCurrent(operation.Sequence))
            {
                return new(RemoteBrowserOperationStatus.Superseded);
            }

            if (!test.Succeeded || test.Failure is not null)
            {
                return new(
                    RemoteBrowserOperationStatus.Failed,
                    ErrorMessage: RemoteBrowserErrors.ForFailure(test.Failure));
            }

            var listed = await ListPageAsync(connection, string.Empty, null, DefaultPageSize, operation.Cancellation.Token)
                .ConfigureAwait(false);
            if (!IsCurrent(operation.Sequence))
            {
                return new(RemoteBrowserOperationStatus.Superseded);
            }

            if (listed.Failure is not null)
            {
                return new(
                    RemoteBrowserOperationStatus.Failed,
                    ErrorMessage: RemoteBrowserErrors.ForFailure(listed.Failure));
            }

            SelectedConnection = connection;
            _history.Reset(string.Empty);
            CurrentSnapshot = CreateSnapshot(connection, listed);
            return new(RemoteBrowserOperationStatus.Succeeded, CurrentSnapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RemoteBrowserOperationStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return new(RemoteBrowserOperationStatus.Superseded);
        }
        catch (Exception error) when (RemoteBrowserErrors.IsExpected(error))
        {
            return new(
                RemoteBrowserOperationStatus.Failed,
                ErrorMessage: RemoteBrowserErrors.ForException(error));
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public async Task<RemoteBrowserNavigationResult> NavigateAsync(
        RemoteBrowserNavigationKind kind,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SelectedConnection is null || CurrentSnapshot is null)
        {
            return new(RemoteBrowserOperationStatus.NoTarget);
        }

        string? normalizedPath = null;
        if (kind == RemoteBrowserNavigationKind.Navigate &&
            !RemoteBrowserPath.TryNormalize(relativePath, out normalizedPath, out var errorMessage))
        {
            return new(RemoteBrowserOperationStatus.Failed, ErrorMessage: errorMessage);
        }

        var target = _history.Resolve(kind, normalizedPath);
        if (target is null)
        {
            return new(RemoteBrowserOperationStatus.NoTarget);
        }

        var operation = BeginOperation(cancellationToken);
        try
        {
            var listed = await ListPageAsync(
                SelectedConnection,
                target,
                null,
                DefaultPageSize,
                operation.Cancellation.Token).ConfigureAwait(false);
            if (!IsCurrent(operation.Sequence))
            {
                return new(RemoteBrowserOperationStatus.Superseded);
            }

            if (listed.Failure is not null)
            {
                return new(
                    RemoteBrowserOperationStatus.Failed,
                    ErrorMessage: RemoteBrowserErrors.ForFailure(listed.Failure));
            }

            _history.Commit(kind, target);
            CurrentSnapshot = CreateSnapshot(SelectedConnection, listed);
            return new(RemoteBrowserOperationStatus.Succeeded, CurrentSnapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RemoteBrowserOperationStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return new(RemoteBrowserOperationStatus.Superseded);
        }
        catch (Exception error) when (RemoteBrowserErrors.IsExpected(error))
        {
            return new(
                RemoteBrowserOperationStatus.Failed,
                ErrorMessage: RemoteBrowserErrors.ForException(error));
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public async Task<RemoteBrowserNavigationResult> LoadMoreAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = CurrentSnapshot;
        if (SelectedConnection is null || current is null || !current.HasMore)
        {
            return new(RemoteBrowserOperationStatus.NoTarget);
        }

        if (current.Entries.Count >= MaximumAccumulatedEntries)
        {
            return new(
                RemoteBrowserOperationStatus.Failed,
                ErrorMessage: "This folder reached the safe display limit. Narrow the path or use a filter at the provider.");
        }

        var operation = BeginOperation(cancellationToken);
        try
        {
            var pageSize = Math.Min(DefaultPageSize, MaximumAccumulatedEntries - current.Entries.Count);
            var listed = await ListPageAsync(
                SelectedConnection,
                current.RelativePath,
                current.ContinuationToken,
                pageSize,
                operation.Cancellation.Token).ConfigureAwait(false);
            if (!IsCurrent(operation.Sequence) ||
                CurrentSnapshot != current ||
                SelectedConnection.ConnectionId != current.Connection.ConnectionId)
            {
                return new(RemoteBrowserOperationStatus.Superseded);
            }

            if (listed.Failure is not null)
            {
                return new(
                    RemoteBrowserOperationStatus.Failed,
                    ErrorMessage: RemoteBrowserErrors.ForFailure(listed.Failure));
            }

            if (!string.Equals(listed.RootIdentity, current.RootIdentity, StringComparison.Ordinal) ||
                string.Equals(listed.ContinuationToken, current.ContinuationToken, StringComparison.Ordinal) ||
                listed.Entries.Any(next => current.Entries.Any(existing =>
                    string.Equals(existing.RelativePath, next.RelativePath, StringComparison.Ordinal))))
            {
                return new(
                    RemoteBrowserOperationStatus.Failed,
                    ErrorMessage: "The provider returned an inconsistent next page.");
            }

            CurrentSnapshot = current with
            {
                Entries = current.Entries.Concat(listed.Entries).ToArray(),
                ContinuationToken = listed.ContinuationToken
            };
            return new(RemoteBrowserOperationStatus.Succeeded, CurrentSnapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RemoteBrowserOperationStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return new(RemoteBrowserOperationStatus.Superseded);
        }
        catch (Exception error) when (RemoteBrowserErrors.IsExpected(error))
        {
            return new(
                RemoteBrowserOperationStatus.Failed,
                ErrorMessage: RemoteBrowserErrors.ForException(error));
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void CancelCurrentOperation()
    {
        CancellationTokenSource? operation;
        lock (_operationLock)
        {
            _operationSequence = checked(_operationSequence + 1);
            operation = _activeOperation;
            _activeOperation = null;
        }

        TryCancel(operation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? operation;
        lock (_operationLock)
        {
            operation = _activeOperation;
            _activeOperation = null;
        }


        TryCancel(operation);

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private Task<StorageListPageResponse> ListPageAsync(
        ConnectionSummary connection,
        string path,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken) => _client.ListStorageAsync(
        new StorageListPageRequest(
            StorageIpcContract.CurrentVersion,
            connection.ConnectionId,
            path,
            pageSize,
            continuationToken,
            IncludeVersions: false,
            Recursive: false),
        cancellationToken);

    private static RemoteBrowserSnapshot CreateSnapshot(
        ConnectionSummary connection,
        StorageListPageResponse response) => new(
        connection,
        response.RelativePath,
        response.Entries,
        response.ContinuationToken,
        response.RootIdentity);

    private RemoteBrowserOperation BeginOperation(CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long sequence;
        CancellationTokenSource? previous;
        lock (_operationLock)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                throw new ObjectDisposedException(nameof(RemoteBrowserController));
            }

            sequence = _operationSequence = checked(_operationSequence + 1);
            previous = _activeOperation;
            _activeOperation = cancellation;
        }

        TryCancel(previous);

        return new RemoteBrowserOperation(sequence, cancellation);
    }

    private bool IsCurrent(long sequence) =>
        Volatile.Read(ref _operationSequence) == sequence && !_disposed;

    private void EndOperation(RemoteBrowserOperation operation)
    {
        lock (_operationLock)
        {
            if (ReferenceEquals(_activeOperation, operation.Cancellation))
            {
                _activeOperation = null;
            }
        }

        operation.Cancellation.Dispose();
    }

    private sealed record RemoteBrowserOperation(long Sequence, CancellationTokenSource Cancellation);

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded operation completed between capture and cancellation.
        }
    }
}

internal sealed class RemoteBrowserHistory
{
    private readonly List<string> _paths = [];
    private int _index = -1;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _paths.Count - 1;

    public void Reset(string path)
    {
        _paths.Clear();
        _paths.Add(path);
        _index = 0;
    }

    public void Clear()
    {
        _paths.Clear();
        _index = -1;
    }

    public string? Resolve(RemoteBrowserNavigationKind kind, string? explicitPath) => kind switch
    {
        RemoteBrowserNavigationKind.Navigate => explicitPath,
        RemoteBrowserNavigationKind.Back when CanGoBack => _paths[_index - 1],
        RemoteBrowserNavigationKind.Forward when CanGoForward => _paths[_index + 1],
        RemoteBrowserNavigationKind.Up when _index >= 0 && _paths[_index].Length > 0 =>
            RemoteBrowserPath.GetParent(_paths[_index]),
        RemoteBrowserNavigationKind.Refresh when _index >= 0 => _paths[_index],
        _ => null
    };

    public void Commit(RemoteBrowserNavigationKind kind, string path)
    {
        switch (kind)
        {
            case RemoteBrowserNavigationKind.Back:
                _index--;
                break;
            case RemoteBrowserNavigationKind.Forward:
                _index++;
                break;
            case RemoteBrowserNavigationKind.Refresh:
                break;
            case RemoteBrowserNavigationKind.Navigate:
            case RemoteBrowserNavigationKind.Up:
                if (_index >= 0 && string.Equals(_paths[_index], path, StringComparison.Ordinal))
                {
                    break;
                }

                if (_index < _paths.Count - 1)
                {
                    _paths.RemoveRange(_index + 1, _paths.Count - _index - 1);
                }

                _paths.Add(path);
                _index = _paths.Count - 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown remote navigation kind.");
        }
    }
}

internal static class RemoteBrowserErrors
{
    public static bool IsExpected(Exception error) => error is
        IOException or
        TimeoutException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        JsonException or
        ArgumentException;

    public static string ForException(Exception error) => error switch
    {
        TimeoutException => "The background agent did not respond in time.",
        UnauthorizedAccessException => "StorageHub could not authenticate to the local background agent.",
        IOException => "The background agent is unavailable.",
        InvalidDataException or JsonException => "The background agent returned an invalid response.",
        _ => "The remote location could not be opened."
    };

    public static string ForFailure(StorageIpcFailure? failure) => failure?.Category switch
    {
        StorageIpcFailureCategory.Validation => "The remote path or connection settings are invalid.",
        StorageIpcFailureCategory.NotFound => "The remote folder or saved connection was not found.",
        StorageIpcFailureCategory.Unauthorized =>
            "The provider rejected the saved username or credential.",
        StorageIpcFailureCategory.Security =>
            "The saved server-identity trust decision is missing or no longer valid.",
        StorageIpcFailureCategory.Timeout => "The remote provider did not respond in time.",
        StorageIpcFailureCategory.Cancelled => "The remote request was cancelled.",
        StorageIpcFailureCategory.Unsupported => "This provider does not support the requested browse operation.",
        StorageIpcFailureCategory.Integrity => "The provider returned an invalid listing.",
        StorageIpcFailureCategory.Unavailable => "The remote provider is temporarily unavailable.",
        _ => "The remote location could not be opened."
    };
}
