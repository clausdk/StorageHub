using System.Globalization;
using System.Security;

namespace StorageHub.Desktop;

public readonly record struct LocalBrowserLocation
{
    private LocalBrowserLocation(string? directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public static LocalBrowserLocation ThisPc { get; } = new(null);

    public string? DirectoryPath { get; }

    public bool IsThisPc => DirectoryPath is null;

    public string DisplayText => DirectoryPath ?? "This PC";

    public static LocalBrowserLocation FromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath.Trim());
        return new LocalBrowserLocation(Path.TrimEndingDirectorySeparator(fullPath));
    }

    public static bool TryParseAddress(
        string? address,
        out LocalBrowserLocation location,
        out string? errorMessage)
    {
        location = ThisPc;
        errorMessage = null;
        var candidate = address?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorMessage = "Enter a folder path or 'This PC'.";
            return false;
        }

        if (string.Equals(candidate, "This PC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        try
        {
            location = FromDirectory(candidate);
            return true;
        }
        catch (Exception error) when (LocalBrowserErrors.IsExpected(error))
        {
            errorMessage = LocalBrowserErrors.ToSafeMessage(error);
            return false;
        }
    }

    public LocalBrowserLocation GetParent()
    {
        if (DirectoryPath is null)
        {
            return ThisPc;
        }

        try
        {
            var parent = Directory.GetParent(DirectoryPath);
            return parent is null ? ThisPc : FromDirectory(parent.FullName);
        }
        catch (Exception error) when (LocalBrowserErrors.IsExpected(error))
        {
            return ThisPc;
        }
    }

    public bool IsSameLocation(LocalBrowserLocation other) =>
        IsThisPc == other.IsThisPc &&
        (IsThisPc || string.Equals(DirectoryPath, other.DirectoryPath, StringComparison.OrdinalIgnoreCase));
}

public sealed record LocalBrowserEntry(
    string Name,
    string FullPath,
    bool IsContainer,
    long? Length,
    DateTimeOffset? Modified,
    string Type,
    string Status);

public sealed record LocalBrowserSnapshot(
    LocalBrowserLocation Location,
    IReadOnlyList<LocalBrowserEntry> Entries,
    string? ContinuationToken = null,
    long IndexedEntryCount = 0)
{
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
}

public interface ILocalFileBrowserDataSource
{
    Task<LocalBrowserSnapshot> BrowseAsync(LocalBrowserLocation location, CancellationToken cancellationToken);
}

public interface IPagedLocalFileBrowserDataSource : ILocalFileBrowserDataSource
{
    Task<LocalBrowserSnapshot> BrowsePageAsync(
        LocalBrowserLocation location,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    void Release(string? continuationToken);
}

public sealed class LocalFileBrowserDataSource : IPagedLocalFileBrowserDataSource, IDisposable
{
    private readonly object _sessionGate = new();
    private readonly Dictionary<string, DirectoryEnumerationSession> _sessions = new(StringComparer.Ordinal);

    public Task<LocalBrowserSnapshot> BrowseAsync(
        LocalBrowserLocation location,
        CancellationToken cancellationToken) =>
        Task.Run(() => Browse(location, cancellationToken), cancellationToken);

    public Task<LocalBrowserSnapshot> BrowsePageAsync(
        LocalBrowserLocation location,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Task.Run(
            () => BrowsePage(location, pageSize, continuationToken, cancellationToken),
            cancellationToken);
    }

    public void Release(string? continuationToken)
    {
        if (string.IsNullOrWhiteSpace(continuationToken)) return;
        lock (_sessionGate)
        {
            if (_sessions.Remove(continuationToken, out var session))
            {
                lock (session.Gate) session.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sessionGate)
        {
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
        }
    }

    private LocalBrowserSnapshot BrowsePage(
        LocalBrowserLocation location,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (location.IsThisPc)
        {
            return new LocalBrowserSnapshot(location, EnumerateDrives(cancellationToken), IndexedEntryCount: DriveInfo.GetDrives().Length);
        }

        DirectoryEnumerationSession session;
        string token;
        var createdSession = false;
        lock (_sessionGate)
        {
            if (continuationToken is null)
            {
                var directory = new DirectoryInfo(location.DirectoryPath!);
                if (!directory.Exists) throw new DirectoryNotFoundException();
                token = Guid.NewGuid().ToString("N");
                session = new DirectoryEnumerationSession(location, directory.EnumerateFileSystemInfos().GetEnumerator());
                _sessions.Add(token, session);
                createdSession = true;
            }
            else if (!_sessions.TryGetValue(continuationToken, out session!) ||
                !session.Location.IsSameLocation(location))
            {
                throw new InvalidOperationException("The local listing continuation is no longer available.");
            }
            else
            {
                token = continuationToken;
            }
        }

        try
        {
            var entries = new List<LocalBrowserEntry>(pageSize);
            var exhausted = false;
            lock (session.Gate)
            {
                for (; entries.Count < pageSize;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!session.Enumerator.MoveNext())
                    {
                        exhausted = true;
                        break;
                    }
                    var item = session.Enumerator.Current;
                    if (!string.Equals(item.Name, ".storagehub-internal", StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(CreateEntry(item));
                    }
                }
                session.IndexedCount += entries.Count;
            }

            entries.Sort(static (left, right) =>
            {
                var containerOrder = right.IsContainer.CompareTo(left.IsContainer);
                return containerOrder != 0 ? containerOrder : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
            if (exhausted) Release(token);
            return new LocalBrowserSnapshot(
                location,
                entries,
                exhausted ? null : token,
                session.IndexedCount);
        }
        catch
        {
            if (createdSession) Release(token);
            throw;
        }
    }

    private static LocalBrowserSnapshot Browse(
        LocalBrowserLocation location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = location.IsThisPc
            ? EnumerateDrives(cancellationToken)
            : EnumerateDirectory(location.DirectoryPath!, cancellationToken);
        return new LocalBrowserSnapshot(location, entries);
    }

    private static LocalBrowserEntry[] EnumerateDrives(CancellationToken cancellationToken)
    {
        var entries = new List<LocalBrowserEntry>();
        foreach (var drive in DriveInfo.GetDrives().OrderBy(static drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isReady = false;
            long? totalSize = null;
            var type = $"{DescribeDriveType(drive.DriveType)} drive";
            var status = "Unavailable";
            try
            {
                isReady = drive.IsReady;
                if (isReady)
                {
                    totalSize = drive.TotalSize;
                    status = $"{UiFormatting.FormatBytes(drive.AvailableFreeSpace)} free";
                }
            }
            catch (Exception error) when (LocalBrowserErrors.IsExpected(error))
            {
                status = "Unavailable";
            }

            entries.Add(new LocalBrowserEntry(
                drive.Name,
                drive.Name,
                IsContainer: true,
                totalSize,
                Modified: null,
                type,
                isReady ? status : "Unavailable"));
        }

        return entries.ToArray();
    }

    private static LocalBrowserEntry[] EnumerateDirectory(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException();
        }

        var entries = new List<LocalBrowserEntry>();
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(item.Name, ".storagehub-internal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(CreateEntry(item));
        }

        entries.Sort(static (left, right) =>
        {
            var containerOrder = right.IsContainer.CompareTo(left.IsContainer);
            return containerOrder != 0
                ? containerOrder
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });
        return entries.ToArray();
    }

    private static LocalBrowserEntry CreateEntry(FileSystemInfo item)
    {
        var isContainer = item is DirectoryInfo;
        long? length = null;
        DateTimeOffset? modified = null;
        var statusParts = new List<string>(2);
        try
        {
            if (item is FileInfo file)
            {
                length = file.Length;
            }

            modified = new DateTimeOffset(item.LastWriteTime);
            var attributes = item.Attributes;
            if ((attributes & FileAttributes.Hidden) != 0)
            {
                statusParts.Add("Hidden");
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                statusParts.Add("Read-only");
            }
        }
        catch (Exception error) when (LocalBrowserErrors.IsExpected(error))
        {
            statusParts.Clear();
            statusParts.Add("Metadata unavailable");
        }

        return new LocalBrowserEntry(
            item.Name,
            item.FullName,
            isContainer,
            length,
            modified,
            isContainer ? "File folder" : LocalBrowserPresentation.DescribeFileType(item.Extension),
            string.Join(" · ", statusParts));
    }

    private static string DescribeDriveType(DriveType driveType) => driveType switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Network => "Network",
        DriveType.Removable => "Removable",
        DriveType.CDRom => "Optical",
        DriveType.Ram => "RAM",
        _ => "Storage"
    };

    private sealed record DirectoryEnumerationSession(
        LocalBrowserLocation Location,
        IEnumerator<FileSystemInfo> Enumerator) : IDisposable
    {
        public object Gate { get; } = new();
        public long IndexedCount { get; set; }
        public void Dispose() => Enumerator.Dispose();
    }
}

public sealed class LocalBrowserHistory
{
    private readonly List<LocalBrowserLocation> _locations = [LocalBrowserLocation.ThisPc];
    private int _index;

    public LocalBrowserLocation Current => _locations[_index];

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index < _locations.Count - 1;

    public LocalBrowserLocation? PeekBack() => CanGoBack ? _locations[_index - 1] : null;

    public LocalBrowserLocation? PeekForward() => CanGoForward ? _locations[_index + 1] : null;

    public void Navigate(LocalBrowserLocation location)
    {
        if (Current.IsSameLocation(location))
        {
            return;
        }

        if (CanGoForward)
        {
            _locations.RemoveRange(_index + 1, _locations.Count - _index - 1);
        }

        _locations.Add(location);
        _index = _locations.Count - 1;
    }

    public bool MoveBack()
    {
        if (!CanGoBack)
        {
            return false;
        }

        _index--;
        return true;
    }

    public bool MoveForward()
    {
        if (!CanGoForward)
        {
            return false;
        }

        _index++;
        return true;
    }
}

public enum LocalBrowserNavigationKind
{
    Navigate,
    Back,
    Forward,
    Up,
    Refresh
}

public enum LocalBrowserNavigationStatus
{
    Succeeded,
    Failed,
    Canceled,
    Superseded,
    NoTarget
}

public sealed record LocalBrowserNavigationResult(
    LocalBrowserNavigationStatus Status,
    LocalBrowserSnapshot? Snapshot = null,
    string? ErrorMessage = null,
    bool AppendedPage = false);

public sealed class LocalBrowserController : IAsyncDisposable
{
    public const int DefaultPageSize = 500;
    private readonly object _sync = new();
    private readonly ILocalFileBrowserDataSource _dataSource;
    private readonly LocalBrowserHistory _history = new();
    private CancellationTokenSource? _activeNavigation;
    private long _navigationSequence;
    private bool _disposed;
    private string? _continuationToken;

    public LocalBrowserController(ILocalFileBrowserDataSource? dataSource = null)
    {
        _dataSource = dataSource ?? new LocalFileBrowserDataSource();
    }

    public LocalBrowserLocation CurrentLocation
    {
        get
        {
            lock (_sync)
            {
                return _history.Current;
            }
        }
    }

    public bool CanGoBack
    {
        get
        {
            lock (_sync)
            {
                return _history.CanGoBack;
            }
        }
    }

    public bool CanGoForward
    {
        get
        {
            lock (_sync)
            {
                return _history.CanGoForward;
            }
        }
    }

    public LocalBrowserSnapshot? CurrentSnapshot { get; private set; }

    public async Task<LocalBrowserNavigationResult> NavigateAsync(
        LocalBrowserNavigationKind kind,
        LocalBrowserLocation? location = null,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource navigationCancellation;
        CancellationTokenSource? previousNavigation;
        LocalBrowserLocation target;
        long sequence;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var resolvedTarget = ResolveTarget(kind, location);
            if (resolvedTarget is null)
            {
                return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.NoTarget);
            }

            target = resolvedTarget.Value;
            previousNavigation = _activeNavigation;
            navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeNavigation = navigationCancellation;
            sequence = ++_navigationSequence;
        }

        TryCancel(previousNavigation);

        try
        {
            ReleaseContinuation();
            var snapshot = await BrowseFirstPageAsync(target, navigationCancellation.Token)
                .ConfigureAwait(false);
            lock (_sync)
            {
                if (sequence != _navigationSequence || _disposed)
                {
                    if (_dataSource is IPagedLocalFileBrowserDataSource paged)
                    {
                        paged.Release(snapshot.ContinuationToken);
                    }
                    return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Superseded);
                }

                CommitNavigation(kind, target);
                _continuationToken = snapshot.ContinuationToken;
                CurrentSnapshot = snapshot;
                return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Succeeded, snapshot);
            }
        }
        catch (OperationCanceledException) when (navigationCancellation.IsCancellationRequested)
        {
            lock (_sync)
            {
                return new LocalBrowserNavigationResult(
                    sequence == _navigationSequence
                        ? LocalBrowserNavigationStatus.Canceled
                        : LocalBrowserNavigationStatus.Superseded);
            }
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                if (sequence != _navigationSequence || _disposed)
                {
                    return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Superseded);
                }
            }

            if (error is DirectoryNotFoundException or DriveNotFoundException && !target.IsThisPc)
            {
                try
                {
                    var fallback = await BrowseNearestAvailableParentAsync(
                        target,
                        navigationCancellation.Token).ConfigureAwait(false);
                    if (fallback is not null)
                    {
                        lock (_sync)
                        {
                            if (sequence != _navigationSequence || _disposed)
                            {
                                if (_dataSource is IPagedLocalFileBrowserDataSource paged)
                                {
                                    paged.Release(fallback.Value.Snapshot.ContinuationToken);
                                }
                                return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Superseded);
                            }

                            _history.Navigate(fallback.Value.Location);
                            _continuationToken = fallback.Value.Snapshot.ContinuationToken;
                            CurrentSnapshot = fallback.Value.Snapshot;
                            return new LocalBrowserNavigationResult(
                                LocalBrowserNavigationStatus.Succeeded,
                                fallback.Value.Snapshot,
                                "That folder no longer exists. StorageHub moved to the nearest available parent.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_sync)
                    {
                        return new LocalBrowserNavigationResult(
                            sequence == _navigationSequence
                                ? LocalBrowserNavigationStatus.Canceled
                                : LocalBrowserNavigationStatus.Superseded);
                    }
                }
            }

            return new LocalBrowserNavigationResult(
                LocalBrowserNavigationStatus.Failed,
                ErrorMessage: LocalBrowserErrors.ToSafeMessage(error));
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeNavigation, navigationCancellation))
                {
                    _activeNavigation = null;
                }
            }

            navigationCancellation.Dispose();
        }
    }

    public async Task<LocalBrowserNavigationResult> LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        string? token;
        LocalBrowserLocation location;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token = _continuationToken;
            location = _history.Current;
        }
        if (token is null || _dataSource is not IPagedLocalFileBrowserDataSource paged)
        {
            return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.NoTarget);
        }

        try
        {
            var page = await paged.BrowsePageAsync(location, DefaultPageSize, token, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || !location.IsSameLocation(_history.Current) || token != _continuationToken)
                {
                    paged.Release(page.ContinuationToken);
                    return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Superseded);
                }
                _continuationToken = page.ContinuationToken;
                CurrentSnapshot = page;
            }
            return new LocalBrowserNavigationResult(
                LocalBrowserNavigationStatus.Succeeded,
                page,
                AppendedPage: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Canceled);
        }
        catch (Exception error)
        {
            return new LocalBrowserNavigationResult(
                LocalBrowserNavigationStatus.Failed,
                ErrorMessage: LocalBrowserErrors.ToSafeMessage(error));
        }
    }

    private async Task<(LocalBrowserLocation Location, LocalBrowserSnapshot Snapshot)?>
        BrowseNearestAvailableParentAsync(
            LocalBrowserLocation missingLocation,
            CancellationToken cancellationToken)
    {
        var candidate = missingLocation.GetParent();
        for (var depth = 0; depth < 256; depth++)
        {
            try
            {
                var snapshot = await BrowseFirstPageAsync(candidate, cancellationToken).ConfigureAwait(false);
                return (candidate, snapshot);
            }
            catch (Exception error) when (error is DirectoryNotFoundException or DriveNotFoundException)
            {
                if (candidate.IsThisPc)
                {
                    return null;
                }

                candidate = candidate.GetParent();
            }
            catch (Exception error) when (LocalBrowserErrors.IsExpected(error) && error is not OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    public void CancelCurrentNavigation()
    {
        CancellationTokenSource? navigation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _navigationSequence++;
            navigation = _activeNavigation;
            _activeNavigation = null;
        }

        TryCancel(navigation);
    }

    public ValueTask DisposeAsync()
    {
        CancellationTokenSource? navigation;
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            CurrentSnapshot = null;
            _navigationSequence++;
            navigation = _activeNavigation;
            _activeNavigation = null;
        }

        TryCancel(navigation);
        ReleaseContinuation();

        if (_dataSource is IDisposable disposable) disposable.Dispose();

        return ValueTask.CompletedTask;
    }

    private async Task<LocalBrowserSnapshot> BrowseFirstPageAsync(
        LocalBrowserLocation location,
        CancellationToken cancellationToken)
    {
        return _dataSource is IPagedLocalFileBrowserDataSource paged
            ? await paged.BrowsePageAsync(location, DefaultPageSize, null, cancellationToken).ConfigureAwait(false)
            : await _dataSource.BrowseAsync(location, cancellationToken).ConfigureAwait(false);
    }

    private void ReleaseContinuation()
    {
        string? token;
        lock (_sync)
        {
            token = _continuationToken;
            _continuationToken = null;
        }
        if (_dataSource is IPagedLocalFileBrowserDataSource paged) paged.Release(token);
    }

    private LocalBrowserLocation? ResolveTarget(
        LocalBrowserNavigationKind kind,
        LocalBrowserLocation? requestedLocation) => kind switch
        {
            LocalBrowserNavigationKind.Navigate => requestedLocation ??
                throw new ArgumentException("A destination is required for direct navigation.", nameof(requestedLocation)),
            LocalBrowserNavigationKind.Back => _history.PeekBack(),
            LocalBrowserNavigationKind.Forward => _history.PeekForward(),
            LocalBrowserNavigationKind.Up => _history.Current.GetParent(),
            LocalBrowserNavigationKind.Refresh => _history.Current,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown navigation kind.")
        };

    private void CommitNavigation(LocalBrowserNavigationKind kind, LocalBrowserLocation target)
    {
        switch (kind)
        {
            case LocalBrowserNavigationKind.Navigate:
            case LocalBrowserNavigationKind.Up:
                _history.Navigate(target);
                break;
            case LocalBrowserNavigationKind.Back:
                _history.MoveBack();
                break;
            case LocalBrowserNavigationKind.Forward:
                _history.MoveForward();
                break;
            case LocalBrowserNavigationKind.Refresh:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown navigation kind.");
        }
    }

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

public static class LocalBrowserPresentation
{
    public static IReadOnlyList<LocalBrowserEntry> ApplyFilter(
        IEnumerable<LocalBrowserEntry> entries,
        string? filter)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var candidate = filter?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return entries as IReadOnlyList<LocalBrowserEntry> ?? entries.ToArray();
        }

        return entries.Where(entry => MatchesFilter(entry.Name, candidate)).ToArray();
    }

    public static bool MatchesFilter(string name, string? filter)
    {
        ArgumentNullException.ThrowIfNull(name);
        var candidate = filter?.Trim();
        return string.IsNullOrEmpty(candidate) || IsMatch(name, candidate);
    }

    public static string FormatModified(DateTimeOffset? modified) =>
        modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    public static string DescribeFileType(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "File";
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }

    private static bool IsMatch(string value, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var retryValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' ||
                 char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}

public static class LocalBrowserErrors
{
    public static bool IsExpected(Exception error) => error is
        UnauthorizedAccessException or
        SecurityException or
        DirectoryNotFoundException or
        DriveNotFoundException or
        IOException or
        ArgumentException or
        NotSupportedException or
        PathTooLongException;

    public static string ToSafeMessage(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            UnauthorizedAccessException or SecurityException =>
                "Access denied. You do not have permission to open this location.",
            DirectoryNotFoundException or DriveNotFoundException =>
                "This location could not be found. It may have moved or been disconnected.",
            PathTooLongException =>
                "This folder path is too long for Windows to open.",
            ArgumentException or NotSupportedException =>
                "The address is not a valid folder path.",
            IOException =>
                "Windows could not read this location. It may be disconnected or in use.",
            _ => "StorageHub could not open this location."
        };
    }
}
