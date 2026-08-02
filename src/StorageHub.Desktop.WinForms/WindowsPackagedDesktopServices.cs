using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace StorageHub.Desktop;

public sealed class WindowsCurrentUserRunEntryStore : ICurrentUserRunEntryStore
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void SetValue(string valueName, string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current-user startup registry key is unavailable.");
        key.SetValue(valueName, commandLine, RegistryValueKind.String);
    }

    public void Remove(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

public sealed class WindowsHiddenAgentProcessLauncher : IAgentProcessLauncher
{
    public bool TryLaunchHidden(string executablePath, string workingDirectory, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}

public sealed class WindowsPackagedAgentProcessMonitor : IPackagedAgentProcessMonitor
{
    public bool IsRunning(string executablePath)
    {
        var expectedPath = RequireAbsolutePath(executablePath);
        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && IsExpectedExecutable(process, expectedPath))
                    {
                        return true;
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    // Processes outside the current user's accessible package are not this Agent.
                }
            }
        }

        return false;
    }

    public async ValueTask<bool> WaitForExitAsync(
        int processId,
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(12))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var expectedPath = RequireAbsolutePath(executablePath);
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            try
            {
                if (process.HasExited || !IsExpectedExecutable(process, expectedPath))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            try
            {
                await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private static bool IsExpectedExecutable(Process process, string expectedPath)
    {
        var actualPath = process.MainModule?.FileName;
        return !string.IsNullOrWhiteSpace(actualPath) &&
            string.Equals(
                Path.GetFullPath(actualPath),
                expectedPath,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(path));
        }

        return Path.GetFullPath(path);
    }
}
