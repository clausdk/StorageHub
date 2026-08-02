using Velopack;
using Velopack.Sources;

namespace StorageHub.Desktop;

internal enum DesktopUpdateState
{
    Idle,
    Disabled,
    Unavailable,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    Installing,
    UpToDate,
    Failed
}

internal sealed record DesktopUpdateSnapshot(
    DesktopUpdateState State,
    string Message,
    string? Version = null,
    int? ProgressPercent = null)
{
    internal static DesktopUpdateSnapshot Initial { get; } =
        new(DesktopUpdateState.Idle, "Updates: idle");
}

internal sealed record DesktopUpdateCandidate(string Version, object EngineValue);

internal interface IDesktopUpdateEngine
{
    bool IsInstalled { get; }

    string CurrentVersion { get; }

    Task<DesktopUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken);

    Task DownloadAsync(
        DesktopUpdateCandidate candidate,
        IProgress<int> progress,
        CancellationToken cancellationToken);

    void PrepareSilentApplyAndRestart(DesktopUpdateCandidate candidate);
}

internal interface IDesktopUpdateEngineFactory
{
    IDesktopUpdateEngine Create(bool includePrereleases);
}

internal sealed class VelopackDesktopUpdateEngineFactory : IDesktopUpdateEngineFactory
{
    internal const string TrustedRepositoryUrl = "https://github.com/clausdk/StorageHub";

    public IDesktopUpdateEngine Create(bool includePrereleases) =>
        new VelopackDesktopUpdateEngine(includePrereleases);
}

internal sealed class VelopackDesktopUpdateEngine : IDesktopUpdateEngine
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private readonly UpdateManager _manager;

    internal VelopackDesktopUpdateEngine(bool includePrereleases)
    {
        var source = new GithubSource(
            VelopackDesktopUpdateEngineFactory.TrustedRepositoryUrl,
            accessToken: null,
            prerelease: includePrereleases);
        _manager = new UpdateManager(
            source,
            new UpdateOptions
            {
                AllowVersionDowngrade = false
            });
    }

    public bool IsInstalled => _manager.IsInstalled && !_manager.IsPortable;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? DesktopApplicationVersion.Current;

    public async Task<DesktopUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var updates = await _manager.CheckForUpdatesAsync()
            .WaitAsync(CheckTimeout, cancellationToken)
            .ConfigureAwait(false);
        return updates is null
            ? null
            : new DesktopUpdateCandidate(
                updates.TargetFullRelease.Version.ToString(),
                updates);
    }

    public Task DownloadAsync(
        DesktopUpdateCandidate candidate,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        var updates = RequireUpdateInfo(candidate);
        return _manager.DownloadUpdatesAsync(
            updates,
            value => progress.Report(Math.Clamp(value, 0, 100)),
            cancellationToken);
    }

    public void PrepareSilentApplyAndRestart(DesktopUpdateCandidate candidate)
    {
        var updates = RequireUpdateInfo(candidate);
        _manager.WaitExitThenApplyUpdates(
            updates.TargetFullRelease,
            silent: true,
            restart: true,
            restartArgs: []);
    }

    private static UpdateInfo RequireUpdateInfo(DesktopUpdateCandidate candidate) =>
        candidate.EngineValue as UpdateInfo
        ?? throw new InvalidOperationException("The update candidate did not originate from Velopack.");
}

internal sealed class DesktopUpdater : IDisposable
{
    private readonly DesktopUpdatePreferencesStore _preferencesStore;
    private readonly IDesktopUpdateEngineFactory _engineFactory;
    private DesktopUpdatePreferences _preferences;
    private IDesktopUpdateEngine? _engine;
    private DesktopUpdateCandidate? _candidate;
    private bool _downloaded;
    private int _operationActive;
    private CancellationTokenSource? _automaticOperation;
    private bool _disposed;

    internal DesktopUpdater(
        DesktopUpdatePreferencesStore preferencesStore,
        IDesktopUpdateEngineFactory? engineFactory = null)
    {
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _engineFactory = engineFactory ?? new VelopackDesktopUpdateEngineFactory();
        _preferences = preferencesStore.Load();
    }

    internal event EventHandler<DesktopUpdateSnapshot>? StatusChanged;

    internal event EventHandler? RestartRequested;

    internal DesktopUpdateSnapshot Snapshot { get; private set; } = DesktopUpdateSnapshot.Initial;

    internal DesktopUpdatePreferences Preferences => _preferences;

    internal void SavePreferences(DesktopUpdatePreferences preferences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(preferences);
        _preferencesStore.Save(preferences);
        var sourceChanged = _preferences.IncludePrereleases != preferences.IncludePrereleases;
        _preferences = preferences;
        if (sourceChanged)
        {
            _engine = null;
            _candidate = null;
            _downloaded = false;
        }

        if (!preferences.CheckAutomatically || sourceChanged)
        {
            Volatile.Read(ref _automaticOperation)?.Cancel();
        }

        if (!preferences.CheckAutomatically)
        {
            Publish(new DesktopUpdateSnapshot(DesktopUpdateState.Disabled, "Updates: automatic checks off"));
        }
    }

    internal async Task RunAutomaticAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_preferences.CheckAutomatically)
        {
            Publish(new DesktopUpdateSnapshot(DesktopUpdateState.Disabled, "Updates: automatic checks off"));
            return;
        }

        using var automaticOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Interlocked.CompareExchange(ref _automaticOperation, automaticOperation, null) is not null)
        {
            return;
        }

        try
        {
            var snapshot = await CheckForUpdatesAsync(automaticOperation.Token).ConfigureAwait(false);
            if (snapshot.State != DesktopUpdateState.UpdateAvailable || !_preferences.DownloadAutomatically)
            {
                return;
            }

            snapshot = await DownloadAvailableAsync(automaticOperation.Token).ConfigureAwait(false);
            if (!_disposed &&
                snapshot.State == DesktopUpdateState.ReadyToRestart &&
                _preferences.CheckAutomatically &&
                _preferences.DownloadAutomatically &&
                _preferences.RestartAutomatically)
            {
                ApplyAndRestart();
            }
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _automaticOperation, null, automaticOperation);
        }
    }

    internal async Task<DesktopUpdateSnapshot> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            return Snapshot;
        }

        try
        {
            _candidate = null;
            _downloaded = false;
            _engine ??= _engineFactory.Create(_preferences.IncludePrereleases);
            if (!_engine.IsInstalled)
            {
                return Publish(new DesktopUpdateSnapshot(
                    DesktopUpdateState.Unavailable,
                    "Updates: install StorageHub to enable"));
            }

            Publish(new DesktopUpdateSnapshot(DesktopUpdateState.Checking, "Updates: checking GitHub…"));
            _candidate = await _engine.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            return _candidate is null
                ? Publish(new DesktopUpdateSnapshot(
                    DesktopUpdateState.UpToDate,
                    $"Updates: current ({_engine.CurrentVersion})"))
                : Publish(new DesktopUpdateSnapshot(
                    DesktopUpdateState.UpdateAvailable,
                    $"Update {_candidate.Version} available",
                    _candidate.Version));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _candidate = null;
            _downloaded = false;
            return Publish(new DesktopUpdateSnapshot(
                DesktopUpdateState.Failed,
                "Updates: check failed; try again later"));
        }
        finally
        {
            Volatile.Write(ref _operationActive, 0);
        }
    }

    internal async Task<DesktopUpdateSnapshot> DownloadAvailableAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            return Snapshot;
        }

        try
        {
            if (_engine is null || _candidate is null)
            {
                return Snapshot;
            }

            var candidate = _candidate;
            Publish(new DesktopUpdateSnapshot(
                DesktopUpdateState.Downloading,
                $"Downloading update {candidate.Version}: 0%",
                candidate.Version,
                0));
            var progress = new InlineProgress<int>(value => Publish(new DesktopUpdateSnapshot(
                DesktopUpdateState.Downloading,
                $"Downloading update {candidate.Version}: {value}%",
                candidate.Version,
                value)));
            await _engine.DownloadAsync(candidate, progress, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(candidate, _candidate))
            {
                throw new InvalidOperationException("The selected update changed while downloading.");
            }

            _downloaded = true;
            return Publish(new DesktopUpdateSnapshot(
                DesktopUpdateState.ReadyToRestart,
                $"Update {candidate.Version} ready — restart to install",
                candidate.Version,
                100));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _downloaded = false;
            return Publish(new DesktopUpdateSnapshot(
                DesktopUpdateState.Failed,
                "Updates: download failed; try again later"));
        }
        finally
        {
            Volatile.Write(ref _operationActive, 0);
        }
    }

    internal bool ApplyAndRestart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = _engine;
        var candidate = _candidate;
        if (!_downloaded || engine is null || candidate is null ||
            Snapshot.State != DesktopUpdateState.ReadyToRestart)
        {
            return false;
        }

        _downloaded = false;
        Publish(new DesktopUpdateSnapshot(
            DesktopUpdateState.Installing,
            $"Update {candidate.Version} will install after StorageHub closes",
            candidate.Version,
            100));
        try
        {
            engine.PrepareSilentApplyAndRestart(candidate);
            RestartRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception)
        {
            Publish(new DesktopUpdateSnapshot(
                DesktopUpdateState.Failed,
                "Updates: could not start the installer"));
            return false;
        }
    }

    private DesktopUpdateSnapshot Publish(DesktopUpdateSnapshot snapshot)
    {
        Snapshot = snapshot;
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Read(ref _automaticOperation)?.Cancel();
        GC.SuppressFinalize(this);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
