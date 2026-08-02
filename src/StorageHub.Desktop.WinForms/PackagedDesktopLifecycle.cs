using System.ComponentModel;
using System.Security;
using Velopack.Locators;

namespace StorageHub.Desktop;

public enum AgentEnsureStatus
{
    AlreadyRunning,
    Started,
    MissingExecutable,
    LaunchFailed,
    StartupTimedOut
}

public sealed record AgentEnsureResult(AgentEnsureStatus Status)
{
    public bool IsReady => Status is AgentEnsureStatus.AlreadyRunning or AgentEnsureStatus.Started;
}

public enum AgentShutdownReason
{
    Update,
    Uninstall,
    Restart
}

public enum AutostartConfigurationStatus
{
    Registered,
    Disabled,
    Removed,
    Failed
}

public interface ICurrentUserRunEntryStore
{
    void SetValue(string valueName, string commandLine);

    void Remove(string valueName);
}

public interface IAgentProcessLauncher
{
    bool TryLaunchHidden(string executablePath, string workingDirectory, string argument);
}

public interface IPackagedAgentLifecycleClient
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WaitUntilAvailableAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RequestShutdownAndWaitAsync(
        AgentShutdownReason reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IPackagedAgentProcessMonitor
{
    bool IsRunning(string executablePath);

    ValueTask<bool> WaitForExitAsync(
        int processId,
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record PackagedDesktopLifecycleOptions
{
    public const string DisableAutostartEnvironmentVariable = "STORAGEHUB_DISABLE_AUTOSTART";

    public string RunEntryName { get; init; } = "StorageHub.Agent";

    public string AgentSubdirectory { get; init; } = "Agent";

    public string AgentExecutableName { get; init; } = "StorageHub.Agent.Windows.exe";

    public string AgentArgument { get; init; } = "--background";

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(8);

    internal void Validate()
    {
        ValidateIdentity(RunEntryName, nameof(RunEntryName));
        ValidateSinglePathSegment(AgentSubdirectory, nameof(AgentSubdirectory));
        ValidateSinglePathSegment(AgentExecutableName, nameof(AgentExecutableName));
        ValidateArgument(AgentArgument, nameof(AgentArgument));
        ValidateTimeout(StartupTimeout, nameof(StartupTimeout));
        ValidateTimeout(ShutdownTimeout, nameof(ShutdownTimeout));
    }

    private static void ValidateIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The lifecycle identity must be between 1 and 128 non-control characters.",
                parameterName);
        }
    }

    private static void ValidateSinglePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A single valid path segment is required.", parameterName);
        }
    }

    private static void ValidateArgument(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded, non-control process argument is required.", parameterName);
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(12))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Package lifecycle waits must be between zero and twelve seconds.");
        }
    }
}

/// <summary>
/// Owns the installed Desktop's narrow responsibilities: the per-user logon command,
/// starting the sibling background Agent, and requesting a bounded graceful stop.
/// It never reads, writes, or removes StorageHub's durable data directory.
/// </summary>
public sealed class PackagedDesktopLifecycle
{
    private const string PackagedDesktopStubName = "StorageHub.exe";
    private readonly string _desktopExecutablePath;
    private readonly string _agentDirectory;
    private readonly string _agentExecutablePath;
    private readonly ICurrentUserRunEntryStore _runEntryStore;
    private readonly IAgentProcessLauncher _processLauncher;
    private readonly IPackagedAgentLifecycleClient _agentClient;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly PackagedDesktopLifecycleOptions _options;

    public PackagedDesktopLifecycle(
        string desktopExecutablePath,
        string applicationDirectory,
        ICurrentUserRunEntryStore runEntryStore,
        IAgentProcessLauncher processLauncher,
        IPackagedAgentLifecycleClient agentClient,
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool>? fileExists = null,
        PackagedDesktopLifecycleOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentNullException.ThrowIfNull(runEntryStore);
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentNullException.ThrowIfNull(agentClient);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        _options = options ?? new PackagedDesktopLifecycleOptions();
        _options.Validate();
        _desktopExecutablePath = RequireAbsoluteExecutablePath(
            desktopExecutablePath,
            nameof(desktopExecutablePath));
        if (!Path.IsPathFullyQualified(applicationDirectory))
        {
            throw new ArgumentException("An absolute application directory is required.", nameof(applicationDirectory));
        }

        var fullApplicationDirectory = Path.GetFullPath(applicationDirectory);
        _agentDirectory = Path.Combine(fullApplicationDirectory, _options.AgentSubdirectory);
        _agentExecutablePath = Path.Combine(_agentDirectory, _options.AgentExecutableName);
        _runEntryStore = runEntryStore;
        _processLauncher = processLauncher;
        _agentClient = agentClient;
        _getEnvironmentVariable = getEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
    }

    public string AgentExecutablePath => _agentExecutablePath;

    public string AutostartCommandLine => $"{QuoteWindowsArgument(_desktopExecutablePath)} --agent-only";

    public static PackagedDesktopLifecycle CreateDefault()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("The StorageHub Desktop executable path is unavailable.");
        }

        var applicationDirectory = AppContext.BaseDirectory;
        var options = new PackagedDesktopLifecycleOptions();
        var agentExecutablePath = Path.Combine(
            applicationDirectory,
            options.AgentSubdirectory,
            options.AgentExecutableName);
        return new PackagedDesktopLifecycle(
            ResolveStableDesktopExecutable(executablePath),
            applicationDirectory,
            new WindowsCurrentUserRunEntryStore(),
            new WindowsHiddenAgentProcessLauncher(),
            new NamedPipePackagedAgentLifecycleClient(
                DesktopApplicationVersion.Current,
                agentExecutablePath,
                new WindowsPackagedAgentProcessMonitor()),
            Environment.GetEnvironmentVariable,
            File.Exists,
            options);
    }

    private static string ResolveStableDesktopExecutable(string executablePath)
    {
        if (!VelopackLocator.IsCurrentSet)
        {
            return executablePath;
        }

        var rootDirectory = VelopackLocator.Current.RootAppDir;
        if (string.IsNullOrWhiteSpace(rootDirectory) ||
            !Path.IsPathFullyQualified(rootDirectory))
        {
            return executablePath;
        }

        // vpk names the root execution stub from --packTitle ("StorageHub"),
        // while the managed entry point inside current remains
        // StorageHub.Desktop.exe. Point logon startup at the root stub so it
        // remains valid while Velopack replaces the current directory.
        var stableExecutable = Path.Combine(rootDirectory, PackagedDesktopStubName);
        return File.Exists(stableExecutable) ? stableExecutable : executablePath;
    }

    public AutostartConfigurationStatus ConfigureAutostart()
    {
        try
        {
            if (IsAutostartDisabled)
            {
                _runEntryStore.Remove(_options.RunEntryName);
                return AutostartConfigurationStatus.Disabled;
            }

            _runEntryStore.SetValue(_options.RunEntryName, AutostartCommandLine);
            return AutostartConfigurationStatus.Registered;
        }
        catch (Exception error) when (IsExpectedPlatformFailure(error))
        {
            return AutostartConfigurationStatus.Failed;
        }
    }

    public bool IsAutostartDisabled => string.Equals(
        _getEnvironmentVariable(PackagedDesktopLifecycleOptions.DisableAutostartEnvironmentVariable)?.Trim(),
        "1",
        StringComparison.Ordinal);

    public AutostartConfigurationStatus RemoveAutostart()
    {
        try
        {
            _runEntryStore.Remove(_options.RunEntryName);
            return AutostartConfigurationStatus.Removed;
        }
        catch (Exception error) when (IsExpectedPlatformFailure(error))
        {
            return AutostartConfigurationStatus.Failed;
        }
    }

    public async ValueTask<AgentEnsureResult> EnsureAgentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _agentClient.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AgentEnsureResult(AgentEnsureStatus.AlreadyRunning);
            }

            if (!_fileExists(_agentExecutablePath))
            {
                return new AgentEnsureResult(AgentEnsureStatus.MissingExecutable);
            }

            if (!_processLauncher.TryLaunchHidden(
                    _agentExecutablePath,
                    _agentDirectory,
                    _options.AgentArgument))
            {
                return new AgentEnsureResult(AgentEnsureStatus.LaunchFailed);
            }

            return await _agentClient
                .WaitUntilAvailableAsync(_options.StartupTimeout, cancellationToken)
                .ConfigureAwait(false)
                ? new AgentEnsureResult(AgentEnsureStatus.Started)
                : new AgentEnsureResult(AgentEnsureStatus.StartupTimedOut);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsExpectedPlatformFailure(error))
        {
            return new AgentEnsureResult(AgentEnsureStatus.LaunchFailed);
        }
    }

    public async ValueTask<bool> TryStopAgentAsync(
        AgentShutdownReason reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _agentClient
                .RequestShutdownAndWaitAsync(reason, _options.ShutdownTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (IsExpectedPlatformFailure(error))
        {
            return false;
        }
    }

    private static string RequireAbsoluteExecutablePath(string value, string parameterName)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("An absolute executable path is required.", parameterName);
        }

        var fullPath = Path.GetFullPath(value);
        if (fullPath.Contains('"'))
        {
            throw new ArgumentException("An absolute executable path is required.", parameterName);
        }

        return fullPath;
    }

    private static string QuoteWindowsArgument(string value) => $"\"{value}\"";

    private static bool IsExpectedPlatformFailure(Exception error) => error is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        Win32Exception or
        InvalidDataException or
        InvalidOperationException or
        TimeoutException;
}

public sealed class DesktopPackageLifecycleHooks(PackagedDesktopLifecycle lifecycle)
{
    private readonly PackagedDesktopLifecycle _lifecycle = lifecycle ??
        throw new ArgumentNullException(nameof(lifecycle));

    public void AfterInstall() => _ = _lifecycle.ConfigureAutostart();

    public void AfterUpdate() => _ = _lifecycle.ConfigureAutostart();

    public void BeforeUpdate() => StopSynchronously(AgentShutdownReason.Update);

    public void BeforeUninstall()
    {
        StopSynchronously(AgentShutdownReason.Uninstall);
        _ = _lifecycle.RemoveAutostart();
    }

    private void StopSynchronously(AgentShutdownReason reason)
    {
        // Velopack fast hooks cannot veto an update or uninstall. Give the
        // Agent a bounded graceful-stop window; if it cannot acknowledge,
        // Velopack's normal locking-process handling remains the final fallback.
        _ = _lifecycle.TryStopAgentAsync(reason).AsTask().GetAwaiter().GetResult();
    }
}

public static class DesktopCommandLine
{
    public const string AgentOnlyArgument = "--agent-only";

    public static bool IsAgentOnly(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument => string.Equals(
            argument,
            AgentOnlyArgument,
            StringComparison.OrdinalIgnoreCase));
    }
}
