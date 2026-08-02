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
    IReadOnlyList<LocalBrowserEntry> Entries);

public interface ILocalFileBrowserDataSource
{
    Task<LocalBrowserSnapshot> BrowseAsync(LocalBrowserLocation location, CancellationToken cancellationToken);
}

public sealed class LocalFileBrowserDataSource : ILocalFileBrowserDataSource
{
    public Task<LocalBrowserSnapshot> BrowseAsync(
        LocalBrowserLocation location,
        CancellationToken cancellationToken) =>
        Task.Run(() => Browse(location, cancellationToken), cancellationToken);

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
    string? ErrorMessage = null);

public sealed class LocalBrowserController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ILocalFileBrowserDataSource _dataSource;
    private readonly LocalBrowserHistory _history = new();
    private CancellationTokenSource? _activeNavigation;
    private long _navigationSequence;
    private bool _disposed;

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
            var snapshot = await _dataSource
                .BrowseAsync(target, navigationCancellation.Token)
                .ConfigureAwait(false);
            lock (_sync)
            {
                if (sequence != _navigationSequence || _disposed)
                {
                    return new LocalBrowserNavigationResult(LocalBrowserNavigationStatus.Superseded);
                }

                CommitNavigation(kind, target);
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
            _navigationSequence++;
            navigation = _activeNavigation;
            _activeNavigation = null;
        }

        TryCancel(navigation);

        return ValueTask.CompletedTask;
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
